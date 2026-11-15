using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

int cases = 0;
void Check(bool condition, string message)
{
    cases++;
    if (!condition) throw new Exception(message);
}
async Task Fails(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.Ordinal))
    {
        cases++;
        return;
    }
    throw new Exception("Expected failure: " + message);
}
var settings = new RemoteSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "https://contoso.sharepoint.com/sites/workshop", "alice01");
var handler = new RemoteOfflineHandler();
using var client = new HttpClient(handler);
int tokens = 0;
var transport = new RemoteTransport(client, settings.Tenant, () =>
{
    tokens++;
    return Task.FromResult("synthetic-user-token");
});
var workshop = new RemoteWorkshop(settings, transport);
handler.Reset();
var unauthenticated = new RemoteWorkshop(settings, new RemoteTransport(client, settings.Tenant,
    () => throw new InvalidOperationException("Synthetic user authentication failed")));
await Fails(() => unauthenticated.ExecuteAsync("retrieve"), "user authentication failed");
await Fails(() => unauthenticated.ExecuteAsync("cleanup", confirm: true), "user authentication failed");
Check(handler.Requests.Count == 0, "Authentication failure made REST calls");

foreach (string command in new[] { "source", "knowledge-base", "retrieve" })
{
    handler.Reset();
    int before = tokens;
    await workshop.ExecuteAsync(command);
    Check(handler.Requests.Count == 1, "Hidden prerequisite actions in " + command);
    var request = handler.Requests[0];
    Check(request.Method == (command == "retrieve" ? "POST" : "PUT"), "Wrong action");
    Check(request.Uri.EndsWith("?api-version=2026-08-01-preview"), "API version");
    Check(request.Authorization == "Bearer synthetic-user-token" && !request.HasApiKey, "Not keyless user authentication");
    Check(request.QueryAuthorization == (command == "retrieve" ? "synthetic-user-token" : null), "Query header differs from Python08");
    Check(tokens - before == (command == "retrieve" ? 2 : 1), "Unexpected authentication calls");
}
handler.Reset();
await workshop.ExecuteAsync("run");
Check(handler.Requests.Count == 3 && handler.Requests.All(r => r.Method != "DELETE"), "Run must be independent and never clean up");
Check(JsonNode.DeepEquals(handler.Requests[0].Body, RemoteWorkshop.SourcePayload(settings)), "Source contract");
Check(JsonNode.DeepEquals(handler.Requests[1].Body, RemoteWorkshop.BasePayload(settings)), "Base contract");
Check(JsonNode.DeepEquals(handler.Requests[2].Body, RemoteWorkshop.RetrievePayload(settings, RemoteWorkshop.Question)), "Query contract");
if (args.Length == 2 && args[0] == "--wire")
    File.WriteAllText(args[1], JsonSerializer.Serialize(handler.Requests));
handler.Reset();
await workshop.ExecuteAsync("retrieve", "allowed onboarding 😀");
Check(handler.Requests[0].Body!["intents"]![0]!["search"]!.GetValue<string>() == "allowed onboarding 😀", "Custom question ignored");
foreach (string question in new[] { "", " \t", new string('a', 1501), string.Concat(Enumerable.Repeat("😀", 1501)) })
{
    handler.Reset();
    await Fails(() => workshop.ExecuteAsync("run", question), "non-empty question");
    Check(handler.Requests.Count == 0, "Invalid query caused resource writes");
}
RemoteWorkshop.ValidateQuestion(string.Concat(Enumerable.Repeat("😀", 1500)));

