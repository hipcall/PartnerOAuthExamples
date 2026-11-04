using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5000");

// 1. Yapılandırma (appsettings.json -> "Hipcall" bölümü)
builder.Services.Configure<HipcallOptions>(builder.Configuration.GetSection("Hipcall"));

// 2. HttpClient ve Durum Saklama Servisleri
builder.Services.AddHttpClient("HipcallApi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<HipcallOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<SpeedDialStateStore>();

var app = builder.Build();

// ============================================================================
// 1. ANA SAYFA (GET /)
// Kullanıcıyı https://use.hipcall.com.tr/oauth/authorize adresine yönlendiren arayüz
// ============================================================================
app.MapGet("/", async (
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    SpeedDialStateStore store) =>
{
    var options = optionsAccessor.Value;
    var state = store.GetState();
    var authorizeUrl = BuildAuthorizationUrl(options, forceConsent: true);

    List<HipcallSpeedDialItemDto>? existingSpeedDials = null;
    string? fetchError = null;

    if (!string.IsNullOrWhiteSpace(state.AccessToken))
    {
        var client = httpClientFactory.CreateClient("HipcallApi");
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{options.BaseUrl.TrimEnd('/')}/api/v3/speed-dials?limit=20&offset=0&sort=inserted_at.desc");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);
            using var res = await client.SendAsync(req);
            var resBody = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(resBody);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    existingSpeedDials = JsonSerializer.Deserialize<List<HipcallSpeedDialItemDto>>(dataEl.GetRawText());
                }
            }
            else
            {
                fetchError = $"HTTP {(int)res.StatusCode}: {resBody}";
            }
        }
        catch (Exception ex)
        {
            fetchError = ex.Message;
        }
    }

    var html = RenderDashboardHtml(options, state, authorizeUrl, existingSpeedDials, fetchError);
    return Results.Content(html, "text/html; charset=utf-8");
});

// OAuth2 Yönlendirme Yardımcısı (GET /oauth/login)
app.MapGet("/oauth/login", (IOptions<HipcallOptions> optionsAccessor) =>
{
    var authorizeUrl = BuildAuthorizationUrl(optionsAccessor.Value, forceConsent: true);
    Console.WriteLine($"[OAUTH2] Kullanıcı Hipcall yetkilendirme ekranına yönlendiriliyor: {authorizeUrl}");
    return Results.Redirect(authorizeUrl);
});

