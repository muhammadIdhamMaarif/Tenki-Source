// ================================================================
// MainProgram.cs (Tenki-Chan)
// ================================================================
// Ringkasan:
// - Komponen MonoBehaviour untuk:
//   (1) Menentukan niat user via OpenAI (intent: "weather" / "chitchat")
//   (2) Resolve lokasi ke format WeatherAPI (lat,lon atau q string)
//   (3) Ambil data cuaca (current/forecast) dari WeatherAPI
//   (4) Susun kalimat ramah (LLM) + putar suara (ElevenLabs)
// - Dibuat untuk Unity 6000.x + target WebGL friendly.
//
// Catatan Penting Arsitektur:
// - "Pipeline ber-epoch": setiap klik kirim akan menaikkan _epoch. Semua coroutine
//   dan request HTTP menyimpan "epoch" saat mulai; bila _epoch berubah, step lama
//   berhenti (guard: if (epoch != _epoch) yield break).
// - "InFlight request tracking": semua UnityWebRequest disimpan di _inFlight agar
//   bisa di-Abort saat user memulai request baru (menghindari race/overlap).
// - "UseSecureRelay": disarankan ON untuk build WebGL production agar kunci API
//   tidak terekspos. Endpoint relay harus menambah header API key di server.
// - Semua pesan LLM diminta STRICT JSON untuk plan (TenkiPlan) agar parsing stabil.
//
// Cara Pakai Singkat (Editor):
// 1) Tambah script ini ke GameObject (misal: "TenkiRunner").
// 2) Assign referensi UI (InputField, OutputText, SendButton, dll) di Inspector.
// 3) Isi API keys ATAU aktifkan UseSecureRelay + RelayBaseUrl.
// 4) Pastikan ada AudioSource (untuk TTS), dan komponen UI untuk output cuaca.
// 5) (Opsional) Subscribe event OnFinalReply/onInteractableEnable/Disable.
//
// Tips Debug:
// - Centang VerboseLogging untuk melihat payload request/response di Console.
// - Status proses muncul di statusText (e.g., "Memahami Kalimatmu", dll).
// ================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;
using Unity.Services.Authentication;
using Unity.Services.CloudCode;
using Unity.Services.CloudCode.GeneratedBindings;
using Unity.Services.Core;
using Object = UnityEngine.Object;

/// <summary>
/// MainProgram
/// - Mengirim teks user ke OpenAI (Chat Completions) dengan JSON schema ketat
/// - Menentukan intent: "weather" atau "chitchat"
/// - Jika "weather": resolve query WeatherAPI.com & ambil cuaca (current/forecast)
/// - Tampilkan balasan ramah dari Tenki-Chan (+ TTS)
/// - Cocok untuk Unity 6000.x & WebGL
/// </summary>
public class MainProgram : MonoBehaviour
{
    // ==============
    // Serialized UI
    // ==============

    [Header("UI (assign in Inspector)")]
    public TMP_InputField InputField;        // input teks dari user
    public TextMeshProUGUI OutputText;       // area output text (untuk chitchat / fallback)
    public Button SendButton;                // tombol kirim
    public TMP_Text statusText;              // status proses (UX kecil agar user paham state)

    // ========================
    // Kunci & konfigurasi API
    // ========================

    [Header("Keys (paste here)")]
    [Tooltip("OpenAI API key (sk-...) — kosongkan jika pakai relay")]
    public string OpenAIApiKey = "";
    [Tooltip("WeatherAPI.com key — kosongkan jika pakai relay")]
    public string WeatherApiKey = "";
    [Tooltip("ElevenLabs API key — dipakai untuk TTS")]
    public string ElevenLabsApiKey = "";
    [Tooltip("EvelenLabs voice ID (contoh: B8gJV1IhpuegLxdpXFOE)")]
    public string ElevenLabsVoiceId = "B8gJV1IhpuegLxdpXFOE";

    [Header("Model & Behavior")]
    [Tooltip("ID model OpenAI. gpt-4o-mini cepat dan bagus untuk output JSON.")]
    public string OpenAIModel = "gpt-4o-mini";
    [Tooltip("True = log debug detail di Console (request/response).")]
    public bool VerboseLogging = false;

    [Header("Networking (Direct)")]
    [Tooltip("OpenAI Chat Completions endpoint")]
    public string OpenAIChatUrl = "https://api.openai.com/v1/chat/completions";
    [Tooltip("Base URL WeatherAPI (tanpa trailing slash)")]
    public string WeatherApiBaseUrl = "https://api.weatherapi.com/v1";
    [Tooltip("Base URL ElevenLabs (tanpa trailing slash)")]
    public string ElevenLabsBaseUrl = "https://api.elevenlabs.io/v1/text-to-speech/";

    [Header("Optional Secure Relay (Recommended for WebGL in production)")]
    [Tooltip("Jika true, semua request akan diarahkan ke RelayBaseUrl.\nRelay server menambahkan API key di sisi server → aman untuk WebGL.")]
    public bool UseSecureRelay = false;
    [Tooltip("Base URL relay (tanpa trailing slash). Contoh: https://worker.example.com")]
    public string RelayBaseUrl = "";

    // =======================
    // Persona & UI cuaca
    // =======================

    [Header("Tenki-Chan Personality")]
    [TextArea(3,6)]
    public string TenkiPersona = "You are Tenki-Chan, a cheerful weather helper. Keep replies concise, friendly, and helpful. Use simple wording.";
    
    [Header("Refs")]
    public AudioSource audioSource;          // untuk memutar suara TTS
    public GameObject outputNormal;          // kontainer tampilan normal (teks)
    public GameObject outputWeather;         // kontainer tampilan khusus cuaca
    public Image weatherIconImage;
    public TMP_Text locationText;
    public TMP_Text weatherConditionText;
    public TMP_Text lattitudeText;
    public TMP_Text longtitudeText;
    public TMP_Text lastUpdateText;
    public TMP_Text temperatureText;
    public TMP_Text windSpeedText;
    public TMP_Text windDirectionText;
    public TMP_Text humidityText;
    public TMP_Text uvText;

