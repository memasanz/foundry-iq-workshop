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
        Check(!error.Message.Contains("synthetic-test-key") && !error.Message.Contains("synthetic-search-token"), "Exception leaked credential");
        return;
    }
    throw new Exception("Expected failure: " + message);
}
var settings = new KnowledgeBaseSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "workshop-ai", "workshop-chat", "gpt-5-mini",
    "alice01", "synthetic-test-key");
var handler = new KnowledgeBaseOfflineHandler();
using var client = new HttpClient(handler);
int tokenCalls = 0;
var transport = new KnowledgeBaseTransport(client, settings.Tenant, () =>
{
    tokenCalls++;
    return Task.FromResult("synthetic-search-token");
});
var workshop = new KnowledgeBaseWorkshop(settings, transport);
await workshop.ExecuteAsync("run");
Check(handler.Requests.Count == 12 && tokenCalls == 12, "Run must verify two indexes, PUT four definitions and retrieve four questions");
Check(handler.Requests.Select(r => r.Method).SequenceEqual(["GET", "POST", "GET", "POST", "PUT", "PUT", "PUT", "PUT", "POST", "POST", "POST", "POST"]), "Hidden run actions");
for (int i = 0; i < handler.Requests.Count; i++)
{
    var request = handler.Requests[i];
    Check(request.Uri.EndsWith("?api-version=2026-08-01-preview"), "API version");
    Check(request.Authorization == "Bearer synthetic-search-token", "Bearer credential");
    Check(request.QueryAuthorization == (i == 3 || i >= 8 ? "synthetic-search-token" : null), "User context only for ACL count/all retrievals");
    Check(!request.HasApiKey, "Web IQ key must not be a direct Search header");
}
Check(handler.Requests[0].Uri.Contains("/indexes/alice01-cu-chunks?") &&
    handler.Requests[2].Uri.Contains("/indexes/alice01-nb7-index?"), "Wrong existing indexes");
var source = handler.Requests[6].Body!;
Check(source["mcpServerParameters"]!["authentication"]!["storedHeadersParameters"]!["headers"]!["x-apikey"]!.ToString() == settings.WebIqKey, "Stored Web IQ key");
Check(source["mcpServerParameters"]!["tools"]!.AsArray().Count == 1 &&
    source["mcpServerParameters"]!["tools"]![0]!["name"]!.ToString() == "web", "Only basic web tool");
Check(handler.Requests[7].Body!["retrievalReasoningEffort"]!["kind"]!.ToString() == "medium", "Current reasoning");
Check(!handler.Requests[5].Body!.ToJsonString().Contains("UserIds"), "ACLs must not be returned in source data");
var wire = handler.Requests.ToArray();

foreach (string command in new[] { "verify-indexes", "blob-source", "sharepoint-source", "webiq-source", "knowledge-base",
    "retrieve-internal", "retrieve-web", "retrieve-sharepoint", "retrieve-mixed" })
{
    handler.Reset();
    int calls = tokenCalls;
    await workshop.ExecuteAsync(command, command.StartsWith("retrieve-") ? "custom question" : null, "extractiveData", diagnostics: true);
    Check(handler.Requests.Count == (command == "verify-indexes" ? 4 : 1), "Independent step has hidden actions");
    Check(tokenCalls - calls == handler.Requests.Count, "More than one token per request");
    if (command.StartsWith("retrieve-"))
    {
        var body = handler.Requests[0].Body!;
        Check(body["outputMode"]!.ToString() == "extractiveData", "Mode ignored");
        Check(body["messages"]![0]!["content"]![0]!["text"]!.ToString() == "custom question", "Question ignored");
        Check(body["knowledgeSourceParams"]!.AsArray().Count == 3 &&
            body["knowledgeSourceParams"]!.AsArray().All(s => s!["includeReferenceSourceData"]!.GetValue<bool>()), "All sources need rich data in every query");
        Check(!body.ToJsonString().Contains(settings.WebIqKey), "Key in retrieve body");
    }
}
handler.Reset();
await workshop.ExecuteAsync("run", "custom mixed", "extractiveData");
for (int i = 0; i < 4; i++)
    Check(handler.Requests[8 + i].Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() ==
        (i == 3 ? "custom mixed" : KnowledgeBaseWorkshop.Question(settings, new[] { "internal", "web", "sharepoint" }[i])), "Run question override");