// ============================================================================
// 2. OAUTH2 CALLBACK & OTOMATİK HIZLI ARAMA KURULUMU (GET /callback)
// ============================================================================
app.MapGet("/callback", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    SpeedDialStateStore store) =>
{
    var options = optionsAccessor.Value;
    var code = context.Request.Query["code"].ToString();
    var returnedState = context.Request.Query["state"].ToString();
    var error = context.Request.Query["error"].ToString();
    var errorDescription = context.Request.Query["error_description"].ToString();

    if (!string.IsNullOrEmpty(error))
    {
        Console.WriteLine($"[OAUTH2 HATA] {error}: {errorDescription}");
        return Results.Content(
            RenderMessageHtml(
                "OAuth2 Yetkilendirme Hatası",
                $"Hipcall hata döndürdü: <b>{WebUtility.HtmlEncode(error)}</b> - {WebUtility.HtmlEncode(errorDescription)}",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.Content(
            RenderMessageHtml("Eksik Parametre", "Query string içinde 'code' parametresi bulunamadı.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (!string.Equals(returnedState, options.State, StringComparison.Ordinal))
    {
        Console.WriteLine($"[GÜVENLİK UYARISI] State uyuşmazlığı! Beklenen: '{options.State}', Gelen: '{returnedState}'");
        return Results.Content(
            RenderMessageHtml(
                "Geçersiz State (CSRF Koruması)",
                $"Beklenen state (<code>{WebUtility.HtmlEncode(options.State)}</code>) ile gelen state (<code>{WebUtility.HtmlEncode(returnedState)}</code>) eşleşmedi.",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var client = httpClientFactory.CreateClient("HipcallApi");

    // Adım 2.1: Authorization Code -> Access Token değişimi (POST /oauth/token)
    Console.WriteLine($"[OAUTH2] Authorization code alındı. Token uç noktasına istek gönderiliyor: {options.TokenUrl}");

    var tokenForm = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["redirect_uri"] = options.RedirectUri,
        ["scope"] = options.Scopes
    };

    using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, options.TokenUrl)
    {
        Content = new FormUrlEncodedContent(tokenForm)
    };
    tokenRequest.Headers.Accept.Clear();
    tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    using var tokenResponse = await client.SendAsync(tokenRequest);
    var tokenRawJson = await tokenResponse.Content.ReadAsStringAsync();

    if (!tokenResponse.IsSuccessStatusCode)
    {
        Console.WriteLine($"[OAUTH2 TOKEN HATA] HTTP {(int)tokenResponse.StatusCode}: {tokenRawJson}");
        return Results.Content(
            RenderMessageHtml(
                "OAuth2 Token Alınamadı",
                $"Token uç noktası <b>HTTP {(int)tokenResponse.StatusCode}</b> döndürdü:<pre>{WebUtility.HtmlEncode(tokenRawJson)}</pre>",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: (int)tokenResponse.StatusCode);
    }

    var tokenData = JsonSerializer.Deserialize<OAuthTokenResponse>(tokenRawJson);
    if (tokenData is null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
    {
        return Results.Content(
            RenderMessageHtml("Geçersiz Token Yanıtı", $"Yanıtta access_token bulunamadı:<pre>{WebUtility.HtmlEncode(tokenRawJson)}</pre>", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway);
    }

    Console.WriteLine("================================================================================");
    Console.WriteLine("[OAUTH2 TOKEN ALINDI]");
    Console.WriteLine($"  Gelen Kapsamlar (Scope): {tokenData.Scope ?? "(Boş veya dönmedi)"}");
    Console.WriteLine($"  Token Tipi             : {tokenData.TokenType ?? "Bearer"}");
    Console.WriteLine($"  Geçerlilik Süresi (sn) : {tokenData.ExpiresIn?.ToString() ?? "-"}");
    Console.WriteLine("================================================================================");

    store.SaveTokens(tokenData.AccessToken, tokenData.RefreshToken, tokenData.Scope);

    // Adım 2.2: Alınan Access Token ile otomatik olarak POST /api/v3/speed-dials çağrısı yap
    var setupResult = await CreateSpeedDialAutomaticallyAsync(client, options, store, tokenData.AccessToken, tokenData.Scope);
    return Results.Content(setupResult.Html, "text/html; charset=utf-8", statusCode: setupResult.StatusCode);
});

// ============================================================================
// SENARYO B: MANUEL API / ACCESS TOKEN İLE HIZLI ARAMA KURULUMU (POST /manual-setup)
// OAuth uygulamasında communication_settings:write scope'u henüz açılmadıysa test için
// ============================================================================
app.MapPost("/manual-setup", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    SpeedDialStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var manualToken = form["access_token"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(manualToken))
    {
        return Results.Content(
            RenderMessageHtml("Eksik Token", "Lütfen geçerli bir Hipcall API Token veya Access Token girin.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var client = httpClientFactory.CreateClient("HipcallApi");
    var options = optionsAccessor.Value;

    store.SaveTokens(manualToken, null, "manual_api_token");
    var setupResult = await CreateSpeedDialAutomaticallyAsync(client, options, store, manualToken, "manual_api_token");
    return Results.Content(setupResult.Html, "text/html; charset=utf-8", statusCode: setupResult.StatusCode);
});

// ============================================================================
// 3. WEBSERVİS DİNLEYİCİ (POST /hizli-arama)
// Kurye 7001'i tuşlayıp sipariş kodunu (örn: 1234) girdiğinde Hipcall buraya POST atar
// ============================================================================
app.MapPost("/hizli-arama", async (
    HttpContext context,
    IOptions<HipcallOptions> optionsAccessor,
    SpeedDialStateStore store) =>
{
    var options = optionsAccessor.Value;

    string rawBody;
    using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
    {
        rawBody = await reader.ReadToEndAsync();
    }

    var authHeader = context.Request.Headers.Authorization.ToString();

    Console.WriteLine();
    Console.WriteLine("================================================================================");
    Console.WriteLine($"[HIZLI ARAMA İSTEĞİ GELDİ] Zaman: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"  Gelen Gövde : {rawBody}");

    // Adım 3.1: Basic Auth Doğrulaması (Kullanıcı: hipcall, Şifre: s3cret-token)
    if (options.WebServiceAuth)
    {
        if (!VerifyBasicAuth(authHeader, options.WebServiceUsername, options.WebServicePassword, out var authDetail))
        {
            Console.WriteLine($"[BASIC AUTH BAŞARISIZ] {authDetail}");
            Console.WriteLine("================================================================================");

            store.AddLookupLog(new SpeedDialLookupLog
            {
                ReceivedAt = DateTimeOffset.Now,
                OrderNumber = "-",
                CallUuid = "-",
                AuthValid = false,
                ReturnedDestination = null,
                Summary = $"Basic Auth başarısız: {authDetail}",
                RawPayload = rawBody
            });

            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Hipcall Speed Dial\"";
            return Results.Json(new { error = "unauthorized", detail = authDetail }, statusCode: StatusCodes.Status401Unauthorized);
        }

        Console.WriteLine($"[BASIC AUTH DOĞRULANDI] Kullanıcı: {options.WebServiceUsername}");
    }

    // Adım 3.2: Gelen JSON içerisindeki "number" (sipariş kodu) alanını oku
    string? orderNumber = null;
    string? callUuid = null;
    long? timestamp = null;
    int? speedDialId = null;
    int? userId = null;

    try
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        orderNumber = GetStringProperty(root, "number");
        callUuid = GetStringProperty(root, "uuid");
        timestamp = GetLongProperty(root, "timestamp");
        speedDialId = GetNullableIntProperty(root, "speed_dial_id");
        userId = GetNullableIntProperty(root, "user_id");
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"[JSON HATASI] Geçersiz JSON gövdesi: {ex.Message}");
        Console.WriteLine("================================================================================");
        return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
    }

    Console.WriteLine($"  Tuşlanan Sipariş Kodu (number) : {orderNumber ?? "(boş)"}");
    Console.WriteLine($"  Çağrı UUID (uuid)              : {callUuid ?? "-"}");
    Console.WriteLine($"  Speed Dial ID (speed_dial_id)  : {speedDialId?.ToString() ?? "-"}");
    Console.WriteLine($"  User ID (user_id)              : {userId?.ToString() ?? "null"}");
    Console.WriteLine($"  Timestamp                      : {timestamp?.ToString() ?? "-"}");

    if (string.IsNullOrWhiteSpace(orderNumber))
    {
        Console.WriteLine("[HATA] 'number' alanı boş veya gönderilmedi.");
        Console.WriteLine("================================================================================");
        return Results.Json(new { error = "missing_number" }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Adım 3.3: Sipariş numarasını kontrol et ve müşterinin gerçek telefon numarasını dön
    var destination = store.ResolveDestination(orderNumber.Trim());
    if (string.IsNullOrWhiteSpace(destination))
    {
        Console.WriteLine($"[SİPARİŞ BULUNAMADI] '{orderNumber}' kodlu sipariş için eşleşen telefon numarası yok.");
        Console.WriteLine("================================================================================");

        store.AddLookupLog(new SpeedDialLookupLog
        {
            ReceivedAt = DateTimeOffset.Now,
            OrderNumber = orderNumber,
            CallUuid = callUuid ?? "-",
            AuthValid = true,
            ReturnedDestination = null,
            Summary = $"Sipariş kodu '{orderNumber}' bulunamadı (HTTP 404)",
            RawPayload = rawBody
        });

        return Results.Json(new { error = "order_not_found", number = orderNumber }, statusCode: StatusCodes.Status404NotFound);
    }

    Console.WriteLine($"[HEDEF NUMARA DÖNDÜRÜLDÜ] Sipariş #{orderNumber} -> destination: {destination}");
    Console.WriteLine("================================================================================");

    store.AddLookupLog(new SpeedDialLookupLog
    {
        ReceivedAt = DateTimeOffset.Now,
        OrderNumber = orderNumber,
        CallUuid = callUuid ?? "-",
        AuthValid = true,
        ReturnedDestination = destination,
        Summary = $"Sipariş #{orderNumber} -> {destination} (HTTP 200)",
        RawPayload = rawBody
    });

    // Hipcall'un beklediği yanıt formatı: {"destination": "905301234567"}
    return Results.Json(new { destination });
});

// ============================================================================
// EK YÖNETİM UÇ NOKTALARI (Durum Değiştirme, Sipariş Ekleme ve Yerel Simülasyon)
// ============================================================================

// Hızlı Arama Durum Değiştirme: PATCH /api/v3/speed-dials/{id} (active <-> passive)
app.MapPost("/toggle-speed-dial-state", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    SpeedDialStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var speedDialId = form["speed_dial_id"].ToString().Trim();
    var targetState = form["target_state"].ToString().Trim().ToLowerInvariant();

    var state = store.GetState();
    if (string.IsNullOrWhiteSpace(speedDialId) || (targetState != "active" && targetState != "passive"))
    {
        return Results.Redirect("/");
    }

    if (string.IsNullOrWhiteSpace(state.AccessToken))
    {
        return Results.Content(
            RenderMessageHtml("Yetkilendirme Eksik", "Önce OAuth2 ile Hipcall hesabınızı bağlayın.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var options = optionsAccessor.Value;
    var client = httpClientFactory.CreateClient("HipcallApi");
    var patchUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/speed-dials/{speedDialId}";

    var payload = JsonSerializer.Serialize(new { state = targetState });
    using var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);

    using var response = await client.SendAsync(request);
    var responseBody = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"[SPEED DIAL PATCH] ID: {speedDialId} -> state='{targetState}' (HTTP {(int)response.StatusCode})");

    if (response.IsSuccessStatusCode && string.Equals(state.SpeedDialId, speedDialId, StringComparison.OrdinalIgnoreCase))
    {
        store.UpdateSpeedDialState(targetState);
    }

    return Results.Redirect("/");
});

// Yeni Sipariş Kodu -> Telefon Numarası Eşleşmesi Ekleme
app.MapPost("/add-order", async (HttpContext context, SpeedDialStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var orderCode = form["order_code"].ToString().Trim();
    var phone = form["phone"].ToString().Trim();

    if (!string.IsNullOrWhiteSpace(orderCode) && !string.IsNullOrWhiteSpace(phone))
    {
        store.SetOrderMapping(orderCode, phone);
        Console.WriteLine($"[SİPARİŞ EŞLEŞMESİ EKLENDİ] Sipariş #{orderCode} -> {phone}");
    }

    return Results.Redirect("/");
});

app.Run();

// ============================================================================
// YARDIMCI METOTLAR VE İŞ MANTIĞI
// ============================================================================
static string BuildAuthorizationUrl(HipcallOptions options, bool forceConsent = true)
{
    var formattedScopes = string.Join("+", options.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    var queryParams = new List<string>
    {
        "response_type=code",
        $"client_id={Uri.EscapeDataString(options.ClientId)}",
        $"redirect_uri={Uri.EscapeDataString(options.RedirectUri)}",
        $"scope={formattedScopes}",
        $"state={Uri.EscapeDataString(options.State)}"
    };

    if (forceConsent)
    {
        queryParams.Add("prompt=consent");
    }

    return $"{options.AuthorizationUrl.TrimEnd('/')}?{string.Join("&", queryParams)}";
}

static async Task<(string Html, int StatusCode)> CreateSpeedDialAutomaticallyAsync(
    HttpClient client,
    HipcallOptions options,
    SpeedDialStateStore store,
    string accessToken,
    string? grantedScope)
{
    var createUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/speed-dials";

    // İstenen birebir JSON gövdesi:
    // {
    //   "extension_number": "7001",
    //   "name": "Order status",
    //   "web_service_auth": true,
    //   "web_service_password": "s3cret-token",
    //   "web_service_url": "https://your-tunnel.ngrok-free.dev/hizli-arama",
    //   "web_service_username": "hipcall"
    // }
    var payload = new CreateSpeedDialRequest
    {
        ExtensionNumber = options.ExtensionNumber,
        Name = options.SpeedDialName,
        WebServiceAuth = options.WebServiceAuth,
        WebServicePassword = options.WebServicePassword,
        WebServiceUrl = options.WebServiceUrl,
        WebServiceUsername = options.WebServiceUsername
    };

    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine($"[HIZLI ARAMA KURULUM] POST {createUrl} adresine istek atılıyor...");
    Console.WriteLine($"[HIZLI ARAMA KURULUM] İstek Gövdesi:\n{payloadJson}");

    using var request = new HttpRequestMessage(HttpMethod.Post, createUrl)
    {
        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await client.SendAsync(request);
    var responseJson = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"[HIZLI ARAMA YANITI] HTTP {(int)response.StatusCode}: {responseJson}");

    const string routingWarningHtml = @"
        <div style=""background:#fffbeb; border:2px solid #f59e0b; border-left:8px solid #d97706; border-radius:10px; padding:18px 20px; margin:20px 0; color:#78350f; font-size:16px; line-height:1.6;"">
            <strong>⚠️ Önemli Yönlendirme Adımı:</strong><br/>
            Hızlı aramanız (7001 dahili hattı) API ile başarıyla kuruldu. Sistemin çalışması için şimdi Hipcall panelinde <strong>Ayarlar -&gt; Telefon Numaraları</strong> bölümüne gidip, numaranızın 'Mesai içi hedefini' yeni oluşan <strong>7001</strong> dahili hattına yönlendirmeyi unutmayın!
        </div>";

    if (!response.IsSuccessStatusCode)
    {
        // Eğer 7001 dahili hattı daha önceki testte zaten oluşturulmuşsa mevcut kaydı da kontrol et
        string extraHint = "";
        if ((int)response.StatusCode == 422)
        {
            extraHint = "<p><strong>İpucu:</strong> <code>7001</code> dahili numarası hesabınızda zaten mevcut olabilir. Ana sayfadaki listeden mevcut <code>7001</code> hızlı arama kaydınızı görebilirsiniz.</p>";
            await TrySyncExistingSpeedDialAsync(client, options, store, accessToken);
        }
        else if ((int)response.StatusCode == 403)
        {
            extraHint = "<p><strong>İpucu:</strong> OAuth token üzerinde <code>communication_settings:write</code> kapsamının ve kullanıcıda <code>communication::settings</code> yetkisinin bulunduğundan emin olun.</p>";
        }

        return (
            RenderMessageHtml(
                $"Hızlı Arama Oluşturulamadı (HTTP {(int)response.StatusCode})",
                $@"{extraHint}
                   {routingWarningHtml}
                   <h4>Gönderilen İstek Gövdesi:</h4>
                   <pre>{WebUtility.HtmlEncode(payloadJson)}</pre>
                   <h4>Hipcall API Yanıtı:</h4>
                   <pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
                isError: true),
            (int)response.StatusCode);
    }

    string speedDialId = "-";
    string name = options.SpeedDialName;
    string state = "active";
    string extensionNumber = options.ExtensionNumber;

    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
        {
            speedDialId = GetStringProperty(dataNode, "id") ?? "-";
            name = GetStringProperty(dataNode, "name") ?? options.SpeedDialName;
            state = GetStringProperty(dataNode, "state") ?? "active";
            extensionNumber = GetStringProperty(dataNode, "extension_number") ?? options.ExtensionNumber;
        }
    }
    catch (JsonException)
    {
    }

    store.SaveSpeedDial(speedDialId, name, extensionNumber, state, options.WebServiceUrl);

    var successHtml = RenderMessageHtml(
        "Hızlı Arama (Speed Dial) Başarıyla Kuruldu!",
        $@"{routingWarningHtml}
           <table style=""width:100%; border-collapse:collapse; margin:16px 0;"">
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Hızlı Arama ID</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;""><code>{WebUtility.HtmlEncode(speedDialId)}</code></td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Ad (name)</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;"">{WebUtility.HtmlEncode(name)}</td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Dahili Hat (extension_number)</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;""><strong>{WebUtility.HtmlEncode(extensionNumber)}</strong></td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Durum (state)</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;""><code>{WebUtility.HtmlEncode(state)}</code></td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Webservis Dinleyici URL</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;""><code>{WebUtility.HtmlEncode(options.WebServiceUrl)}</code></td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Basic Auth</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;"">Kullanıcı: <code>{WebUtility.HtmlEncode(options.WebServiceUsername)}</code> (Parola güvenlik gereği API yanıtında döndürülmez)</td></tr>
               <tr><td style=""padding:8px; border-bottom:1px solid #e2e8f0; font-weight:600;"">Onaylanan Kapsamlar (Scope)</td><td style=""padding:8px; border-bottom:1px solid #e2e8f0;""><code>{WebUtility.HtmlEncode(grantedScope ?? "-")}</code></td></tr>
           </table>
           <h4>Hipcall API Yanıtı (<code>POST /api/v3/speed-dials</code>):</h4>
           <pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
        isError: false);

    return (successHtml, StatusCodes.Status200OK);
}

static async Task TrySyncExistingSpeedDialAsync(
    HttpClient client,
    HipcallOptions options,
    SpeedDialStateStore store,
    string accessToken)
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v3/speed-dials?limit=50");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode) return;

        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in dataArr.EnumerateArray())
        {
            var ext = GetStringProperty(item, "extension_number");
            if (string.Equals(ext, options.ExtensionNumber, StringComparison.OrdinalIgnoreCase))
            {
                var id = GetStringProperty(item, "id") ?? "-";
                var name = GetStringProperty(item, "name") ?? options.SpeedDialName;
                var st = GetStringProperty(item, "state") ?? "active";
                var url = GetStringProperty(item, "web_service_url") ?? options.WebServiceUrl;
                store.SaveSpeedDial(id, name, ext!, st, url);
                break;
            }
        }
    }
    catch
    {
    }
}

static bool VerifyBasicAuth(string authorizationHeader, string expectedUsername, string expectedPassword, out string detail)
{
    if (string.IsNullOrWhiteSpace(authorizationHeader))
    {
        detail = "Authorization başlığı gönderilmedi.";
        return false;
    }

    if (!authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        detail = "Authorization başlığı 'Basic ' ile başlamıyor.";
        return false;
    }

    var base64Token = authorizationHeader["Basic ".Length..].Trim();
    byte[] decodedBytes;
    try
    {
        decodedBytes = Convert.FromBase64String(base64Token);
    }
    catch (FormatException)
    {
        detail = "Authorization başlığındaki Base64 değeri çözülemedi.";
        return false;
    }

    var credentials = Encoding.UTF8.GetString(decodedBytes);
    var separatorIndex = credentials.IndexOf(':');
    if (separatorIndex < 0)
    {
        detail = "Basic Auth değeri 'kullanici:parola' formatında değil.";
        return false;
    }

    var actualUsername = credentials[..separatorIndex];
    var actualPassword = credentials[(separatorIndex + 1)..];

    var userMatch = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(actualUsername),
        Encoding.UTF8.GetBytes(expectedUsername));
    var passMatch = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(actualPassword),
        Encoding.UTF8.GetBytes(expectedPassword));

    if (!userMatch || !passMatch)
    {
        detail = "Kullanıcı adı veya parola hatalı.";
        return false;
    }

    detail = "OK";
    return true;
}

static string? GetStringProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind != JsonValueKind.Null)
    {
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }
    return null;
}

static long? GetLongProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop))
    {
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
            return parsed;
    }
    return null;
}

static int? GetNullableIntProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop))
    {
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
    }
    return null;
}

