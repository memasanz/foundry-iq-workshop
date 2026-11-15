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
var settings = new WebIqSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "workshop-ai", "workshop-chat", "gpt-5-mini",
    "alice01", "synthetic-test-key");
var handler = new WebIqOfflineHandler();
using var client = new HttpClient(handler);
int tokenCalls = 0;
var transport = new WebIqTransport(client, settings.Tenant, () =>
{
    tokenCalls++;
    return Task.FromResult("synthetic-search-token");
});
var workshop = new WebIqWorkshop(settings, transport);
const string question = "What is the latest stable Python release and its release date, according to Python.org?";
handler.Reset();
await workshop.ExecuteAsync("run", question);
Check(handler.Requests.Count == 3 && handler.Requests.All(r => r.Method != "DELETE"), "Run probes/cleans up");
Check(tokenCalls == 3, "Search token acquisition count");
foreach (var request in handler.Requests)
{
    Check(request.Uri.EndsWith("?api-version=2026-08-01-preview"), "Wrong Search API");
    Check(request.Authorization == "Bearer synthetic-search-token", "Wrong Search credential");
    Check(!request.HasApiKey && request.QueryAuthorization is null, "Web IQ key/query-user header sent directly to Search");
}
var source = handler.Requests[0].Body!;
Check(source["mcpServerParameters"]!["authentication"]!["kind"]!.ToString() == "storedHeaders", "Wrong Web IQ auth");
Check(source["mcpServerParameters"]!["authentication"]!["storedHeadersParameters"]!["headers"]!["x-apikey"]!.ToString() == settings.WebIqKey, "Key not stored");
Check(source["mcpServerParameters"]!["tools"]!.AsArray().Count == 1 &&
    source["mcpServerParameters"]!["tools"]![0]!["name"]!.ToString() == "web", "Not exactly one web tool");
Check(handler.Requests[1].Body!["knowledgeSources"]!.AsArray().Count == 1, "Not Web-only");
Check(!handler.Requests[2].Body!.ToJsonString().Contains(settings.WebIqKey), "Retrieve sends Web IQ key");
if (args.Length == 2 && args[0] == "--wire")
    File.WriteAllText(args[1], JsonSerializer.Serialize(handler.Requests));

foreach (string command in new[] { "source", "knowledge-base", "retrieve" })
{
    handler.Reset();
    await workshop.ExecuteAsync(command, question);
    Check(handler.Requests.Count == 1, "Hidden prerequisite actions: " + command);
    Check(handler.Requests[0].Method == (command == "retrieve" ? "POST" : "PUT"), "Wrong step method");
}
handler.Reset();
await workshop.ExecuteAsync("retrieve", question, "extractiveData");
Check(handler.Requests[0].Body!["outputMode"]!.ToString() == "extractiveData", "Output mode ignored");
foreach (var argsCase in new[]
{
    new[] { "run", "", "--output-mode", "answerSynthesis" },
    new[] { "retrieve", "--output-mode", "news" },
    new[] { "cleanup" }, new[] { "cleanup", "--confirm", "other" },
    new[] { "run", "--output-mode" }, new[] { "run", "one", "two" }
})
{
    handler.Reset();
    await Fails(() => Task.Run(() => WebIqOptions.Parse(argsCase)), argsCase[0] == "cleanup" ? "cleanup --confirm" :
        argsCase.Contains("news") ? "answerSynthesis or extractiveData" : argsCase.Contains("") ? "non-empty" : "Use one quoted query");
    Check(handler.Requests.Count == 0, "Invalid args call HTTP");
}
Check(WebIqOptions.Parse(["run", question, "--output-mode", "extractiveData"]).OutputMode == "extractiveData", "Argument mode");
handler.Reset();
await Fails(() => workshop.ExecuteAsync("run", question, "wrong"), "answerSynthesis or extractiveData");
await Fails(() => workshop.ExecuteAsync("run", " "), "non-empty question");
await Fails(() => workshop.ExecuteAsync("cleanup"), "cleanup --confirm");
Check(handler.Requests.Count == 0, "Validation/cleanup guard after writes");
var noAuth = new WebIqWorkshop(settings, new WebIqTransport(client, settings.Tenant,
    () => throw new InvalidOperationException("Synthetic authentication failed")));
await Fails(() => noAuth.ExecuteAsync("retrieve"), "authentication failed");
Check(handler.Requests.Count == 0, "Auth failure reached HTTP");

