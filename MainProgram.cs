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
/// TenkiChatController
/// - Sends user text to OpenAI Chat Completions with a strict JSON schema
/// - Determines intent: "weather" or "chitchat"
/// - If "weather": resolves a WeatherAPI.com query and fetches weather (current or forecast)
/// - Displays a friendly Tenki-Chan reply
/// - Designed for Unity 6000.x and WebGL compatibility
/// </summary>
public class MainProgram : MonoBehaviour
{
    [Header("UI (assign in Inspector)")]
    public TMP_InputField InputField;
    public TextMeshProUGUI OutputText;
    public Button SendButton;
    public TMP_Text statusText;

    [Header("Keys (paste here)")]
    [Tooltip("OpenAI API key (sk-...)")]
    public string OpenAIApiKey = "";
    [Tooltip("WeatherAPI.com key")]
    public string WeatherApiKey = "";
    [Tooltip("ElevenLabs API key")]
    public string ElevenLabsApiKey = "";
    [Tooltip("EvelenLabs voice ID")]
    public string ElevenLabsVoiceId = "B8gJV1IhpuegLxdpXFOE";

    [Header("Model & Behavior")]
    [Tooltip("OpenAI model id. gpt-4o-mini is fast and good for JSON. You can change if needed.")]
    public string OpenAIModel = "gpt-4o-mini";
    [Tooltip("Set true to produce more verbose debugging to the console.")]
    public bool VerboseLogging = false;

    [Header("Networking (Direct)")]
    [Tooltip("OpenAI base URL (Chat Completions)")]
    public string OpenAIChatUrl = "https://api.openai.com/v1/chat/completions";
    [Tooltip("WeatherAPI base URL (no trailing slash)")]
    public string WeatherApiBaseUrl = "https://api.weatherapi.com/v1";
    [Tooltip("ElevenLabs base URL (no trailing slash)")]
    public string ElevenLabsBaseUrl = "https://api.elevenlabs.io/v1/text-to-speech/";

    [Header("Optional Secure Relay (Recommended for WebGL in production)")]
    [Tooltip("If true, requests will be sent to RelayBaseUrl instead of the official endpoints.\nImplement a simple relay that adds the API keys server-side.")]
    public bool UseSecureRelay = false;
    [Tooltip("Your relay base URL (no trailing slash). Example: https://your-worker.example.com")]
    public string RelayBaseUrl = "";

    [Header("Tenki-Chan Personality")]
    [TextArea(3,6)]
    public string TenkiPersona = "You are Tenki-Chan, a cheerful weather helper. Keep replies concise, friendly, and helpful. Use simple wording.";
    
    [Header("Refs")]
    public AudioSource audioSource;
    public GameObject outputNormal;
    public GameObject outputWeather;
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
    public UnityEvent<string> OnFinalReply; // emits the final display string
    public UnityEvent onInteractableEnable;
    public UnityEvent onInteractableDisable;

    public static TenkiChatController instance;
    
    private string previousUserText = "";
    public bool isAskingWeather = false;
    
    // ADD
    private Coroutine _pipeline;
    private int _epoch;
    private readonly List<UnityWebRequest> _inFlight = new();

    private void CancelInFlight()
    {
        // Abort any network request that might still be yielding
        foreach (var r in _inFlight)
        {
            if (r != null) r.Abort();
        }
        _inFlight.Clear();
    }