foreach (string index in new[] { "blob", "sharepoint" })
    foreach (var (mode, expected, localRequests) in new[]
    {
        ("missing", "HTTP 404", 1), ("schema", "retrievable content/source", 1),
        ("page-schema", "retrievable content/source", 1), ("unretrievable", "retrievable content/source", 1),
        ("unsearchable", "retrievable content/source", 1), ("semantic", "missing semantic configuration", 1),
        ("empty", "No chunks are visible", 2), ("bool-count", "No chunks are visible", 2),
        ("string-count", "No chunks are visible", 2), ("float-count", "No chunks are visible", 2),
        ("missing-count", "No chunks are visible", 2), ("partial", "partial response", 1)
    })
    {
        handler.Reset(mode, index);
        await Fails(() => workshop.ExecuteAsync("run"), expected);
        Check(handler.Requests.Count == localRequests + (index == "blob" ? 0 : 2) &&
            handler.Requests.All(r => r.Method != "PUT"), "Prerequisite failure wrote resources");
    }
foreach (string mode in new[] { "acl-disabled", "acl-user", "acl-group", "acl-missing" })
{
    handler.Reset(mode, "sharepoint");
    await Fails(() => workshop.ExecuteAsync("run"), "user/group permission filtering");
    Check(handler.Requests.Count == 3 && handler.Requests.All(r => r.Method != "PUT"), "ACL failure wrote resources");
}

foreach (string selection in new[] { "internal", "web", "sharepoint", "mixed" })
    foreach (var (mode, expected) in new[]
    {
        ("denied", "HTTP 403"), ("unauthorized", "HTTP 401"), ("throttled", "HTTP 429"),
        ("partial", "partial or a source failed"), ("activity-error", "partial or a source failed"),
        ("malformed", "invalid JSON"), ("malformed-shape", "Malformed retrieval response"),
        ("malformed-activity", "Malformed retrieval response"), ("malformed-references", "Malformed retrieval response"),
        ("network", "network failure"), ("timeout", "timed out"),
        ("echo", ""), ("no-references", ""), ("missing-references", "")
    })
    {
        handler.Reset(mode);
        var output = new StringWriter();
        var errors = new StringWriter();
        TextWriter savedOutput = Console.Out, savedError = Console.Error;
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            if (expected.Length == 0) await workshop.ExecuteAsync("retrieve-" + selection, diagnostics: true);
            else await Fails(() => workshop.ExecuteAsync("retrieve-" + selection, diagnostics: true), expected);
        }
        finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
        string logs = output + errors.ToString();
        Check(handler.Requests.Count == 1, "Automatic paid retry");
        Check(!logs.Contains(settings.WebIqKey) && !logs.Contains("synthetic-search-token"), "Credential echo leak");
        if (mode is "denied" or "unauthorized" or "throttled")
            foreach (string text in new[] { "UTC (Date):", "synthetic-request", "synthetic-correlation", "Response body:", "<redacted>" })
                Check(logs.Contains(text), "Missing diagnostics: " + text);
        if (mode == "throttled") Check(logs.Contains("Retry-After", StringComparison.OrdinalIgnoreCase), "Quota diagnostics");
        if (mode == "echo")
            foreach (string text in new[] { "Full activity:", "Web IQ activity only:", "Raw references:", "[ref_id:0] travel.pdf pp. 2-4",
                "https://private.blob.core.windows.net/docs/travel.pdf#page=2", "[ref_id:1] report.pdf p. 7",
                "[ref_id:2] Public evidence", "https://example.test/public#section", "<redacted>" })
                Check(logs.Contains(text), "Missing mixed evidence/diagnostic: " + text);
        if (mode is "no-references" or "missing-references")
            Check(logs.Contains("alone does not establish an authentication failure"), "False auth inference");
    }
handler.Reset();
var normalOutput = new StringWriter();
TextWriter prior = Console.Out;
try { Console.SetOut(normalOutput); await workshop.ExecuteAsync("retrieve-web"); }
finally { Console.SetOut(prior); }
Check(!normalOutput.ToString().Contains("Raw references:") && !normalOutput.ToString().Contains("private passage"), "Unexpected raw diagnostics");
int before = handler.Requests.Count, authBefore = tokenCalls;
KnowledgeBaseResults.Diagnostics(transport.LastResult, transport.LastStatus);
Check(handler.Requests.Count == before && tokenCalls == authBefore, "Diagnostics made another request");
handler.Reset("network");
await Fails(() => workshop.ExecuteAsync("retrieve-web"), "network failure");
Check(transport.LastResult is null && transport.LastStatus == 0, "Failed request retained stale diagnostics");

