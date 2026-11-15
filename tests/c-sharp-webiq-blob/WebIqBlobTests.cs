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
var handler = new WebIqBlobOfflineHandler();
using var client = new HttpClient(handler);
int tokenCalls = 0;
var transport = new WebIqTransport(client, settings.Tenant, () =>
{
    tokenCalls++;
    return Task.FromResult("synthetic-search-token");
});
var workshop = new WebIqWorkshop(settings, transport);
await workshop.ExecuteAsync("run");
Check(handler.Requests.Count == 8 && tokenCalls == 8, "Run must check, author exactly three objects and retrieve three times");
Check(handler.Requests.Select(r => r.Method).SequenceEqual(["GET", "POST", "PUT", "PUT", "PUT", "POST", "POST", "POST"]),
    "Run hidden actions or cleanup");
foreach (var request in handler.Requests)
{
    Check(request.Uri.EndsWith("?api-version=2026-08-01-preview"), "Wrong API");
    Check(request.Authorization == "Bearer synthetic-search-token", "Search bearer auth");
    Check(!request.HasApiKey && !request.HasQueryAuthorization, "Wrong direct Search header");
}
Check(handler.Requests[0].Uri.Contains("/indexes/alice01-cu-chunks?"), "Must use CU index");
Check(handler.Requests[2].Body!["searchIndexParameters"]!["searchIndexName"]!.ToString() == "alice01-cu-chunks", "Wrong Blob index");
Check(handler.Requests[3].Body!["mcpServerParameters"]!["authentication"]!["storedHeadersParameters"]!["headers"]!["x-apikey"]!.ToString() == settings.WebIqKey, "Wrong stored profile header");
Check(handler.Requests[3].Body!["mcpServerParameters"]!["tools"]!.AsArray().Count == 1 &&
    handler.Requests[3].Body!["mcpServerParameters"]!["tools"]![0]!["name"]!.ToString() == "web", "Only web allowlisted");
Check(handler.Requests[4].Body!["knowledgeSources"]!.AsArray().Count == 2, "Exactly two sources");
if (args.Length == 2 && args[0] == "--wire")
    File.WriteAllText(args[1], JsonSerializer.Serialize(handler.Requests));

foreach (string command in new[] { "verify-index", "blob-source", "webiq-source", "knowledge-base", "retrieve-blob", "retrieve-web", "retrieve-mixed" })
{
    handler.Reset();
    await workshop.ExecuteAsync(command, command.StartsWith("retrieve-") ? "custom public query" : null, "extractiveData");
    Check(handler.Requests.Count == (command == "verify-index" ? 2 : 1), "Hidden actions: " + command);
    if (command.StartsWith("retrieve-"))
    {
        Check(handler.Requests[0].Body!["outputMode"]!.ToString() == "extractiveData", "Mode ignored");
        Check(handler.Requests[0].Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() == "custom public query", "Query ignored");
        Check(handler.Requests[0].Body!["knowledgeSourceParams"]!.AsArray().Count == (command == "retrieve-mixed" ? 2 : 1), "Source selection ignored");
    }
}
handler.Reset();
await workshop.ExecuteAsync("run", "custom comparison", "extractiveData");
Check(handler.Requests[5].Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() == WebIqWorkshop.Question(settings, "blob") &&
    handler.Requests[6].Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() == WebIqWorkshop.Question(settings, "web") &&
    handler.Requests[7].Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() == "custom comparison", "Run query should replace only mixed");

foreach (var (mode, expected, requests) in new[]
{
    ("missing", "HTTP 404", 1), ("schema", "retrievable content/source fields", 1),
    ("unretrievable", "retrievable content/source fields", 1), ("unsearchable", "retrievable content/source fields", 1),
    ("semantic", "missing semantic configuration", 1), ("empty", "No chunks are visible", 2),
    ("bool-count", "No chunks are visible", 2), ("string-count", "No chunks are visible", 2),
    ("float-count", "No chunks are visible", 2), ("missing-count", "No chunks are visible", 2)
})
{
    handler.Reset(mode);
    await Fails(() => workshop.ExecuteAsync("run"), expected);
    Check(handler.Requests.Count == requests && handler.Requests.All(r => r.Method != "PUT"), "Prerequisite failure wrote resources");
}
foreach (string selection in new[] { "blob", "web", "mixed" })
    foreach (var (mode, expected) in new[]
    {
        ("denied", "HTTP 403"), ("unauthorized", "HTTP 401"), ("throttled", "HTTP 429"),
        ("partial", "partial or a source failed"), ("activity-error", "partial or a source failed"),
        ("malformed", "invalid JSON"), ("network", "network failure"), ("timeout", "timed out"),
        ("echo", ""), ("no-references", "")
    })
    {
        handler.Reset(mode);
        var output = new StringWriter();
        var errors = new StringWriter();
        TextWriter savedOutput = Console.Out, savedError = Console.Error;
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            if (expected.Length == 0) await workshop.ExecuteAsync("retrieve-" + selection);
            else await Fails(() => workshop.ExecuteAsync("retrieve-" + selection), expected);
        }
        finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
        string logs = output + errors.ToString();
        Check(handler.Requests.Count == 1, "Automatic retrieval retry");
        Check(!logs.Contains(settings.WebIqKey) && !logs.Contains("synthetic-search-token"), "Credential echo leak");
        if (mode is "denied" or "unauthorized" or "throttled")
            foreach (string text in new[] { "UTC (Date):", "synthetic-request", "synthetic-correlation", "Response body:", "<redacted>" })
                Check(logs.Contains(text), "Missing diagnostics: " + text);
        if (mode == "throttled") Check(logs.Contains("retry-after", StringComparison.OrdinalIgnoreCase) && logs.Contains("Quota or throttling"), "Missing quota diagnostics");
        if (mode == "echo")
            Check(logs.Contains("https://example.test/handbook") && logs.Contains("Receipt evidence") && logs.Contains("<redacted>"), "Blob reference parsing/redaction");
        if (mode == "no-references")
            Check(logs.Contains("alone does not establish an authentication failure"), "False auth inference");
    }
