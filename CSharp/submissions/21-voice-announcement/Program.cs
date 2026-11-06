using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Yapılandırma dosyasını ve ortam değişkenlerini yükle
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5000));

// 1. Hipcall Yapılandırma Ayarlarını DI Konteynerine Ekle
builder.Services.Configure<HipcallOptions>(builder.Configuration.GetSection("Hipcall"));

// 2. Token Yönetimi ve IHttpClientFactory Yapılandırması
builder.Services.AddHttpClient("HipcallClient", (sp, httpClient) =>
{
    var options = sp.GetRequiredService<IOptions<HipcallOptions>>().Value;

    var baseUrl = Environment.GetEnvironmentVariable("HIPCALL_BASE_URL")
        ?? options.BaseUrl
        ?? "https://use.hipcall.com.tr";

    var apiToken = Environment.GetEnvironmentVariable("HIPCALL_API_TOKEN")
        ?? options.ApiToken;

    httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
    httpClient.DefaultRequestHeaders.Accept.Clear();
    httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    if (!string.IsNullOrWhiteSpace(apiToken) && !apiToken.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    httpClient.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Statik dosyaları (wwwroot/index.html) aktif et
app.UseDefaultFiles();
app.UseStaticFiles();

// JSON Serileştirme Seçenekleri
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true
};

// ============================================================================
// POST /api/send-voice-message
// Ön yüzden gelen hedef numara ve metni Hipcall Announcement API'sine gönderir
// ============================================================================
app.MapPost("/api/send-voice-message", async (
    SendVoiceMessageRequest request,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor) =>
{
    // 1. Temel Doğrulamalar
    if (string.IsNullOrWhiteSpace(request.CalleeNumber))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Hedef telefon numarası (callee_number) zorunludur."
        });
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Sesli mesaj metni (text) boş olamaz."
        });
    }

    var options = optionsAccessor.Value;
    var repeat = request.Repeat ?? options.DefaultRepeat;

    // Belgedeki kural: repeat 1 ile 5 arasında olmalıdır
    if (repeat < 1 || repeat > 5)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Tekrar sayısı (repeat) 1 ile 5 arasında olmalıdır."
        });
    }

    var numberId = request.NumberId ?? options.NumberId;
    var language = string.IsNullOrWhiteSpace(request.Language)
        ? (options.DefaultLanguage ?? "tr-TR")
        : request.Language;

    // 2. Hipcall API İstek Gövdesini Oluştur
    var announcementPayload = new HipcallAnnouncementRequest
    {
        CalleeNumber = request.CalleeNumber.Trim(),
        NumberId = numberId,
        Repeat = repeat,
        Tts = new HipcallTts
        {
            Language = language,
            Text = request.Text.Trim()
        }
    };

    var payloadJson = JsonSerializer.Serialize(announcementPayload, jsonOptions);

    // 3. IHttpClientFactory ile İstek Gönder
    var httpClient = httpClientFactory.CreateClient("HipcallClient");

    // Eğer istemci başlığında henüz Authorization yoksa ortam değişkeninden dinamik ekle
    if (httpClient.DefaultRequestHeaders.Authorization == null)
    {
        var token = Environment.GetEnvironmentVariable("HIPCALL_API_TOKEN") ?? options.ApiToken;
        if (!string.IsNullOrWhiteSpace(token) && !token.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            return Results.Json(new
            {
                success = false,
                statusCode = 401,
                message = "HIPCALL_API_TOKEN tanımlı değil. Lütfen appsettings.json dosyasını veya ortam değişkenini doldurun."
            }, statusCode: 401);
        }
    }

    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

    Console.WriteLine($"[ANTI-GRAVITY] POST /api/v3/calls/announcement çağrılıyor -> Hedef: {announcementPayload.CalleeNumber}, Dil: {language}, Tekrar: {repeat}");

    HttpResponseMessage response;
    string responseBody;

    try
    {
        response = await httpClient.PostAsync("/api/v3/calls/announcement", content);
        responseBody = await response.Content.ReadAsStringAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[HATA] Hipcall API çağrısı başarısız: {ex.Message}");
        return Results.Json(new
        {
            success = false,
            statusCode = 500,
            message = $"Hipcall sunucusuna bağlanırken hata oluştu: {ex.Message}"
        }, statusCode: 500);
    }

    var statusCodeInt = (int)response.StatusCode;
    Console.WriteLine($"[ANTI-GRAVITY] Yanıt alındı: {statusCodeInt} -> {responseBody}");

    // 4. API Cevabını Çözümle ve Ön Yüze Döndür
    if (response.IsSuccessStatusCode)
    {
        string? callId = null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("id", out var idProp))
            {
                callId = idProp.GetString();
            }
        }
        catch
        {
            // Yanıt JSON parse edilemezse null kalır
        }

        return Results.Json(new
        {
            success = true,
            statusCode = statusCodeInt,
            callId = callId,
            message = "Sesli mesaj çağrısı başarıyla oluşturuldu.",
            calleeNumber = announcementPayload.CalleeNumber,
            rawResponse = JsonSerializer.Deserialize<object>(responseBody, jsonOptions)
        }, statusCode: statusCodeInt);
    }
    else
    {
        object? parsedErrors = null;
        try
        {
            parsedErrors = JsonSerializer.Deserialize<object>(responseBody, jsonOptions);
        }
        catch
        {
            // Parse edilemezse raw metin döner
        }

        return Results.Json(new
        {
            success = false,
            statusCode = statusCodeInt,
            message = $"Hipcall API hatası ({statusCodeInt}).",
            errors = parsedErrors,
            rawResponse = responseBody
        }, statusCode: statusCodeInt);
    }
});

// GET /api/info (Sağlık ve aktif ayarları kontrol etme)
app.MapGet("/api/info", (IOptions<HipcallOptions> optionsAccessor) =>
{
    var options = optionsAccessor.Value;
    var tokenSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIPCALL_API_TOKEN")) ||
                   (!string.IsNullOrWhiteSpace(options.ApiToken) && !options.ApiToken.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase));

    return Results.Ok(new
    {
        service = "Hipcall Voice Announcement (Sesli Mesaj / Anons)",
        baseUrl = options.BaseUrl,
        numberId = options.NumberId,
        defaultLanguage = options.DefaultLanguage,
        isTokenConfigured = tokenSet
    });
});

app.Run();

// ============================================================================
// Model ve Sınıf Tanımları
// ============================================================================

public sealed class HipcallOptions
{
    public string BaseUrl { get; set; } = "https://use.hipcall.com.tr";
    public string ApiToken { get; set; } = "";
    public int NumberId { get; set; } = 942;
    public string DefaultLanguage { get; set; } = "tr-TR";
    public int DefaultRepeat { get; set; } = 1;
}

public sealed class SendVoiceMessageRequest
{
    public string? CalleeNumber { get; set; }
    public string? Text { get; set; }
    public int? Repeat { get; set; }
    public string? Language { get; set; }
    public int? NumberId { get; set; }
}

public sealed class HipcallAnnouncementRequest
{
    [JsonPropertyName("callee_number")]
    public string CalleeNumber { get; set; } = "";

    [JsonPropertyName("number_id")]
    public int NumberId { get; set; } = 942;

    [JsonPropertyName("repeat")]
    public int Repeat { get; set; } = 1;

    [JsonPropertyName("tts")]
    public HipcallTts Tts { get; set; } = new();
}

public sealed class HipcallTts
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "tr-TR";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

