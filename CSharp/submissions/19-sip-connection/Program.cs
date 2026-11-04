using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5000));

string Config(string envName, string configKey) =>
    Environment.GetEnvironmentVariable(envName)
    ?? builder.Configuration[configKey]
    ?? throw new InvalidOperationException($"{envName} ortam değişkeni veya {configKey} ayarı tanımlı değil.");

var baseUrl = Environment.GetEnvironmentVariable("HIPCALL_BASE_URL")
    ?? builder.Configuration["Hipcall:BaseUrl"]
    ?? "https://use.hipcall.com.tr";
var clientId = Config("HIPCALL_CLIENT_ID", "Hipcall:ClientId");
var clientSecret = Config("HIPCALL_CLIENT_SECRET", "Hipcall:ClientSecret");
var publicUrl = Config("PUBLIC_URL", "Hipcall:PublicUrl").TrimEnd('/');
var redirectUri = $"{publicUrl}/callback";

// Vapi SIP sabitleri (ortam değişkeninden veya appsettings.json'dan okunur)
var vapiTerminationUri = Environment.GetEnvironmentVariable("VAPI_TERMINATION_URI")
    ?? builder.Configuration["Hipcall:VapiTerminationUri"]
    ?? "sip:sip.vapi.ai";
var sipUsername = (Environment.GetEnvironmentVariable("SIP_USERNAME")
    ?? builder.Configuration["Hipcall:SipUsername"]
    ?? "acme-vapi-01").TrimStart('+');
var sipPassword = Environment.GetEnvironmentVariable("SIP_PASSWORD")
    ?? builder.Configuration["Hipcall:SipPassword"]
    ?? "VapiSip!2026StrongPassword";
var terminationAuthPassword = Environment.GetEnvironmentVariable("TERMINATION_AUTH_PASSWORD")
    ?? builder.Configuration["Hipcall:TerminationAuthPassword"]
    ?? sipPassword;

Console.WriteLine($"[YAPILANDIRMA OKUNDU] SipUsername: {sipUsername}, VapiTerminationUri: {vapiTerminationUri}");

// SIP bağlantısı oluşturmak için OAuth2 token üzerinde communication_settings:write kapsamı
// ve onaylayan Hipcall kullanıcısında communication::settings izni bulunmalıdır.
const string scope = "profile+email+offline_access+communication_settings:read+communication_settings:write";

var app = builder.Build();

var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

var pendingStates = new ConcurrentDictionary<string, bool>();