    [Header("Events (optional)")]
    public UnityEvent<string> OnFinalReply; // terpanggil saat final text siap
    public UnityEvent onInteractableEnable; // UI kembali bisa diinteraksi (enable)
    public UnityEvent onInteractableDisable;// UI di-lock saat proses (disable)

    // =====================
    // State & concurrency
    // =====================

    public static MainProgram instance;      // singleton ringan (optional)

    private string previousUserText = "";    // cache percakapan sebelumnya (konteks ringan)
    public bool isAskingWeather = false;     // flag UI: sedang mode cuaca?

    // ====== ADD: Manajemen pipeline & cancelation ======
    private Coroutine _pipeline;             // coroutine pipeline aktif
    private int _epoch;                      // naik setiap request baru → invalidasi yang lama
    private readonly List<UnityWebRequest> _inFlight = new(); // semua request aktif

    /// <summary>
    /// Membatalkan semua request HTTP yang masih jalan.
    /// Penting saat user menekan Kirim lagi → hindari overlap/duplikat hasil.
    /// </summary>
    private void CancelInFlight()
    {
        foreach (var r in _inFlight)
        {
            if (r != null) r.Abort();        // Abort aman → UnityWebRequest akan fail dengan error sendiri
        }
        _inFlight.Clear();
    }

    // =========================================================
    // ====== INTERNAL DATA MODELS (LLM Plan & WeatherAPI) =====
    // =========================================================
    // Catatan:
    // - Semua [Serializable] agar bisa dipakai JsonUtility (Unity).
    // - Untuk OpenAI: kita masukkan "response_format": "json_object" agar isi choices.message.content
    //   benar-benar JSON dan bisa langsung di-FromJson ke TenkiPlan.
    // - Untuk WeatherAPI: model disesuaikan dengan field utama yang digunakan.

    [Serializable] public class ChatMessage { public string role; public string content; }

