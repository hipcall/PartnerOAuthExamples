using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true,
    reloadOnChange: true);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5000));

string Config(string envName, string configKey)
{
    var val = Environment.GetEnvironmentVariable(envName) ?? builder.Configuration[configKey];
    if (string.IsNullOrWhiteSpace(val) || val.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{envName} ortam değişkeni tanımlı değil.");
    }
    return val;
}

var baseUrl = (Environment.GetEnvironmentVariable("HIPCALL_BASE_URL")
    ?? builder.Configuration["Hipcall:BaseUrl"]
    ?? "https://use.hipcall.com.tr").TrimEnd('/');

var webhookSecret = Config("HIPCALL_WEBHOOK_SECRET", "Hipcall:WebhookSecret");
var apiToken = Config("HIPCALL_API_TOKEN", "Hipcall:ApiToken");

// B5 Kuralı: Hipcall API katı şema doğrulaması uygular.
// shortText satırında "user_id": null gönderilirse API 422 Unprocessable Entity döner.
// Bu nedenle SnakeCaseLower ve WhenWritingNull zorunludur.
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true
};

var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Hipcall.InsightCard",
    webhook_endpoints = new[] { "/hipcall/events/{secret}", "/evet={secret}" }
}));

async Task<IResult> HandleWebhookAsync(string secret, HttpRequest request)
{
    var expectedBytes = Encoding.UTF8.GetBytes(webhookSecret);
    var providedBytes = Encoding.UTF8.GetBytes(secret);
    if (expectedBytes.Length != providedBytes.Length ||
        !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
    {
        Console.WriteLine($"[YETKİSİZ] Hatalı webhook secret: {secret}");
        return Results.Unauthorized();
    }

    HipcallWebhookPayload? payload;
    try
    {
        payload = await JsonSerializer.DeserializeAsync<HipcallWebhookPayload>(
            request.Body,
            jsonOptions,
            request.HttpContext.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON payload." });
    }

    var eventName = payload?.Event ?? payload?.Type;
    if (payload?.Data is null || string.IsNullOrWhiteSpace(eventName))
    {
        return Results.BadRequest(new { error = "Missing event or data field." });
    }

    Console.WriteLine($"[WEBHOOK ALINDI] Olay: {eventName}");

    // Yalnızca call.initiated olaylarında kart basılır
    var isCallInitiated = string.Equals(eventName, "call.initiated", StringComparison.OrdinalIgnoreCase);

    if (!isCallInitiated)
    {
        return Results.Ok(new { status = "ignored", @event = eventName });
    }

    // 3. Çağrı Yönüne Göre Numara Ayıklama (Bölüm D2 Kuralı)
    // inbound ise müşteri numarası caller_number, outbound ise callee_number alanındadır.
    var data = payload.Data;
    var targetPhoneNumber = string.Equals(data.Direction, "inbound", StringComparison.OrdinalIgnoreCase)
        ? data.CallerNumber
        : data.CalleeNumber;

    // 4. Arka Plan İşlemi (Bölüm C4 ve D1 Kuralları: < 50 ms içinde 200 OK dön)
    _ = Task.Run(() => ProcessInsightCardAsync(data, targetPhoneNumber, http, jsonOptions));

    return Results.Ok(new { status = "accepted", call_uuid = data.Uuid });
}

// 2. Webhook Alıcısı (POST /hipcall/events/{secret} ve mevcut Fansa webhook'u için /evet={secret})
app.MapPost("/hipcall/events/{secret}", HandleWebhookAsync);
app.MapPost("/evet={secret}", HandleWebhookAsync);

app.Run();

static async Task ProcessInsightCardAsync(
    HipcallCallData callData,
    string? rawPhoneNumber,
    HttpClient http,
    JsonSerializerOptions jsonOptions)
{
    if (string.IsNullOrWhiteSpace(callData.Uuid) || string.IsNullOrWhiteSpace(rawPhoneNumber))
    {
        Console.WriteLine("[UYARI] Çağrı UUID veya hedef numara eksik, işlem sonlandırıldı.");
        return;
    }

    var normalizedPhone = NormalizePhone(rawPhoneNumber);
    Console.WriteLine($"[ÇAĞRI YAKALANDI] UUID: {callData.Uuid}, Yön: {callData.Direction}, Hedef Numara: {normalizedPhone}");

    // Sahte CRM kontrolü (Gerçek senaryoda CRM veritabanı sorgulanır, bütçe < 2 sn olmalıdır)
    if (normalizedPhone is "+90551XXXXXXX" or "+905321112233")
    {
        // B1 Kuralı: type yalnızca "title", "shortText" veya "user" olabilir.
        var cardPayload = new InsightCardRoot
        {
            Card =
            [
                new InsightCardItem
                {
                    Type = "title",
                    Text = "Acme CRM",
                    Link = "https://crm.example.com/customers/1234"
                },
                new InsightCardItem
                {
                    Type = "shortText",
                    Label = "Müşteri Adı",
                    Text = "Ahmet Yılmaz",
                    Link = "https://crm.example.com/contact/1234",
                    Ios = "acmecrm://contact/id=1234",
                    Android = "acmecrm://contact/id=1234"
                },
                new InsightCardItem
                {
                    Type = "shortText",
                    Label = "Şirket",
                    Text = "Tech A.Ş."
                },
                new InsightCardItem
                {
                    Type = "shortText",
                    Label = "Segment / Bakiye",
                    Text = "VIP - 0,00 TL Borç"
                },
                new InsightCardItem
                {
                    Type = "shortText",
                    Label = "Son Sipariş",
                    Text = "#9558 - Kargoda"
                }
            ]
        };

        var json = JsonSerializer.Serialize(cardPayload, jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"/api/v3/calls/{Uri.EscapeDataString(callData.Uuid)}/cards", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[INSIGHT CARD] POST /api/v3/calls/{callData.Uuid}/cards -> {(int)response.StatusCode}: {responseBody}");
    }
    else
    {
        // Bölüm A2 ve D3 Kuralı: Müşteri CRM'de yoksa ASLA {"card": []} gönderilmez,
        // hiçbir HTTP isteği atılmadan işlem sessizce sonlandırılır.
        Console.WriteLine($"[BİLGİ] {normalizedPhone} CRM'de bulunamadı. Boş kart basımı engellendi.");
    }
}

static string NormalizePhone(string phone)
{
    var digits = new StringBuilder();
    foreach (var ch in phone)
    {
        if (char.IsDigit(ch))
        {
            digits.Append(ch);
        }
    }

    var raw = digits.ToString();
    if (raw.Length == 10 && !raw.StartsWith('0'))
    {
        return "+90" + raw;
    }

    if (raw.Length == 11 && raw.StartsWith('0'))
    {
        return "+9" + raw;
    }

    return "+" + raw;
}

public sealed class HipcallWebhookPayload
{
    public string? Event { get; set; }
    public string? Type { get; set; }
    public string? Timestamp { get; set; }
    public HipcallCallData? Data { get; set; }
}

public sealed class HipcallCallData
{
    public string? Uuid { get; set; }
    public string? Direction { get; set; }
    public string? CallerNumber { get; set; }
    public string? CalleeNumber { get; set; }
    public string? StartedAt { get; set; }
    public int? UserId { get; set; }
}

public sealed class InsightCardRoot
{
    public List<InsightCardItem> Card { get; set; } = [];
}

public sealed class InsightCardItem
{
    public required string Type { get; set; }
    public string? Text { get; set; }
    public string? Label { get; set; }
    public string? Link { get; set; }
    public string? Ios { get; set; }
    public string? Android { get; set; }
    public int? UserId { get; set; }
}