foreach (string mode in new[] { "gateway", "denied", "partial", "activity-error", "outside-site", "sibling-site", "malformed" })
{
    handler.Reset(mode);
    var errors = new StringWriter();
    var output = new StringWriter();
    TextWriter oldErrors = Console.Error;
    TextWriter oldOutput = Console.Out;
    try
    {
        Console.SetError(errors);
        Console.SetOut(output);
        await Fails(() => workshop.ExecuteAsync("retrieve"),
            mode switch
            {
                "gateway" => "HTTP 502",
                "denied" => "HTTP 403",
                "partial" or "activity-error" => "partial or a source failed",
                "outside-site" or "sibling-site" => "outside the selected site",
                _ => "invalid JSON"
            });
    }
    finally { Console.SetError(oldErrors); Console.SetOut(oldOutput); }
    Check(handler.Requests.Count == 1, "Automatic billable retry");
    if (mode is "gateway" or "denied")
    {
        string diagnostics = errors.ToString();
        foreach (string expected in new[] { "HTTP:", "UTC (Date):", "2030", "request-id: synthetic-request",
            "x-ms-request-id: synthetic-ms-request", "x-ms-correlation-request-id: synthetic-correlation", "Response body:" })
            Check(diagnostics.Contains(expected), "Missing error diagnostics: " + expected);
        Check(!diagnostics.Contains("synthetic-user-token"), "Token leaked");
        if (mode == "gateway")
            Check(diagnostics.Contains("/beta/copilot/retrieval") && diagnostics.Contains("BadGateway"), "Upstream failure hidden");
        Check(!output.ToString().Contains("No source references"), "Failure treated as an empty success");
    }
}
handler.Reset("no-references");
await workshop.ExecuteAsync("retrieve");
Check(handler.Requests.Count == 1, "Empty query result retried");

handler.Reset();
await Fails(() => workshop.ExecuteAsync("cleanup"), "cleanup --confirm");
Check(handler.Requests.Count == 0, "Cleanup not explicitly confirmed");
foreach (string mode in new[] { "", "missing" })
{
    handler.Reset(mode);
    await workshop.ExecuteAsync("cleanup", confirm: true);
    Check(handler.Requests.Select(r => r.Uri).SequenceEqual(new[]
    {
        "https://workshop.search.windows.net/knowledgebases/alice01-sp-remote-kb?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-sp-remote-source?api-version=2026-08-01-preview"
    }), "Cleanup must delete exactly derived KB then source");
    Check(handler.Requests.All(r => r.Method == "DELETE" && r.Body == null && r.QueryAuthorization == null), "Cleanup made hidden calls");
}
handler.Reset("denied");
await Fails(() => workshop.ExecuteAsync("cleanup", confirm: true), "HTTP 403");
Check(handler.Requests.Count == 1, "Cleanup ignored authorization failure");

foreach (var invalid in new[]
{
    settings with { Tenant = "not-a-guid" },
    settings with { Prefix = "ab" }, settings with { Prefix = "Alice01" },
    settings with { Prefix = "alice01/other" }, settings with { Prefix = new string('a', 21) },
    settings with { SearchEndpoint = "http://workshop.search.windows.net" },
    settings with { SearchEndpoint = "https://workshop.search.windows.net.evil.test" },
    settings with { SearchEndpoint = "https://workshop.search.windows.net/indexes/other" },
    settings with { SearchEndpoint = "https://user@workshop.search.windows.net" },
    settings with { SearchEndpoint = "https://workshop.search.windows.net?api-version=other" },
    settings with { SearchEndpoint = "https://workshop.search.windows.net:8443" },
    settings with { SearchEndpoint = "https://workshop.search.windows.net#fragment" },
    settings with { SiteUrl = "http://contoso.sharepoint.com/sites/workshop" },
    settings with { SiteUrl = "https://contoso.sharepoint.com.evil.test/sites/workshop" },
    settings with { SiteUrl = "https://user@contoso.sharepoint.com/sites/workshop" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop?sharing=1" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop#fragment" },
    settings with { SiteUrl = "https://contoso.sharepoint.com:8443/sites/workshop" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/:w:/s/workshop/document" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop/Documents/file.docx" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop\";Path:\"other" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop%22" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop%0d%0a" },
    settings with { SiteUrl = "https://contoso.sharepoint.com/sites/workshop\\other" }
})
{
    handler.Reset();
    int before = tokens;
    await Fails(() => new RemoteWorkshop(invalid, transport).ExecuteAsync("source"), "Use a");
    Check(handler.Requests.Count == 0 && tokens == before, "Validation happened after authentication/write");
}
foreach (string site in new[] { "https://contoso.sharepoint.com", "https://contoso.sharepoint.com/teams/workshop",
    "https://contoso.sharepoint.com/sites/Workshop%20Test" })
    (settings with { SiteUrl = site }).Validate();

