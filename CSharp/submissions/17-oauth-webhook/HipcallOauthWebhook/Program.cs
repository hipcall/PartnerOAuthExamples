using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddSingleton<WebhookStateStore>();

var app = builder.Build();

// ============================================================================
// 1. ANA SAYFA (GET /)
// Senaryo A (OAuth2 Akışı) & Senaryo B (Manuel Token Akışı) Arayüzü
// ============================================================================
app.MapGet("/", async (
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var options = optionsAccessor.Value;
    var state = store.GetState();
    var authorizeUrl = BuildAuthorizationUrl(options, forceConsent: true);

    List<HipcallWebhookItemDto>? existingWebhooks = null;
    string? webhooksFetchError = null;

    // Yalnızca kullanıcı OAuth2 ile bağlandığında veya bir API token girdiğinde webhook listesini çek
    if (!string.IsNullOrWhiteSpace(state.AccessToken))
    {
        var client = httpClientFactory.CreateClient("HipcallApi");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);
            using var res = await client.SendAsync(req);
            var resBody = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(resBody);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    existingWebhooks = JsonSerializer.Deserialize<List<HipcallWebhookItemDto>>(dataEl.GetRawText());
                }
            }
            else
            {
                webhooksFetchError = $"HTTP {(int)res.StatusCode}: {resBody}";
            }
        }
        catch (Exception ex)
        {
            webhooksFetchError = ex.Message;
        }
    }

    var html = RenderDashboardHtml(options, state, authorizeUrl, existingWebhooks, webhooksFetchError);
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
// 2. OAUTH2 CALLBACK & OTOMATİK WEBHOOK KURULUMU (GET /callback)
// ============================================================================
app.MapGet("/callback", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
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
            RenderMessageHtml("OAuth2 Yetkilendirme Hatası", $"Hipcall hata döndürdü: <b>{WebUtility.HtmlEncode(error)}</b> - {WebUtility.HtmlEncode(errorDescription)}", isError: true),
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
            RenderMessageHtml("Geçersiz State (CSRF Koruması)", $"Beklenen state (<code>{WebUtility.HtmlEncode(options.State)}</code>) ile gelen state (<code>{WebUtility.HtmlEncode(returnedState)}</code>) eşleşmedi.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var client = httpClientFactory.CreateClient("HipcallApi");

    // Adım 2.1: Authorization Code -> Access Token değişimi (application/x-www-form-urlencoded)
    Console.WriteLine($"[OAUTH2] Authorization code alındı. Token uç noktasına istek gönderiliyor: {options.TokenUrl}");

    var tokenForm = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["client_id"] = options.ClientId,
        ["client_secret"] = options.ClientSecret,
        ["redirect_uri"] = options.RedirectUri,
        ["scope"] = options.Scopes // Kapsamları token isteğinde de açıkça iletiyoruz
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

    // Adım 2.1 bittiğinde token'ı diske kaydediyoruz
    store.SaveTokens(tokenData.AccessToken, tokenData.RefreshToken, "OAuth2");

    // Adım 2.2: Alınan Access Token ile otomatik olarak call.ended webhook'u kur
    var setupResult = await CreateWebhookAutomaticallyAsync(client, options, store, tokenData.AccessToken, tokenData.RefreshToken, "OAuth2", tokenData.Scope);
    return Results.Content(setupResult.Html, "text/html; charset=utf-8", statusCode: setupResult.StatusCode);
});