    [Serializable]
    public class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
        public ResponseFormat response_format;
        public float temperature = 0.2f; // rendah → lebih patuh schema
    }

    [Serializable] public class ResponseFormat { public string type = "json_object"; }

    [Serializable] public class OpenAIChoiceMessage { public string role; public string content; }

    [Serializable]
    public class OpenAIChoice
    {
        public OpenAIChoiceMessage message;
        public string finish_reason;
        public int index;
    }

    [Serializable]
    public class OpenAIResponse
    {
        public string id;
        public string @object;
        public long created;
        public OpenAIChoice[] choices;
    }

    /// <summary>
    /// Rencana kerja hasil LLM. Ini adalah "sumber kebenaran" pipeline:
    /// - intent: "weather" / "chitchat"
    /// - weather_api: preferensi endpoint/days/dt/units
    /// - location/time: konteks lokasi & waktu
    /// - reply: dipakai kalau chitchat
    /// </summary>
    [Serializable]
    public class TenkiPlan
    {
        public string intent;
        public WeatherPlan weather_api; // optional
        public PlanLocation location;   // optional
        public PlanTime time;           // optional
        public string reply;            // untuk chitchat
    }

    [Serializable]
    public class WeatherPlan
    {
        public string endpoint; // "current" | "forecast"
        public string q;        // WeatherAPI q; bisa "lat,lon" atau string lokasi
        public int days;        // forecast length (1..7)
        public string dt;       // YYYY-MM-DD (historical/forecast single day)
        public string units;    // "metric"|"imperial" (hanya hint untuk formatting UI)
    }

    [Serializable]
    public class PlanLocation
    {
        public string query;  // contoh: "Kecamatan Dukun, Indonesia"
        public double lat;
        public double lon;
        public string country;
        public string admin;  // provinsi/kab/kota
    }

    [Serializable]
    public class PlanTime
    {
        public string type;     // "now" | "date" | "relative"
        public string date;     // YYYY-MM-DD
        public string time;     // HH:mm (opsional)
        public string timezone; // contoh: "Asia/Tokyo"
    }

    // ==== ElevenLabs (TTS) payload minimal ====
    [Serializable]
    public class ElevenLabsVoice
    {
        public string text; 
        public string language_code; 
        // public string model_id;        // bisa diaktifkan kalau mau
        // public ElevenLabsVoiceSetting voice_settings; 
    }
    
    [Serializable]
    public class ElevenLabsVoiceSetting 
    { 
        public float stability; 
        public bool use_speaker_boost; 
        public float similarity_boost; 
        public float speed; 
    }

    // =========================
    // ===== UNITY LIFECYCLE ===
    // =========================

    /// <summary>
    /// Setup singleton dan wiring tombol. Pastikan hanya ada 1 instance.
    /// </summary>
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);        // mencegah duplikasi
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);  // tetap hidup saat scene change

        if (SendButton != null)
        {
            SendButton.onClick.RemoveAllListeners();
            SendButton.onClick.AddListener(OnSendClicked);
        }
    }
    
    /// <summary>
    /// Init Unity Services + ambil API keys lewat Cloud Code.
    /// NB: Aman buat WebGL karena keys datang dari server, bukan disimpan di build.
    /// </summary>
    private async void Start()
    {
        // Inisialisasi Unity Services
        await UnityServices.InitializeAsync();

        // Login anonymous (cukup untuk panggilan Cloud Code)
        await AuthenticationService.Instance.SignInAnonymouslyAsync();

        try
        {
            // Panggil module Cloud Code yang dibuat via GeneratedBindings
            var module = new MyModuleBindings(CloudCodeService.Instance);

            // Ambil kunci dari server (hindari hardcode di klien)
            OpenAIApiKey = await module.OpenAI();
            WeatherApiKey = await module.WeaterApi();
            ElevenLabsApiKey = await module.ElevenLabsApi();
        }
        catch (CloudCodeException exception)
        {
            Debug.LogException(exception); // log error detail
        }
    }

    // ======================================
    // ===== INPUT HANDLER (Tombol Kirim) ===
    // ======================================

    /// <summary>
    /// Handler klik tombol Kirim.
    /// Validasi keys (jika tidak pakai relay), ambil teks user, start pipeline.
    /// </summary>
    public void OnSendClicked()
    {
        statusText.text = "Sedang Memulai";

        // Kalau tidak pakai relay, kunci wajib ada di klien.
        if (string.IsNullOrWhiteSpace(OpenAIApiKey) && !UseSecureRelay)
        {
            SafeOutput("⚠️ Error with LLM API key. Please contact the developer.");
            return;
        }
        if (string.IsNullOrWhiteSpace(WeatherApiKey) && !UseSecureRelay)
        {
            SafeOutput("⚠️ Error with WeatherAPI key. Please contact the developer.");
            return;
        }

        // Ambil teks yang diketik user
        var text = InputField != null ? InputField.text.Trim() : "";
        if (string.IsNullOrEmpty(text))
        {
            SafeOutput("Ketik sesuatu untuk Tenki-Chan dahulu ☺️");
            return;
        }

        // ==== Mulai pipeline baru (cancel yang lama) ====
        _epoch++;                           // invalidate semua step lama
        if (_pipeline != null) StopCoroutine(_pipeline);
        CancelInFlight();                   // batalkan semua request HTTP lama
        audioSource.Stop();                 // hentikan audio TTS yang mungkin masih jalan

        // Jalankan pipeline utama (lihat di bawah)
        _pipeline = StartCoroutine(ProcessUserMessage(text, _epoch));
    }
    
    // =================================
    // ====== PIPELINE UTAMA (CORE) ====
    // =================================
    // Step by step:
    // 1) Lock UI → status "Memahami Kalimatmu"
    // 2) Minta LLM menyusun TenkiPlan (intent + parameter cuaca)
    // 3) Jika intent = chitchat → output reply + TTS, selesai
    // 4) Jika weather:
    //      a) Derive q (pakai lat,lon bila ada; kalau tidak, pakai query & resolve via search)
    //      b) Tentukan endpoint: current vs forecast (lihat time/type)
    //      c) Fetch cuaca (current/forecast)
    //      d) Minta LLM format narasi (ResultToSpeekableText)
    //      e) Render UI cuaca & jalankan TTS
    // 5) Set UI kembali interactable di akhir / on finally

    /// <summary>
    /// Proses lengkap 1 permintaan user. Semua langkah dijaga dengan "epoch" agar tidak balapan.
    /// </summary>
    public IEnumerator ProcessUserMessage(string userText, int epoch)
    {
        audioSource.Stop();                 // pastikan tidak ada audio lama
        SetInteractable(false);             // lock input agar tidak spam
        statusText.text = "Memahami Kalimatmu";

        try
        {
            TenkiPlan plan = null;

            // 1) Minta rencana dari LLM (strict JSON → TenkiPlan)
            yield return StartCoroutine(GetTenkiPlanFromLLM(
                userText,
                p => plan = p,
                err => { SafeOutput("Maaf, terdapat kesalahan sistem. (" + err + ")"); },
                epoch
            ));

            // Jika pipeline sudah kedaluwarsa (user kirim lagi), keluar diam-diam
            if (epoch != _epoch) yield break;
            if (plan == null) yield break;

            if (VerboseLogging) Debug.Log("[Tenki] Plan JSON: " + JsonUtility.ToJson(plan));

            // 2) Kalau hanya chit-chat → tampilkan & TTS
            if (string.Equals(plan.intent, "chitchat", StringComparison.OrdinalIgnoreCase))
            {
                var reply = string.IsNullOrWhiteSpace(plan.reply) ? "Ayo ngobrol! ☺️" : plan.reply.Trim();
                SafeOutput(reply);
                OnFinalReply?.Invoke(reply);
                isAskingWeather = false;
                previousUserText = reply;

                statusText.text="Mengubah Teks ke Suara";
                StartCoroutine(TextToSpeechStart(reply, epoch)); // fire & forget (tetap guard epoch internal)
                yield break;
            }

            // 3) Mode cuaca
            isAskingWeather = true;
            statusText.text="Membenarkan Format Cuaca";

            // Derive q untuk WeatherAPI (prioritas: lat,lon → location.query → userText)
            string q = DeriveWeatherQ(plan, userText);
            if (string.IsNullOrWhiteSpace(q)) q = userText;

            // Resolve string lokasi ke lat,lon via /search bila perlu (lebih presisi untuk WeatherAPI)
            string resolvedQ = q;
            if (!LooksLikeLatLon(q))
            {
                yield return StartCoroutine(ResolveWithWeatherSearch(
                    q,
                    rq => resolvedQ = rq,
                    _ => { /* diamkan error; fallback pakai q original */ },
                    epoch
                ));
                if (epoch != _epoch) yield break;
            }

            // Tentukan apakah pakai forecast atau current
            bool useForecast = ShouldUseForecast(plan);
            int days = Mathf.Clamp(GetForecastDays(plan), 1, 7);
            string dt = GetPlanDate(plan);

            // 4) Fetch data cuaca
            WeatherResult result = null;
            statusText.text="Mencari Info Cuaca Terkini";

            if (useForecast || !string.IsNullOrEmpty(dt))
            {
                yield return StartCoroutine(FetchForecast(
                    resolvedQ, days, dt,
                    r => result = r,
                    err => { SafeOutput("Weather lookup failed: " + err); },
                    epoch
                ));
            }
            else
            {
                yield return StartCoroutine(FetchCurrent(
                    resolvedQ,
                    r => result = r,
                    err => { SafeOutput("Weather lookup failed: " + err); },
                    epoch
                ));
            }
            if (epoch != _epoch) yield break;
            if (result == null) yield break;

            // 5) Minta LLM membentuk narasi yang "speakable"
            string final = null;
            statusText.text = "Mengubah Info Cuaca ke Teks";
            yield return StartCoroutine(ResultToSpeekableText(
                result,
                f => final = f,
                err => { SafeOutput("Maaf, terdapat kesalahan sistem. (" + err + ")"); },
                epoch
            ));
            if (epoch != _epoch) yield break;

            // 6) Render UI khusus cuaca + trigger event + clear input + TTS
            OutputWeater(plan, result);          // NOTE: nama method "OutputWeater" memang typo "Weater"
            OnFinalReply?.Invoke(final);
            previousUserText = final;
            InputField.text = string.Empty;

            statusText.text = "Mengubah Teks ke Suara";
            StartCoroutine(TextToSpeechStart(final, epoch));
        }
        finally
        {
            // Catatan: UI di-enable kembali saat TTS selesai (lihat TextToSpeechStart → SetInteractable(true))
            // Kalau mau enable di sini juga, pastikan tidak ganggu UX saat TTS.
            // if (epoch == _epoch) SetInteractable(true);
        }
    }
    
    /// <summary>
    /// API publik untuk memulai chat dari luar (mis. button lain / auto-prompt).
    /// Efeknya sama seperti user mengetik & menekan kirim.
    /// </summary>
    public void StartChatFromExternal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _epoch++;                                   // invalidasi pipeline lama
        if (_pipeline != null) StopCoroutine(_pipeline);
        CancelInFlight();                           // batalkan semua request lama
        audioSource.Stop();

        _pipeline = StartCoroutine(ProcessUserMessage(text, _epoch));
    }

    // ================================================================
    // MainProgram.cs (Tenki-Chan)
    // Bagian ini membahas:
    // - GetTenkiPlanFromLLM (minta "plan" ke LLM dengan output JSON strict)
    // - Prompt builder (BuildSystemPrompt / BuildSystemPromptForWeather)
    // - WeatherAPI models + fetchers (current / forecast / search resolve)
    // - Helpers (UI lock/unlock, output ke UI cuaca, parsing logika waktu)
    // - Formatter narasi (ResultToSpeekableText → panggil LLM lagi untuk script)
    // - ElevenLabs TTS (TextToSpeechStart) & SubmitFromButton()
    // ================================================================
    
    /* ----------------------------------------------------------------
     * ========== 1) LLM (OpenAI) : Minta "Plan" berbentuk JSON ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Meminta rencana (TenkiPlan) dari LLM:
    /// - Susun system prompt + user prompt
    /// - Paksa response_format = json_object agar content berisi JSON valid
    /// - Kirim ke OpenAI (langsung atau via relay)
    /// - Parse JSON → TenkiPlan
    /// - Gunakan "epoch guard" biar coroutine yang kedaluwarsa berhenti
    /// </summary>
    private IEnumerator GetTenkiPlanFromLLM(
        string userText,
        Action<TenkiPlan> onSuccess,
        Action<string> onError,
        int epoch)
    {
        // Bangun system prompt yang menjelaskan schema JSON final yang kita mau
        var sys = BuildSystemPrompt();
    
        // Isi user prompt: masukkan teks user + previousUserText sebagai konteks ringan
        var user = $"User said: {userText}. \nPrevious user's prompt (to add more context): {previousUserText}\n\nReturn ONLY JSON matching the schema. No markdown, no backticks.";
    
        // Payload request ke Chat Completions
        var reqObj = new ChatRequest
        {
            model = OpenAIModel,
            messages = new[]
            {
                new ChatMessage{ role="system", content = sys },
                new ChatMessage{ role="user", content = user }
            },
            response_format = new ResponseFormat { type = "json_object" },
            temperature = 0.2f        // suhu rendah supaya patuh schema & tidak halu
        };
    
        // Serialisasi ke JSON string
        var json = JsonUtility.ToJson(reqObj);
    
        // Pilih endpoint: relay (aman untuk WebGL) vs direct endpoint
        var url = UseSecureRelay ? (RelayBaseUrl.TrimEnd('/') + "/openai/chat") : OpenAIChatUrl;
    
        using (var req = new UnityWebRequest(url, "POST"))
        {
            // Pasang body dan header dasar
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
    
            // Jika tidak pakai relay, kirim Authorization di klien (kurang aman untuk WebGL)
            if (!UseSecureRelay)
                req.SetRequestHeader("Authorization", "Bearer " + OpenAIApiKey);
    
            if (VerboseLogging) Debug.Log("[Tenki] OpenAI request: " + json);
    
            req.timeout = 30;     // timeout agar UI tidak menggantung
            _inFlight.Add(req);   // tracking request aktif (bisa di-Abort saat cancel)
    
            // Kirim request (yield menunggu selesai)
            yield return req.SendWebRequest();
    
            _inFlight.Remove(req);          // lepas dari tracking
            if (epoch != _epoch) yield break; // kalau sudah kedaluwarsa, stop
    
            // HTTP error?
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
    
            statusText.text = "Mengubah Respons LLM";
    
            // Baca teks mentah (OpenAIResponse)
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] OpenAI raw response: " + text);
    
            // Parse respons ke OpenAIResponse (bukan langsung TenkiPlan)
            OpenAIResponse resp;
            try
            {
                resp = JsonUtility.FromJson<OpenAIResponse>(text);
            }
            catch (Exception ex)
            {
                onError?.Invoke("Invalid OpenAI response JSON: " + ex.Message);
                yield break;
            }
    
            // Validasi minimal
            if (resp == null || resp.choices == null || resp.choices.Length == 0 || resp.choices[0].message == null)
            {
                onError?.Invoke("OpenAI returned no choices");
                yield break;
            }
    
            // Ambil content (harusnya JSON plan)
            string content = resp.choices[0].message.content;
    
            // Parse content → TenkiPlan
            TenkiPlan plan = null;
            try
            {
                plan = JsonUtility.FromJson<TenkiPlan>(content);
            }
            catch
            {
                // Kadang ada whitespace/teks nyeleneh → coba Trim & parse ulang
                try
                {
                    content = content.Trim();
                    plan = JsonUtility.FromJson<TenkiPlan>(content);
                }
                catch (Exception ex2)
                {
                    onError?.Invoke("Failed to parse plan JSON: " + ex2.Message + " | content: " + content);
                    yield break;
                }
            }
    
            if (plan == null || string.IsNullOrEmpty(plan.intent))
            {
                onError?.Invoke("Plan missing intent.");
                yield break;
            }
    
            onSuccess?.Invoke(plan);
        }
    }
    
    /// <summary>
    /// System prompt untuk meminta output JSON strict sesuai schema TenkiPlan.
    /// Poin penting:
    /// - Hanya boleh output 1 object JSON (no code fences, no extra text)
    /// - Ada "Rules" untuk mapping intent/time/location ke parameter WeatherAPI
    /// - "reply" untuk chitchat harus Bahasa Indonesia
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine(TenkiPersona);
        sb.AppendLine();
        sb.AppendLine("You ONLY output a single JSON object (no code fences, no extra text).");
        sb.AppendLine("Schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"intent\": \"weather\" | \"chitchat\",");
        sb.AppendLine("  \"reply\": string (optional; used when intent is chitchat),");
        sb.AppendLine("  \"location\": {");
        sb.AppendLine("     \"query\": string (freeform, e.g., \"Kecamatan Dukun, Indonesia\"),");
        sb.AppendLine("     \"lat\": number,");
        sb.AppendLine("     \"lon\": number,");
        sb.AppendLine("     \"country\": string,");
        sb.AppendLine("     \"admin\": string");
        sb.AppendLine("  },");
        sb.AppendLine("  \"time\": {");
        sb.AppendLine("     \"type\": \"now\" | \"date\" | \"relative\",");
        sb.AppendLine("     \"date\": \"YYYY-MM-DD\",");
        sb.AppendLine("     \"time\": \"HH:mm\",");
        sb.AppendLine("     \"timezone\": string");
        sb.AppendLine("  },");
        sb.AppendLine("  \"weather_api\": {");
        sb.AppendLine("     \"endpoint\": \"current\" | \"forecast\",");
        sb.AppendLine("     \"q\": string (WeatherAPI q; either \"lat,lon\" like \"-7.712,112.027\" or a place string),");
        sb.AppendLine("     \"days\": number (1-7),");
        sb.AppendLine("     \"dt\": \"YYYY-MM-DD\",");
        sb.AppendLine("     \"units\": \"metric\" | \"imperial\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- If the user wants weather, set intent=weather.");
        sb.AppendLine("- Try to include location.lat/lon when possible; if unsure, populate location.query with the best place string.");
        sb.AppendLine("- If the user asks for today's/now, time.type=now and weather_api.endpoint=current.");
        sb.AppendLine("- If the user asks about a specific date or future days, prefer endpoint=forecast and include weather_api.days or weather_api.dt.");
        sb.AppendLine("- For Indonesia places like \"Kecamatan Dukun\", output either lat/lon or a precise query string WeatherAPI can resolve.");
        sb.AppendLine("- Keep values minimal; omit properties you’re not confident about rather than guessing wildly.");
        sb.AppendLine("- Return \"reply\" chitchat intent only using Bahasa Indonesia, other language are not allowed.");
        return sb.ToString();
    }
    
    /// <summary>
    /// System prompt kedua khusus untuk memformat data cuaca (JSON WeatherResult)
    /// menjadi naskah yang “enak dibacakan” (80–160 kata, BI, tanpa JSON/markdown).
    /// </summary>
    private string BuildSystemPromptForWeather()
    {
        var sb = new StringBuilder();
        sb.AppendLine(TenkiPersona);
        sb.AppendLine();
        sb.AppendLine("CONTRACT");
        sb.AppendLine("- Input: The user will send a single JSON string containing the WeatherAPI.com response.");
        sb.AppendLine("- Output: Return ONLY the speaking script as plain text. No JSON. No markdown. No preface or epilogue. No extra whitespace before/after.");
        sb.AppendLine();
        sb.AppendLine("TONE & LENGTH");
        sb.AppendLine("- Clear, concise, natural newscaster cadence. Friendly but not cheesy.");
        sb.AppendLine("- 80–160 words. If persona is provided, match it briefly (voice and pacing) without breaking the style rules.");
        sb.AppendLine();
        sb.AppendLine("CONTENT RULES (use only fields present; never invent)");
        sb.AppendLine("- Location/time: Use location.name, region, country, and location.tz_id; prefer location.localtime for timing references.");
        sb.AppendLine("- Units: Prefer metric fields (temp_c, wind_kph, precip_mm, uv). If only imperial exists, use it.");
        sb.AppendLine("- Rounding: Temperatures & wind → whole numbers; precipitation → 1 decimal if <10, else whole number.");
        sb.AppendLine();
        sb.AppendLine("FORMAT ENFORCEMENT");
        sb.AppendLine("- Output must be ONLY the final script text (no JSON, no markdown, no quotes).");
        sb.AppendLine("- Use only Bahasa Indonesia, other languages are not allowed.");
        return sb.ToString();
    }
    
    /* ----------------------------------------------------------------
     * ========== 2) WeatherAPI Models & Fetchers ==========
     * ----------------------------------------------------------------
     * Catatan model:
     * - Dipilih field yang sering dipakai di UI & formatter.
     * - Beberapa field (seperti Day.temp) kita simpan ganda agar gampang akses.
     * - Bila ingin memperkaya UI (UV index detail, vis, pressure), tinggal tambah.
     */
    
    // ------- Models utama (lihat file aslimu untuk deklarasi lengkapnya) -------
    // [Serializable] public class WeatherLocation { ... }
    // [Serializable] public class Condition { ... }
    // [Serializable] public class Current { ... }
    // [Serializable] public class ForecastDayTemp { ... }
    // [Serializable] public class Day { ... }
    // [Serializable] public class Astro { ... }
    // [Serializable] public class Hour { ... }
    // [Serializable] public class ForecastDay { ... }
    // [Serializable] public class Forecast { ... }
    // [Serializable] public class WeatherApiCurrentResponse { ... }
    // [Serializable] public class WeatherApiForecastResponse { ... }
    //
    // public class WeatherResult { public WeatherLocation location; public Current current; public Forecast forecast; public bool isForecast; }
    
    /// <summary>
    /// Ambil cuaca saat ini (current.json). Jika UseSecureRelay = true,
    /// kita memanggil endpoint relay (tanpa key di klien).
    /// </summary>
    private IEnumerator FetchCurrent(
        string q,
        Action<WeatherResult> onSuccess,
        Action<string> onError,
        int epoch)
    {
        // Susun URL
        string url = UseSecureRelay
            ? (RelayBaseUrl.TrimEnd('/') + "/weatherapi/current?q=" + UnityWebRequest.EscapeURL(q))
            : $"{WeatherApiBaseUrl}/current.json?key={WeatherApiKey}&q={UnityWebRequest.EscapeURL(q)}&aqi=no";
    
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 30;
            _inFlight.Add(req);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            if (epoch != _epoch) yield break;
    
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
    
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] Weather current: " + text);
    
            // Parse JSON → model ringan
            WeatherApiCurrentResponse data = null;
            try { data = JsonUtility.FromJson<WeatherApiCurrentResponse>(text); }
            catch (Exception ex)
            {
                onError?.Invoke("Parse error: " + ex.Message);
                yield break;
            }
    
            if (data == null || data.location == null || data.current == null)
            {
                onError?.Invoke("WeatherAPI current: missing fields");
                yield break;
            }
    
            onSuccess?.Invoke(new WeatherResult
            {
                location = data.location,
                current = data.current,
                forecast = null,
                isForecast = false
            });
        }
    }
    
    /// <summary>
    /// Ambil forecast (forecast.json). Bisa pakai days (1–7) dan/atau dt (YYYY-MM-DD).
    /// Tips: untuk "besok/lusa" gunakan days; untuk tanggal spesifik gunakan dt.
    /// </summary>
    private IEnumerator FetchForecast(
        string q,
        int days,
        string dt,
        Action<WeatherResult> onSuccess,
        Action<string> onError,
        int epoch)
    {
        // Susun URL dan query string
        string baseUrl = UseSecureRelay ? (RelayBaseUrl.TrimEnd('/') + "/weatherapi/forecast") : (WeatherApiBaseUrl + "/forecast.json");
        var builder = new StringBuilder(baseUrl);
        if (UseSecureRelay)
        {
            builder.Append("?q=").Append(UnityWebRequest.EscapeURL(q));
            if (days > 0) builder.Append("&days=").Append(days);
            if (!string.IsNullOrEmpty(dt)) builder.Append("&dt=").Append(dt);
        }
        else
        {
            builder.Append("?key=").Append(WeatherApiKey)
                   .Append("&q=").Append(UnityWebRequest.EscapeURL(q));
            if (days > 0) builder.Append("&days=").Append(days);
            if (!string.IsNullOrEmpty(dt)) builder.Append("&dt=").Append(dt);
            builder.Append("&aqi=no&alerts=no");
        }
    
        using (var req = UnityWebRequest.Get(builder.ToString()))
        {
            req.timeout = 30;
            _inFlight.Add(req);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            if (epoch != _epoch) yield break;
    
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
    
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] Weather forecast: " + text);
    
            WeatherApiForecastResponse data = null;
            try { data = JsonUtility.FromJson<WeatherApiForecastResponse>(text); }
            catch (Exception ex)
            {
                onError?.Invoke("Parse error: " + ex.Message);
                yield break;
            }
    
            if (data == null || data.location == null || data.forecast == null || data.forecast.forecastday == null)
            {
                onError?.Invoke("WeatherAPI forecast: missing fields");
                yield break;
            }
    
            onSuccess?.Invoke(new WeatherResult
            {
                location = data.location,
                current = data.current,    // kadang WeatherAPI juga menyertakan current dalam forecast
                forecast = data.forecast,
                isForecast = true
            });
        }
    }
    
    /// <summary>
    /// Resolve query string (mis. "Kecamatan Dukun") ke koordinat lat,lon
    /// via WeatherAPI /search.json. Kita ambil kandidat pertama untuk presisi.
    /// </summary>
    private IEnumerator ResolveWithWeatherSearch(
        string q,
        Action<string> onResolved,
        Action<string> onError,
        int epoch)
    {
        string url = UseSecureRelay
            ? (RelayBaseUrl.TrimEnd('/') + "/weatherapi/search?q=" + UnityWebRequest.EscapeURL(q))
            : $"{WeatherApiBaseUrl}/search.json?key={WeatherApiKey}&q={UnityWebRequest.EscapeURL(q)}";
    
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 30;
            _inFlight.Add(req);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            if (epoch != _epoch) yield break;
    
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
    
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] Weather search: " + text);
    
            try
            {
                // Trick: JsonUtility tidak bisa parse array top-level → bungkus dengan property dummy
                var wrapped = "{\"items\":" + text + "}";
                var items = JsonUtility.FromJson<SearchWrapper>(wrapped);
                if (items != null && items.items != null && items.items.Length > 0)
                {
                    var first = items.items[0];
                    // Format "lat,lon" → paling aman untuk konsistensi
                    var resolved = $"{first.lat.ToString(CultureInfo.InvariantCulture)},{first.lon.ToString(CultureInfo.InvariantCulture)}";
                    onResolved?.Invoke(resolved);
                    yield break;
                }
            }
            catch
            {
                // Abaikan error parse → pakai q original (fallback)
            }
    
            onResolved?.Invoke(q); // fallback ke query awal
        }
    }
    
    // Wrapper kecil untuk parse array /search.json
    [Serializable] private class SearchWrapper { public SearchItem[] items; }
    [Serializable] private class SearchItem
    {
        public string name;
        public string region;
        public string country;
        public double lat;
        public double lon;
        public string url;
    }
    
    /* ----------------------------------------------------------------
     * ========== 3) Helpers UI & Formatting ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Enable/disable interaksi UI (input & tombol).
    /// Juga toggle panel output sesuai mode (chitchat vs weather) dan invoke event.
    /// </summary>
    private void SetInteractable(bool enabled)
    {
        if (SendButton != null) SendButton.interactable = enabled;
        if (InputField != null) InputField.interactable = enabled;
    
        if (enabled)
        {
            if (outputNormal != null && outputWeather != null)
            {
                outputNormal.SetActive(!isAskingWeather);
                outputWeather.SetActive(isAskingWeather);
            }
            onInteractableEnable.Invoke();
        }
        else
        {
            onInteractableDisable.Invoke();
        }
    }
    
    /// <summary>
    /// Tulis pesan aman ke OutputText + Debug jika Verbose.
    /// </summary>
    private void SafeOutput(string msg)
    {
        if (OutputText != null) OutputText.text = msg;
        if (VerboseLogging) Debug.Log("[Tenki] " + msg);
    }
    
    /// <summary>
    /// Tampilkan hasil cuaca ke UI (panel weather).
    /// - Pilih units sesuai plan.weather_api.units (default metric)
    /// - Ambil sprite ikon dari WeatherIcons via condition code & is_day
    /// </summary>
    private void OutputWeater(TenkiPlan plan, WeatherResult result)
    {
        bool metric = true;
        if (plan?.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.units))
            metric = plan.weather_api.units.Equals("metric", StringComparison.OrdinalIgnoreCase);
    
        if (!result.isForecast)
        {
            // Set ikon (perhatikan dependency: WeatherIcons utility harus disiapkan di project)
            weatherIconImage.sprite = WeatherIcons.GetSprite(result.current.condition.code, result.current.is_day == 1);
    
            // Teks lokasi: kita ambil dari plan.location.query (jika tersedia)
            locationText.text = plan?.location?.query;
    
            weatherConditionText.text = result.current.condition.text;
            lattitudeText.text    = $"lat: {plan?.location?.lat}";
            longtitudeText.text   = $"lon: {plan?.location?.lon}";
            lastUpdateText.text   = $"update: {result.current.last_updated}";
    
            temperatureText.text  = metric
                ? $"temp: {result.current.temp_c:0.#}°C"
                : $"temp: {result.current.temp_f:0.#}°F";
    
            windSpeedText.text    = metric
                ? $"wind speed: {result.current.wind_kph:0.#} kph"
                : $"wind speed: {result.current.wind_mph:0.#} mph";
    
            windDirectionText.text= $"wind direction: {result.current.wind_dir} ({result.current.wind_degree})°";
            humidityText.text     = $"humidity: {result.current.humidity}%";
            uvText.text           = $"uv: {result.current.uv}";
        }
    
        // TODO (opsional): render panel forecast (loop forecastday) kalau result.isForecast = true
        // Misal tampilkan hari ke-1..N, maxtemp/min temp, chance_of_rain, dsb.
    }
    
    /// <summary>
    /// Cek apakah string terlihat seperti "lat,lon".
    /// Dipakai untuk memutuskan perlu resolve /search atau tidak.
    /// </summary>
    private static bool LooksLikeLatLon(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return false;
        var parts = q.Split(',');
        if (parts.Length != 2) return false;
    
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
    
    /// <summary>
    /// Tentukan nilai q untuk WeatherAPI dari plan & userText:
    /// - Prioritas: plan.weather_api.q → plan.location.lat,lon → plan.location.query → userText
    /// </summary>
    private string DeriveWeatherQ(TenkiPlan plan, string userText)
    {
        if (plan?.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.q))
            return plan.weather_api.q.Trim();
    
        if (plan?.location != null)
        {
            // Kalau ada lat/lon, selalu prefer
            if (Math.Abs(plan.location.lat) > 0.00001 || Math.Abs(plan.location.lon) > 0.00001)
            {
                return plan.location.lat.ToString(CultureInfo.InvariantCulture) + "," +
                       plan.location.lon.ToString(CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(plan.location.query))
                return plan.location.query.Trim();
        }
    
        // fallback: biarkan WeatherAPI search mencoba menebak dari kalimat user
        return userText;
    }
    
    /// <summary>
    /// Putuskan pakai endpoint forecast atau current berdasarkan plan.time/endpoint:
    /// - Jika plan.weather_api.endpoint = "forecast" → true
    /// - Jika time.type = "relative" → forecast
    /// - Jika time.type = "date": bila tanggal di masa depan → forecast; hari ini → current
    /// - Default: current
    /// </summary>
    private bool ShouldUseForecast(TenkiPlan plan)
    {
        if (plan?.weather_api != null && string.Equals(plan.weather_api.endpoint, "forecast", StringComparison.OrdinalIgnoreCase))
            return true;
    
        if (plan?.time != null)
        {
            if (string.Equals(plan.time.type, "now", StringComparison.OrdinalIgnoreCase)) return false;
    
            if (string.Equals(plan.time.type, "date", StringComparison.OrdinalIgnoreCase))
            {
                var dt = GetPlanDate(plan);
                if (DateTime.TryParse(dt, out var d))
                {
                    var today = DateTime.UtcNow.Date;
                    if (d.Date > today) return true; // masa depan → forecast
                }
                return false; // hari ini/past → current
            }
    
            if (string.Equals(plan.time.type, "relative", StringComparison.OrdinalIgnoreCase))
                return true;
        }
    
        return false;
    }
    
    /// <summary>
    /// Tentukan jumlah hari forecast default:
    /// - Jika plan.weather_api.days > 0 → pakai
    /// - Jika time.type = relative → 3 (default ergonomis)
    /// - Jika ada tanggal spesifik (dt) → 1
    /// - Default → 1
    /// </summary>
    private int GetForecastDays(TenkiPlan plan)
    {
        if (plan?.weather_api != null && plan.weather_api.days > 0)
            return plan.weather_api.days;
    
        if (plan?.time != null && string.Equals(plan.time.type, "relative", StringComparison.OrdinalIgnoreCase))
            return 3;
    
        if (!string.IsNullOrEmpty(GetPlanDate(plan)))
            return 1;
    
        return 1;
    }
    
    /// <summary>
    /// Ambil tanggal dari plan (prioritas: weather_api.dt → time.date).
    /// Mengembalikan null jika tidak ada.
    /// </summary>
    private string GetPlanDate(TenkiPlan plan)
    {
        if (plan?.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.dt))
            return plan.weather_api.dt.Trim();
        if (plan?.time != null && !string.IsNullOrWhiteSpace(plan.time.date))
            return plan.time.date.Trim();
        return null;
    }
    
    /// <summary>
    /// Placeholder kalau suatu saat butuh format manual tanpa LLM.
    /// Saat ini tidak digunakan (kita pakai ResultToSpeekableText).
    /// </summary>
    private string FormatTenkiReply(TenkiPlan plan, WeatherResult res)
    {
        return "";
    }
    
    /* ----------------------------------------------------------------
     * ========== 4) Formatter Narasi via LLM ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Kirim JSON WeatherResult ke LLM untuk diformat jadi script “siap dibacakan”
    /// dalam Bahasa Indonesia (tanpa JSON/markdown).
    /// Note: response_format = "text" karena kita mau plain text.
    /// </summary>
    private IEnumerator ResultToSpeekableText(
        WeatherResult res,
        Action<string> onSuccess,
        Action<string> onError,
        int epoch)
    {
        var sys = BuildSystemPromptForWeather();
    
        // User message berisi JSON dari WeatherResult (serialize sekali jalan)
        var user = $"User said: \n{JsonUtility.ToJson(res)}\n\n\nReturn ONLY speakable speaking script as plain text. No markdown, no backticks.";
    
        var reqObj = new ChatRequest
        {
            model = OpenAIModel,
            messages = new[]
            {
                new ChatMessage{ role="system", content = sys },
                new ChatMessage{ role="user", content = user }
            },
            response_format = new ResponseFormat { type = "text" },
            temperature = 0.2f
        };
    
        var json = JsonUtility.ToJson(reqObj);
        var url = UseSecureRelay ? (RelayBaseUrl.TrimEnd('/') + "/openai/chat") : OpenAIChatUrl;
    
        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
    
            if (!UseSecureRelay)
                req.SetRequestHeader("Authorization", "Bearer " + OpenAIApiKey);
    
            if (VerboseLogging) Debug.Log("[Tenki] OpenAI request: " + json);
    
            req.timeout = 30;
            _inFlight.Add(req);
            yield return req.SendWebRequest();
            _inFlight.Remove(req);
            if (epoch != _epoch) yield break;
    
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.Log("Ga Sukses bang, error code : " + req.responseCode);
                onError?.Invoke(req.error);
                yield break;
            }
    
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] OpenAI raw response: " + text);
    
            OpenAIResponse resp;
            try
            {
                resp = JsonUtility.FromJson<OpenAIResponse>(text);
            }
            catch (Exception ex)
            {
                onError?.Invoke("Invalid OpenAI response JSON: " + ex.Message);
                yield break;
            }
    
            if (resp == null || resp.choices == null || resp.choices.Length == 0 || resp.choices[0].message == null)
            {
                onError?.Invoke("OpenAI returned no choices");
                yield break;
            }
    
            string content = resp.choices[0].message.content; // ini sudah plain text
            onSuccess?.Invoke(content);
        }
    }
    
    /* ----------------------------------------------------------------
     * ========== 5) Utilitas kecil ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Gabungkan name, region, country jadi 1 kalimat lokasi.
    /// Dipakai kalau ingin menampilkan nama lokasi yang manusiawi.
    /// </summary>
    private string ComposePlaceName(WeatherLocation loc)
    {
        if (loc == null) return "your location";
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(loc.name)) items.Add(loc.name);
        if (!string.IsNullOrWhiteSpace(loc.region)) items.Add(loc.region);
        if (!string.IsNullOrWhiteSpace(loc.country)) items.Add(loc.country);
        return string.Join(", ", items);
    }
    
    /// <summary>
    /// Bangun string debug HTTP (kode, body, Retry-After).
    /// Berguna saat observasi error rate limit, dsb.
    /// </summary>
    private string BuildHttpDebug(UnityWebRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP ").Append(req.responseCode);
    
        var body = req.downloadHandler != null ? req.downloadHandler.text : null;
        if (!string.IsNullOrEmpty(body)) sb.Append(" | body: ").Append(body);
    
        var retryAfter = req.GetResponseHeader("Retry-After");
        if (!string.IsNullOrEmpty(retryAfter)) sb.Append(" | retry-after: ").Append(retryAfter);
    
        return sb.ToString();
    }
    
    /* ----------------------------------------------------------------
     * ========== 6) ElevenLabs TTS ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Mengirim teks ke ElevenLabs untuk diubah jadi audio (MP3),
    /// menyimpan sementara ke file, lalu memainkannya via AudioSource.
    /// Catatan:
    /// - UnityWebRequestMultimedia.GetAudioClip() tidak bisa load MP3 dari memory,
    ///   jadi kita tulis ke file sementara (persistentDataPath) dulu.
    /// - Gunakan language_code "id" untuk Bahasa Indonesia.
    /// - Pastikan ElevenLabsVoiceId & ApiKey terisi (atau via relay).
    /// </summary>
    private IEnumerator TextToSpeechStart(string text, int epoch)
    {
        // Bentuk payload minimal
        var reqObj = new ElevenLabsVoice
        {
            text = text,
            language_code = "id",
            // model_id / voice_settings bisa diaktifkan kalau mau kustomisasi suara/kecepatan
        };
        var json = JsonUtility.ToJson(reqObj);
    
        // Convert ke bytes
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
    
        // Create POST request
        UnityWebRequest request = new UnityWebRequest(ElevenLabsBaseUrl + ElevenLabsVoiceId, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
    
        // Auth header (kalau tidak pakai relay)
        request.SetRequestHeader("xi-api-key", ElevenLabsApiKey);
    
        request.timeout = 30;
    
        _inFlight.Add(request);
        yield return request.SendWebRequest();
        _inFlight.Remove(request);
        if (epoch != _epoch) yield break;
    
        if (request.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("Audio data received!");
    
            // Ambil bytes MP3 dari response
            byte[] audioData = request.downloadHandler.data;
    
            // Simpan ke file sementara
            string tempPath = Application.persistentDataPath + "/tts.mp3";
            System.IO.File.WriteAllBytes(tempPath, audioData);
    
            // Load kembali sebagai AudioClip (AudioType.MPEG)
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + tempPath, AudioType.MPEG))
            {
                www.timeout = 30;
                _inFlight.Add(www);
                yield return www.SendWebRequest();
                _inFlight.Remove(www);
                if (epoch != _epoch) yield break;
    
                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    audioSource.Stop();
                    audioSource.clip = clip;
                    audioSource.Play();
                }
                else
                {
                    Debug.LogError("Failed to load audio: " + www.error);
                }
            }
        }
        else
        {
            Debug.LogError("Error: " + request.error);
        }
    
        // Selesai TTS → UI kembali bisa dipakai
        SetInteractable(true);
        statusText.text = "Finishing";
    }
    
    /* ----------------------------------------------------------------
     * ========== 7) Public API kecil untuk tombol lain ==========
     * ---------------------------------------------------------------- */
    
    /// <summary>
    /// Kalau kamu tidak assign SendButton di Inspector, hubungkan method ini ke
    /// Button.onClick secara manual (mis. dari UI lain).
    /// </summary>
    public void SubmitFromButton()
    {
        OnSendClicked();
    }
}