string root = Path.Combine(Directory.GetCurrentDirectory(), ".csharp-remote-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string[] names = ["AZURE_TENANT_ID", "AZURE_SEARCH_ENDPOINT", "SHAREPOINT_SITE_URL", "WORKSHOP_PREFIX"];
var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
    await Fails(() => Task.FromResult(RemoteSettings.Load(root)), "Missing setting:");
    File.WriteAllText(Path.Combine(root, ".env"),
        $"AZURE_TENANT_ID={settings.Tenant}\nAZURE_SEARCH_ENDPOINT={settings.SearchEndpoint}\nSHAREPOINT_SITE_URL={settings.SiteUrl}\nWORKSHOP_PREFIX=wrong01\n");
    File.WriteAllText(Path.Combine(root, ".env.azure"), "export WORKSHOP_PREFIX='alice01'\n");
    Environment.SetEnvironmentVariable("WORKSHOP_PREFIX", "environment01");
    Check(RemoteSettings.Load(root) == settings, "Root dotenv override order differs");
    Check(!Directory.Exists(Path.Combine(root, ".workshop")), "Remote lab needs no local cache");
}
finally
{
    foreach (var entry in original) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"Passed {cases} offline console protocol/runtime cases.");

public sealed record CapturedRemoteRequest(string Method, string Uri, string? Authorization,
    string? QueryAuthorization, bool HasApiKey, JsonObject? Body);

public sealed class RemoteOfflineHandler : HttpMessageHandler
{
    public List<CapturedRemoteRequest> Requests = new();
    public string Mode = "";

    public void Reset(string mode = "")
    {
        Requests.Clear();
        Mode = mode;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(new CapturedRemoteRequest(request.Method.Method, request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-ms-query-source-authorization", out var query) ? query.Single() : null,
            request.Headers.Contains("api-key"),
            request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject()));
        int status = request.Method == HttpMethod.Delete ? 204 : 200;
        string body = request.Method == HttpMethod.Post
            ? """{"references":[{"webUrl":"https://contoso.sharepoint.com/sites/workshop/Documents/onboarding.docx","sourceData":{"Title":"Synthetic onboarding","Author":"Test User"}}],"activity":[]}"""
            : request.Method == HttpMethod.Delete ? "" : "{}";
        if (Mode == "gateway")
        {
            status = 502;
            body = """{"error":{"message":"Copilot retrieval call failed: BadGateway /beta/copilot/retrieval"}}""";
        }
        else if (Mode == "denied") { status = 403; body = "synthetic authorization failure"; }
        else if (Mode == "missing") { status = 404; body = "synthetic missing object"; }
        else if (Mode == "partial") status = 206;
        else if (Mode == "activity-error") body = """{"references":[],"activity":[{"error":{"message":"BadGateway /beta/copilot/retrieval"}}]}""";
        else if (Mode == "no-references") body = """{"references":[],"activity":[]}""";
        else if (Mode == "outside-site") body = """{"references":[{"webUrl":"https://other.sharepoint.com/sites/workshop/doc.docx"}]}""";
        else if (Mode == "sibling-site") body = """{"references":[{"sourceData":{"webUrl":"https://contoso.sharepoint.com/sites/workshop-other/doc.docx"}}]}""";
        else if (Mode == "malformed") body = "not JSON";
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        response.Headers.Date = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        response.Headers.Add("request-id", "synthetic-request");
        response.Headers.Add("x-ms-request-id", "synthetic-ms-request");
        response.Headers.Add("x-ms-correlation-request-id", "synthetic-correlation");
        return response;
    }
}