// ============================================================================
// SENARYO B: MANUEL ACCESS TOKEN İLE OTOMATİK WEBHOOK KURULUMU (POST /manual-setup)
// ============================================================================
app.MapPost("/manual-setup", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var manualToken = form["access_token"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(manualToken))
    {
        return Results.Content(
            RenderMessageHtml("Eksik Access Token", "Lütfen geçerli bir Hipcall OAuth2 Access Token veya Statik API Token girin.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var client = httpClientFactory.CreateClient("HipcallApi");
    var options = optionsAccessor.Value;

    Console.WriteLine("[MANUEL KURULUM] Manuel girilen Access/API Token ile otomatik webhook kurulumu başlatılıyor...");
    store.SaveTokens(manualToken, null, "ManualToken");
    var setupResult = await CreateWebhookAutomaticallyAsync(client, options, store, manualToken, refreshToken: null, "ManualToken", grantedScope: null);
    return Results.Content(setupResult.Html, "text/html; charset=utf-8", statusCode: setupResult.StatusCode);
});

// Manuel Secret veya Mevcut Webhook ID Güncelleme (İsteğe Bağlı Yardımcı Form)
app.MapPost("/save-manual-secret", async (HttpContext context, WebhookStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var webhookId = form["webhook_id"].ToString().Trim();
    var secret = form["secret"].ToString().Trim();
    var accessToken = form["access_token"].ToString().Trim();

    store.UpdateCredentials(webhookId, secret, accessToken);
    Console.WriteLine($"[YAPILANDIRMA] Webhook bilgileri manuel güncellendi. ID: {webhookId}");
    return Results.Redirect("/");
});

// ============================================================================
// 3. WEBHOOK DİNLEYİCİ VE STANDARD WEBHOOKS DOĞRULAMASI (POST /evet=12345)
// ============================================================================
app.MapPost("/evet=12345", async (HttpContext context, WebhookStateStore store) =>
{
    // Çok Önemli (.NET 8 İpucu): İstek gövdesini imza doğrulaması için bayt dizisi
    // (raw body) olarak bozmadan okumak adına EnableBuffering() kullanıyoruz.
    context.Request.EnableBuffering();

    byte[] rawBodyBytes;
    using (var ms = new MemoryStream())
    {
        await context.Request.Body.CopyToAsync(ms);
        rawBodyBytes = ms.ToArray();
    }
    context.Request.Body.Position = 0;

    var rawBody = Encoding.UTF8.GetString(rawBodyBytes);

    // Standard Webhooks HTTP Başlıklarını (Headers) Oku
    var webhookId = context.Request.Headers["webhook-id"].ToString();
    var webhookTimestamp = context.Request.Headers["webhook-timestamp"].ToString();
    var webhookSignature = context.Request.Headers["webhook-signature"].ToString();

    Console.WriteLine();
    Console.WriteLine("================================================================================");
    Console.WriteLine($"[WEBHOOK GELDİ] Zaman: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"  webhook-id        : {webhookId}");
    Console.WriteLine($"  webhook-timestamp : {webhookTimestamp}");
    Console.WriteLine($"  webhook-signature : {webhookSignature}");
    Console.WriteLine("--------------------------------------------------------------------------------");

    var state = store.GetState();
    if (string.IsNullOrWhiteSpace(state.WebhookSecret))
    {
        Console.WriteLine("[İMZA HATASI] Sistemde kayıtlı 'whsec_...' imza anahtarı bulunamadı! Önce OAuth2 veya Manuel Token ile webhook kurun.");
        return Results.Json(new { error = "webhook_secret_not_configured" }, statusCode: StatusCodes.Status400BadRequest);
    }

    // Standard Webhooks (https://www.standardwebhooks.com/) İmza Doğrulaması
    var isValid = StandardWebhookVerifier.Verify(
        rawBody: rawBody,
        webhookId: webhookId,
        webhookTimestamp: webhookTimestamp,
        webhookSignatureHeader: webhookSignature,
        primarySecret: state.WebhookSecret,
        previousSecret: state.PreviousWebhookSecret,
        out var verificationDetail);

    if (!isValid)
    {
        Console.WriteLine($"[İMZA DOĞRULAMA BAŞARISIZ] {verificationDetail}");
        Console.WriteLine("================================================================================");
        store.AddEventLog(new WebhookEventLog
        {
            ReceivedAt = DateTimeOffset.Now,
            WebhookIdHeader = webhookId,
            EventType = "SIGNATURE_INVALID",
            Verified = false,
            Summary = verificationDetail,
            RawPayload = rawBody
        });
        return Results.Json(new { error = "invalid_signature", detail = verificationDetail }, statusCode: StatusCodes.Status401Unauthorized);
    }

    Console.WriteLine($"[İMZA DOĞRULANDI] Standard Webhooks HMAC-SHA256 imzası geçerli ({verificationDetail}).");

    // Gövdeyi JSON olarak ayrıştır
    try
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        var eventNode = root;
        if (root.TryGetProperty("data", out var outerData) &&
            outerData.ValueKind == JsonValueKind.Object &&
            outerData.TryGetProperty("payload", out var innerPayload) &&
            innerPayload.ValueKind == JsonValueKind.Object)
        {
            eventNode = innerPayload;
        }

        var eventType = GetStringProperty(eventNode, "type") ?? "unknown";
        var eventId = GetStringProperty(eventNode, "id") ?? webhookId;
        var eventTimestamp = GetStringProperty(eventNode, "timestamp") ?? webhookTimestamp;

        if (string.Equals(eventType, "webhook.test", StringComparison.OrdinalIgnoreCase))
        {
            // Kural: Gelen olay "type": "webhook.test" ise konsola "Test webhook olayı başarıyla alındı" yazdır.
            Console.WriteLine("Test webhook olayı başarıyla alındı");

            string? endpointId = null;
            string? message = null;
            if (eventNode.TryGetProperty("data", out var testData) && testData.ValueKind == JsonValueKind.Object)
            {
                endpointId = GetStringProperty(testData, "endpoint_id");
                message = GetStringProperty(testData, "message");
            }

            Console.WriteLine($"  Olay Kimliği (id) : {eventId}");
            Console.WriteLine($"  Endpoint ID       : {endpointId ?? "-"}");
            Console.WriteLine($"  Mesaj             : {message ?? "Hipcall webhook test"}");
            Console.WriteLine("================================================================================");

            store.AddEventLog(new WebhookEventLog
            {
                ReceivedAt = DateTimeOffset.Now,
                WebhookIdHeader = webhookId,
                EventType = "webhook.test",
                Verified = true,
                Summary = $"Test webhook olayı başarıyla alındı (Mesaj: {message ?? "Hipcall webhook test"})",
                RawPayload = rawBody
            });
        }
        else if (string.Equals(eventType, "call.ended", StringComparison.OrdinalIgnoreCase))
        {
            // Kural: Gelen olay "type": "call.ended" ise PDF'teki 8. Endpoint payload yapısına uygun olarak;
            // caller_number, callee_number, call_duration, hangup_by ve uuid alanlarını ayrıştırıp Console.WriteLine ile detaylıca logla.
            string callerNumber = "-";
            string calleeNumber = "-";
            int callDuration = 0;
            string hangupBy = "-";
            string uuid = "-";
            string direction = "-";
            string startedAt = "-";
            string endedAt = "-";
            bool missingCall = false;

            if (eventNode.TryGetProperty("data", out var callData) && callData.ValueKind == JsonValueKind.Object)
            {
                callerNumber = GetStringProperty(callData, "caller_number") ?? "-";
                calleeNumber = GetStringProperty(callData, "callee_number") ?? "-";
                callDuration = GetIntProperty(callData, "call_duration");
                hangupBy = GetStringProperty(callData, "hangup_by") ?? ExtractHangupByFromCallFlow(callData) ?? "-";
                uuid = GetStringProperty(callData, "uuid") ?? "-";
                direction = GetStringProperty(callData, "direction") ?? "-";
                startedAt = GetStringProperty(callData, "started_at") ?? "-";
                endedAt = GetStringProperty(callData, "ended_at") ?? "-";
                missingCall = GetBoolProperty(callData, "missing_call");
            }

            Console.WriteLine("[CALL.ENDED - ARAMA SONLANDI OLAYI DETAYLARI]");
            Console.WriteLine($"  Event ID          : {eventId}");
            Console.WriteLine($"  Event Timestamp   : {eventTimestamp}");
            Console.WriteLine($"  UUID (uuid)       : {uuid}");
            Console.WriteLine($"  Arayan Numara     : {callerNumber} (caller_number)");
            Console.WriteLine($"  Aranan Numara     : {calleeNumber} (callee_number)");
            Console.WriteLine($"  Görüşme Süresi    : {callDuration} sn (call_duration)");
            Console.WriteLine($"  Kimin Kapattığı   : {hangupBy} (hangup_by)");
            Console.WriteLine($"  Arama Yönü        : {direction}");
            Console.WriteLine($"  Başlangıç / Bitiş : {startedAt} -> {endedAt}");
            Console.WriteLine($"  Cevapsız Çağrı mı : {(missingCall ? "Evet" : "Hayır")}");
            Console.WriteLine("================================================================================");

            store.AddEventLog(new WebhookEventLog
            {
                ReceivedAt = DateTimeOffset.Now,
                WebhookIdHeader = webhookId,
                EventType = "call.ended",
                Verified = true,
                Summary = $"UUID: {uuid} | {callerNumber} -> {calleeNumber} | Süre: {callDuration} sn | Kapatan: {hangupBy}",
                RawPayload = rawBody
            });
        }
        else
        {
            Console.WriteLine($"[BİLGİ] Farklı tipte bir webhook olayı alındı: {eventType}");
            Console.WriteLine(rawBody);
            Console.WriteLine("================================================================================");

            store.AddEventLog(new WebhookEventLog
            {
                ReceivedAt = DateTimeOffset.Now,
                WebhookIdHeader = webhookId,
                EventType = eventType,
                Verified = true,
                Summary = $"Olay tipi: {eventType} (ID: {eventId})",
                RawPayload = rawBody
            });
        }
    }
    catch (JsonException ex)
    {
        Console.WriteLine($"[JSON AYRIŞTIRMA HATASI] {ex.Message}");
        Console.WriteLine(rawBody);
        Console.WriteLine("================================================================================");
    }

    // İstek işlendiğinde Hipcall'a hemen 200 OK dön
    return Results.Ok(new { status = "ok", received = true });
});

// ============================================================================
// 4. TEST VE YÖNETİM UÇ NOKTASI (/trigger-test)
// ============================================================================
app.MapMethods("/trigger-test", new[] { "GET", "POST" }, async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var state = store.GetState();
    var webhookId = state.WebhookId;
    var accessToken = state.AccessToken;

    if (context.Request.HasFormContentType)
    {
        var form = await context.Request.ReadFormAsync();
        if (!string.IsNullOrWhiteSpace(form["webhook_id"]))
            webhookId = form["webhook_id"].ToString().Trim();
        if (!string.IsNullOrWhiteSpace(form["access_token"]))
            accessToken = form["access_token"].ToString().Trim();
    }
    else
    {
        if (!string.IsNullOrWhiteSpace(context.Request.Query["webhook_id"]))
            webhookId = context.Request.Query["webhook_id"].ToString().Trim();
    }

    if (string.IsNullOrWhiteSpace(webhookId) || string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Content(
            RenderMessageHtml(
                "Test Gönderilemedi",
                "Kayıtlı <code>WebhookId</code> veya <code>AccessToken</code> bulunamadı. Lütfen önce ana sayfadan OAuth2 veya Manuel Token ile webhook kurulumunu tamamlayın.",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var options = optionsAccessor.Value;
    var client = httpClientFactory.CreateClient("HipcallApi");
    var testEndpoint = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{webhookId}/test";

    Console.WriteLine($"[TEST TETİKLENDİ] POST {testEndpoint} çağrılıyor...");

    using var request = new HttpRequestMessage(HttpMethod.Post, testEndpoint)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8)
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await client.SendAsync(request);
    var responseJson = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"[TEST YANITI] HTTP {(int)response.StatusCode}: {responseJson}");

    if (!response.IsSuccessStatusCode)
    {
        var hint = (int)response.StatusCode switch
        {
            409 => "Not: Aynı anda tek bir test isteği yolda olabilir (409 Conflict - webhook_test_pending). Birkaç saniye bekleyip tekrar deneyin.",
            422 or 400 => "Not: Webhook 'passive' veya 'disabled' durumda olabilir (webhook_not_active). Yalnızca 'active' durumdaki webhook'lara test olayı gönderilebilir.",
            _ => "Access token yetkilerini (webhooks:write ve settings::developer) kontrol edin."
        };

        return Results.Content(
            RenderMessageHtml(
                $"Test Olayı Gönderilemedi (HTTP {(int)response.StatusCode})",
                $"<p>{WebUtility.HtmlEncode(hint)}</p><pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: (int)response.StatusCode);
    }

    return Results.Content(
        RenderMessageHtml(
            "Test Olayı Kuyruğa Alındı (webhook.test)",
            $@"<p>Hipcall API <code>POST /api/v3/webhooks/{WebUtility.HtmlEncode(webhookId)}/test</code> çağrısına <b>HTTP {(int)response.StatusCode}</b> yanıtı verdi.</p>
               <p>Hipcall arka planda <code>{WebUtility.HtmlEncode(options.WebhookReceiverUrl)}</code> adresine imzalı <code>webhook.test</code> paketini iletiyor. Konsol ekranını ve ana sayfadaki olay tablosunu kontrol edebilirsiniz.</p>
               <h4>Hipcall API Yanıtı:</h4>
               <pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
            isError: false),
        "text/html; charset=utf-8");
});

// ============================================================================
// EK YÖNETİM UÇ NOKTASI: İMZA ANAHTARINI YENİLEME (POST /rotate-secret)
// ============================================================================
app.MapPost("/rotate-secret", async (
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var state = store.GetState();
    if (string.IsNullOrWhiteSpace(state.WebhookId) || string.IsNullOrWhiteSpace(state.AccessToken))
    {
        return Results.Content(
            RenderMessageHtml("Anahtar Yenilenemedi", "Kayıtlı WebhookId veya AccessToken bulunamadı.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    var options = optionsAccessor.Value;
    var client = httpClientFactory.CreateClient("HipcallApi");
    var rotateUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{state.WebhookId}/rotate-secret";

    using var request = new HttpRequestMessage(HttpMethod.Post, rotateUrl)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8)
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);

    using var response = await client.SendAsync(request);
    var responseJson = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        return Results.Content(
            RenderMessageHtml(
                $"Rotate Secret Başarısız (HTTP {(int)response.StatusCode})",
                $"<pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: (int)response.StatusCode);
    }

    using var doc = JsonDocument.Parse(responseJson);
    if (doc.RootElement.TryGetProperty("data", out var dataNode))
    {
        var newSecret = GetStringProperty(dataNode, "secret");
        if (!string.IsNullOrWhiteSpace(newSecret))
        {
            store.RotateSecret(newSecret);
            Console.WriteLine($"[ROTATE SECRET] Webhook imza anahtarı yenilendi. 24 saatlik çift imza (zero-downtime) koruması aktif.");
        }
    }

    return Results.Content(
        RenderMessageHtml(
            "İmza Anahtarı Başarıyla Yenilendi (Zero-Downtime Rotate Secret)",
            $@"<p>Yeni <code>whsec_...</code> anahtarı kaydedildi ve eski anahtar 24 saatlik geçiş süresi için yedek olarak tutuldu.</p>
               <pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
            isError: false),
        "text/html; charset=utf-8");
});

// ============================================================================
// WEBHOOK SİLME UÇ NOKTASI: DELETE /api/v3/webhooks/{id}
// ============================================================================
app.MapPost("/delete-current-webhook", async (
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var state = store.GetState();
    var options = optionsAccessor.Value;
    var client = httpClientFactory.CreateClient("HipcallApi");

    if (!string.IsNullOrWhiteSpace(state.WebhookId))
    {
        var tokenToUse = !string.IsNullOrWhiteSpace(state.AccessToken) ? state.AccessToken : null;
        if (!string.IsNullOrWhiteSpace(tokenToUse))
        {
            var deleteUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{state.WebhookId}";
            Console.WriteLine($"[WEBHOOK SİLME] DELETE {deleteUrl} çağrılıyor...");
            using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenToUse);
            using var response = await client.SendAsync(request);
            Console.WriteLine($"[WEBHOOK SİLME YANITI] HTTP {(int)response.StatusCode}");
        }
    }

    store.ClearWebhook();
    return Results.Redirect("/");
});

// ============================================================================
// WEBHOOK DURUM DEĞİŞTİRME UÇ NOKTASI: PATCH /api/v3/webhooks/{id} (active <-> passive)
// ============================================================================
app.MapPost("/toggle-webhook-state", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var webhookId = form["webhook_id"].ToString().Trim();
    var targetState = form["target_state"].ToString().Trim().ToLowerInvariant();

    var state = store.GetState();
    var accessToken = state.AccessToken;

    if (string.IsNullOrWhiteSpace(webhookId) || (targetState != "active" && targetState != "passive"))
    {
        return Results.Content(
            RenderMessageHtml("Geçersiz İstek", "Webhook ID veya hedef durum ('active' / 'passive') eksik ya da hatalı.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Results.Content(
            RenderMessageHtml("Yetkilendirme Eksik", "İşlemi yapmak için geçerli bir Access Token bulunamadı. Lütfen önce OAuth2 ile bağlanın veya token girin.", isError: true),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var options = optionsAccessor.Value;
    var client = httpClientFactory.CreateClient("HipcallApi");
    var patchUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{webhookId}";

    var payload = JsonSerializer.Serialize(new { state = targetState });
    using var request = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await client.SendAsync(request);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"[WEBHOOK DURUM DEĞİŞTİRME HATA] HTTP {(int)response.StatusCode}: {responseBody}");
        return Results.Content(
            RenderMessageHtml(
                $"Webhook Durumu Değiştirilemedi (HTTP {(int)response.StatusCode})",
                $"<p>Hedef durum: <code>{targetState}</code></p><pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseBody))}</pre>",
                isError: true),
            "text/html; charset=utf-8",
            statusCode: (int)response.StatusCode);
    }

    Console.WriteLine($"[WEBHOOK DURUM DEĞİŞTİ] Webhook {webhookId} durumu '{targetState}' olarak güncellendi.");

    if (string.Equals(state.WebhookId, webhookId, StringComparison.OrdinalIgnoreCase))
    {
        store.UpdateWebhookState(targetState);
    }

    return Results.Redirect("/");
});

// ============================================================================
// LİSTEDEN WEBHOOK SEÇME UÇ NOKTASI (Takip edilen webhook'u güncelle)
// ============================================================================
app.MapPost("/select-webhook", async (HttpContext context, WebhookStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var webhookId = form["webhook_id"].ToString().Trim();
    var name = form["webhook_name"].ToString().Trim();
    var url = form["webhook_url"].ToString().Trim();
    var state = form["webhook_state"].ToString().Trim();

    if (!string.IsNullOrWhiteSpace(webhookId))
    {
        store.SelectWebhook(webhookId, name, url, state);
        Console.WriteLine($"[YAPILANDIRMA] Aktif takip edilen webhook güncellendi: {webhookId} ({name})");
    }

    return Results.Redirect("/");
});

// ============================================================================
// WEBHOOK SİLME UÇ NOKTASI: DELETE /api/v3/webhooks/{id}
// ============================================================================
app.MapPost("/delete-webhook", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<HipcallOptions> optionsAccessor,
    WebhookStateStore store) =>
{
    var form = await context.Request.ReadFormAsync();
    var webhookId = form["webhook_id"].ToString().Trim();

    var state = store.GetState();
    var accessToken = state.AccessToken;

    if (string.IsNullOrWhiteSpace(webhookId))
    {
        return Results.Redirect("/");
    }

    if (!string.IsNullOrWhiteSpace(accessToken))
    {
        var options = optionsAccessor.Value;
        var client = httpClientFactory.CreateClient("HipcallApi");
        var deleteUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{webhookId}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        Console.WriteLine($"[WEBHOOK SİLİNDİ] ID: {webhookId}, HTTP {(int)response.StatusCode}");
    }

    if (string.Equals(state.WebhookId, webhookId, StringComparison.OrdinalIgnoreCase))
    {
        store.ClearWebhook();
    }

    return Results.Redirect("/");
});

app.Run();

// ============================================================================
// YARDIMCI METOTLAR VE İŞ MANTIĞI
// ============================================================================
static string BuildAuthorizationUrl(HipcallOptions options, bool forceConsent = true)
{
    // OAuth2 scopes: '+' ile birleştiriyoruz ve iki nokta üst üste (':') karakterini
    // RFC gereği bozmadan koruyoruz (webhooks:write, communication:read).
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
        // prompt=consent: Kullanıcının daha önce verdiği eski izinleri atlayıp
        // yeni eklenen 'webhooks:write' yetkisini onaylamasını zorunlu kılar.
        queryParams.Add("prompt=consent");
    }

    return $"{options.AuthorizationUrl.TrimEnd('/')}?{string.Join("&", queryParams)}";
}

static async Task<(string Html, int StatusCode)> CreateWebhookAutomaticallyAsync(
    HttpClient client,
    HipcallOptions options,
    WebhookStateStore store,
    string accessToken,
    string? refreshToken,
    string authFlowSource,
    string? grantedScope)
{
    var createWebhookUrl = $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks";
    var payload = new CreateWebhookRequest
    {
        Name = options.DefaultWebhookName,
        Url = options.WebhookReceiverUrl,
        EventTypes = options.DefaultEventTypes.Length > 0 ? options.DefaultEventTypes.Distinct().ToArray() : new[] { "call.ended" }
    };

    var payloadJson = JsonSerializer.Serialize(payload);
    Console.WriteLine($"[WEBHOOK KURULUM] POST {createWebhookUrl} adresine istek atılıyor...");
    Console.WriteLine($"[WEBHOOK KURULUM] İstek Gövdesi: {payloadJson}");

    using var request = new HttpRequestMessage(HttpMethod.Post, createWebhookUrl)
    {
        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var response = await client.SendAsync(request);
    var responseJson = await response.Content.ReadAsStringAsync();

    // Limit Dolumu Kontrolü (HTTP 422 - webhook_limit_reached) ve Otomatik Temizleme Denemesi
    if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
        responseJson.Contains("webhook_limit_reached", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[WEBHOOK LİMİTİ DOLDU] Hesap webhook limitine ulaştı. Eski webhook temizlenip yeniden deneniyor...");

        bool autoCleaned = false;

        // 1. OAuth token ile listelenebilen eski webhook'lar varsa sil
        try
        {
            using var listReq = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks");
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var listResp = await client.SendAsync(listReq);
            if (listResp.IsSuccessStatusCode)
            {
                var listJson = await listResp.Content.ReadAsStringAsync();
                using var listDoc = JsonDocument.Parse(listJson);
                if (listDoc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        var oldId = GetStringProperty(item, "id");
                        if (!string.IsNullOrWhiteSpace(oldId))
                        {
                            Console.WriteLine($"[TEMİZLİK] Eski webhook siliniyor (OAuth Token): {oldId}");
                            using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{oldId}");
                            delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                            using var delResp = await client.SendAsync(delReq);
                            if (delResp.IsSuccessStatusCode)
                            {
                                autoCleaned = true;
                                Console.WriteLine($"[TEMİZLİK BAŞARILI] Eski webhook silindi: {oldId}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEMİZLİK UYARI] OAuth token ile silme denenirken hata: {ex.Message}");
        }

        // 2. Eğer OAuth token ile bulunamadıysa, daha önce store'a kaydedilmiş eski bir WebhookId var mı bak
        if (!autoCleaned)
        {
            var savedState = store.GetState();
            if (!string.IsNullOrWhiteSpace(savedState.WebhookId))
            {
                var tokenToUse = !string.IsNullOrWhiteSpace(savedState.AccessToken) ? savedState.AccessToken : accessToken;
                try
                {
                    Console.WriteLine($"[TEMİZLİK] Kayıtlı eski webhook ID siliniyor ({savedState.WebhookId})...");
                    using var delSavedReq = new HttpRequestMessage(HttpMethod.Delete, $"{options.BaseUrl.TrimEnd('/')}/api/v3/webhooks/{savedState.WebhookId}");
                    delSavedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenToUse);
                    using var delSavedResp = await client.SendAsync(delSavedReq);
                    if (delSavedResp.IsSuccessStatusCode)
                    {
                        autoCleaned = true;
                        Console.WriteLine($"[TEMİZLİK BAŞARILI] Kayıtlı eski webhook silindi: {savedState.WebhookId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TEMİZLİK UYARI] Kayıtlı token ile silme denenirken hata: {ex.Message}");
                }
            }
        }

        // 3. Eğer eski webhook silindiyse, POST /api/v3/webhooks çağrısını anında tekrar dene!
        if (autoCleaned)
        {
            Console.WriteLine("[OTOMATİK TEKRAR] Eski webhook temizlendi! POST /api/v3/webhooks çağrısı tekrar yapılıyor...");
            using var retryReq = new HttpRequestMessage(HttpMethod.Post, createWebhookUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var retryResp = await client.SendAsync(retryReq);
            var retryJson = await retryResp.Content.ReadAsStringAsync();

            if (retryResp.IsSuccessStatusCode)
            {
                response = retryResp;
                responseJson = retryJson;
            }
            else
            {
                responseJson = retryJson;
            }
        }
    }

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"[WEBHOOK KURULUM HATA] HTTP {(int)response.StatusCode}: {responseJson}");

        // Scope eksikliği hatası (HTTP 403 Forbidden - insufficient_scope / webhooks:write) özel teşhis ekranı:
        var isScopeError = response.StatusCode == HttpStatusCode.Forbidden ||
                           responseJson.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase) ||
                           responseJson.Contains("webhooks:write", StringComparison.OrdinalIgnoreCase);

        var isLimitError = response.StatusCode == HttpStatusCode.UnprocessableEntity &&
                           responseJson.Contains("webhook_limit_reached", StringComparison.OrdinalIgnoreCase);

        string htmlError;
        if (isScopeError)
        {
            var reauthorizeUrl = BuildAuthorizationUrl(options, forceConsent: true);
            htmlError = RenderScopeErrorHtml(options, grantedScope, reauthorizeUrl, responseJson);
        }
        else if (isLimitError)
        {
            htmlError = RenderLimitReachedHtml(options, responseJson, store.GetState());
        }
        else
        {
            htmlError = RenderMessageHtml(
                $"Webhook Oluşturulamadı (HTTP {(int)response.StatusCode})",
                $@"<p>Access token alındı ancak <code>POST /api/v3/webhooks</code> çağrısı başarısız oldu.</p>
                   <p><b>Olası Nedenler (PDF Dokümantasyonu):</b></p>
                   <ul>
                     <li>Kullanıcının <code>settings::developer</code> yetkisi olmayabilir.</li>
                     <li>Hesap webhook limitine ulaşmış (<code>webhook_limit_reached</code>) veya paket özelliği desteklemiyor (<code>api_feature_missing</code>) olabilir.</li>
                   </ul>
                   <h4>Hipcall API Hata Yanıtı:</h4>
                   <pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
                isError: true);
        }

        return (htmlError, (int)response.StatusCode);
    }

    string? webhookId = null;
    string? webhookSecret = null;
    string? webhookState = null;

    using (var doc = JsonDocument.Parse(responseJson))
    {
        if (doc.RootElement.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
        {
            webhookId = GetStringProperty(dataNode, "id");
            webhookSecret = GetStringProperty(dataNode, "secret");
            webhookState = GetStringProperty(dataNode, "state");
        }
    }

    if (string.IsNullOrWhiteSpace(webhookId) || string.IsNullOrWhiteSpace(webhookSecret))
    {
        return (RenderMessageHtml(
            "Eksik Webhook Yanıtı",
            $"Yanıtta <code>data.id</code> veya <code>data.secret</code> bulunamadı:<pre>{WebUtility.HtmlEncode(FormatJsonSafe(responseJson))}</pre>",
            isError: true), StatusCodes.Status502BadGateway);
    }

    // Kritik Kural: Yanıtta dönen data.secret (whsec_...) ve data.id değerlerini güvenli şekilde sakla
    store.SaveWebhookSetup(new PersistedWebhookState
    {
        WebhookId = webhookId,
        WebhookSecret = webhookSecret,
        WebhookName = payload.Name,
        WebhookUrl = payload.Url,
        WebhookState = webhookState ?? "active",
        EventTypes = payload.EventTypes,
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        AuthFlowSource = authFlowSource,
        ConfiguredAt = DateTimeOffset.Now
    });

    Console.WriteLine("================================================================================");
    Console.WriteLine("[WEBHOOK KURULUMU BAŞARILI]");
    Console.WriteLine($"  Kaynak Akış       : {authFlowSource}");
    Console.WriteLine($"  Webhook ID        : {webhookId}");
    Console.WriteLine($"  Webhook Secret    : {webhookSecret}");
    Console.WriteLine($"  Dinleyici URL     : {payload.Url}");
    Console.WriteLine($"  Abone Olaylar     : {string.Join(", ", payload.EventTypes)}");
    Console.WriteLine("================================================================================");

    var successHtml = RenderSetupSuccessHtml(webhookId, webhookSecret, payload, authFlowSource, responseJson);
    return (successHtml, StatusCodes.Status200OK);
}

static string? GetStringProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind != JsonValueKind.Null &&
        prop.ValueKind != JsonValueKind.Undefined)
    {
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }
    return null;
}

static int GetIntProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop))
    {
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            return val;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
    }
    return 0;
}

static bool GetBoolProperty(JsonElement element, string propertyName)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop))
    {
        if (prop.ValueKind == JsonValueKind.True) return true;
        if (prop.ValueKind == JsonValueKind.False) return false;
    }
    return false;
}