foreach (string command in new[] { "blob-source", "webiq-source", "knowledge-base", "cleanup" })
{
    handler.Reset("denied");
    await Fails(() => workshop.ExecuteAsync(command, confirm: true), "HTTP 403");
    Check(handler.Requests.Count == 1, "Management failure ignored");
}
foreach (string command in new[] { "blob-source", "webiq-source", "knowledge-base" })
{
    handler.Reset("echo");
    var output = new StringWriter();
    TextWriter savedOutput = Console.Out;
    try
    {
        Console.SetOut(output);
        await workshop.ExecuteAsync(command);
    }
    finally { Console.SetOut(savedOutput); }
    Check(!output.ToString().Contains("Receipt evidence") &&
        !output.ToString().Contains(settings.WebIqKey) &&
        !output.ToString().Contains("synthetic-search-token"), "Source/base response echoed");
}
foreach (string mode in new[] { "", "missing" })
{
    handler.Reset(mode);
    await workshop.ExecuteAsync("cleanup", confirm: true);
    Check(handler.Requests.Select(r => r.Uri).SequenceEqual(new[]
    {
        "https://workshop.search.windows.net/knowledgebases/alice01-nb10-kb?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-nb10-webiq?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-nb10-blob?api-version=2026-08-01-preview"
    }), "Cleanup exact scope/order");
    Check(handler.Requests.All(r => r.Method == "DELETE" && r.Body is null), "Cleanup touched index");
}
handler.Reset();
await Fails(() => workshop.ExecuteAsync("cleanup"), "cleanup --confirm");
await Fails(() => workshop.ExecuteAsync("run", " "), "non-empty");
await Fails(() => workshop.ExecuteAsync("run", outputMode: "invalid"), "answerSynthesis or extractiveData");
await Fails(() => workshop.ExecuteAsync("unknown"), "Unknown command");
Check(handler.Requests.Count == 0, "Invalid invocation made HTTP requests");
var noAuth = new WebIqWorkshop(settings, new WebIqTransport(client, settings.Tenant,
    () => throw new InvalidOperationException("auth failed synthetic-test-key")));