foreach (string command in new[] { "blob-source", "sharepoint-source", "webiq-source", "knowledge-base", "cleanup" })
{
    handler.Reset("denied");
    await Fails(() => workshop.ExecuteAsync(command, confirm: true), "HTTP 403");
    Check(handler.Requests.Count == 1, "Management failure ignored");
}
foreach (string command in new[] { "blob-source", "sharepoint-source", "webiq-source", "knowledge-base" })
{
    handler.Reset("echo");
    var output = new StringWriter();
    prior = Console.Out;
    try { Console.SetOut(output); await workshop.ExecuteAsync(command); }
    finally { Console.SetOut(prior); }
    Check(!output.ToString().Contains("private passage") && !output.ToString().Contains(settings.WebIqKey), "Management payload echoed");
}
foreach (string mode in new[] { "", "missing" })
{
    handler.Reset(mode);
    await workshop.ExecuteAsync("cleanup", confirm: true);
    Check(handler.Requests.Select(r => r.Uri).SequenceEqual(new[]
    {
        "https://workshop.search.windows.net/knowledgebases/alice01-kb?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-kb-webiq?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-kb-sharepoint?api-version=2026-08-01-preview",
        "https://workshop.search.windows.net/knowledgesources/alice01-kb-blob?api-version=2026-08-01-preview"
    }), "Cleanup exact scope/order");
    Check(handler.Requests.All(r => r.Method == "DELETE" && r.QueryAuthorization is null), "Cleanup touched indexes/user context");
}
handler.Reset();
await Fails(() => workshop.ExecuteAsync("cleanup"), "cleanup --confirm");
await Fails(() => workshop.ExecuteAsync("run", " "), "non-empty");
await Fails(() => workshop.ExecuteAsync("run", outputMode: "wrong"), "answerSynthesis or extractiveData");
await Fails(() => workshop.ExecuteAsync("unknown"), "Unknown command");
Check(handler.Requests.Count == 0, "Invalid command made requests");
var noAuth = new KnowledgeBaseWorkshop(settings, new KnowledgeBaseTransport(client, settings.Tenant,
    () => throw new InvalidOperationException("auth failed synthetic-test-key")));
await Fails(() => noAuth.ExecuteAsync("run"), "auth failed <redacted>");
Check(handler.Requests.Count == 0, "Auth failure reached HTTP");
foreach (string[] argsCase in new[]
{
    new[] { "unknown" }, new[] { "cleanup" }, new[] { "cleanup", "--confirm", "extra" }, new[] { "cleanup", "--help" },
    new[] { "run", "-h" }, new[] { "run", "--bad" }, new[] { "run", "one", "two" }, new[] { "run", " " },
    new[] { "retrieve-web", "--output-mode" }, new[] { "retrieve-web", "--output-mode", "bad" },
    new[] { "run", "--output-mode", "extractiveData", "--output-mode", "extractiveData" },
    new[] { "run", "--diagnostics", "--diagnostics" }, new[] { "webiq-source", "--diagnostics" }, new[] { "help", "extra" }
})
{
    bool rejected = false;
    try { KnowledgeBaseOptions.Parse(argsCase); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Invalid CLI arguments accepted");
}
foreach (string[] argsCase in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" }, new[] { "-h" } })
    Check(KnowledgeBaseOptions.Parse(argsCase).Command == "help", "Offline help");
Check(KnowledgeBaseOptions.Parse(["retrieve-web", "--diagnostics", "custom", "--output-mode", "extractiveData"]).Diagnostics, "Diagnostics flag parsing");

var referenceCases = new List<object>();
foreach (var (data, docKey, expected) in new (JsonNode?, string?, string)[]
{
    (JsonNode.Parse("""{"source/file_name":"flat.pdf","source/url":"https://example.test/a#old","page_from":1,"page_to":2}"""), null, "flat.pdf pp. 1-2 https://example.test/a#page=1"),
    (JsonNode.Parse("""{"source":{"file_name":"nested.pdf","url":"https://example.test/n"},"page_from":3,"page_to":3}"""), null, "nested.pdf p. 3 https://example.test/n#page=3"),
    (JsonValue.Create("""{"sourceFile":"sp.pdf","webUrl":"https://example.test/sp#old","page":4,"pageTo":6}"""), null, "sp.pdf pp. 4-6 https://example.test/sp#page=4"),
    (JsonNode.Parse("""{"content":{"title":"Web title","url":"https://example.test/web#section"}}"""), null, "Web title  https://example.test/web#section"),
    (JsonValue.Create("""{"content":"{\"title\":\"Encoded web\",\"url\":\"https://example.test/encoded#fragment\"}"}"""), null, "Encoded web  https://example.test/encoded#fragment"),
    (JsonNode.Parse("""{"source":"not an object","content":"not json","title":"Fallback"}"""), null, "Fallback  (No source URL returned.)"),
    (JsonValue.Create("not json"), "chunk-42", "chunk-42  (No source URL returned.)"),
    (JsonValue.Create("[1,2]"), null, "Source  (No source URL returned.)"),
    (JsonNode.Parse("""{"source/url":"https://example.test/bad#keep","page_from":2,"page_to":1}"""), null, "Source  https://example.test/bad#keep"),
    (JsonNode.Parse("""{"url":"https://example.test/zero#keep","page_from":0,"page_to":1}"""), null, "Source  https://example.test/zero#keep"),
    (JsonNode.Parse("""{"url":"https://example.test/string#keep","page_from":"1","page_to":2}"""), null, "Source  https://example.test/string#keep"),
    (JsonNode.Parse("""{"url":"https://example.test/bool#keep","page_from":true,"page_to":2}"""), null, "Source  https://example.test/bool#keep"),
    (JsonNode.Parse("""{"url":"https://example.test/missing#keep","page_from":1}"""), null, "Source  https://example.test/missing#keep"),
    (JsonNode.Parse("""{"url":"https://example.test/null#keep","page_from":null,"page":1,"pageTo":2}"""), null, "Source  https://example.test/null#keep"),
    (null, null, "Source  (No source URL returned.)")
})
{
    var reference = new JsonObject { ["id"] = 9, ["sourceData"] = data?.DeepClone(), ["docKey"] = docKey };
    string formatted = KnowledgeBaseResults.FormatReference(reference);
    Check(formatted == "- [ref_id:9] " + expected, "Rich reference fallback: " + formatted);
    referenceCases.Add(new { Reference = reference, Formatted = formatted });
}
string key = "synthetic\"key\\secret";
var escapedSettings = new KnowledgeBaseSettings(settings.Tenant, settings.SearchEndpoint, settings.Account,
    settings.Deployment, settings.Model, settings.Prefix, key);
handler.Reset("escaped");
var escapedOutput = new StringWriter();
prior = Console.Out;
try { Console.SetOut(escapedOutput); await new KnowledgeBaseWorkshop(escapedSettings, transport).ExecuteAsync("retrieve-mixed", diagnostics: true); }
finally { Console.SetOut(prior); }
Check(!escapedOutput.ToString().Contains("secret") && escapedOutput.ToString().Contains("<redacted>"), "Nested escaped key leak");
if (args.Length == 2 && args[0] == "--wire")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { Requests = wire, ReferenceCases = referenceCases }));
Console.WriteLine($"Passed {cases} offline console protocol/runtime cases.");

