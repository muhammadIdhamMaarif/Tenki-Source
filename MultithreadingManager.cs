/*
==========================================================
 MultithreadingManager.cs — Panduan Cepat & Dokumentasi
==========================================================

🔎 Gambaran Besar
-----------------
Script ini ngerjain pipeline "Input lokasi → Fetch cuaca (paralel) → Export hasil".
Targetnya perform buat WebGL (bisa ratusan/barisan lokasi), tapi juga jalan di Editor/Standalone.

Alur besarnya:
1) User upload CSV/XLSX (kolom minimal: Name, Lat, Lon)
2) Klik "Process" → script bikin worker pool (coroutines) sesuai `maxConcurrency`
3) Tiap row di-hit ke WeatherAPI (atau via relay) + retry/backoff buat error transient
4) Hasil diisi ke _rows → bisa diunduh sebagai CSV/XLSX

🧩 Komponen Utama
-----------------
- UI Button (Pick/Process/Download CSV/XLSX) + `statusText` untuk progress/status
- Bridge WebGL:
  - `FileBridge_PickFile` (JS → C#) untuk pick file dari browser
  - `FileBridge_DownloadText` untuk nyimpen hasil ke file (WebGL pakai Blob)
- Parser:
  - CSV: splitter sederhana yang aman terhadap kutip ganda
  - XLSX minimal: baca `xl/sharedStrings.xml` + `xl/worksheets/sheet1.xml`
  - Penulisan XLSX minimal: bikin ZIP dengan part wajib (workbook, sheet1, sharedStrings, props)
- Networking:
  - Langsung ke WeatherAPI, atau via `relayBaseUrl` kalau `useRelay = true`
  - Retry `maxRetries` dgn backoff + jitter untuk 429/5xx/timeout, dll.
- Concurrency:
  - Worker pool coroutine (Indeks job diambil atomik via `Interlocked.Increment`)
  - `maxConcurrency` cocoknya 32–64 untuk WebGL (tuning sesuai limit API)

🗂 Struktur Data
----------------
- `KecamatanRow`: input (Name, Lat, Lon) + output (LastUpdate, SuhuC, Kelembapan, dll.)
- `_rows`: list semua baris
- `_sourceFilename` dan `_sourceKind`: info asal file untuk penamaan output

🧪 Cara Pakai (Quick Start)
---------------------------
1) Assign semua referensi UI di Inspector (btnPick, btnProcess, btnDownloadCsv/Xlsx, statusText)
2) Set `weatherApiBaseUrl` (default udah ok), pasang API key di `tenkiChatController.WeatherApiKey`
3) (Opsional) Jika mau relay, set `useRelay = true` dan isi `relayBaseUrl`
4) Build WebGL → user bisa upload file di browser.
   Di Editor: kalau lupa upload, drop `StreamingAssets/input.csv` lalu klik Process.

🧭 Format Input
---------------
- CSV/XLSX harus punya header yang cocok dengan pola:
  - nameHeader: "nama|name|kecamatan"
  - latHeader : "lat|latitude|lintang"
  - lonHeader : "lon|lng|longitude|bujur"
  Kamu boleh ubah reglernya di Inspector biar fleksibel (Ind/Eng).

📤 Output
---------
- CSV header: Name,Latitude,Longitude,Last Update,Suhu (°C),Kelembapan (%),Kondisi,Kecepatan Angin (kph),Arah Angin,Sinar UV
- XLSX: single-sheet, minimal parts (cukup buat Excel/Numbers/LibreOffice)

⚙️ Tuning & Best Practices
--------------------------
- `maxConcurrency`: naikin perlahan. Ingat batas rate-limit API cuaca.
- `maxRetries`: 2–4 udah umum. Di-up kalau jaringan kamu kurang stabil.
- `verbose = true` buat debug (parse error / give-up log).
- Gunakan relay kalau: 
  - butuh sembunyiin API key di front-end (WebGL), atau 
  - mau agregasi/limit request di server.

🛡️ Error Handling
------------------
- Missing key → statusText ngasih tahu
- File gagal parse → statusText + Debug.LogException
- Networking gagal → retry otomatis utk 429/5xx/timeout; kalau mentok → give up per-row (aman, pipeline lanjut)

🧠 Catatan Implementasi
-----------------------
- CSV splitter handle kutip ganda: "a, ""b""" → a, "b"
- XLSX reader pakai regex (cukup untuk sheet sederhana; kalau sheet kompleks, pakai lib dedicated)
- XLSX writer: nulis angka sebagai <v>number</v> kalau parse-able; selainnya ke sharedStrings
- WebGL download XLSX: kirim "binary-as-Latin1" string supaya byte tetap utuh di Blob

🧰 Ekstensi yang gampang ditambah
---------------------------------
- Tambah kolom input lain (mis. id, province) → simpan di `KecamatanRow`
- Tambah sheet lain di XLSX writer
- Ganti sumber API cuaca (asalkan JSON-nya di-map ke struct `Current`)

📌 FAQ Mini
-----------
Q: Kenapa pakai coroutine worker pool, bukan Task/Thread?
A: Unity WebGL nggak support thread native. Coroutine + UnityWebRequest paling aman lintas target.

Q: Kenapa XLSX-nya "minimal"?
A: Biar dependency nol dan cocok di WebGL. Kalau butuh fitur kaya (formatting/style), pakai lib eksternal di platform yang mendukung.

Diagram Alur (sederhana)
------------------------
[Pick/Editor Fallback] → [Parse CSV/XLSX → _rows] → (Process) → 
→ [N Workers] → (Fetch Weather per row + Retry) → [_rows terisi] → [Download CSV/XLSX]

==========================================================
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression; // for minimal XLSX read/write
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Michsky.DreamOS;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using CompressionLevel = System.IO.Compression.CompressionLevel;

/// <summary>
/// Manager untuk:
/// - Baca CSV/XLSX berisi minimal kolom Name/Lat/Lon
/// - Ambil data cuaca (WeatherAPI) per baris secara paralel (coroutine worker pool)
/// - Export hasil ke CSV atau XLSX minimal (tanpa styling)
///
/// Desain agar nyaman dipakai di WebGL (file picker & download via JS bridge).
/// </summary>
public class MultithreadingManager : MonoBehaviour
{
    /// <summary>
    /// Referensi ke <see cref="MainProgram"/> yang menyimpan WeatherApiKey.
    /// Pastikan key terisi kalau tidak memakai relay.
    /// </summary>
    public MainProgram tenkiChatController;
    
    [Header("Assign in Inspector")]
    /// <summary>Tombol untuk memilih file input (CSV/XLSX) via file picker (WebGL).</summary>
    public ButtonManager btnPick;
    /// <summary>Tombol untuk mulai proses fetch cuaca.</summary>
    public ButtonManager btnProcess;
    /// <summary>Tombol untuk download hasil dalam format CSV.</summary>
    public ButtonManager btnDownloadCsv;
    /// <summary>Tombol untuk download hasil dalam format XLSX minimal.</summary>
    public ButtonManager btnDownloadXlsx;
    /// <summary>Teks status/progress untuk pengguna.</summary>
    public TMP_Text statusText;

    [Header("Networking")]
    /// <summary>Base URL untuk WeatherAPI (default sudah benar).</summary>
    public string weatherApiBaseUrl = "https://api.weatherapi.com/v1";
    /// <summary>Jika true, request tidak langsung ke WeatherAPI tapi lewat relay server.</summary>
    public bool useRelay = false;
    /// <summary>Base URL relay (contoh: https://your-worker.example.com). Hanya dipakai jika <see cref="useRelay"/> = true.</summary>
    public string relayBaseUrl = ""; // e.g., https://your-worker.example.com

    [Header("Performance")]
    [Tooltip("How many concurrent requests; start with 32..64 for WebGL")]
    /// <summary>Jumlah worker paralel. Sesuaikan agar tidak kena rate limit (32–64 lazim).</summary>
    public int maxConcurrency = 48;
    [Tooltip("Max retries per row for transient errors")]
    /// <summary>Retry maksimum per baris untuk error transient (429/5xx/timeout).</summary>
    public int maxRetries = 3;

    [Header("CSV/XLSX Mapping")]
    [Tooltip("Column headers to detect in input (case-insensitive). You can change Indonesian/English names here.")]
    /// <summary>Pola header untuk kolom nama (regex OR). Bebas ganti sesuai input kamu.</summary>
    public string nameHeader = "nama|name|kecamatan";
    /// <summary>Pola header untuk kolom latitude.</summary>
    public string latHeader  = "lat|latitude|lintang";
    /// <summary>Pola header untuk kolom longitude.</summary>
    public string lonHeader  = "lon|lng|longitude|bujur";

    [Header("Debug")]
    /// <summary>Kalau true, tulis log tambahan (parse error/give-up per row).</summary>
    public bool verbose = false;

#if UNITY_WEBGL && !UNITY_EDITOR
    // Bridge ke JavaScript (hanya aktif di WebGL build, bukan Editor)
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void FileBridge_PickFile(string accept, string gameObjectName, string callbackMethod);

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void FileBridge_DownloadText(string filename, string mime, string data);
#else
    // Editor/Standalone fallback: 
    // - PickFile: kasih warning (pakai StreamingAssets/input.csv kalau lupa upload)
    // - DownloadText: tulis file ke disk (parent folder dari Assets)
    private static void FileBridge_PickFile(string accept, string gameObjectName, string callbackMethod)
    {
        Debug.LogWarning("Use WebGL build to test browser file picker. In Editor: drag CSV into /StreamingAssets/input.csv and press Process.");
    }
    private static void FileBridge_DownloadText(string filename, string mime, string data)
    {
        var path = Path.Combine(Application.dataPath, "..", filename);
        File.WriteAllText(path, data, new UTF8Encoding(true));
        Debug.Log($"Saved: {Path.GetFullPath(path)}");
    }
#endif

    // ===== Data model =====

    /// <summary>
    /// Satu baris data (input + output cuaca).
    /// </summary>
    [Serializable]
    public class KecamatanRow
    {
        // Input
        public string Name;
        public double Lat;
        public double Lon;

        // Output dari WeatherAPI
        public string LastUpdate;
        public double SuhuC;
        public int Kelembapan;
        public string Kondisi;
        public double KecepatanAnginKph;
        public string ArahAngin;
        public double UV;
    }

    // Internal buffer semua row yang sedang/akan diproses.
    private readonly List<KecamatanRow> _rows = new();
    // Nama file sumber (tanpa ekstensi) → dipakai untuk nama output.
    private string _sourceFilename = "input";
    // Jenis sumber (csv/xlsx) — hanya informasi.
    private string _sourceKind = "csv"; // csv/xlsx

    /// <summary>
    /// Awake: pasang event listener tombol & set status awal.
    /// </summary>
    void Awake()
    {
        if (btnPick)         btnPick.onClick.AddListener(OnPick);
        if (btnProcess)      btnProcess.onClick.AddListener(OnProcess);
        if (btnDownloadCsv)  btnDownloadCsv.onClick.AddListener(()=> Download("csv"));
        if (btnDownloadXlsx) btnDownloadXlsx.onClick.AddListener(()=> Download("xlsx"));

        statusText.text = "Ready. Upload a .csv or .xlsx.";
    }

    // ===== UI actions =====

    /// <summary>
    /// Handler tombol "Pick" → panggil file picker (WebGL).
    /// Editor/Standalone: cuma warning (gunakan StreamingAssets sebagai fallback).
    /// </summary>
    void OnPick()
    {
        Debug.Log("File Clicked");
        FileBridge_PickFile(".csv,.xlsx", gameObject.name, nameof(OnWebFilePicked));
    }

    /// <summary>
    /// Callback dari JS saat file dipilih.
    /// Format payload: "name.ext|ext|&lt;base64&gt;"
    /// </summary>
    /// <param name="payload">String gabungan nama|ekstensi|base64</param>
    public void OnWebFilePicked(string payload)
    {
        try
        {
            // Pisahkan "name.ext|ext|<base64>"
            var firstPipe = payload.IndexOf('|');
            var secondPipe = payload.IndexOf('|', firstPipe + 1);
            var filename = payload.Substring(0, firstPipe);
            var ext = payload.Substring(firstPipe + 1, secondPipe - firstPipe - 1).ToLowerInvariant();
            var b64 = payload.Substring(secondPipe + 1);

            _sourceFilename = Path.GetFileNameWithoutExtension(filename);
            _sourceKind = (ext == "xlsx") ? "xlsx" : "csv";

            // Decode isi file
            var bytes = Convert.FromBase64String(b64);
            if (_sourceKind == "csv")
            {
                var csv = Encoding.UTF8.GetString(bytes);
                ParseCsv(csv);
            }
            else
            {
                ParseXlsx(bytes);
            }

            statusText.text = $"Loaded {_rows.Count} rows from {filename}.";
        }
        catch (Exception ex)
        {
            statusText.text = $"Failed to load file: {ex.Message}";
            Debug.LogException(ex);
        }
    }

    /// <summary>
    /// Handler tombol "Process".
    /// - Validasi API key (kecuali pakai relay)
    /// - Fallback Editor: cari /StreamingAssets/input.csv jika belum ada _rows
    /// - Mulai coroutine worker pool
    /// </summary>
    void OnProcess()
    {
        if (string.IsNullOrWhiteSpace(tenkiChatController.WeatherApiKey) && !useRelay)
        {
            statusText.text = "WeatherAPI key required.";
            return;
        }
        if (_rows.Count == 0)
        {
            // Editor fallback: quick import if user forgot to upload in Editor
#if !UNITY_WEBGL || UNITY_EDITOR
            var path = Path.Combine(Application.streamingAssetsPath, "input.csv");
            if (File.Exists(path))
            {
                ParseCsv(File.ReadAllText(path, Encoding.UTF8));
            }
#endif
        }
        if (_rows.Count == 0)
        {
            statusText.text = "No rows to process. Upload a file first.";
            return;
        }
        StopAllCoroutines();
        StartCoroutine(ProcessAll());
    }

    /// <summary>
    /// Download hasil sebagai CSV atau XLSX.
    /// WebGL: lewat JS bridge. Editor: disimpan di parent folder Assets.
    /// </summary>
    /// <param name="kind">"csv" atau "xlsx"</param>
    void Download(string kind)
    {
        if (_rows.Count == 0)
        {
            statusText.text = "Nothing to download. Process something first.";
            return;
        }

        try
        {
            if (kind == "csv")
            {
                var csv = BuildCsv();
                FileBridge_DownloadText($"{_sourceFilename}_weather.csv", "text/csv", csv);
                statusText.text = "CSV downloaded.";
            }
            else
            {
                var xlsxBytes = BuildXlsx();
                var b64 = Convert.ToBase64String(xlsxBytes);
                // WebGL: paling aman kirim sebagai "binary-as-Latin1" supaya Blob nerima byte asli.
                var binary = Base64ToLatin1(b64);
                FileBridge_DownloadText($"{_sourceFilename}_weather.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", binary);
                statusText.text = "XLSX downloaded.";
            }
        }
        catch (Exception ex)
        {
            statusText.text = $"Download failed: {ex.Message}";
            Debug.LogException(ex);
        }
    }

    /// <summary>
    /// Utility: ubah base64 → string Latin1 (tiap byte jadi 1 char 0..255) agar JS Blob dapet byte persis.
    /// </summary>
    string Base64ToLatin1(string b64)
    {
        var bytes = Convert.FromBase64String(b64);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append((char)b);
        return sb.ToString();
    }

    // ===== CSV =====

    /// <summary>
    /// Parse CSV ke dalam <see cref="_rows"/>.
    /// - Baris pertama dianggap header
    /// - Deteksi kolom pakai regex dari nameHeader/latHeader/lonHeader
    /// - Split baris aman untuk tanda kutip ganda
    /// </summary>
    void ParseCsv(string csv)
    {
        _rows.Clear();
        using var reader = new StringReader(csv);
        string header = reader.ReadLine();
        if (header == null) return;

        var headers = SplitCsvLine(header).ToArray();
        int idxName = FindIndex(headers, nameHeader);
        int idxLat  = FindIndex(headers, latHeader);
        int idxLon  = FindIndex(headers, lonHeader);

        if (idxName < 0 || idxLat < 0 || idxLon < 0)
            throw new Exception("Input CSV must contain columns: Name, Lat, Lon (see nameHeader/latHeader/lonHeader patterns).");

        for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = SplitCsvLine(line).ToArray();
            if (cols.Length <= Math.Max(idxName, Math.Max(idxLat, idxLon))) continue;

            var row = new KecamatanRow
            {
                Name = cols[idxName].Trim(),
                Lat  = ParseDouble(cols[idxLat]),
                Lon  = ParseDouble(cols[idxLon]),
            };
            _rows.Add(row);
        }
    }

    /// <summary>
    /// Split satu baris CSV. Support:
    /// - Koma sebagai delimiter
    /// - Tanda kutip ganda untuk mengapit field yang mengandung koma/kutip/newline
    /// - Escape kutip ganda di dalam quoted field: "" → "
    /// </summary>
    IEnumerable<string> SplitCsvLine(string line)
    {
        // simple CSV splitter (handles quotes)
        bool inQuotes = false;
        var sb = new StringBuilder();
        for (int i=0;i<line.Length;i++)
        {
            char c = line[i];
            if (c == '\"')
            {
                if (inQuotes && i+1<line.Length && line[i+1]=='\"')
                { sb.Append('\"'); i++; }         // "" → tambahkan satu " ke hasil
                else inQuotes = !inQuotes;        // toggle quoted mode
            }
            else if (c == ',' && !inQuotes)
            { yield return sb.ToString(); sb.Length = 0; } // delimiter di luar quotes
            else sb.Append(c);
        }
        yield return sb.ToString();
    }

    /// <summary>
    /// Cari index header yang cocok dgn pola regex:
    /// - Coba exact match dulu (^(?:pattern)$)
    /// - Kalau gagal, fallback ke "contains" (Regex.IsMatch tanpa anchor)
    /// </summary>
    int FindIndex(string[] headers, string pattern)
    {
        var rx = new Regex($"^(?:{pattern})$", RegexOptions.IgnoreCase);
        for (int i=0;i<headers.Length;i++)
        {
            var h = headers[i].Trim();
            if (rx.IsMatch(h)) return i;
        }
        // try contains
        for (int i=0;i<headers.Length;i++)
        {
            if (Regex.IsMatch(headers[i], pattern, RegexOptions.IgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Parse double yang toleran:
    /// - Ganti koma → titik (mendukung format Eropa)
    /// - Gunakan CultureInfo.InvariantCulture
    /// </summary>
    double ParseDouble(string s)
    {
        s = s.Trim().Replace(",", "."); // robust for European CSVs
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return 0;
    }

    /// <summary>
    /// Susun CSV output lengkap (dengan escape kalau ada koma/kutip/newline).
    /// Urutan kolom sesuai spesifikasi.
    /// </summary>
    string BuildCsv()
    {
        // header: Name,Lat,Lon, then the 7 outputs
        var sb = new StringBuilder();
        sb.AppendLine("Name,Latitude,Longitude,Last Update,Suhu (°C),Kelembapan (%),Kondisi,Kecepatan Angin (kph),Arah Angin,Sinar UV");
        foreach (var r in _rows)
        {
            sb.Append(Escape(r.Name)).Append(',')
              .Append(r.Lat.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Lon.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(r.LastUpdate)).Append(',')
              .Append(r.SuhuC.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Kelembapan).Append(',')
              .Append(Escape(r.Kondisi)).Append(',')
              .Append(r.KecepatanAnginKph.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(r.ArahAngin)).Append(',')
              .Append(r.UV.ToString("0.#", CultureInfo.InvariantCulture)).AppendLine();
        }
        return sb.ToString();

        string Escape(string v)
        {
            if (v == null) return "";
            if (v.Contains(",") || v.Contains("\"") || v.Contains("\n"))
                return $"\"{v.Replace("\"","\"\"")}\"";
            return v;
        }
    }

    // ===== Minimal XLSX (first sheet only; strings/numbers) =====
    // Read: sharedStrings + sheet1. Write: a simple single-sheet workbook.

    /// <summary>
    /// Parse XLSX minimal:
    /// - Baca sharedStrings (kalau ada) untuk mapping index → string
    /// - Ambil sheet pertama (sheet1.xml atau sheetX.xml)
    /// - Extract cell (A1, B2, dst), handle tipe "s" (shared string) vs angka
    /// - Baris pertama dianggap header → deteksi kolom Name/Lat/Lon
    /// </summary>
    void ParseXlsx(byte[] bytes)
    {
        _rows.Clear();

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, true);

        // sharedStrings
        var sharedStrings = new List<string>();
        var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry != null)
        {
            using var sst = new StreamReader(sstEntry.Open(), Encoding.UTF8);
            var xml = sst.ReadToEnd();
            foreach (Match m in Regex.Matches(xml, "<t[^>]*>(.*?)</t>", RegexOptions.Singleline))
                sharedStrings.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        // sheet1 (atau sheetX pertama yang ditemukan)
        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml") ?? zip.Entries.FirstOrDefault(e=>e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
        if (sheetEntry == null) throw new Exception("No worksheet found in XLSX.");

        string sheetXml;
        using (var sr = new StreamReader(sheetEntry.Open(), Encoding.UTF8)) sheetXml = sr.ReadToEnd();

        // rows <row> ... <c r="A1" t="s"><v>0</v></c> ...
        var rows = new List<List<string>>();
        foreach (Match rowM in Regex.Matches(sheetXml, "<row[^>]*>(.*?)</row>", RegexOptions.Singleline))
        {
            var rowXml = rowM.Groups[1].Value;
            var cells = new Dictionary<int,string>();
            foreach (Match cM in Regex.Matches(rowXml, "<c[^>]*?r=\"([A-Z]+)\\d+\"(?:[^>]*?t=\"([^\"]+)\")?[^>]*>(.*?)</c>", RegexOptions.Singleline))
            {
                var colLetters = cM.Groups[1].Value;
                var t = cM.Groups[2].Value; // "s" => shared string
                var cInner = cM.Groups[3].Value;
                var vMatch = Regex.Match(cInner, "<v>(.*?)</v>", RegexOptions.Singleline);
                var val = vMatch.Success ? vMatch.Groups[1].Value : "";

                int colIndex = ColLettersToIndex(colLetters); // 0-based
                string cellText = val;
                if (t == "s") // shared string → map index ke teks
                {
                    if (int.TryParse(val, out var sidx) && sidx >= 0 && sidx < sharedStrings.Count)
                        cellText = sharedStrings[sidx];
                    else cellText = "";
                }
                cells[colIndex] = cellText;
            }
            // Bikin list kolom berukuran maxCol+1 biar index aman
            var maxCol = cells.Count == 0 ? 0 : cells.Keys.Max();
            var row = new List<string>(Enumerable.Repeat("", maxCol+1));
            foreach (var kv in cells) row[kv.Key] = kv.Value;
            rows.Add(row);
        }
        if (rows.Count == 0) return;

        // header detection
        var headers = rows[0].Select(h=>h?.Trim() ?? "").ToArray();
        int idxName = FindIndex(headers, nameHeader);
        int idxLat  = FindIndex(headers, latHeader);
        int idxLon  = FindIndex(headers, lonHeader);
        if (idxName < 0 || idxLat < 0 || idxLon < 0)
            throw new Exception("XLSX sheet1 must contain columns: Name, Lat, Lon (see nameHeader/latHeader/lonHeader patterns).");

        // Isi _rows mulai baris ke-2 (index 1)
        for (int r=1; r<rows.Count; r++)
        {
            var cols = rows[r];
            if (cols.Count <= Math.Max(idxName, Math.Max(idxLat, idxLon))) continue;
            _rows.Add(new KecamatanRow {
                Name = (cols[idxName] ?? "").Trim(),
                Lat = ParseDouble(cols[idxLat] ?? "0"),
                Lon = ParseDouble(cols[idxLon] ?? "0")
            });
        }
    }

    /// <summary>
    /// Convert huruf kolom (A, B, ..., Z, AA, AB, ...) menjadi index 0-based.
    /// </summary>
    int ColLettersToIndex(string letters)
    {
        int n=0;
        foreach (var ch in letters) n = n*26 + (ch - 'A' + 1);
        return n-1;
    }

    /// <summary>
    /// Build file XLSX minimal (1 sheet, no styles) dari konten <see cref="_rows"/>.
    /// - Teks tertentu dipaksa jadi sharedStrings
    /// - Angka valid ditulis sebagai numeric value (bukan string)
    /// </summary>
    byte[] BuildXlsx()
    {
        // Build a super-simple XLSX with one sheet and no styles
        // We’ll write only text and numbers; enough for downstream Excel usage.
        var rows = new List<string[]>();
        rows.Add(new[] {
            "Name","Latitude","Longitude",
            "Last Update","Suhu (°C)","Kelembapan (%)","Kondisi","Kecepatan Angin (kph)","Arah Angin","Sinar UV"
        });
        foreach (var r in _rows)
        {
            rows.Add(new[] {
                r.Name,
                r.Lat.ToString(CultureInfo.InvariantCulture),
                r.Lon.ToString(CultureInfo.InvariantCulture),
                r.LastUpdate ?? "",
                r.SuhuC.ToString("0.#", CultureInfo.InvariantCulture),
                r.Kelembapan.ToString(CultureInfo.InvariantCulture),
                r.Kondisi ?? "",
                r.KecepatanAnginKph.ToString("0.#", CultureInfo.InvariantCulture),
                r.ArahAngin ?? "",
                r.UV.ToString("0.#", CultureInfo.InvariantCulture)
            });
        }

        // Build sharedStrings and sheet XML
        var sst = new List<string>(); // unique strings
        string S(string s) // add to shared strings and return index as string
        {
            if (s == null) s = "";
            var idx = sst.IndexOf(s);
            if (idx < 0) { sst.Add(s); idx = sst.Count - 1; }
            return idx.ToString();
        }

        var sheetSb = new StringBuilder();
        sheetSb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (int r=0; r<rows.Count; r++)
        {
            sheetSb.Append($"<row r=\"{r+1}\">");
            var cols = rows[r];
            for (int c=0; c<cols.Length; c++)
            {
                string a1 = ToA1(c) + (r+1).ToString();
                var v = cols[c] ?? "";
                // Kolom teks: header baris 0, juga kolom 0(Name),3(LastUpdate),6(Kondisi),8(ArahAngin)
                if (r==0 || (c==0 || c==3 || c==6 || c==8))
                {
                    sheetSb.Append($"<c r=\"{a1}\" t=\"s\"><v>{S(v)}</v></c>");
                }
                else
                {
                    // Coba tulis sebagai angka kalau bisa
                    if (double.TryParse(v.Replace(",","."), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        sheetSb.Append($"<c r=\"{a1}\"><v>{num.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    else
                        sheetSb.Append($"<c r=\"{a1}\" t=\"s\"><v>{S(v)}</v></c>");
                }
            }
            sheetSb.Append("</row>");
        }
        sheetSb.Append("</sheetData></worksheet>");

        string sharedStringsXml =
            "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"" + sst.Count + "\" uniqueCount=\"" + sst.Count + "\">" +
            string.Join("", sst.Select(s => $"<si><t>{XmlEscape(s)}</t></si>")) + "</sst>";

        // Other minimal parts (paket XLSX wajib punya ini)
        string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>";

        string relsRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        string workbook =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

        string workbookRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
            "</Relationships>";

        string appProps =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
            "<Application>Unity</Application></Properties>";

        string coreProps =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\">" +
            $"<dc:title>Weather Output {_sourceFilename}</dc:title></cp:coreProperties>";

        using var outMs = new MemoryStream();
        using (var zip = new ZipArchive(outMs, ZipArchiveMode.Create, true))
        {
            WriteZip(zip, "[Content_Types].xml", contentTypes);
            WriteZip(zip, "_rels/.rels", relsRels);
            WriteZip(zip, "xl/workbook.xml", workbook);
            WriteZip(zip, "xl/_rels/workbook.xml.rels", workbookRels);
            WriteZip(zip, "xl/sharedStrings.xml", sharedStringsXml);
            WriteZip(zip, "xl/worksheets/sheet1.xml", sheetSb.ToString());
            WriteZip(zip, "docProps/app.xml", appProps);
            WriteZip(zip, "docProps/core.xml", coreProps);
        }
        return outMs.ToArray();

        static void WriteZip(ZipArchive zip, string path, string text)
        {
            var e = zip.CreateEntry(path, CompressionLevel.Fastest);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(text);
        }
        static string XmlEscape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"","&quot;");
        static string ToA1(int col)
        {
            var s = "";
            col++;
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                s = (char)('A' + rem) + s;
                col = (col - 1) / 26;
            }
            return s;
        }
    }

    // ===== Concurrency: worker pool of coroutines =====

    /// <summary>
    /// Jalankan worker pool untuk memproses semua baris di <see cref="_rows"/>.
    /// - Bikin N worker (N = maxConcurrency, min 1)
    /// - Masing-masing worker ambil index baris berikutnya secara atomik
    /// - Update status tiap 50 baris atau saat selesai
    /// </summary>
    IEnumerator ProcessAll()
    {
        statusText.text = $"Processing {_rows.Count} rows…";
        var startTime = Time.realtimeSinceStartup;

        int completed = 0;
        int idx = -1;
        int N = Mathf.Max(1, maxConcurrency);

        var workers = new List<Coroutine>(N);
        for (int i=0;i<N;i++)
            workers.Add(StartCoroutine(Worker()));

        // Tunggu semua worker selesai
        foreach (var w in workers) yield return w;

        var dur = Time.realtimeSinceStartup - startTime;
        statusText.text = $"Done: {completed}/{_rows.Count} rows in {dur:0.0}s";

        // Worker lokal (closure) yang ambil task satu-per-satu
        IEnumerator Worker()
        {
            while (true)
            {
                // Ambil index berikutnya secara thread-safe
                int my = System.Threading.Interlocked.Increment(ref idx);
                if (my >= _rows.Count) yield break;

                var row = _rows[my];
                // Ambil cuaca (punya mekanisme retry di dalamnya)
                yield return StartCoroutine(FillWeather(row));

                int c = System.Threading.Interlocked.Increment(ref completed);
                if (c % 50 == 0 || c == _rows.Count)
                    statusText.text = $"Processed {c}/{_rows.Count}";
            }
        }
    }

    /// <summary>
    /// Ambil data cuaca untuk satu baris lokasi:
    /// - Susun query "lat,lon"
    /// - Pilih endpoint (relay atau WeatherAPI langsung)
    /// - Kirim request (timeout 30s)
    /// - Kalau error transient (429/5xx/timeout), lakukan retry dengan backoff + jitter
    /// - Isi field output di row jika berhasil parse
    /// </summary>
    IEnumerator FillWeather(KecamatanRow row)
    {
        string q = $"{row.Lat.ToString(CultureInfo.InvariantCulture)},{row.Lon.ToString(CultureInfo.InvariantCulture)}";

        int attempt = 0;
        while (true)
        {
            attempt++;
            UnityWebRequest req;
            if (useRelay)
            {
                var url = $"{relayBaseUrl.TrimEnd('/')}/weatherapi/current?q={UnityWebRequest.EscapeURL(q)}";
                req = UnityWebRequest.Get(url);
            }
            else
            {
                var key = tenkiChatController.WeatherApiKey.Trim();
                var url = $"{weatherApiBaseUrl}/current.json?key={key}&q={UnityWebRequest.EscapeURL(q)}&aqi=no";
                req = UnityWebRequest.Get(url);
            }
            req.timeout = 30;

            // Kirim request + tunggu selesai
            yield return req.SendWebRequest();

            // Tanda error transient => eligible untuk retry
            bool transient =
                req.result != UnityWebRequest.Result.Success ||
                req.responseCode == 429 || // rate limit
                (req.responseCode >= 500 && req.responseCode < 600);

            if (!transient)
            {
                try
                {
                    var json = req.downloadHandler.text;
                    var data = JsonUtility.FromJson<WeatherApiCurrentResponse>(json);
                    if (data?.current == null) throw new Exception("Missing current.");

                    // Map field JSON ke row output
                    row.LastUpdate = data.current.last_updated ?? "";
                    row.SuhuC = data.current.temp_c;
                    row.Kelembapan = data.current.humidity;
                    row.Kondisi = data.current.condition?.text ?? "";
                    row.KecepatanAnginKph = data.current.wind_kph;
                    row.ArahAngin = string.IsNullOrEmpty(data.current.wind_dir)
                        ? $"{data.current.wind_degree}°"
                        : data.current.wind_dir;
                    row.UV = data.current.uv;
                }
                catch (Exception ex)
                {
                    // Kalau JSON berubah/aneh: kita log & lanjut (row tetap ada tapi mungkin kosong)
                    if (verbose) Debug.LogWarning($"Parse error for {row.Name}: {ex.Message}");
                }
                yield break; // selesai untuk row ini (berhasil atau minimal sudah dicoba parse)
            }

            // Gagal & bukan kondisi yang bisa diatasi langsung → cek batas retry
            if (attempt >= maxRetries)
            {
                if (verbose) Debug.LogWarning($"Giving up {row.Name} after {attempt} tries. HTTP {req.responseCode} {req.error}");
                yield break;
            }

            // Backoff dengan jitter: makin sering gagal, makin lama nunggu (tapi dibatasi 10 detik)
            float delay = Mathf.Min(10f, 0.8f * attempt + UnityEngine.Random.Range(0f, 0.6f));
            yield return new WaitForSecondsRealtime(delay);
        }
    }

    // Minimal response types (subset of your Tenki types)
    /// <summary>Substruktur kondisi cuaca.</summary>
    [Serializable] public class Condition { public string text; public string icon; public int code; }
    /// <summary>Substruktur data "current" dari WeatherAPI.</summary>
    [Serializable] public class Current
    {
        public string last_updated;
        public double temp_c;
        public Condition condition;
        public double wind_kph;
        public int humidity;
        public double uv;
        public double wind_degree;
        public string wind_dir;
    }
    /// <summary>Struktur utama untuk deserialisasi JSON WeatherAPI (current.json).</summary>
    [Serializable] public class WeatherApiCurrentResponse
    {
        public Location location;
        public Current current;
    }
    /// <summary>Info lokasi dari respons WeatherAPI (tidak semuanya dipakai).</summary>
    [Serializable] public class Location
    {
        public string name; public string region; public string country;
        public double lat; public double lon; public string tz_id;
        public string localtime;
    }
}