    // ====== Internal JSON data models ======

    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;
    }

    [Serializable]
    public class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
        public ResponseFormat response_format;
        public float temperature = 0.2f;
    }

    [Serializable]
    public class ResponseFormat
    {
        public string type = "json_object";
    }

    [Serializable]
    public class OpenAIChoiceMessage
    {
        public string role;
        public string content;
    }

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

    // Model → Our normalized plan
    [Serializable]
    public class TenkiPlan
    {
        public string intent; // "weather" or "chitchat"
        public WeatherPlan weather_api; // may be null
        public PlanLocation location;   // may be null
        public PlanTime time;           // may be null
        public string reply;            // used for chitchat
    }

    [Serializable]
    public class WeatherPlan
    {
        // endpoint: "current" | "forecast" (we'll pick based on time if not set)
        public string endpoint;
        // WeatherAPI q parameter. If not provided, we’ll derive it from location.
        public string q;
        // For forecast; default 1..3 days if future date unknown.
        public int days;
        // Optional date (YYYY-MM-DD) for historical/forecast single day
        public string dt;
        // Units hint: "metric"|"imperial" (WeatherAPI uses c/f toggles in response; we’ll format accordingly)
        public string units;
    }

    [Serializable]
    public class PlanLocation
    {
        public string query; // freeform, e.g., "Kecamatan Dukun, Indonesia"
        public double lat;
        public double lon;
        public string country;
        public string admin; // state/province/etc
    }

    [Serializable]
    public class PlanTime
    {
        public string type;   // "now" | "date" | "relative"
        public string date;   // YYYY-MM-DD
        public string time;   // HH:mm (optional)
        public string timezone; // e.g., "Asia/Tokyo"
    }

    [Serializable]
    public class ElevenLabsVoice
    {
        public string text; 
        // public string model_id; 
        public string language_code; 
        // public ElevenLabsVoiceSetting voice_settings; 
    }
    
    [Serializable] public class ElevenLabsVoiceSetting 
    { 
        public float stability; 
        public bool use_speaker_boost; 
        public float similarity_boost; 
        public float speed; 
    }

    // ====== Unity lifecycle ======

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        if (SendButton != null)
        {
            SendButton.onClick.RemoveAllListeners();
            SendButton.onClick.AddListener(OnSendClicked);
        }
    }
    
    private async void Start()
        {
            // Initialize the Unity Services Core SDK
            await UnityServices.InitializeAsync();
    
            // Authenticate by logging into an anonymous account
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    
            try
            {
                // Call the function within the module and provide the parameters we defined in there
                var module = new MyModuleBindings(CloudCodeService.Instance);
                // var result = await module.OpenAI("World");
                OpenAIApiKey = await module.OpenAI();
                WeatherApiKey = await module.WeaterApi();
                ElevenLabsApiKey = await module.ElevenLabsApi();
                // Debug.Log(result);
            }
            catch (CloudCodeException exception)
            {
                Debug.LogException(exception);
            }
        }


    public void OnSendClicked()
    {
        statusText.text = "Sedang Memulai";
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
        // REPLACE the last lines of OnSendClicked()
        var text = InputField != null ? InputField.text.Trim() : "";
        if (string.IsNullOrEmpty(text))
        {
            SafeOutput("Ketik sesuatu untuk Tenki-Chan dahulu ☺️");
            return;
        }
        _epoch++;                          // invalidate older work
        if (_pipeline != null) StopCoroutine(_pipeline);
        CancelInFlight();                  // abort network ops from previous run
        audioSource.Stop();

        _pipeline = StartCoroutine(ProcessUserMessage(text, _epoch));

    }
    

    // ====== Main pipeline ======

    // CHANGE SIGNATURE
    public IEnumerator ProcessUserMessage(string userText, int epoch)
    {
        audioSource.Stop();
        // Debug.Log("Checkpoint 2: " + userText);
        SetInteractable(false);
        statusText.text = "Memahami Kalimatmu";

        try
        {
            TenkiPlan plan = null;
            // PASS epoch into sub-steps
            yield return StartCoroutine(GetTenkiPlanFromLLM(userText, p => plan = p, err =>
            {
                SafeOutput("Maaf, terdapat kesalahan sistem. (" + err + ")");
            }, epoch));

            // CANCELED? exit quietly
            if (epoch != _epoch) yield break;

            if (plan == null) yield break;

            if (VerboseLogging) Debug.Log("[Tenki] Plan JSON: " + JsonUtility.ToJson(plan));

            if (string.Equals(plan.intent, "chitchat", StringComparison.OrdinalIgnoreCase))
            {
                var reply = string.IsNullOrWhiteSpace(plan.reply) ? "Ayo ngobrol! ☺️" : plan.reply.Trim();
                SafeOutput(reply);
                OnFinalReply?.Invoke(reply);
                isAskingWeather = false;
                previousUserText = reply;
                statusText.text="Mengubah Teks ke Suara";
                
                StartCoroutine(TextToSpeechStart(reply, epoch));
                yield break;
            }

            isAskingWeather = true;
            
            statusText.text="Membenarkan Format Cuaca";

            string q = DeriveWeatherQ(plan, userText);
            if (string.IsNullOrWhiteSpace(q)) q = userText;
            
            string resolvedQ = q;
            if (!LooksLikeLatLon(q))
            {
                yield return StartCoroutine(ResolveWithWeatherSearch(q, rq => resolvedQ = rq, _ => { }, epoch));
                if (epoch != _epoch) yield break;
            }

            bool useForecast = ShouldUseForecast(plan);
            int days = Mathf.Clamp(GetForecastDays(plan), 1, 7);
            string dt = GetPlanDate(plan);

            WeatherResult result = null;
            statusText.text="Mencari Info Cuaca Terkini";
            if (useForecast || !string.IsNullOrEmpty(dt))
            {
                yield return StartCoroutine(FetchForecast(resolvedQ, days, dt, r => result = r, err =>
                {
                    SafeOutput("Weather lookup failed: " + err);
                }, epoch));
            }
            else
            {
                yield return StartCoroutine(FetchCurrent(resolvedQ, r => result = r, err =>
                {
                    SafeOutput("Weather lookup failed: " + err);
                }, epoch));
            }
            if (epoch != _epoch) yield break;

            if (result == null) yield break;

            string final = null;
            statusText.text = "Mengubah Info Cuaca ke Teks";
            yield return StartCoroutine(ResultToSpeekableText(result, f => final = f, err =>
            {
                SafeOutput("Maaf, terdapat kesalahan sistem. (" + err + ")");
            }, epoch));
            if (epoch != _epoch) yield break;

            OutputWeater(plan, result);
            OnFinalReply?.Invoke(final);
            previousUserText = final;
            InputField.text = string.Empty;
            
            statusText.text = "Mengubah Teks ke Suara";
            StartCoroutine(TextToSpeechStart(final, epoch));
        }
        finally
        {
            // Safety net: if this is still the active request, ensure UI is enabled
            // if (epoch == _epoch) SetInteractable(true);
        }
    }
    
    // ADD inside TenkiChatController
    public void StartChatFromExternal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _epoch++;                                   // invalidate older runs
        if (_pipeline != null) StopCoroutine(_pipeline);
        CancelInFlight();                           // abort any in-flight requests
        audioSource.Stop();

        _pipeline = StartCoroutine(ProcessUserMessage(text, _epoch)); // pass epoch
    }


    // ====== LLM (OpenAI) ======

    private IEnumerator GetTenkiPlanFromLLM(string userText, Action<TenkiPlan> onSuccess, Action<string> onError, int epoch)
    {
        // Debug.Log("Checkpoint 4: " + userText);
        var sys = BuildSystemPrompt();
        var user = $"User said: {userText}. \nPrevious user's prompt (to add more context): {previousUserText}\n\nReturn ONLY JSON matching the schema. No markdown, no backticks.";

        var reqObj = new ChatRequest
        {
            model = OpenAIModel,
            messages = new[]
            {
                new ChatMessage{ role="system", content = sys },
                new ChatMessage{ role="user", content = user }
            },
            response_format = new ResponseFormat { type = "json_object" },
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
            {
                req.SetRequestHeader("Authorization", "Bearer " + OpenAIApiKey);
            }

            if (VerboseLogging) Debug.Log("[Tenki] OpenAI request: " + json);
            
            req.timeout = 30;             // ADD timeout
            _inFlight.Add(req);           // ADD tracking

            yield return req.SendWebRequest();

            _inFlight.Remove(req);        // REMOVE tracking
            if (epoch != _epoch) yield break;  // CANCELED? stop processing

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }

            statusText.text = "Mengubah Respons LLM";

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

            string content = resp.choices[0].message.content;
            // content is expected to be JSON string of TenkiPlan
            TenkiPlan plan = null;
            try
            {
                plan = JsonUtility.FromJson<TenkiPlan>(content);
            }
            catch
            {
                // Try to sanitize minimal issues like leading/trailing whitespace
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

            if (string.IsNullOrEmpty(plan.intent))
            {
                onError?.Invoke("Plan missing intent.");
                yield break;
            }

            onSuccess?.Invoke(plan);
        }
    }

    private string BuildSystemPrompt()
    {
        // Strict instruction for JSON-only output compatible with WeatherAPI
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
    
    private string BuildSystemPromptForWeather()
    {
        // Strict instruction for JSON-only output compatible with WeatherAPI
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

    // ====== WeatherAPI fetchers ======

    [Serializable]
    public class WeatherLocation
    {
        public string name;
        public string region;
        public string country;
        public double lat;
        public double lon;
        public string tz_id;
        public long localtime_epoch;
        public string localtime;
    }

    [Serializable]
    public class Condition
    {
        public string text;
        public string icon;
        public int code;
    }

    [Serializable]
    public class Current
    {
        public string last_updated;
        public double temp_c;
        public double temp_f;
        public Condition condition;
        public double wind_kph;
        public double wind_mph;
        public int humidity;
        public double feelslike_c;
        public double feelslike_f;
        public double uv;
        public double precip_mm;
        public double precip_in;
        public double wind_degree;
        public string wind_dir;
        public int is_day;
    }

    [Serializable]
    public class ForecastDayTemp
    {
        public double maxtemp_c;
        public double mintemp_c;
        public double avgtemp_c;
        public double maxtemp_f;
        public double mintemp_f;
        public double avgtemp_f;
    }

    [Serializable]
    public class Day
    {
        public ForecastDayTemp temp; // not native; we’ll map after parse
        public double maxtemp_c;
        public double mintemp_c;
        public double avgtemp_c;
        public double maxtemp_f;
        public double mintemp_f;
        public double avgtemp_f;
        public Condition condition;
        public double maxwind_kph;
        public double totalprecip_mm;
        public int daily_chance_of_rain;
    }

    [Serializable]
    public class Astro { public string sunrise; public string sunset; }

    [Serializable]
    public class Hour
    {
        public string time;
        public double temp_c;
        public double temp_f;
        public Condition condition;
        public double wind_kph;
        public int chance_of_rain;
    }

    [Serializable]
    public class ForecastDay
    {
        public string date;
        public Day day;
        public Astro astro;
        public Hour[] hour;
    }

    [Serializable]
    public class Forecast
    {
        public ForecastDay[] forecastday;
    }

    [Serializable]
    public class WeatherApiCurrentResponse
    {
        public WeatherLocation location;
        public Current current;
    }

    [Serializable]
    public class WeatherApiForecastResponse
    {
        public WeatherLocation location;
        public Forecast forecast;
        public Current current; // present in some responses
    }

    public class WeatherResult
    {
        public WeatherLocation location;
        public Current current;
        public Forecast forecast;
        public bool isForecast;
    }

    private IEnumerator FetchCurrent(string q, Action<WeatherResult> onSuccess, Action<string> onError, int epoch)
    {
        string url = UseSecureRelay
            ? (RelayBaseUrl.TrimEnd('/') + "/weatherapi/current?q=" + UnityWebRequest.EscapeURL(q))
            : $"{WeatherApiBaseUrl}/current.json?key={WeatherApiKey}&q={UnityWebRequest.EscapeURL(q)}&aqi=no";

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 30;           // ADD
            _inFlight.Add(req);         // ADD
            yield return req.SendWebRequest();
            _inFlight.Remove(req);      // ADD
            if (epoch != _epoch) yield break;
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] Weather current: " + text);

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

    private IEnumerator FetchForecast(string q, int days, string dt, Action<WeatherResult> onSuccess, Action<string> onError, int epoch)
    {
        // Use forecast.json; include &days or &dt (WeatherAPI supports both)
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
            req.timeout = 30;           // ADD
            _inFlight.Add(req);         // ADD
            yield return req.SendWebRequest();
            _inFlight.Remove(req);      // ADD
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
                current = data.current,
                forecast = data.forecast,
                isForecast = true
            });
        }
    }

    private IEnumerator ResolveWithWeatherSearch(string q, Action<string> onResolved, Action<string> onError, int epoch)
    {
        string url = UseSecureRelay
            ? (RelayBaseUrl.TrimEnd('/') + "/weatherapi/search?q=" + UnityWebRequest.EscapeURL(q))
            : $"{WeatherApiBaseUrl}/search.json?key={WeatherApiKey}&q={UnityWebRequest.EscapeURL(q)}";

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 30;         // ADD
            _inFlight.Add(req);       // ADD
            yield return req.SendWebRequest();
            _inFlight.Remove(req);    // ADD
            if (epoch != _epoch) yield break;

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke(req.error);
                yield break;
            }
            var text = req.downloadHandler.text;
            if (VerboseLogging) Debug.Log("[Tenki] Weather search: " + text);

            // WeatherAPI search returns an array of location objects
            // We only need lat/lon or a canonical name. We'll pick the first.
            try
            {
                // Unity's JsonUtility can't parse bare arrays directly. Do a tiny wrapper.
                var wrapped = "{\"items\":" + text + "}";
                var items = JsonUtility.FromJson<SearchWrapper>(wrapped);
                if (items != null && items.items != null && items.items.Length > 0)
                {
                    var first = items.items[0];
                    var resolved = $"{first.lat.ToString(CultureInfo.InvariantCulture)},{first.lon.ToString(CultureInfo.InvariantCulture)}";
                    onResolved?.Invoke(resolved);
                    yield break;
                }
            }
            catch
            {
                // ignore and keep q as-is
            }

            onResolved?.Invoke(q); // fallback
        }
    }

    [Serializable]
    private class SearchWrapper
    {
        public SearchItem[] items;
    }

    [Serializable]
    private class SearchItem
    {
        public string name;
        public string region;
        public string country;
        public double lat;
        public double lon;
        public string url;
    }

    // ====== Helpers ======

    private void SetInteractable(bool enabled)
    {
        if (SendButton != null) SendButton.interactable = enabled;
        if (InputField != null) InputField.interactable = enabled;
        // Debug.Log("Checkpoint 3: " + enabled);
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
            onInteractableDisable.Invoke();       
    }

    private void SafeOutput(string msg)
    {
        if (OutputText != null) OutputText.text = msg;
        if (VerboseLogging) Debug.Log("[Tenki] " + msg);
    }

    private void OutputWeater(TenkiPlan plan, WeatherResult result)
    {
        bool metric = true;
        if (plan?.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.units))
        {
            metric = plan.weather_api.units.Equals("metric", StringComparison.OrdinalIgnoreCase);
        }

        if (!result.isForecast)
        {
            weatherIconImage.sprite = WeatherIcons.GetSprite(result.current.condition.code, result.current.is_day == 1);
            locationText.text = plan?.location.query;
            weatherConditionText.text = result.current.condition.text;
            lattitudeText.text = $"lat: {plan?.location.lat}";
            longtitudeText.text = $"lon: {plan?.location.lon}";
            lastUpdateText.text = $"update: {result.current.last_updated}";
            temperatureText.text = metric ? $"temp: {result.current.temp_c:0.#}°C" : $"temp: {result.current.temp_f:0.#}°F";
            windSpeedText.text = metric ? $"wind speed: {result.current.wind_kph:0.#} kph" : $"wind speed: {result.current.wind_mph:0.#} mph";
            windDirectionText.text = $"wind direction: {result.current.wind_dir} ({result.current.wind_degree})°";
            humidityText.text = $"humidity: {result.current.humidity}%";
            uvText.text = $"uv: {result.current.uv}";
        }

        // var temp = metric ? $"{c.temp_c:0.#}°C" : $"{c.temp_f:0.#}°F";
        // var feels = metric ? $"{c.feelslike_c:0.#}°C" : $"{c.feelslike_f:0.#}°F";
        // var wind = metric ? $"{c.wind_kph:0.#} kph" : $"{c.wind_mph:0.#} mph";
    }

    private static bool LooksLikeLatLon(string q)
    {
        // simple check: contains a comma and two numbers
        if (string.IsNullOrWhiteSpace(q)) return false;
        var parts = q.Split(',');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private string DeriveWeatherQ(TenkiPlan plan, string userText)
    {
        if (plan.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.q))
            return plan.weather_api.q.Trim();

        if (plan.location != null)
        {
            // Prefer lat,lon if provided
            if (Math.Abs(plan.location.lat) > 0.00001 || Math.Abs(plan.location.lon) > 0.00001)
            {
                return plan.location.lat.ToString(CultureInfo.InvariantCulture) + "," +
                       plan.location.lon.ToString(CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(plan.location.query))
                return plan.location.query.Trim();
        }

        // fallback to the raw text; search.json will try to resolve it
        return userText;
    }

    private bool ShouldUseForecast(TenkiPlan plan)
    {
        if (plan?.weather_api != null && string.Equals(plan.weather_api.endpoint, "forecast", StringComparison.OrdinalIgnoreCase))
            return true;

        if (plan?.time != null)
        {
            if (string.Equals(plan.time.type, "now", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(plan.time.type, "date", StringComparison.OrdinalIgnoreCase))
            {
                // If date is today or past? WeatherAPI can still forecast same-day; but we default to current for today.
                var dt = GetPlanDate(plan);
                if (DateTime.TryParse(dt, out var d))
                {
                    var today = DateTime.UtcNow.Date;
                    if (d.Date > today) return true;
                }
                // same-day -> current
                return false;
            }
            // relative (e.g., "tomorrow") -> forecast
            if (string.Equals(plan.time.type, "relative", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private int GetForecastDays(TenkiPlan plan)
    {
        if (plan?.weather_api != null && plan.weather_api.days > 0)
            return plan.weather_api.days;

        // Default if relative without explicit days: 3
        if (plan?.time != null && string.Equals(plan.time.type, "relative", StringComparison.OrdinalIgnoreCase))
            return 3;

        // If a future date is specified, 1 day is enough
        if (!string.IsNullOrEmpty(GetPlanDate(plan))) return 1;

        return 1;
    }

    private string GetPlanDate(TenkiPlan plan)
    {
        if (plan?.weather_api != null && !string.IsNullOrWhiteSpace(plan.weather_api.dt))
            return plan.weather_api.dt.Trim();
        if (plan?.time != null && !string.IsNullOrWhiteSpace(plan.time.date))
            return plan.time.date.Trim();
        return null;
    }

    private string FormatTenkiReply(TenkiPlan plan, WeatherResult res)
    {
        return "";
    }

    private IEnumerator ResultToSpeekableText(WeatherResult res, Action<string> onSuccess, Action<string> onError, int epoch)
    {
        var sys = BuildSystemPromptForWeather();
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
            {
                req.SetRequestHeader("Authorization", "Bearer " + OpenAIApiKey);
            }

            if (VerboseLogging) Debug.Log("[Tenki] OpenAI request: " + json);
            
            req.timeout = 30;         // ADD
            _inFlight.Add(req);       // ADD
            yield return req.SendWebRequest();
            _inFlight.Remove(req);    // ADD
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

            string content = resp.choices[0].message.content;
            // content is expected to be JSON string of TenkiPlan
            onSuccess?.Invoke(content);
        }
    }

    private string ComposePlaceName(WeatherLocation loc)
    {
        if (loc == null) return "your location";
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(loc.name)) items.Add(loc.name);
        if (!string.IsNullOrWhiteSpace(loc.region)) items.Add(loc.region);
        if (!string.IsNullOrWhiteSpace(loc.country)) items.Add(loc.country);
        return string.Join(", ", items);
    }
    
    private string BuildHttpDebug(UnityWebRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP ").Append(req.responseCode);

        // OpenAI returns JSON: {"error":{"message":"...","type":"...","code":"..."}}
        var body = req.downloadHandler != null ? req.downloadHandler.text : null;
        if (!string.IsNullOrEmpty(body)) sb.Append(" | body: ").Append(body);

        var retryAfter = req.GetResponseHeader("Retry-After");
        if (!string.IsNullOrEmpty(retryAfter)) sb.Append(" | retry-after: ").Append(retryAfter);

        return sb.ToString();
    }

    private IEnumerator TextToSpeechStart(string text, int epoch)
    {
        var reqObj = new ElevenLabsVoice
        {
            text = text,
            // model_id = "eleven_v3",
            language_code = "id",
            // voice_settings = new ElevenLabsVoiceSetting
            // {
            //     stability = 0.1f,
            //     similarity_boost = 0.9f,
            //     speed = 0.8f,
            //     use_speaker_boost = true
            // }
        };
        var json = JsonUtility.ToJson(reqObj);
        
        // Convert to bytes
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        // Create POST request
        UnityWebRequest request = new UnityWebRequest(ElevenLabsBaseUrl + ElevenLabsVoiceId, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        
        // If API requires authentication key
        request.SetRequestHeader("xi-api-key", ElevenLabsApiKey);

        request.timeout = 30;          // ADD timeout

        _inFlight.Add(request);        // ADD
        yield return request.SendWebRequest();
        _inFlight.Remove(request);     // ADD
        if (epoch != _epoch) yield break;


        if (request.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("Audio data received!");

            // Get raw audio bytes
            byte[] audioData = request.downloadHandler.data;

            // Convert MP3 -> AudioClip
            // Unity doesn’t natively decode MP3 from memory, so we save then reload
            string tempPath = Application.persistentDataPath + "/tts.mp3";
            System.IO.File.WriteAllBytes(tempPath, audioData);

            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + tempPath, AudioType.MPEG))
            {
                www.timeout = 30;      // ADD
                _inFlight.Add(www);    // ADD
                yield return www.SendWebRequest();
                _inFlight.Remove(www); // ADD
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
        SetInteractable(true);
        statusText.text = "Finishing";
    }

    // ====== Public API for hooking a button directly ======

    /// <summary>
    /// Hook this to any Button.onClick if you don't assign SendButton.
    /// </summary>
    public void SubmitFromButton()
    {
        OnSendClicked();
    }
}

