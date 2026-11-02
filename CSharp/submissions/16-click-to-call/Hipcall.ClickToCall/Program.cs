using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(5080));

string Config(string envName, string configKey) =>
    Environment.GetEnvironmentVariable(envName)
    ?? builder.Configuration[configKey]
    ?? throw new InvalidOperationException($"{envName} is not set.");

var baseUrl = Environment.GetEnvironmentVariable("HIPCALL_BASE_URL")
    ?? builder.Configuration["Hipcall:BaseUrl"]
    ?? "https://use.hipcall.com.tr";
var clientId = Config("HIPCALL_CLIENT_ID", "Hipcall:ClientId");
var clientSecret = Config("HIPCALL_CLIENT_SECRET", "Hipcall:ClientSecret");
var publicUrl = Config("PUBLIC_URL", "Hipcall:PublicUrl").TrimEnd('/');
var redirectUri = $"{publicUrl}/callback";
const string scope = "profile+email+offline_access+communication";

var app = builder.Build();

var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
var pendingStates = new ConcurrentDictionary<string, bool>();
string? savedToken = null;

app.MapGet("/", () => Results.Content("<a href=\"/login\">Connect Hipcall</a>", "text/html"));

app.MapGet("/login", () =>
{
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    pendingStates[state] = true;

    return Results.Redirect($"{baseUrl}/oauth/authorize?response_type=code"
        + $"&client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&scope={scope}&state={state}");
});

app.MapGet("/callback", async (string? code, string? state, string? error) =>
{
    if (error is not null)
    {
        return Results.BadRequest($"Hipcall returned an error: {error}");
    }

    if (code is null || state is null || !pendingStates.TryRemove(state, out _))
    {
        return Results.BadRequest("The code is missing or the state is unknown.");
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
        return Results.Content(tokenBody, "application/json", statusCode: (int)tokenResponse.StatusCode);
    }

    using var tokenJson = JsonDocument.Parse(tokenBody);
    savedToken = tokenJson.RootElement.GetProperty("access_token").GetString()!;
    var granted = tokenJson.RootElement.TryGetProperty("scope", out var scopeValue) ? scopeValue.GetString() : null;
    Console.WriteLine($"Granted scopes: {granted ?? "(not returned)"}");

    return Results.Content(
        "<form method=\"post\" action=\"/call\">"
        + "<input name=\"user_id\" placeholder=\"4200\" required /> "
        + "<input name=\"callee_number\" placeholder=\"+90530XXXXXXX\" required /> "
        + "<button>Start call</button>"
        + "</form>",
        "text/html");
});

app.MapPost("/call", async (HttpRequest req) =>
{
    if (savedToken is null)
    {
        return Results.BadRequest("Connect Hipcall first.");
    }

    var form = await req.ReadFormAsync();
    var userId = form["user_id"].ToString();
    var calleeNumber = form["callee_number"].ToString();

    var payload = JsonSerializer.Serialize(new
    {
        callee_number = calleeNumber,
        ring_user_first = true,
        call_masking = true,
        call_masking_name = "Support Team"
    });
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v3/users/{userId}/call")
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", savedToken);

    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"Call response ({(int)response.StatusCode}): {body}");

    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
});

app.Run();