static string FormatJsonSafe(string rawJson)
{
    if (string.IsNullOrWhiteSpace(rawJson)) return "(Boş yanıt)";
    try
    {
        using var doc = JsonDocument.Parse(rawJson);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch
    {
        return rawJson;
    }
}

static string RenderDashboardHtml(
    HipcallOptions options,
    SpeedDialPersistentState state,
    string authorizeUrl,
    List<HipcallSpeedDialItemDto>? existingSpeedDials,
    string? fetchError)
{
    var orderRowsSb = new StringBuilder();
    foreach (var kvp in state.OrderMappings.OrderBy(k => k.Key))
    {
        orderRowsSb.AppendLine($@"
            <tr>
                <td><code>{WebUtility.HtmlEncode(kvp.Key)}</code></td>
                <td><code>{WebUtility.HtmlEncode(kvp.Value)}</code></td>
            </tr>");
    }

    var speedDialsSb = new StringBuilder();
    if (!string.IsNullOrWhiteSpace(state.AccessToken))
    {
        if (!string.IsNullOrWhiteSpace(fetchError))
        {
            speedDialsSb.AppendLine($"<div class=\"alert alert-error\">Hızlı aramalar listelenemedi: {WebUtility.HtmlEncode(fetchError)}</div>");
        }
        else if (existingSpeedDials is { Count: > 0 })
        {
            speedDialsSb.AppendLine(@"
                <table>
                    <thead>
                        <tr>
                            <th>Dahili (extension_number)</th>
                            <th>Ad (name)</th>
                            <th>Web Service URL</th>
                            <th>Basic Auth</th>
                            <th>Durum (state)</th>
                            <th>İşlem</th>
                        </tr>
                    </thead>
                    <tbody>");
            foreach (var item in existingSpeedDials)
            {
                var isActive = string.Equals(item.State, "active", StringComparison.OrdinalIgnoreCase);
                var nextState = isActive ? "passive" : "active";
                var btnText = isActive ? "Pasife Al" : "Aktif Et";
                var badgeClass = isActive ? "badge-ok" : "badge-warn";

                speedDialsSb.AppendLine($@"
                    <tr>
                        <td><strong>{WebUtility.HtmlEncode(item.ExtensionNumber)}</strong></td>
                        <td>{WebUtility.HtmlEncode(item.Name)}</td>
                        <td><code>{WebUtility.HtmlEncode(item.WebServiceUrl)}</code></td>
                        <td>{(item.WebServiceAuth ? $"Açık (<code>{WebUtility.HtmlEncode(item.WebServiceUsername ?? "-")}</code>)" : "Kapalı")}</td>
                        <td><span class=""badge {badgeClass}"">{WebUtility.HtmlEncode(item.State)}</span></td>
                        <td>
                            <form method=""post"" action=""/toggle-speed-dial-state"" style=""margin:0;"">
                                <input type=""hidden"" name=""speed_dial_id"" value=""{WebUtility.HtmlEncode(item.Id)}"" />
                                <input type=""hidden"" name=""target_state"" value=""{nextState}"" />
                                <button type=""submit"" class=""btn-sm"">{btnText}</button>
                            </form>
                        </td>
                    </tr>");
            }
            speedDialsSb.AppendLine("</tbody></table>");
        }
        else
        {
            speedDialsSb.AppendLine("<p>Hesapta henüz kayıtlı hızlı arama bulunamadı.</p>");
        }
    }

    var logsSb = new StringBuilder();
    if (state.RecentLookups.Count == 0)
    {
        logsSb.AppendLine("<p style=\"color:#64748b;\">Henüz <code>POST /hizli-arama</code> uç noktasına gelen bir kurye araması yok.</p>");
    }
    else
    {
        logsSb.AppendLine(@"
            <table>
                <thead>
                    <tr>
                        <th>Zaman</th>
                        <th>Sipariş Kodu (number)</th>
                        <th>Hedef Numara (destination)</th>
                        <th>Basic Auth</th>
                        <th>Özet</th>
                    </tr>
                </thead>
                <tbody>");
        foreach (var log in state.RecentLookups)
        {
            logsSb.AppendLine($@"
                <tr>
                    <td>{log.ReceivedAt:HH:mm:ss}</td>
                    <td><code>{WebUtility.HtmlEncode(log.OrderNumber)}</code></td>
                    <td><code>{WebUtility.HtmlEncode(log.ReturnedDestination ?? "-")}</code></td>
                    <td>{(log.AuthValid ? "<span class=\"badge badge-ok\">Geçerli</span>" : "<span class=\"badge badge-err\">Hatalı</span>")}</td>
                    <td>{WebUtility.HtmlEncode(log.Summary)}</td>
                </tr>");
        }
        logsSb.AppendLine("</tbody></table>");
    }

    var routingBanner = !string.IsNullOrWhiteSpace(state.SpeedDialId)
        ? @"<div class=""warning-box"">
                <strong>⚠️ Hızlı Arama Yönlendirme Uyarısı:</strong><br/>
                Hızlı aramanız (7001 dahili hattı) API ile başarıyla kuruldu. Sistemin çalışması için şimdi Hipcall panelinde <strong>Ayarlar -&gt; Telefon Numaraları</strong> bölümüne gidip, numaranızın 'Mesai içi hedefini' yeni oluşan <strong>7001</strong> dahili hattına yönlendirmeyi unutmayın!
            </div>"
        : "";

    var curlBasicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.WebServiceUsername}:{options.WebServicePassword}"));

    return $@"<!DOCTYPE html>
<html lang=""tr"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>Acme SaaS - Hipcall OAuth2 &amp; Hızlı Arama (Speed Dial) Entegrasyonu</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f8fafc; color: #0f172a; margin: 0; padding: 24px; }}
        .container {{ max-width: 1020px; margin: 0 auto; }}
        .card {{ background: #ffffff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 22px; margin-bottom: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.04); }}
        h1 {{ margin-top: 0; font-size: 24px; }}
        h2 {{ margin-top: 0; font-size: 18px; color: #1e293b; }}
        .btn {{ display: inline-block; background: #2563eb; color: #ffffff; text-decoration: none; padding: 12px 20px; border-radius: 8px; font-weight: 600; border: none; cursor: pointer; font-size: 15px; }}
        .btn:hover {{ background: #1d4ed8; }}
        .btn-sm {{ background: #475569; color: #fff; border: none; padding: 6px 12px; border-radius: 6px; cursor: pointer; font-size: 13px; }}
        .warning-box {{ background: #fffbeb; border: 2px solid #f59e0b; border-left: 8px solid #d97706; border-radius: 10px; padding: 16px 20px; margin-bottom: 20px; color: #78350f; line-height: 1.6; }}
        table {{ width: 100%; border-collapse: collapse; margin-top: 10px; font-size: 14px; }}
        th, td {{ text-align: left; padding: 10px; border-bottom: 1px solid #e2e8f0; }}
        th {{ background: #f1f5f9; font-weight: 600; }}
        code {{ background: #f1f5f9; padding: 2px 6px; border-radius: 4px; font-size: 13px; }}
        pre {{ background: #0f172a; color: #f8fafc; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 13px; }}
        .badge {{ padding: 3px 8px; border-radius: 999px; font-size: 12px; font-weight: 600; }}
        .badge-ok {{ background: #dcfce7; color: #166534; }}
        .badge-warn {{ background: #fef3c7; color: #92400e; }}
        .badge-err {{ background: #fee2e2; color: #991b1b; }}
        input[type=text] {{ padding: 8px 10px; border: 1px solid #cbd5e1; border-radius: 6px; font-size: 14px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Acme SaaS — Hipcall OAuth2 &amp; Hızlı Arama (Speed Dial)</h1>
        {routingBanner}

        <div class=""card"">
            <h2>1. OAuth2 ile Hipcall Hesabını Bağla ve Hızlı Arama (7001) Kur</h2>
            <p>Aşağıdaki butona tıkladığınızda Hipcall OAuth2 ekranına yönlendirilirsiniz (<code>scope: {WebUtility.HtmlEncode(options.Scopes)}</code>). Onay verdiğinizde uygulama otomatik olarak <code>POST /api/v3/speed-dials</code> uç noktasını çağırır ve <strong>{WebUtility.HtmlEncode(options.ExtensionNumber)} ({WebUtility.HtmlEncode(options.SpeedDialName)})</strong> hızlı aramasını kurar.</p>
            <p><a class=""btn"" href=""{WebUtility.HtmlEncode(authorizeUrl)}"">Hipcall ile Bağlan ve Hızlı Arama Kur</a></p>
            <hr style=""border:none; border-top:1px solid #e2e8f0; margin:18px 0;"" />
            <p style=""font-size:14px; color:#475569; margin-bottom:8px;""><strong>Alternatif (Manuel API Token ile Kurulum):</strong> OAuth uygulamanızda <code>communication_settings:write</code> kapsamı henüz aktif edilmediyse Hipcall API anahtarınızı girerek de <code>7001</code> hızlı aramasını hemen kurabilirsiniz:</p>
            <form method=""post"" action=""/manual-setup"" style=""display:flex; gap:8px; align-items:center;"">
                <input type=""text"" name=""access_token"" placeholder=""Hipcall API Token yapıştırın..."" style=""flex:1;"" required />
                <button type=""submit"" class=""btn-sm"">Token ile 7001 Kur</button>
            </form>
        </div>

        {(!string.IsNullOrWhiteSpace(state.AccessToken) ? $@"
        <div class=""card"">
            <h2>Hesaptaki Hızlı Aramalar (<code>GET /api/v3/speed-dials</code>)</h2>
            {speedDialsSb}
        </div>" : "")}

        <div class=""card"">
            <h2>2. Sipariş Numarası Eşleşmeleri (<code>POST /hizli-arama</code>)</h2>
            <p>Kurye <strong>{WebUtility.HtmlEncode(options.ExtensionNumber)}</strong> dahili hattını arayıp sipariş kodunu tuşladığında Hipcall <code>{WebUtility.HtmlEncode(options.WebServiceUrl)}</code> adresine Basic Auth (<code>{WebUtility.HtmlEncode(options.WebServiceUsername)}:{WebUtility.HtmlEncode(options.WebServicePassword)}</code>) ile POST isteği atar.</p>
            <table>
                <thead>
                    <tr>
                        <th>Tuşlanan Sipariş Kodu (<code>number</code>)</th>
                        <th>Dönecek Müşteri Telefonu (<code>destination</code>)</th>
                    </tr>
                </thead>
                <tbody>
                    {orderRowsSb}
                </tbody>
            </table>
            <form method=""post"" action=""/add-order"" style=""margin-top:14px; display:flex; gap:8px; align-items:center;"">
                <input type=""text"" name=""order_code"" placeholder=""Sipariş Kodu (örn: 5678)"" required />
                <input type=""text"" name=""phone"" placeholder=""Hedef Numara (örn: 905309998877)"" required />
                <button type=""submit"" class=""btn-sm"">Sipariş Ekle / Güncelle</button>
            </form>
            <h4 style=""margin-bottom:6px;"">cURL ile Hızlı Test Komutu:</h4>
            <pre>curl -X POST ""{WebUtility.HtmlEncode(options.WebServiceUrl)}"" \
  -H ""Authorization: Basic {curlBasicToken}"" \
  -H ""Content-Type: application/json"" \
  -d '{{""timestamp"":1791891730,""number"":""1234"",""uuid"":""e7850000-0000-0000-0000-0000000087e5"",""user_id"":null,""speed_dial_id"":19}}'</pre>
        </div>

        <div class=""card"">
            <h2>3. Gelen Hızlı Arama İstek Kayıtları (Canlı Log)</h2>
            {logsSb}
        </div>
    </div>
</body>
</html>";
}

static string RenderMessageHtml(string title, string bodyHtml, bool isError)
{
    var borderColor = isError ? "#dc2626" : "#16a34a";
    return $@"<!DOCTYPE html>
<html lang=""tr"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>{WebUtility.HtmlEncode(title)}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f8fafc; color: #0f172a; margin: 0; padding: 32px; }}
        .card {{ max-width: 820px; margin: 0 auto; background: #ffffff; border: 1px solid #e2e8f0; border-top: 6px solid {borderColor}; border-radius: 12px; padding: 24px; box-shadow: 0 2px 6px rgba(0,0,0,0.05); }}
        h1 {{ margin-top: 0; font-size: 22px; }}
        pre {{ background: #0f172a; color: #f8fafc; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 13px; }}
        code {{ background: #f1f5f9; padding: 2px 6px; border-radius: 4px; }}
        .btn {{ display: inline-block; margin-top: 16px; background: #2563eb; color: #ffffff; text-decoration: none; padding: 10px 18px; border-radius: 8px; font-weight: 600; }}
    </style>
</head>
<body>
    <div class=""card"">
        <h1>{WebUtility.HtmlEncode(title)}</h1>
        {bodyHtml}
        <p><a class=""btn"" href=""/"">Ana Sayfaya Dön</a></p>
    </div>
</body>
</html>";
}

// ============================================================================
// VERİ MODELLERİ VE DURUM SAKLAMA SINIFI
// ============================================================================
public sealed class HipcallOptions
{
    public string BaseUrl { get; set; } = "https://use.hipcall.com.tr";
    public string AuthorizationUrl { get; set; } = "https://use.hipcall.com.tr/oauth/authorize";
    public string TokenUrl { get; set; } = "https://use.hipcall.com.tr/oauth/token";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "https://your-tunnel.ngrok-free.dev/callback";
    public string WebServiceUrl { get; set; } = "https://your-tunnel.ngrok-free.dev/hizli-arama";
    public string State { get; set; } = "YOUR_CSRF_STATE";
    public string Scopes { get; set; } = "profile email offline_access communication_settings:write";
    public string ExtensionNumber { get; set; } = "7001";
    public string SpeedDialName { get; set; } = "Order status";
    public bool WebServiceAuth { get; set; } = true;
    public string WebServiceUsername { get; set; } = "hipcall";
    public string WebServicePassword { get; set; } = "s3cret-token";
    public string StateStorePath { get; set; } = "speed_dial_store.json";
}

public sealed class CreateSpeedDialRequest
{
    [JsonPropertyName("extension_number")]
    public string ExtensionNumber { get; set; } = "7001";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Order status";

    [JsonPropertyName("web_service_auth")]
    public bool WebServiceAuth { get; set; } = true;

    [JsonPropertyName("web_service_password")]
    public string WebServicePassword { get; set; } = "s3cret-token";

    [JsonPropertyName("web_service_url")]
    public string WebServiceUrl { get; set; } = string.Empty;

    [JsonPropertyName("web_service_username")]
    public string WebServiceUsername { get; set; } = "hipcall";
}

public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed class HipcallSpeedDialItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("extension_number")]
    public string ExtensionNumber { get; set; } = string.Empty;

    [JsonPropertyName("web_service_url")]
    public string WebServiceUrl { get; set; } = string.Empty;

    [JsonPropertyName("web_service_auth")]
    public bool WebServiceAuth { get; set; }

    [JsonPropertyName("web_service_username")]
    public string? WebServiceUsername { get; set; }
}

public sealed class SpeedDialLookupLog
{
    public DateTimeOffset ReceivedAt { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string CallUuid { get; set; } = string.Empty;
    public bool AuthValid { get; set; }
    public string? ReturnedDestination { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
}

public sealed class SpeedDialPersistentState
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? GrantedScope { get; set; }
    public string? SpeedDialId { get; set; }
    public string? SpeedDialName { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? SpeedDialState { get; set; }
    public string? WebServiceUrl { get; set; }
    public Dictionary<string, string> OrderMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1234"] = "905301234567",
        ["1001"] = "905321112233",
        ["2026"] = "905554443322"
    };
    public List<SpeedDialLookupLog> RecentLookups { get; set; } = new();
}

public sealed class SpeedDialStateStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private SpeedDialPersistentState _state;

    public SpeedDialStateStore(IOptions<HipcallOptions> optionsAccessor, IHostEnvironment env)
    {
        var configuredPath = optionsAccessor.Value.StateStorePath;
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(env.ContentRootPath, configuredPath);
        _state = LoadFromDisk();
    }

    public SpeedDialPersistentState GetState()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    public void SaveTokens(string accessToken, string? refreshToken, string? grantedScope)
    {
        lock (_lock)
        {
            _state.AccessToken = accessToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
                _state.RefreshToken = refreshToken;
            _state.GrantedScope = grantedScope;
            SaveToDisk();
        }
    }

    public void SaveSpeedDial(string id, string name, string extensionNumber, string state, string webServiceUrl)
    {
        lock (_lock)
        {
            _state.SpeedDialId = id;
            _state.SpeedDialName = name;
            _state.ExtensionNumber = extensionNumber;
            _state.SpeedDialState = state;
            _state.WebServiceUrl = webServiceUrl;
            SaveToDisk();
        }
    }

    public void UpdateSpeedDialState(string newState)
    {
        lock (_lock)
        {
            _state.SpeedDialState = newState;
            SaveToDisk();
        }
    }

    public void SetOrderMapping(string orderCode, string destinationPhone)
    {
        lock (_lock)
        {
            _state.OrderMappings[orderCode] = destinationPhone;
            SaveToDisk();
        }
    }

    public string? ResolveDestination(string orderCode)
    {
        lock (_lock)
        {
            return _state.OrderMappings.TryGetValue(orderCode, out var dest) ? dest : null;
        }
    }

    public void AddLookupLog(SpeedDialLookupLog log)
    {
        lock (_lock)
        {
            _state.RecentLookups.Insert(0, log);
            if (_state.RecentLookups.Count > 50)
            {
                _state.RecentLookups.RemoveRange(50, _state.RecentLookups.Count - 50);
            }
            SaveToDisk();
        }
    }

    private SpeedDialPersistentState LoadFromDisk()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<SpeedDialPersistentState>(json);
                if (loaded is not null)
                {
                    if (!loaded.OrderMappings.ContainsKey("1234"))
                    {
                        loaded.OrderMappings["1234"] = "905301234567";
                    }
                    return loaded;
                }
            }
        }
        catch
        {
        }
        return new SpeedDialPersistentState();
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
        }
    }
}