public sealed record CapturedKnowledgeBaseRequest(string Method, string Uri, string? Authorization,
    bool HasApiKey, string? QueryAuthorization, JsonObject? Body);

public sealed class KnowledgeBaseOfflineHandler : HttpMessageHandler
{
    public List<CapturedKnowledgeBaseRequest> Requests { get; } = [];
    public string Mode { get; set; } = "";
    public string FailureIndex { get; set; } = "";
    public void Reset(string mode = "", string failureIndex = "") { Requests.Clear(); Mode = mode; FailureIndex = failureIndex; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
            request.Headers.Contains("x-apikey"),
            request.Headers.TryGetValues("x-ms-query-source-authorization", out var context) ? context.Single() : null,
            request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject()));
        string path = request.RequestUri.AbsolutePath;
        bool sharePoint = path.Contains("-nb7-index");
        string mode = FailureIndex == "" || sharePoint == (FailureIndex == "sharepoint") ? Mode : "";
        if (mode == "network") throw new HttpRequestException("network failure synthetic-test-key synthetic-search-token");
        if (mode == "timeout") throw new TaskCanceledException("synthetic-test-key");
        int status = mode switch { "denied" => 403, "unauthorized" => 401, "throttled" => 429, "missing" => 404, "partial" => 206, _ => 200 };
        JsonObject body;
        if (path.StartsWith("/indexes/") && request.Method == HttpMethod.Get)
        {
            string[] names = sharePoint ? ["id", "content", "sourceFile", "webUrl", "page", "pageTo"] :
                ["chunk_id", "chunk", "title", "page_from", "page_to"];
            var fields = new JsonArray(names.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n,
                ["retrievable"] = true,
                ["searchable"] = n == (sharePoint ? "content" : "chunk")
            }).ToArray());
            if (sharePoint)
            {
                fields.Add(new JsonObject { ["name"] = "UserIds", ["permissionFilter"] = "userIds" });
                fields.Add(new JsonObject { ["name"] = "GroupIds", ["permissionFilter"] = "groupIds" });
            }
            else fields.Add(new JsonObject
            {
                ["name"] = "source",
                ["fields"] = new JsonArray(new JsonObject { ["name"] = "file_name", ["retrievable"] = true },
                    new JsonObject { ["name"] = "url", ["retrievable"] = true })
            });
            body = new JsonObject
            {
                ["fields"] = fields,
                ["semantic"] = new JsonObject { ["configurations"] = new JsonArray(new JsonObject { ["name"] = sharePoint ? "semantic-config" : "chunk-semantic" }) }
            };
            if (sharePoint) body["permissionFilterOption"] = "enabled";
            if (mode == "schema") fields.RemoveAt(0);
            if (mode == "page-schema") fields.RemoveAt(4);
            if (mode == "unretrievable") fields[2]!["retrievable"] = false;
            if (mode == "unsearchable") fields[1]!["searchable"] = false;
            if (mode == "semantic") body["semantic"]!["configurations"] = new JsonArray();
            if (mode == "acl-disabled") body["permissionFilterOption"] = "disabled";
            if (mode == "acl-user") fields[6]!["permissionFilter"] = "groupIds";
            if (mode == "acl-group") fields[7]!["permissionFilter"] = "userIds";
            if (mode == "acl-missing") fields.RemoveAt(6);
        }
        else if (path.EndsWith("/docs/search"))
        {
            body = new JsonObject { ["@odata.count"] = 7 };
            if (mode == "empty") body["@odata.count"] = 0;
            if (mode == "bool-count") body["@odata.count"] = true;
            if (mode == "string-count") body["@odata.count"] = "7";
            if (mode == "float-count") body["@odata.count"] = 1.5;
            if (mode == "missing-count") body.Remove("@odata.count");
        }
        else
        {
            string echoed = mode == "escaped" ? "synthetic\"key\\secret" : "synthetic-test-key synthetic-search-token";
            var references = new JsonArray(
                new JsonObject
                {
                    ["id"] = "0",
                    ["sourceData"] = new JsonObject
                    {
                        ["source"] = new JsonObject { ["file_name"] = "travel.pdf", ["url"] = "https://private.blob.core.windows.net/docs/travel.pdf#old" },
                        ["chunk"] = "private passage",
                        ["page_from"] = 2,
                        ["page_to"] = 4
                    }
                },
                new JsonObject
                {
                    ["id"] = "1",
                    ["sourceData"] = new JsonObject
                    {
                        ["sourceFile"] = "report.pdf",
                        ["webUrl"] = "https://example.sharepoint.com/report.pdf",
                        ["page"] = 7,
                        ["pageTo"] = 7
                    }.ToJsonString()
                },
                new JsonObject
                {
                    ["id"] = "2",
                    ["sourceData"] = new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["title"] = "Public evidence",
                            ["url"] = "https://example.test/public#section",
                            ["debug"] = mode is "echo" or "escaped" ? echoed : ""
                        }.ToJsonString()
                    }.ToJsonString()
                });
            body = new JsonObject
            {
                ["activity"] = new JsonArray(new JsonObject { ["type"] = "searchIndex", ["knowledgeSourceName"] = "alice01-kb-blob", ["count"] = 1 },
                    new JsonObject { ["type"] = "searchIndex", ["knowledgeSourceName"] = "alice01-kb-sharepoint", ["count"] = 1 },
                    new JsonObject { ["type"] = "mcpServer", ["knowledgeSourceName"] = "alice01-kb-webiq", ["count"] = 1 }),
                ["response"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["text"] = mode is "echo" or "escaped" ? echoed : "Evidence [ref_id:0] [ref_id:1] [ref_id:2]" }) }),
                ["references"] = references
            };
            if (mode == "activity-error") body["activity"]![2]!["error"] = new JsonObject { ["message"] = "auth_missing_token " + echoed };
            if (mode == "no-references") body["references"] = new JsonArray();
            if (mode == "missing-references") body.Remove("references");
            if (mode == "malformed-shape") body["response"] = "bad";
            if (mode == "malformed-activity") body["activity"] = new JsonArray("bad");
            if (mode == "malformed-references") body["references"] = new JsonObject();
        }
        string text = status >= 400 ? "Failure synthetic-test-key synthetic-search-token" :
            mode == "malformed" ? "{synthetic-test-key" : body.ToJsonString();
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text) };
        response.Headers.Date = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", "synthetic-request");
        response.Headers.Add("x-ms-correlation-request-id", "synthetic-correlation");
        response.Headers.Add("Retry-After", "30");
        return response;
    }
}