app.MapGet("/", () => Results.Content(
    """
    <!DOCTYPE html>
    <html lang="tr">
    <head>
        <meta charset="utf-8" />
        <title>Acme SaaS — Vapi SIP Entegrasyonu</title>
        <style>
            body { font-family: system-ui, sans-serif; max-width: 760px; margin: 40px auto; padding: 0 20px; color: #0f172a; background: #f8fafc; }
            .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 10px; padding: 24px; margin-bottom: 20px; }
            .btn { display: inline-block; background: #2563eb; color: #fff; padding: 12px 20px; border-radius: 8px; text-decoration: none; font-weight: 600; border: none; cursor: pointer; }
            input[type=text] { padding: 10px; border: 1px solid #cbd5e1; border-radius: 6px; width: 360px; }
            code { background: #f1f5f9; padding: 2px 6px; border-radius: 4px; }
        </style>
    </head>
    <body>
        <div class="card">
            <h2>1. OAuth2 ile Vapi SIP Bağlantısını Otomatik Kur</h2>
            <p>Hipcall hesabınızı bağladığınızda uygulama aktif telefon numaranızı (<code>GET /api/v3/numbers</code>) ve boş bir dahili numarayı (<code>GET /api/v3/extensions</code> + <code>GET /api/v3/sip-connections</code>) bulur, ardından <code>POST /api/v3/sip-connections</code> ile Vapi SIP bağlantısını oluşturur.</p>
            <p><a class="btn" href="/login">Hipcall ile Bağlan ve Vapi SIP Kur</a></p>
        </div>
        <div class="card">
            <h3>Alternatif: Manuel API Token ile Kurulum</h3>
            <p>OAuth uygulamanızda <code>communication_settings:write</code> kapsamı henüz açılmadıysa Hipcall API anahtarınızla da aynı akışı test edebilirsiniz:</p>
            <form method="post" action="/manual-setup">
                <input type="text" name="access_token" placeholder="Hipcall API Token yapıştırın..." required />
                <button type="submit" class="btn">Token ile Kur</button>
            </form>
        </div>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));

// 1. OAuth2 Yetkilendirme (/login)
app.MapGet("/login", () =>
{
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    pendingStates[state] = true;

    var authorizeUrl = $"{baseUrl}/oauth/authorize?response_type=code"
        + $"&client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&scope={scope}&state={state}";

    Console.WriteLine($"[OAUTH2] Kullanıcı yönlendiriliyor: {authorizeUrl}");
    return Results.Redirect(authorizeUrl);
});

// 1.2 OAuth2 Callback (/callback) -> Token Takası ve SIP Kurulumu
app.MapGet("/callback", async (string? code, string? state, string? error) =>
{
    if (error is not null)
    {
        return Results.BadRequest($"Hipcall OAuth2 hatası: {error}");
    }

    if (code is null || state is null || !pendingStates.TryRemove(state, out _))
    {
        return Results.BadRequest("Eksik code veya geçersiz state parametresi.");
    }

    using var tokenResponse = await http.PostAsync("/oauth/token", new FormUrlEncodedContent(
        new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri
        }));

    var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
    if (!tokenResponse.IsSuccessStatusCode)
    {
        Console.WriteLine($"[OAUTH2 TOKEN HATA] HTTP {(int)tokenResponse.StatusCode}: {tokenBody}");
        return Results.Content(tokenBody, "application/json", statusCode: (int)tokenResponse.StatusCode);
    }

    using var tokenJson = JsonDocument.Parse(tokenBody);
    var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;
    var granted = tokenJson.RootElement.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() : null;
    Console.WriteLine($"[OAUTH2] Token alındı. Onaylanan kapsamlar: {granted ?? "(dönmedi)"}");

    return await ProvisionVapiSipConnectionAsync(accessToken);
});

app.MapPost("/manual-setup", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var accessToken = form["access_token"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.BadRequest("Access token boş olamaz.");
    }

    return await ProvisionVapiSipConnectionAsync(accessToken);
});

app.Run();

async Task<IResult> ProvisionVapiSipConnectionAsync(string accessToken)
{
    // 2.1 Aktif Telefon Numarasını Bulma (GET /api/v3/numbers)
    using var numbersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/numbers?limit=100");
    numbersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var numbersResponse = await http.SendAsync(numbersRequest);
    var numbersBody = await numbersResponse.Content.ReadAsStringAsync();
    Console.WriteLine($"[GET /api/v3/numbers] HTTP {(int)numbersResponse.StatusCode}: {numbersBody}");

    if (!numbersResponse.IsSuccessStatusCode)
    {
        return RenderResultHtml("Telefon Numaraları Alınamadı (GET /api/v3/numbers)", numbersBody, (int)numbersResponse.StatusCode, isError: true);
    }

    int? activeNumberId = null;
    string? activePhoneNumber = null;

    using (var numbersDoc = JsonDocument.Parse(numbersBody))
    {
        if (numbersDoc.RootElement.TryGetProperty("data", out var numbersArray) && numbersArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in numbersArray.EnumerateArray())
            {
                var itemState = item.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (string.Equals(itemState, "active", StringComparison.OrdinalIgnoreCase))
                {
                    activeNumberId = item.GetProperty("id").GetInt32();
                    activePhoneNumber = item.GetProperty("number").GetString();
                    break;
                }
            }
        }
    }

    if (activeNumberId is null || string.IsNullOrWhiteSpace(activePhoneNumber))
    {
        return RenderResultHtml(
            "Aktif Telefon Numarası Bulunamadı",
            "Hesapta state: \"active\" olan bir telefon numarası bulunamadı.\n" + numbersBody,
            StatusCodes.Status422UnprocessableEntity,
            isError: true);
    }

    Console.WriteLine($"[NUMARA SEÇİLDİ] number_id: {activeNumberId}, number: {activePhoneNumber}");

    // 2.2 Boş Dahili Numara Belirleme (GET /api/v3/extensions ve GET /api/v3/sip-connections)
    var usedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    using var extensionsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/extensions?limit=100");
    extensionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var extensionsResponse = await http.SendAsync(extensionsRequest);
    var extensionsBody = await extensionsResponse.Content.ReadAsStringAsync();
    Console.WriteLine($"[GET /api/v3/extensions] HTTP {(int)extensionsResponse.StatusCode}: {extensionsBody}");

    if (!extensionsResponse.IsSuccessStatusCode)
    {
        return RenderResultHtml("Dahili Numaralar Alınamadı (GET /api/v3/extensions)", extensionsBody, (int)extensionsResponse.StatusCode, isError: true);
    }

    using (var extensionsDoc = JsonDocument.Parse(extensionsBody))
    {
        if (extensionsDoc.RootElement.TryGetProperty("data", out var extArray) && extArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extArray.EnumerateArray())
            {
                if (item.TryGetProperty("number", out var numProp))
                {
                    usedExtensions.Add(numProp.ToString());
                }
            }
        }
    }

    using var existingSipReq = new HttpRequestMessage(HttpMethod.Get, "/api/v3/sip-connections?limit=100");
    existingSipReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    using var existingSipRes = await http.SendAsync(existingSipReq);
    if (existingSipRes.IsSuccessStatusCode)
    {
        var existingSipBody = await existingSipRes.Content.ReadAsStringAsync();
        using var existingSipDoc = JsonDocument.Parse(existingSipBody);
        if (existingSipDoc.RootElement.TryGetProperty("data", out var sipArr) && sipArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sipArr.EnumerateArray())
            {
                if (item.TryGetProperty("extension_number", out var extProp) && extProp.ValueKind != JsonValueKind.Null)
                {
                    usedExtensions.Add(extProp.ToString());
                }
            }
        }
    }

    // 4001'den başlayarak listede olmayan ilk boş dahili numarayı seç
    var candidateExt = 4001;
    while (usedExtensions.Contains(candidateExt.ToString()))
    {
        candidateExt++;
    }
    var freeExtension = candidateExt.ToString();

    Console.WriteLine($"[DAHİLİ SEÇİLDİ] extension_number: {freeExtension}, username: {sipUsername}");

    // 3. SIP Bağlantısının Kurulması (POST /api/v3/sip-connections)
    // KRİTİK KURAL:
    // 1) İstekte gönderilen "username", "termination_auth_username" (+90...) ve "password" / "termination_auth_password"
    //    değerlerinin Vapi tarafındaki Inbound SIP (BYO SIP Trunk) ayarlarındaki yetkilendirme bilgileriyle birebir aynı olması gerekir.
    // 2) "termination_auth_enabled": true olduğunda "termination_auth_username" ile birlikte "termination_auth_password"
    //    alanının da gönderilmesi zorunludur (GET yanıtlarında güvenlik nedeniyle şifre alanları gösterilmez).
    var sipPayload = JsonSerializer.Serialize(new
    {
        extension_number = freeExtension,
        name = "Vapi SIP Integration",
        number_id = activeNumberId.Value,
        termination_uri = vapiTerminationUri,
        username = sipUsername,
        password = sipPassword,
        termination_auth_enabled = true,
        termination_auth_username = activePhoneNumber,
        termination_auth_password = terminationAuthPassword
    }, new JsonSerializerOptions { WriteIndented = true });

    Console.WriteLine($"[POST /api/v3/sip-connections] Gönderilen Gövde:\n{sipPayload}");

    using var sipRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v3/sip-connections")
    {
        Content = new StringContent(sipPayload, Encoding.UTF8, "application/json")
    };
    sipRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var sipResponse = await http.SendAsync(sipRequest);
    var sipResponseBody = await sipResponse.Content.ReadAsStringAsync();

    Console.WriteLine($"[POST /api/v3/sip-connections YANITI] HTTP {(int)sipResponse.StatusCode}:\n{sipResponseBody}");

    return RenderResultHtml(
        sipResponse.IsSuccessStatusCode
            ? $"Vapi SIP Bağlantısı Başarıyla Oluşturuldu (Dahili: {freeExtension})"
            : $"SIP Bağlantısı Oluşturulamadı (HTTP {(int)sipResponse.StatusCode})",
        sipResponseBody,
        (int)sipResponse.StatusCode,
        isError: !sipResponse.IsSuccessStatusCode,
        freeExtension: freeExtension,
        activePhoneNumber: activePhoneNumber,
        activeNumberId: activeNumberId,
        sentPayload: sipPayload);
}

static IResult RenderResultHtml(
    string title,
    string jsonBody,
    int statusCode,
    bool isError,
    string? freeExtension = null,
    string? activePhoneNumber = null,
    int? activeNumberId = null,
    string? sentPayload = null)
{
    var formattedJson = jsonBody;
    try
    {
        using var doc = JsonDocument.Parse(jsonBody);
        formattedJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch
    {
    }

    var border = isError ? "#dc2626" : "#16a34a";
    var summaryHtml = freeExtension is not null
        ? $"""
          <p><strong>Seçilen Aktif Numara:</strong> <code>{WebUtility.HtmlEncode(activePhoneNumber)}</code> (ID: <code>{activeNumberId}</code>)<br/>
          <strong>Seçilen Boş Dahili Numara:</strong> <code>{WebUtility.HtmlEncode(freeExtension)}</code></p>
          <div style="background:#fffbeb; border-left:6px solid #d97706; padding:14px; border-radius:6px; margin:16px 0;">
              <strong>Sonraki Adım (Yönlendirme &amp; Vapi BYO SIP Trunk):</strong><br/>
              1. Vapi üzerindeki Inbound SIP (BYO SIP Trunk) ayarlarınızda kullanıcı adı ve parola bilgilerinin buradaki değerlerle birebir aynı olduğundan emin olun.<br/>
              2. Gelen dış aramaların Vapi'ye düşmesi için Hipcall panelinde <strong>Ayarlar -&gt; Telefon Sistemi -&gt; Telefon Numaraları</strong> menüsünden <code>{WebUtility.HtmlEncode(activePhoneNumber)}</code> numarasını seçip <strong>Çalışma Saatleri -&gt; Mesai içi hedef</strong> alanını <strong>{WebUtility.HtmlEncode(freeExtension)}</strong> dahili numarasına yönlendirin.
          </div>
          """
        : "";

    var sentHtml = sentPayload is not null
        ? $"<h4>Gönderilen İstek Gövdesi (POST /api/v3/sip-connections):</h4><pre>{WebUtility.HtmlEncode(sentPayload)}</pre>"
        : "";

    var html = $$"""
        <!DOCTYPE html>
        <html lang="tr">
        <head>
            <meta charset="utf-8" />
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
                body { font-family: system-ui, sans-serif; max-width: 820px; margin: 40px auto; padding: 0 20px; color: #0f172a; background: #f8fafc; }
                .card { background: #fff; border: 1px solid #e2e8f0; border-top: 6px solid {{border}}; border-radius: 10px; padding: 24px; }
                pre { background: #0f172a; color: #f8fafc; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 13px; }
                code { background: #f1f5f9; padding: 2px 6px; border-radius: 4px; }
                a { color: #2563eb; text-decoration: none; font-weight: 600; }
            </style>
        </head>
        <body>
            <div class="card">
                <h2>{{WebUtility.HtmlEncode(title)}}</h2>
                {{summaryHtml}}
                {{sentHtml}}
                <h4>Hipcall API Yanıtı (HTTP {{statusCode}}):</h4>
                <pre>{{WebUtility.HtmlEncode(formattedJson)}}</pre>
                <p><a href="/">← Ana Sayfaya Dön</a></p>
            </div>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
}