await Fails(() => noAuth.ExecuteAsync("run"), "auth failed <redacted>");
Check(handler.Requests.Count == 0, "Authentication failure reached HTTP");
foreach (string[] argsCase in new[]
{
    new[] { "unknown" }, new[] { "cleanup" }, new[] { "cleanup", "--confirm", "extra" },
    new[] { "run", "-h" }, new[] { "run", "--bad" }, new[] { "run", "one", "two" },
    new[] { "retrieve-web", "--output-mode" }, new[] { "retrieve-web", "--output-mode", "bad" },
    new[] { "blob-source", "extra" }, new[] { "help", "extra" }
})
{
    bool rejected = false;
    try { WebIqOptions.Parse(argsCase); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Unknown/extra args accepted");
}
foreach (string[] argsCase in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" }, new[] { "-h" } })
    Check(WebIqOptions.Parse(argsCase).Command == "help", "Offline help alias");
string key = "synthetic\"key\\secret";
Check(!WebIqResults.Redact(JsonSerializer.Serialize(key), key).Contains("secret"), "JSON escaped secret leak");
Check(!WebIqResults.Redact(Uri.EscapeDataString(key), key).Contains("secret"), "URI escaped secret leak");
var escapedSettings = new WebIqSettings(settings.Tenant, settings.SearchEndpoint, settings.Account,
    settings.Deployment, settings.Model, settings.Prefix, key);
handler.Reset("escaped");
var escapedOutput = new StringWriter();
TextWriter priorOutput = Console.Out;
try
{
    Console.SetOut(escapedOutput);
    await new WebIqWorkshop(escapedSettings, transport).ExecuteAsync("retrieve-mixed");
}
finally { Console.SetOut(priorOutput); }
Check(!escapedOutput.ToString().Contains("secret") && escapedOutput.ToString().Contains("<redacted>"), "Nested reference secret leak");
Console.WriteLine($"Passed {cases} offline console protocol/runtime cases.");

public sealed record CapturedWebIqBlobRequest(string Method, string Uri, string? Authorization,
    bool HasApiKey, bool HasQueryAuthorization, JsonObject? Body);

public sealed class WebIqBlobOfflineHandler : HttpMessageHandler
{
    public List<CapturedWebIqBlobRequest> Requests { get; } = [];
    public string Mode { get; set; } = "";
    public void Reset(string mode = "") { Requests.Clear(); Mode = mode; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
            request.Headers.Contains("x-apikey"), request.Headers.Contains("x-ms-query-source-authorization"),
            request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject()));
        if (Mode == "network") throw new HttpRequestException("network failure synthetic-test-key synthetic-search-token");
        if (Mode == "timeout") throw new TaskCanceledException("synthetic-test-key");
        int status = Mode switch { "denied" => 403, "unauthorized" => 401, "throttled" => 429, "missing" => 404, "partial" => 206, _ => 200 };
        JsonObject body;
        if (request.RequestUri.AbsolutePath.StartsWith("/indexes/") && request.Method == HttpMethod.Get)
        {
            body = new JsonObject
            {
                ["fields"] = new JsonArray(
                    new JsonObject { ["name"] = "chunk_id", ["retrievable"] = true },
                    new JsonObject { ["name"] = "chunk", ["retrievable"] = true, ["searchable"] = true },
                    new JsonObject { ["name"] = "title", ["retrievable"] = true },
                    new JsonObject
                    {
                        ["name"] = "source",
                        ["fields"] = new JsonArray(
                        new JsonObject { ["name"] = "file_name", ["retrievable"] = true },
                        new JsonObject { ["name"] = "url", ["retrievable"] = true })
                    }),
                ["semantic"] = new JsonObject { ["configurations"] = new JsonArray(new JsonObject { ["name"] = "chunk-semantic" }) }
            };
            if (Mode == "schema") body["fields"]!.AsArray().RemoveAt(3);
            if (Mode == "unretrievable") body["fields"]![3]!["fields"]![1]!["retrievable"] = false;
            if (Mode == "unsearchable") body["fields"]![1]!["searchable"] = false;
            if (Mode == "semantic") body["semantic"]!["configurations"] = new JsonArray();
        }
        else if (request.RequestUri.AbsolutePath.EndsWith("/docs/search"))
        {
            body = new JsonObject { ["@odata.count"] = 7 };
            if (Mode == "empty") body["@odata.count"] = 0;
            if (Mode == "bool-count") body["@odata.count"] = true;
            if (Mode == "string-count") body["@odata.count"] = "7";
            if (Mode == "float-count") body["@odata.count"] = 1.5;
            if (Mode == "missing-count") body.Remove("@odata.count");
        }
        else
        {
            string echoed = Mode == "escaped" ? "synthetic\"key\\secret" : "synthetic-test-key synthetic-search-token";
            body = new JsonObject
            {
                ["activity"] = new JsonArray(new JsonObject { ["knowledgeSourceName"] = "alice01-nb10-blob", ["count"] = 1 }),
                ["response"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["text"] = Mode is "echo" or "escaped" ? echoed : "Receipts [ref_id:0]" })
                }),
                ["references"] = new JsonArray(new JsonObject
                {
                    ["id"] = "0",
                    ["sourceData"] = new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["title"] = "Travel handbook",
                            ["source"] = new JsonObject { ["url"] = "https://example.test/handbook" },
                            ["chunk"] = "Receipt evidence " + (Mode is "echo" or "escaped" ? echoed : "")
                        }.ToJsonString()
                    }
                })
            };
            if (Mode == "activity-error") body["activity"]![0]!["error"] = new JsonObject { ["message"] = "auth_missing_token " + echoed };
            if (Mode == "no-references") body["references"] = new JsonArray();
        }
        string text = status >= 400 ? "Failure synthetic-test-key synthetic-search-token" :
            Mode == "malformed" ? "{synthetic-test-key" : body.ToJsonString();
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
        response.Headers.Date = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", "synthetic-request");
        response.Headers.Add("x-ms-correlation-request-id", "synthetic-correlation");
        response.Headers.Add("Retry-After", "30");
        return response;
    }
}