foreach (string mode in new[] { "denied", "gateway", "partial", "activity-error", "malformed", "network", "timeout", "echo", "no-references" })
{
    handler.Reset(mode);
    var output = new StringWriter();
    var errors = new StringWriter();
    TextWriter savedOutput = Console.Out, savedErrors = Console.Error;
    try
    {
        Console.SetOut(output);
        Console.SetError(errors);
        if (mode is "echo" or "no-references") await workshop.ExecuteAsync("retrieve", question);
        else await Fails(() => workshop.ExecuteAsync("retrieve", question), mode switch
        {
            "denied" => "HTTP 403",
            "gateway" => "HTTP 502",
            "partial" or "activity-error" => "partial or Web IQ failed",
            "network" => "network failure",
            "timeout" => "timed out",
            _ => "invalid JSON"
        });
    }
    finally { Console.SetOut(savedOutput); Console.SetError(savedErrors); }
    string logs = output + errors.ToString();
    Check(handler.Requests.Count == 1, "Automatic billable retry");
    Check(!logs.Contains(settings.WebIqKey) && !logs.Contains("synthetic-search-token"), "Key/token leak in success/error");
    if (mode is "denied" or "gateway")
        foreach (string expected in new[] { "UTC (Date):", "2030", "synthetic-request", "synthetic-correlation", "Response body:", "<redacted>" })
            Check(logs.Contains(expected), "Missing diagnostic: " + expected);
    if (mode == "echo")
    {
        Check(logs.Contains("https://example.test/forecast") && logs.Contains("Seattle forecast"), "Nested reference parsing");
        Check(logs.Contains("<redacted>"), "Echo not redacted");
    }
    if (mode == "no-references")
        Check(logs.Contains("alone does not establish an authentication failure"), "Missing reference misdiagnosed as auth");
}
foreach (string command in new[] { "source", "knowledge-base", "cleanup" })
{
    handler.Reset("denied");
    await Fails(() => workshop.ExecuteAsync(command, confirm: true), "HTTP 403");
    Check(handler.Requests.Count == 1, "Management failure did not stop");
}
foreach (string mode in new[] { "", "missing" })
{
    handler.Reset(mode);
    await workshop.ExecuteAsync("cleanup", confirm: true);
    Check(handler.Requests.Select(r => r.Uri).SequenceEqual(new[]
    {
        "https://workshop.search.windows.net/knowledgebases/alice01-nb14-kb?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-nb14-webiq?api-version=2026-08-01-preview"
    }), "Cleanup exact legacy names/order");
    Check(handler.Requests.All(r => r.Method == "DELETE" && r.Body is null), "Cleanup scope");
}
handler.Reset("probe");
int beforeTokens = tokenCalls;
await workshop.ExecuteAsync("connectivity");
Check(tokenCalls == beforeTokens && handler.Requests.Count == 1, "Probe authenticates");
Check(handler.Requests[0].Authorization is null && !handler.Requests[0].HasApiKey &&
    handler.Requests[0].Method == "GET", "Probe sends credentials/writes");
handler.Reset();
await workshop.ExecuteAsync("diagnostics");
Check(handler.Requests.Count == 0 && tokenCalls == beforeTokens, "Local diagnostics makes calls");
string key = "synthetic\"key\\secret";
Check(!WebIqResults.Redact(JsonSerializer.Serialize(key), key).Contains("secret"), "JSON escaped key leak");
Check(!WebIqResults.Redact(Uri.EscapeDataString(key), key).Contains("secret"), "URI escaped key leak");
var nestedOutput = new StringWriter();
TextWriter priorOutput = Console.Out;
try
{
    Console.SetOut(nestedOutput);
    WebIqResults.Print(new JsonObject
    {
        ["references"] = new JsonArray(new JsonObject
        {
            ["sourceData"] = new JsonObject
            {
                ["content"] = new JsonObject { ["content"] = key, ["title"] = key }.ToJsonString()
            }
        })
    }, 200, key);
}
finally { Console.SetOut(priorOutput); }
Check(!nestedOutput.ToString().Contains("secret") && nestedOutput.ToString().Contains("<redacted>"), "Nested parsed reference key leak");
Console.WriteLine($"Passed {cases} offline console protocol/runtime cases.");

public sealed record CapturedWebIqRequest(string Method, string Uri, string? Authorization,
    bool HasApiKey, string? QueryAuthorization, JsonObject? Body);

public sealed class WebIqOfflineHandler : HttpMessageHandler
{
    public List<CapturedWebIqRequest> Requests { get; } = [];
    public string Mode { get; set; } = "";
    public void Reset(string mode = "") { Requests.Clear(); Mode = mode; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(),
            request.Headers.Authorization?.ToString(), request.Headers.Contains("x-apikey"),
            request.Headers.TryGetValues("x-ms-query-source-authorization", out var values) ? values.First() : null,
            request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject()));
        if (Mode == "network")
            throw new HttpRequestException("network failure synthetic-test-key synthetic-search-token");
        if (Mode == "timeout") throw new TaskCanceledException("synthetic-test-key");
        int status = Mode switch { "denied" => 403, "gateway" => 502, "missing" => 404, "partial" => 206, "probe" => 401, _ => 200 };
        var body = new JsonObject
        {
            ["activity"] = new JsonArray(new JsonObject { ["type"] = "mcpServer", ["count"] = 1 }),
            ["response"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["text"] = Mode == "echo" ?
                    "synthetic-test-key synthetic-search-token" : "Forecast [ref_id:0]"
                })
            }),
            ["references"] = new JsonArray(new JsonObject
            {
                ["id"] = "0",
                ["sourceData"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["title"] = "Forecast",
                        ["url"] = "https://example.test/forecast",
                        ["content"] = "Seattle forecast evidence"
                    }.ToJsonString()
                }
            })
        };
        if (Mode == "activity-error")
            body["activity"]![0]!["error"] = new JsonObject { ["message"] = "auth_missing_token synthetic-test-key" };
        if (Mode == "no-references") body["references"] = new JsonArray();
        string text = status >= 400 ? "Search upstream failure synthetic-test-key synthetic-search-token" :
            Mode == "malformed" ? "{synthetic-test-key" : body.ToJsonString();
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
        response.Headers.Date = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", "synthetic-request");
        response.Headers.Add("x-ms-correlation-request-id", "synthetic-correlation");
        return response;
    }
}