static string? ExtractHangupByFromCallFlow(JsonElement callData)
{
    if (callData.TryGetProperty("call_flow", out var flowArray) && flowArray.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in flowArray.EnumerateArray())
        {
            var action = GetStringProperty(item, "action");
            if (string.Equals(action, "hangup", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.Object)
            {
                var hangupBy = GetStringProperty(detail, "hangup_by");
                if (!string.IsNullOrWhiteSpace(hangupBy))
                    return hangupBy;
            }
        }
    }
    return null;
}

static string FormatJsonSafe(string rawJson)
{
    if (string.IsNullOrWhiteSpace(rawJson)) return "(Boş Yanıt)";
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

// ============================================================================
// HTML ŞABLONLARI (Arayüz, Kurulum Başarı Ekranı ve Hata Mesajları)
// ============================================================================
static string RenderScopeErrorHtml(HipcallOptions options, string? grantedScope, string reauthorizeUrl, string rawApiResponse)
{
    var scopeDisplay = string.IsNullOrWhiteSpace(grantedScope)
        ? "<span style='color:#fbbf24;'>Hipcall token yanıtında scope dönmedi (veya yalnızca temel profil izinleri verildi).</span>"
        : $"<code>{WebUtility.HtmlEncode(grantedScope)}</code>";

    return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='utf-8' />
    <title>Yetki Eksikliği (HTTP 403 - webhooks:write) - Acme SaaS</title>
    <style>
        body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 32px; line-height: 1.6; }}
        .container {{ max-width: 900px; margin: 0 auto; background: #1e293b; border: 1px solid #ef4444; border-left: 6px solid #ef4444; border-radius: 10px; padding: 28px; box-shadow: 0 4px 14px rgba(0,0,0,0.3); }}
        h1 {{ margin-top: 0; color: #f87171; font-size: 22px; }}
        h2 {{ font-size: 16px; color: #f1f5f9; margin-top: 20px; }}
        code {{ background: #0f172a; color: #38bdf8; padding: 2px 7px; border-radius: 4px; font-size: 13px; word-break: break-all; }}
        pre {{ background: #0f172a; color: #cbd5e1; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 12px; border: 1px solid #334155; }}
        .btn {{ display: inline-block; padding: 11px 18px; border-radius: 7px; font-weight: 600; text-decoration: none; border: none; cursor: pointer; font-size: 14px; }}
        .btn-primary {{ background: #3b82f6; color: #fff; }}
        .btn-green {{ background: #16a34a; color: #fff; }}
        .btn-secondary {{ background: #475569; color: #fff; }}
        .step-box {{ background: #0f172a; border: 1px solid #334155; border-radius: 8px; padding: 16px; margin: 16px 0; }}
        input[type='text'] {{ width: 100%; box-sizing: border-box; padding: 10px 12px; border-radius: 6px; border: 1px solid #475569; background: #0f172a; color: #f8fafc; font-family: monospace; margin: 8px 0 12px 0; }}
        label {{ font-size: 13px; color: #cbd5e1; font-weight: 600; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>Webhook Oluşturulamadı: <code>webhooks:write</code> İzni Eksik (HTTP 403)</h1>
        <p>Hipcall OAuth2 sunucusu ile bağlantı kuruldu ve Access Token alındı; ancak bu token üzerinde webhook oluşturmak için zorunlu olan <b><code>webhooks:write</code></b> yetkisi bulunmuyor.</p>

        <div class='step-box'>
            <b>Hipcall Tarafından Bu Token'a Tanınan Yetkiler (Granted Scopes):</b><br/>
            <div style='margin-top:6px;'>{scopeDisplay}</div>
        </div>

        <h2>Bu Durum Nasıl Düzeltilir?</h2>

        <div class='step-box'>
            <h3 style='margin-top:0;color:#38bdf8;'>Çözüm 1: Hipcall Geliştirici Portalında Uygulama İzinlerini Kontrol Edin</h3>
            <p>Eğer Client ID (<code>{WebUtility.HtmlEncode(options.ClientId)}</code>) oluşturulurken <code>webhooks:write</code> izni eklenmediyse, Hipcall OAuth sunucusu bu yetkiyi token'a dahil etmez:</p>
            <ol>
                <li>Hipcall yönetim panelinde (<code>https://use.hipcall.com.tr</code>) <b>Ayarlar &gt; Geliştirici &gt; OAuth Uygulamaları</b> sayfasına gidin.</li>
                <li>Uygulamanızı düzenleyin ve izinler listesinde <b><code>webhooks:write</code></b> ile <b><code>communication:read</code></b> kutucuklarını işaretleyip kaydedin.</li>
                <li>Ardından aşağıdaki butona tıklayarak yeni yetkilerle tekrar onay verin:</li>
            </ol>
            <div style='margin-top:12px;'>
                <a href='{reauthorizeUrl}' class='btn btn-primary'>Hipcall ile Yeniden Yetkilendir (Consent Prompt)</a>
            </div>
        </div>

        <div class='step-box' style='border-left: 4px solid #22c55e;'>
            <h3 style='margin-top:0;color:#4ade80;'>Çözüm 2: Statik API Token ile Anında Kurun (Scope Sınırı Yoktur)</h3>
            <p>PDF Dokümantasyonu (Sayfa 1): <i>'Statik API Token kullanılıyorsa scope gerekmez.'</i><br/>
            Hipcall panelinizden aldığınız statik API token'ı buraya yapıştırarak webhook'u saniyeler içinde oluşturabilirsiniz:</p>
            <form method='post' action='/manual-setup'>
                <label for='access_token'>Hipcall Statik API Token (veya Yetkili Access Token):</label>
                <input type='text' id='access_token' name='access_token' placeholder='Örn: API anahtarı veya yetkili Bearer token' required />
                <button type='submit' class='btn btn-green'>Statik Token ile Webhook Kur</button>
            </form>
        </div>

        <h3>Hipcall API Hata Yanıtı:</h3>
        <pre>{WebUtility.HtmlEncode(FormatJsonSafe(rawApiResponse))}</pre>

        <div style='margin-top:20px;'>
            <a href='/' class='btn btn-secondary'>Ana Sayfaya Dön</a>
        </div>
    </div>
</body>
</html>";
}

static string RenderLimitReachedHtml(HipcallOptions options, string rawApiResponse, PersistedWebhookState savedState)
{
    var hasSavedId = !string.IsNullOrWhiteSpace(savedState.WebhookId);
    var savedIdHtml = hasSavedId
        ? $@"<div class='step-box' style='border-left: 4px solid #ef4444;'>
                <h3 style='margin-top:0;color:#f87171;'>Sistemde Kayıtlı Eski Webhook Bulundu</h3>
                <p>Önceki denemede oluşturulan Webhook ID: <code>{WebUtility.HtmlEncode(savedState.WebhookId)}</code></p>
                <form method='post' action='/delete-current-webhook'>
                    <button type='submit' class='btn' style='background:#dc2626;color:#fff;'>Kayıtlı Eski Webhook'u Sil (DELETE)</button>
                </form>
             </div>"
        : "";

    return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='utf-8' />
    <title>Webhook Limiti Doldu (HTTP 422) - Acme SaaS</title>
    <style>
        body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 32px; line-height: 1.6; }}
        .container {{ max-width: 900px; margin: 0 auto; background: #1e293b; border: 1px solid #f59e0b; border-left: 6px solid #f59e0b; border-radius: 10px; padding: 28px; box-shadow: 0 4px 14px rgba(0,0,0,0.3); }}
        h1 {{ margin-top: 0; color: #fbbf24; font-size: 22px; }}
        h2 {{ font-size: 16px; color: #f1f5f9; margin-top: 20px; }}
        code {{ background: #0f172a; color: #38bdf8; padding: 2px 7px; border-radius: 4px; font-size: 13px; word-break: break-all; }}
        pre {{ background: #0f172a; color: #cbd5e1; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 12px; border: 1px solid #334155; }}
        .btn {{ display: inline-block; padding: 11px 18px; border-radius: 7px; font-weight: 600; text-decoration: none; border: none; cursor: pointer; font-size: 14px; }}
        .btn-primary {{ background: #3b82f6; color: #fff; }}
        .btn-green {{ background: #16a34a; color: #fff; }}
        .btn-secondary {{ background: #475569; color: #fff; }}
        .step-box {{ background: #0f172a; border: 1px solid #334155; border-radius: 8px; padding: 16px; margin: 16px 0; }}
        .badge-success {{ background: #16a34a; color: #fff; padding: 4px 10px; border-radius: 12px; font-size: 13px; font-weight: 600; display: inline-block; }}
    </style>
</head>
<body>
    <div class='container'>
        <div style='margin-bottom:12px;'>
            <span class='badge-success'>✓ Yetkilendirme Başarılı (webhooks:write onaylandı)</span>
        </div>
        <h1>Hesap Webhook Limitine Ulaştı (HTTP 422 - webhook_limit_reached)</h1>
        <p>OAuth2 akışı sorunsuz tamamlandı ve <code>webhooks:write</code> izni başarıyla alındı! Ancak Hipcall hesabınızın paket limitinde izin verilen maksimum webhook sayısına ulaşıldığı için yeni webhook eklenemedi.</p>

        <div class='step-box'>
            <h3 style='margin-top:0;color:#38bdf8;'>Nasıl Çözülür?</h3>
            <p style='color:#cbd5e1;font-size:14px;'>OAuth2 yetkilendirmeniz başarıyla tamamlandı ve erişim izniniz kaydedildi! Ancak Hipcall hesabınızın paketindeki webhook limiti dolu olduğu için yeni bir kayıt açılamadı.</p>
            <p style='color:#cbd5e1;font-size:14px;'>Aşağıdaki butona tıklayarak ana sayfaya gidebilir; <b>mevcut tüm webhook'larınızı listeleyebilir, durumlarını Aktif/Pasif yapabilir, test olayı gönderebilir veya eski bir webhook'u tek tıkla silerek</b> yeni webhook için yer açabilirsiniz.</p>
            <div style='margin-top:14px;'>
                <a href='/' class='btn btn-primary'>Ana Sayfaya Git ve Webhook Listesini Yönet</a>
            </div>
        </div>

        {savedIdHtml}

        <h3>Hipcall API Yanıtı:</h3>
        <pre>{WebUtility.HtmlEncode(FormatJsonSafe(rawApiResponse))}</pre>
    </div>
</body>
</html>";
}

static string RenderDashboardHtml(HipcallOptions options, PersistedWebhookState state, string authorizeUrl, List<HipcallWebhookItemDto>? existingWebhooks = null, string? webhooksFetchError = null)
{
    var hasWebhook = !string.IsNullOrWhiteSpace(state.WebhookId);
    var eventsHtml = new StringBuilder();

    if (state.RecentEvents.Count == 0)
    {
        eventsHtml.Append("<tr><td colspan='5' style='text-align:center;color:#64748b;padding:18px;'>Henüz gelen bir webhook olayı yok. Kurulumdan sonra 'Test Olayı Gönder' butonuna tıklayın veya bir arama sonlandırın.</td></tr>");
    }
    else
    {
        foreach (var ev in state.RecentEvents)
        {
            var badgeColor = ev.Verified ? "#16a34a" : "#dc2626";
            var badgeText = ev.Verified ? "Doğrulandı (HMAC-SHA256)" : "İmza Geçersiz";
            eventsHtml.Append($@"
                <tr>
                    <td>{ev.ReceivedAt:HH:mm:ss}</td>
                    <td><code>{WebUtility.HtmlEncode(ev.EventType)}</code></td>
                    <td><code>{WebUtility.HtmlEncode(ev.WebhookIdHeader)}</code></td>
                    <td><span style='background:{badgeColor};color:#fff;padding:3px 8px;border-radius:12px;font-size:12px;'>{badgeText}</span></td>
                    <td>{WebUtility.HtmlEncode(ev.Summary)}</td>
                </tr>");
        }
    }

        var allWebhooksCard = new StringBuilder();
    var hasToken = !string.IsNullOrWhiteSpace(state.AccessToken);

    if (hasToken)
    {
        allWebhooksCard.Append(@"
        <div class='card'>
            <div style='display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;'>
                <h2 style='margin:0;'>Hesaptaki Tüm Webhook'lar (GET /api/v3/webhooks)</h2>
                <a href='/' class='btn btn-secondary' style='padding:6px 12px;font-size:12px;'>Listeyi Yenile</a>
            </div>");

        if (!string.IsNullOrWhiteSpace(webhooksFetchError))
        {
            allWebhooksCard.Append($@"<div style='background:#7f1d1d;color:#fecaca;padding:10px 14px;border-radius:6px;font-size:13px;margin-bottom:12px;'>Webhook listesi alınırken hata oluştu: <code>{WebUtility.HtmlEncode(webhooksFetchError)}</code></div>");
        }
        else if (existingWebhooks is null || existingWebhooks.Count == 0)
        {
            allWebhooksCard.Append(@"<p style='color:#94a3b8;font-size:14px;margin:0;'>Bu hesaba/uygulamaya ait kayıtlı webhook bulunamadı.</p>");
        }
        else
        {
            allWebhooksCard.Append(@"
            <table>
                <thead>
                    <tr>
                        <th>Webhook Adı / ID</th>
                        <th>Hedef URL</th>
                        <th>Abone Olaylar</th>
                        <th>Durum</th>
                        <th style='text-align:right;'>İşlemler</th>
                    </tr>
                </thead>
                <tbody>");

            foreach (var wh in existingWebhooks)
            {
                var isActive = string.Equals(wh.State, "active", StringComparison.OrdinalIgnoreCase);
                var stateBadge = isActive
                    ? "<span style='background:#16a34a;color:#fff;padding:3px 8px;border-radius:12px;font-size:12px;'>Aktif</span>"
                    : "<span style='background:#ca8a04;color:#fff;padding:3px 8px;border-radius:12px;font-size:12px;'>Pasif</span>";

                var isCurrent = string.Equals(state.WebhookId, wh.Id, StringComparison.OrdinalIgnoreCase);
                var currentTag = isCurrent ? " <span style='background:#3b82f6;color:#fff;padding:1px 6px;border-radius:8px;font-size:11px;'>Takip Edilen</span>" : "";

                var events = wh.EventTypes is not null && wh.EventTypes.Count > 0
                    ? string.Join(", ", wh.EventTypes)
                    : "-";

                allWebhooksCard.Append($@"
                    <tr>
                        <td>
                            <b>{WebUtility.HtmlEncode(wh.Name ?? "İsimsiz")}</b>{currentTag}<br/>
                            <code style='font-size:11px;'>{WebUtility.HtmlEncode(wh.Id)}</code>
                        </td>
                        <td><code style='font-size:12px;'>{WebUtility.HtmlEncode(wh.Url ?? "-")}</code></td>
                        <td><code style='font-size:12px;'>{WebUtility.HtmlEncode(events)}</code></td>
                        <td>{stateBadge}</td>
                        <td style='text-align:right;white-space:nowrap;'>");

                if (isActive)
                {
                    allWebhooksCard.Append($@"
                        <form method='post' action='/toggle-webhook-state' style='display:inline;margin:0 4px 0 0;'>
                            <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(wh.Id)}' />
                            <input type='hidden' name='target_state' value='passive' />
                            <button type='submit' class='btn' style='background:#ca8a04;color:#fff;padding:5px 10px;font-size:12px;'>Pasife Al</button>
                        </form>");
                }
                else
                {
                    allWebhooksCard.Append($@"
                        <form method='post' action='/toggle-webhook-state' style='display:inline;margin:0 4px 0 0;'>
                            <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(wh.Id)}' />
                            <input type='hidden' name='target_state' value='active' />
                            <button type='submit' class='btn btn-green' style='padding:5px 10px;font-size:12px;'>Aktife Al</button>
                        </form>");
                }

                allWebhooksCard.Append($@"
                    <form method='post' action='/trigger-test' style='display:inline;margin:0 4px 0 0;'>
                        <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(wh.Id)}' />
                        <button type='submit' class='btn btn-secondary' style='padding:5px 10px;font-size:12px;'>Test Gönder</button>
                    </form>");

                if (!isCurrent)
                {
                    allWebhooksCard.Append($@"
                    <form method='post' action='/select-webhook' style='display:inline;margin:0;'>
                        <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(wh.Id)}' />
                        <input type='hidden' name='webhook_name' value='{WebUtility.HtmlEncode(wh.Name)}' />
                        <input type='hidden' name='webhook_url' value='{WebUtility.HtmlEncode(wh.Url)}' />
                        <input type='hidden' name='webhook_state' value='{WebUtility.HtmlEncode(wh.State)}' />
                        <button type='submit' class='btn btn-primary' style='padding:5px 10px;font-size:12px;'>Seç / Takip Et</button>
                    </form>

                    <form method='post' action='/delete-webhook' style='display:inline;margin-left:4px;' onsubmit='return confirm(""Bu webhook silinsin mi?"");'>
                        <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(wh.Id)}' />
                        <button type='submit' class='btn' style='background:#dc2626;color:#fff;padding:5px 10px;font-size:12px;'>Sil</button>
                    </form>");
                }

                allWebhooksCard.Append(@"
                        </td>
                    </tr>");
            }

            allWebhooksCard.Append(@"
                </tbody>
            </table>");
        }

        allWebhooksCard.Append(@"
        </div>");
    }

var activeWebhookCard = hasWebhook
        ? $@"
        <div class='card success-border'>
            <h2>Aktif Webhook Yapılandırması ({WebUtility.HtmlEncode(state.AuthFlowSource ?? "Kayıtlı")})</h2>
            <div class='kv-grid'>
                <div><b>Webhook ID:</b></div><div><code>{WebUtility.HtmlEncode(state.WebhookId)}</code></div>
                <div><b>Webhook Adı:</b></div><div>{WebUtility.HtmlEncode(state.WebhookName)}</div>
                <div><b>Dinleyici URL:</b></div><div><code>{WebUtility.HtmlEncode(state.WebhookUrl)}</code></div>
                <div><b>Abone Olaylar:</b></div><div><code>{WebUtility.HtmlEncode(string.Join(", ", state.EventTypes))}</code></div>
                <div><b>Signing Secret:</b></div><div><code>{WebUtility.HtmlEncode(state.WebhookSecret)}</code> <small>(whsec_...)</small></div>
                <div><b>Kurulum Zamanı:</b></div><div>{state.ConfiguredAt:yyyy-MM-dd HH:mm:ss}</div>
            </div>
            <div style='margin-top:16px; display:flex; gap:12px; flex-wrap:wrap;'>
                <form method='post' action='/trigger-test' style='margin:0;'>
                    <button type='submit' class='btn btn-green'>Test Olayı Gönder (POST /api/v3/webhooks/{WebUtility.HtmlEncode(state.WebhookId)}/test)</button>
                </form>
                <form method='post' action='/rotate-secret' style='margin:0;'>
                    <button type='submit' class='btn btn-secondary'>İmza Anahtarını Yenile (Rotate Secret)</button>
                </form>
                <form method='post' action='/delete-current-webhook' onsubmit='return confirm(&apos;Bu webhook silinsin mi?&apos;);' style='margin:0;'>
                    <button type='submit' class='btn' style='background:#dc2626;color:#fff;'>Webhook&apos;u Sil (DELETE)</button>
                </form>
            </div>
        </div>"
        : @"
        <div class='card warning-border'>
            <h2>Henüz Aktif Webhook Kurulmadı</h2>
            <p>Aşağıdaki <b>Senaryo A (OAuth2 Akışı)</b> veya <b>Senaryo B (Manuel Token Akışı)</b> seçeneklerinden birini kullanarak kullanıcının Hipcall paneline girmesine gerek kalmadan otomatik <code>call.ended</code> webhook kurulumunu başlatabilirsiniz.</p>
        </div>";

    return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1' />
    <title>Acme SaaS - Hipcall Entegrasyonu</title>
    <style>
        body {{ font-family: system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 28px; line-height: 1.5; }}
        .container {{ max-width: 1040px; margin: 0 auto; }}
        header {{ margin-bottom: 24px; border-bottom: 1px solid #334155; padding-bottom: 16px; }}
        h1 {{ margin: 0 0 6px 0; color: #f8fafc; font-size: 26px; }}
        h2 {{ margin-top: 0; font-size: 19px; color: #f1f5f9; }}
        .subtitle {{ color: #94a3b8; font-size: 14px; }}
        .grid-2 {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(440px, 1fr)); gap: 20px; margin-bottom: 20px; }}
        .card {{ background: #1e293b; border: 1px solid #334155; border-radius: 10px; padding: 20px; margin-bottom: 20px; box-shadow: 0 4px 12px rgba(0,0,0,0.25); }}
        .success-border {{ border-left: 5px solid #22c55e; }}
        .warning-border {{ border-left: 5px solid #f59e0b; }}
        .btn {{ display: inline-block; padding: 11px 18px; border-radius: 7px; font-weight: 600; text-decoration: none; border: none; cursor: pointer; font-size: 14px; transition: opacity 0.15s; }}
        .btn:hover {{ opacity: 0.92; }}
        .btn-primary {{ background: #3b82f6; color: #ffffff; }}
        .btn-green {{ background: #16a34a; color: #ffffff; }}
        .btn-secondary {{ background: #475569; color: #ffffff; }}
        input[type='text'], input[type='password'] {{ width: 100%; box-sizing: border-box; padding: 10px 12px; border-radius: 6px; border: 1px solid #475569; background: #0f172a; color: #f8fafc; font-family: monospace; margin-bottom: 12px; }}
        label {{ display: block; font-size: 13px; color: #cbd5e1; margin-bottom: 4px; font-weight: 600; }}
        code {{ background: #0f172a; color: #38bdf8; padding: 2px 6px; border-radius: 4px; font-size: 13px; word-break: break-all; }}
        .kv-grid {{ display: grid; grid-template-columns: 160px 1fr; gap: 8px 12px; font-size: 14px; }}
        table {{ width: 100%; border-collapse: collapse; font-size: 13px; }}
        th, td {{ text-align: left; padding: 10px; border-bottom: 1px solid #334155; }}
        th {{ color: #94a3b8; font-weight: 600; background: #0f172a; }}
        .scope-pill {{ display: inline-block; background: #0f172a; border: 1px solid #334155; color: #a5f3fc; padding: 2px 8px; border-radius: 12px; font-size: 12px; margin: 2px; font-family: monospace; }}
    </style>
</head>
<body>
    <div class='container'>
        <header>
            <h1>Acme SaaS - Hipcall Entegrasyonu</h1>
            <div class='subtitle'>Hipcall Webhook API v3 &amp; OAuth2 Otomatik <code>call.ended</code> Kurulumu ve Standard Webhooks Doğrulayıcısı (.NET 8)</div>
        </header>

        {activeWebhookCard}

        {allWebhooksCard}

        <div class='grid-2'>
            <!-- SENARYO A: OAuth2 Akışı -->
            <div class='card'>
                <h2>Senaryo A: OAuth2 Akışı (Tek Tıkla Otomatik Kurulum)</h2>
                <p style='font-size:14px;color:#cbd5e1;'>Kullanıcıyı Hipcall OAuth2 sayfasına yönlendirir, yetki alındığında arka planda otomatik olarak <code>call.ended</code> olayına abone olan webhook'u kurar ve <code>whsec_</code> imza anahtarını kaydeder.</p>
                <div style='margin-bottom:14px;'>
                    <div style='font-size:12px;color:#94a3b8;margin-bottom:4px;'>İstenen Zorunlu OAuth2 Kapsamları (Scopes):</div>
                    {string.Join("", options.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => $"<span class='scope-pill'>{WebUtility.HtmlEncode(s)}</span>"))}
                </div>
                <p style='font-size:12px;color:#94a3b8;'>
                    <b>Redirect URI:</b> <code>{WebUtility.HtmlEncode(options.RedirectUri)}</code><br/>
                    <b>State:</b> <code>{WebUtility.HtmlEncode(options.State)}</code>
                </p>
                <a class='btn btn-primary' href='{WebUtility.HtmlEncode(authorizeUrl)}'>Hipcall ile Bağlan ve Webhook Kur</a>
            </div>

            <!-- SENARYO B: Manuel Token Akışı -->
            <div class='card'>
                <h2>Senaryo B: Manuel Access / API Token Akışı</h2>
                <p style='font-size:14px;color:#cbd5e1;'>Doğrudan mevcut bir OAuth2 Access Token veya Statik API Token girerek Hipcall paneline girmeden otomatik <code>call.ended</code> webhook kurulumunu başlatın.</p>
                <form method='post' action='/manual-setup'>
                    <label for='access_token'>Hipcall Access Token veya Statik API Token (Bearer):</label>
                    <input type='text' id='access_token' name='access_token' placeholder='Örn: eyJhbGciOi... veya statik API anahtarı' required />
                    <div style='font-size:12px;color:#94a3b8;margin-bottom:12px;'>
                        Hedef Dinleyici: <code>{WebUtility.HtmlEncode(options.WebhookReceiverUrl)}</code>
                    </div>
                    <button type='submit' class='btn btn-green'>Manuel Token ile Webhook Kur</button>
                </form>
            </div>
        </div>

        <div class='card'>
            <div style='display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;'>
                <h2 style='margin:0;'>Son Gelen Webhook Olayları (POST /evet=12345)</h2>
                <a href='/' class='btn btn-secondary' style='padding:6px 12px;font-size:12px;'>Yenile</a>
            </div>
            <table>
                <thead>
                    <tr>
                        <th>Saat</th>
                        <th>Olay Tipi</th>
                        <th>webhook-id</th>
                        <th>İmza Durumu</th>
                        <th>Özet / Detay</th>
                    </tr>
                </thead>
                <tbody>
                    {eventsHtml}
                </tbody>
            </table>
        </div>
    </div>
</body>
</html>";
}

static string RenderSetupSuccessHtml(
    string webhookId,
    string webhookSecret,
    CreateWebhookRequest requestPayload,
    string authFlowSource,
    string rawApiResponse)
{
    return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='utf-8' />
    <title>Webhook Kurulumu Başarılı - Acme SaaS</title>
    <style>
        body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 32px; line-height: 1.5; }}
        .container {{ max-width: 860px; margin: 0 auto; background: #1e293b; border: 1px solid #334155; border-left: 6px solid #22c55e; border-radius: 10px; padding: 28px; }}
        h1 {{ margin-top: 0; color: #4ade80; font-size: 24px; }}
        code {{ background: #0f172a; color: #38bdf8; padding: 3px 7px; border-radius: 4px; font-size: 13px; word-break: break-all; }}
        pre {{ background: #0f172a; color: #cbd5e1; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 12px; border: 1px solid #334155; }}
        .btn {{ display: inline-block; padding: 12px 20px; border-radius: 7px; font-weight: 600; text-decoration: none; border: none; cursor: pointer; font-size: 14px; }}
        .btn-green {{ background: #16a34a; color: #fff; }}
        .btn-secondary {{ background: #475569; color: #fff; }}
        .kv {{ margin: 16px 0; display: grid; grid-template-columns: 200px 1fr; gap: 10px; font-size: 14px; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>Hipcall Webhook Kurulumu Başarıyla Tamamlandı!</h1>
        <p>Kullanıcının Hipcall paneline girmesine gerek kalmadan <b>{WebUtility.HtmlEncode(authFlowSource)}</b> akışı ile <code>call.ended</code> webhook'u otomatik olarak oluşturuldu ve imza anahtarı güvenli depoya kaydedildi.</p>

        <div class='kv'>
            <div><b>Oluşturulan Webhook ID:</b></div>
            <div><code>{WebUtility.HtmlEncode(webhookId)}</code></div>

            <div><b>Signing Secret (whsec_):</b></div>
            <div><code>{WebUtility.HtmlEncode(webhookSecret)}</code></div>

            <div><b>Webhook Adı:</b></div>
            <div>{WebUtility.HtmlEncode(requestPayload.Name)}</div>

            <div><b>Dinleyici URL:</b></div>
            <div><code>{WebUtility.HtmlEncode(requestPayload.Url)}</code></div>

            <div><b>Abone Olay Tipleri:</b></div>
            <div><code>{WebUtility.HtmlEncode(string.Join(", ", requestPayload.EventTypes))}</code></div>
        </div>

        <div style='margin: 24px 0; display:flex; gap: 12px; flex-wrap:wrap;'>
            <form method='post' action='/trigger-test' style='margin:0;'>
                <input type='hidden' name='webhook_id' value='{WebUtility.HtmlEncode(webhookId)}' />
                <button type='submit' class='btn btn-green'>Test Olayı Gönder (POST /api/v3/webhooks/{WebUtility.HtmlEncode(webhookId)}/test)</button>
            </form>
            <a href='/' class='btn btn-secondary'>Ana Sayfaya Dön</a>
        </div>

        <h3>Hipcall API Yanıtı (POST /api/v3/webhooks):</h3>
        <pre>{WebUtility.HtmlEncode(FormatJsonSafe(rawApiResponse))}</pre>
    </div>
</body>
</html>";
}

static string RenderMessageHtml(string title, string bodyHtml, bool isError)
{
    var borderColor = isError ? "#ef4444" : "#22c55e";
    var titleColor = isError ? "#f87171" : "#4ade80";

    return $@"<!DOCTYPE html>
<html lang='tr'>
<head>
    <meta charset='utf-8' />
    <title>{WebUtility.HtmlEncode(title)} - Acme SaaS</title>
    <style>
        body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 32px; line-height: 1.5; }}
        .container {{ max-width: 820px; margin: 0 auto; background: #1e293b; border: 1px solid #334155; border-left: 6px solid {borderColor}; border-radius: 10px; padding: 28px; }}
        h1 {{ margin-top: 0; color: {titleColor}; font-size: 22px; }}
        code {{ background: #0f172a; color: #38bdf8; padding: 2px 6px; border-radius: 4px; }}
        pre {{ background: #0f172a; color: #cbd5e1; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 12px; border: 1px solid #334155; }}
        .btn {{ display: inline-block; padding: 10px 18px; border-radius: 7px; font-weight: 600; text-decoration: none; background: #3b82f6; color: #fff; margin-top: 14px; }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>{WebUtility.HtmlEncode(title)}</h1>
        <div>{bodyHtml}</div>
        <a href='/' class='btn'>Ana Sayfaya Dön</a>
    </div>
</body>
</html>";
}

// ============================================================================
// STANDARD WEBHOOKS (https://www.standardwebhooks.com/) İMZA DOĞRULAYICI
// ============================================================================
public static class StandardWebhookVerifier
{
    public const string SecretPrefix = "whsec_";

    public static bool Verify(
        string rawBody,
        string webhookId,
        string webhookTimestamp,
        string webhookSignatureHeader,
        string primarySecret,
        string? previousSecret,
        out string detail)
    {
        if (string.IsNullOrWhiteSpace(webhookId) ||
            string.IsNullOrWhiteSpace(webhookTimestamp) ||
            string.IsNullOrWhiteSpace(webhookSignatureHeader))
        {
            detail = "Zorunlu Standard Webhooks başlıklarından biri (webhook-id, webhook-timestamp, webhook-signature) eksik.";
            return false;
        }

        // 1. Önce aktif secret ile doğrula
        if (VerifyWithSingleSecret(rawBody, webhookId, webhookTimestamp, webhookSignatureHeader, primarySecret, out detail))
        {
            return true;
        }

        // 2. Eğer /rotate-secret yapıldıysa 24 saatlik kesintisiz geçiş (zero-downtime) için eski secret'ı da kontrol et
        if (!string.IsNullOrWhiteSpace(previousSecret) &&
            VerifyWithSingleSecret(rawBody, webhookId, webhookTimestamp, webhookSignatureHeader, previousSecret, out var prevDetail))
        {
            detail = $"Önceki anahtar (24 saatlik rotate-secret geçiş süresi) ile doğrulandı: {prevDetail}";
            return true;
        }

        return false;
    }

    private static bool VerifyWithSingleSecret(
        string rawBody,
        string webhookId,
        string webhookTimestamp,
        string webhookSignatureHeader,
        string secretWithPrefix,
        out string detail)
    {
        try
        {
            // Adım 1: "whsec_" önekini kaldır ve Base64 decode et
            var base64Secret = secretWithPrefix.StartsWith(SecretPrefix, StringComparison.Ordinal)
                ? secretWithPrefix[SecretPrefix.Length..]
                : secretWithPrefix;

            byte[] secretKeyBytes = Convert.FromBase64String(base64Secret);

            // Adım 2: "${webhook_id}.${webhook_timestamp}.${raw_body}" metnini oluştur
            string signedContent = $"{webhookId}.{webhookTimestamp}.{rawBody}";
            byte[] signedContentBytes = Encoding.UTF8.GetBytes(signedContent);

            // Adım 3: HMAC-SHA256 ile imzala ve Base64 string üret
            byte[] computedHashBytes = HMACSHA256.HashData(secretKeyBytes, signedContentBytes);
            string expectedBase64Signature = Convert.ToBase64String(computedHashBytes);
            byte[] expectedSignatureBytes = Encoding.UTF8.GetBytes(expectedBase64Signature);

            // Adım 4: webhook-signature başlığındaki imzaları ayrıştır (Örn: "v1,base64sig1 v1,base64sig2")
            // ve CryptographicOperations.FixedTimeEquals ile zaman sabitli karşılaştır.
            var signatureParts = webhookSignatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in signatureParts)
            {
                string candidateBase64 = part;
                if (part.StartsWith("v1,", StringComparison.OrdinalIgnoreCase))
                {
                    candidateBase64 = part["v1,".Length..];
                }
                else if (part.Contains(','))
                {
                    var commaIndex = part.IndexOf(',');
                    candidateBase64 = part[(commaIndex + 1)..];
                }

                byte[] candidateBytes = Encoding.UTF8.GetBytes(candidateBase64);
                if (CryptographicOperations.FixedTimeEquals(expectedSignatureBytes, candidateBytes))
                {
                    detail = $"v1,{expectedBase64Signature}";
                    return true;
                }
            }

            detail = "Hesaplanan HMAC-SHA256 imzası, webhook-signature başlığındaki hiçbir imza ile eşleşmedi.";
            return false;
        }
        catch (FormatException ex)
        {
            detail = $"Webhook secret Base64 decode edilemedi: {ex.Message}";
            return false;
        }
    }
}

// ============================================================================
// DURUM SAKLAMA SERVİSİ (Bellek + JSON Dosyası Kalıcılığı)
// ============================================================================
public sealed class WebhookStateStore
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private PersistedWebhookState _state;

    public WebhookStateStore(IOptions<HipcallOptions> optionsAccessor, IWebHostEnvironment env)
    {
        var configuredPath = optionsAccessor.Value.StateStorePath;
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(env.ContentRootPath, configuredPath);

        _state = LoadFromDisk();
    }

    public PersistedWebhookState GetState()
    {
        lock (_sync)
        {
            return _state.Clone();
        }
    }

    public void SaveWebhookSetup(PersistedWebhookState newState)
    {
        lock (_sync)
        {
            newState.RecentEvents = _state.RecentEvents;
            newState.PreviousWebhookSecret = _state.WebhookSecret;
            _state = newState;
            PersistToDisk();
        }
    }

    public void RotateSecret(string newSecret)
    {
        lock (_sync)
        {
            _state.PreviousWebhookSecret = _state.WebhookSecret;
            _state.WebhookSecret = newSecret;
            PersistToDisk();
        }
    }

    public void UpdateCredentials(string? webhookId, string? secret, string? accessToken)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(webhookId)) _state.WebhookId = webhookId;
            if (!string.IsNullOrWhiteSpace(secret)) _state.WebhookSecret = secret;
            if (!string.IsNullOrWhiteSpace(accessToken)) _state.AccessToken = accessToken;
            PersistToDisk();
        }
    }

    public void UpdateWebhookState(string newState)
    {
        lock (_sync)
        {
            _state.WebhookState = newState;
            PersistToDisk();
        }
    }

    public void SaveTokens(string accessToken, string? refreshToken, string authFlowSource)
    {
        lock (_sync)
        {
            _state.AccessToken = accessToken;
            if (!string.IsNullOrWhiteSpace(refreshToken)) _state.RefreshToken = refreshToken;
            _state.AuthFlowSource = authFlowSource;
            PersistToDisk();
        }
    }

    public void SelectWebhook(string webhookId, string? name, string? url, string? state)
    {
        lock (_sync)
        {
            _state.WebhookId = webhookId;
            if (!string.IsNullOrWhiteSpace(name)) _state.WebhookName = name;
            if (!string.IsNullOrWhiteSpace(url)) _state.WebhookUrl = url;
            if (!string.IsNullOrWhiteSpace(state)) _state.WebhookState = state;
            PersistToDisk();
        }
    }

    public void ClearWebhook()
    {
        lock (_sync)
        {
            _state.WebhookId = null;
            _state.WebhookSecret = null;
            _state.PreviousWebhookSecret = null;
            _state.WebhookName = null;
            _state.WebhookState = null;
            PersistToDisk();
        }
    }

    public void AddEventLog(WebhookEventLog log)
    {
        lock (_sync)
        {
            _state.RecentEvents.Insert(0, log);
            if (_state.RecentEvents.Count > 25)
            {
                _state.RecentEvents.RemoveRange(25, _state.RecentEvents.Count - 25);
            }
            PersistToDisk();
        }
    }

    private PersistedWebhookState LoadFromDisk()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<PersistedWebhookState>(json);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UYARI] '{_filePath}' okunamadı: {ex.Message}");
        }

        return new PersistedWebhookState();
    }

    private void PersistToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UYARI] '{_filePath}' dosyasına yazılamadı: {ex.Message}");
        }
    }
}

// ============================================================================
// MODELLER VE DTO'LAR
// ============================================================================
public sealed class HipcallWebhookItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("event_types")]
    public List<string>? EventTypes { get; set; }
}

public sealed class HipcallOptions
{
    public string BaseUrl { get; set; } = "https://use.hipcall.com.tr";
    public string AuthorizationUrl { get; set; } = "https://use.hipcall.com.tr/oauth/authorize";
    public string TokenUrl { get; set; } = "https://use.hipcall.com.tr/oauth/token";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string WebhookReceiverUrl { get; set; } = string.Empty;
    public string State { get; set; } = "YOUR_CSRF_STATE";
    public string Scopes { get; set; } = "profile email offline_access contacts:read webhooks:read webhooks:write communication:read";
    public string DefaultWebhookName { get; set; } = "Acme SaaS - Hangup Sync";
    public string[] DefaultEventTypes { get; set; } = new[] { "call.ended" };
    public string StateStorePath { get; set; } = "webhook_store.json";
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

public sealed class CreateWebhookRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Acme SaaS - Hangup Sync";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("event_types")]
    public string[] EventTypes { get; set; } = new[] { "call.ended" };
}

public sealed class PersistedWebhookState
{
    public string? WebhookId { get; set; }
    public string? WebhookSecret { get; set; }
    public string? PreviousWebhookSecret { get; set; }
    public string? WebhookName { get; set; }
    public string? WebhookUrl { get; set; }
    public string? WebhookState { get; set; }
    public string[] EventTypes { get; set; } = new[] { "call.ended" };
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? AuthFlowSource { get; set; }
    public DateTimeOffset ConfiguredAt { get; set; }
    public List<WebhookEventLog> RecentEvents { get; set; } = new();

    public PersistedWebhookState Clone() => new()
    {
        WebhookId = WebhookId,
        WebhookSecret = WebhookSecret,
        PreviousWebhookSecret = PreviousWebhookSecret,
        WebhookName = WebhookName,
        WebhookUrl = WebhookUrl,
        WebhookState = WebhookState,
        EventTypes = EventTypes.ToArray(),
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        AuthFlowSource = AuthFlowSource,
        ConfiguredAt = ConfiguredAt,
        RecentEvents = RecentEvents.ToList()
    };
}

public sealed class WebhookEventLog
{
    public DateTimeOffset ReceivedAt { get; set; }
    public string WebhookIdHeader { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
}
