using System.Text.Json;
using System.Text.Json.Nodes;

TextWriter rootOutput = Console.Out, rootError = Console.Error;
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
int cases = 0;
void Check(bool condition, string message)
{
    cases++;
    if (!condition) throw new Exception(message);
}
async Task Fails(Func<Task> action, string expected)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(expected, StringComparison.Ordinal))
    {
        Check(!error.Message.Contains("synthetic-key") && !error.Message.Contains(ApimOfflineHandler.Jwt()), "Exception credential leak");
        return;
    }
    throw new Exception("Expected failure: " + expected);
}
Environment.SetEnvironmentVariable("WEBIQ_API_KEY", "synthetic-key");
Environment.SetEnvironmentVariable("BROKER_CLIENT_SECRET", "synthetic-broker-secret");
var settings = new ApimSettings(ApimOfflineHandler.Tenant, "https://offline.search.windows.net",
    ApimOfflineHandler.Broker, "https://offline.azure-api.net", "alice01");
var handler = new ApimOfflineHandler();
using var client = new HttpClient(handler);
int searchCalls = 0, brokerCalls = 0;
var transport = new ApimTransport(client, settings,
    () => { searchCalls++; return Task.FromResult("synthetic-search-token"); },
    () => { brokerCalls++; return Task.FromResult(ApimOfflineHandler.Jwt()); });
var workshop = new ApimWorkshop(settings, transport);
Check(handler.Requests.Count == 0 && searchCalls == 0 && brokerCalls == 0, "Hidden setup calls");
await workshop.ExecuteAsync("run");
Check(handler.Requests.Count == 16, "Run HTTP count");
Check(searchCalls == 12 && brokerCalls == 4, "Run token acquisition count");
Check(handler.Requests.Count(r => r.Method == "PUT") == 1 && handler.Requests.All(r => r.Method != "DELETE"), "Run writes only own KB; no cleanup");
var wire = handler.Requests.ToArray();
foreach (var request in wire)
{
    Check(request.Uri.EndsWith("?api-version=2026-08-01-preview"), "Version");
    bool gateway = request.Uri.Contains("azure-api.net/iq/", StringComparison.Ordinal);
    Check(request.Headers.All(h => h is "Authorization"), "Caller sent key/elevated-read/user-context headers");
    if (!gateway) Check(request.Authorization == "Bearer synthetic-search-token", "Search setup auth");
    else if (request.Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() != "hello")
        Check(request.Authorization == "Bearer " + ApimOfflineHandler.Jwt(), "Gateway broker auth");
    Check(!request.Uri.Contains("indexes/") && !request.Uri.Contains("management.azure.com"), "Changed index/gateway");
}
Check(wire[4].Method == "PUT" && wire[4].Uri.Contains("/knowledgebases/alice01-apim-kb?"), "Own KB target");
Check(wire[4].Body!["retrievalReasoningEffort"]!["kind"]!.ToString() == "medium", "Canonical medium");
Check(wire[4].Body!["retrievalInstructions"]!.ToString().Contains("iq-workshop-webiq") &&
    !wire[4].Body!["retrievalInstructions"]!.ToString().Contains("alice01-kb-webiq"), "Routing rewrite");
Check(JsonNode.DeepEquals(wire[4].Body!["models"], ApimOfflineHandler.BaseKb()["models"]), "Model reuse");
foreach (string command in new[] { "inspect-token", "check-webiq-source", "knowledge-base", "retrieve-internal", "retrieve-web", "retrieve-mixed", "check-rejections" })
{
    handler.Reset();
    await workshop.ExecuteAsync(command, command.StartsWith("retrieve-") ? "custom question" : null, "extractiveData", diagnostics: true);
    Check(handler.Requests.Count == command switch
    {
        "inspect-token" => 0,
        "check-webiq-source" => 1,
        "knowledge-base" => 5,
        "check-rejections" => 2,
        _ => 3
    }, "Independent command hidden work");
    if (command.StartsWith("retrieve-", StringComparison.Ordinal))
    {
        var request = handler.Requests[^1];
        Check(request.Body!["messages"]![0]!["content"]![0]!["text"]!.ToString() == "custom question", "Question override");
        Check(request.Body!["outputMode"]!.ToString() == "extractiveData", "Mode override");
        var parameters = request.Body!["knowledgeSourceParams"]!.AsArray();
        Check(parameters.All(s => s!["includeReferences"]!.GetValue<bool>() && s["includeReferenceSourceData"]!.GetValue<bool>()), "Reference data for all sources");
        Check(parameters[0]!["kind"]!.ToString() == "azureBlob" && parameters[1]!["kind"]!.ToString() == "searchIndex", "Actual source kinds");
    }
}
foreach (var (mode, expected, target) in new[]
{
    ("missing", "HTTP 404", ""), ("wrong-source", "Shared source", ""), ("wrong-server", "Shared source", ""),
    ("no-kind", "no kind", "-kb-blob"), ("no-model", "model and routing", "/alice01-kb"),
    ("no-routing", "model and routing", "/alice01-kb"), ("partial", "Partial setup", ""),
    ("malformed", "Invalid JSON", ""), ("denied", "HTTP 403", "")
})
{
    handler.Reset(mode, target);
    await Fails(() => workshop.ExecuteAsync("knowledge-base"), expected);
    Check(handler.Requests.All(r => r.Method == "GET"), "Prerequisite failure wrote own/shared resources");
}
foreach (string selection in new[] { "internal", "web", "mixed" })
    foreach (var (mode, expected) in new[]
    {
        ("denied", "HTTP 403"), ("unauthorized", "HTTP 401"), ("throttled", "HTTP 429"),
        ("partial", "partial or a source failed"), ("activity-error", "partial or a source failed"),
        ("shape", "Malformed retrieval"), ("activity-shape", "Malformed retrieval"), ("reference-shape", "Malformed retrieval"),
        ("malformed", "Invalid JSON"), ("network", "network failure"), ("timeout", "timed out"),
        ("echo", ""), ("no-references", "")
    })
    {
        handler.Reset(mode, "/iq/");
        var output = new StringWriter(); var errors = new StringWriter();
        TextWriter originalOut = Console.Out, originalError = Console.Error;
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            if (expected.Length == 0) await workshop.ExecuteAsync("retrieve-" + selection, diagnostics: true);
            else await Fails(() => workshop.ExecuteAsync("retrieve-" + selection, diagnostics: true), expected);
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        string logs = output + errors.ToString();
        Check(handler.Requests.Count == 3, "Extra retrieval/retry");
        foreach (string secret in new[] { "synthetic-key", "synthetic-broker-secret", ApimOfflineHandler.Jwt(), "unconfigured-key", "unconfigured-secret" })
            Check(!logs.Contains(secret), "Output secret leak: " + secret);
        if (mode is "denied" or "unauthorized" or "throttled")
        {
            Check(logs.Contains("offline-request") && logs.Contains("offline-correlation") && logs.Contains("UTC (Date):"), "Error IDs/date");
            Check(errors.ToString().Length < 8800, "Unbounded error body");
        }
        if (mode == "echo")
            Check(logs.Contains("travel.pdf pp. 2-4") && logs.Contains("report.pdf p. 7") &&
                logs.Contains("https://example.test/public#section") && logs.Contains("Raw references:"), "Rich references/diagnostics");
    }
handler.Reset("echo");
var managementOutput = new StringWriter();
TextWriter saved = Console.Out;
try { Console.SetOut(managementOutput); await workshop.ExecuteAsync("check-webiq-source"); }
finally { Console.SetOut(saved); }
Check(!managementOutput.ToString().Contains("unconfigured-shared-key"), "Shared source credential print");
foreach (string mode in new[] { "", "missing" })
{
    handler.Reset(mode);
    await workshop.ExecuteAsync("cleanup", confirm: true);
    Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "DELETE" &&
        handler.Requests[0].Uri == "https://offline.search.windows.net/knowledgebases/alice01-apim-kb?api-version=2026-08-01-preview", "Exact cleanup target");
}
handler.Reset();
await Fails(() => workshop.ExecuteAsync("cleanup"), "cleanup --confirm");
await Fails(() => workshop.ExecuteAsync("unknown"), "Unknown command");
await Fails(() => workshop.ExecuteAsync("run", " "), "non-empty");
Check(handler.Requests.Count == 0, "Invalid step reached HTTP");
await Fails(() => transport.SendAsync(HttpMethod.Put, "knowledgesources/iq-workshop-webiq"), "Only this lab");
await Fails(() => transport.SendAsync(HttpMethod.Delete, "knowledgebases/alice01-kb"), "Only this lab");
await Fails(() => transport.SendAsync(HttpMethod.Post, "knowledgebases/alice01-apim-kb/retrieve"), "only APIM");
foreach (var (token, expected) in new[]
{
    (ApimOfflineHandler.Jwt("https://search.azure.com"), "not for the broker"),
    (ApimOfflineHandler.Jwt(scope: "not_user_impersonation"), "missing delegated"),
    (ApimOfflineHandler.Jwt(tenant: ApimOfflineHandler.Broker), "tenant differs"), ("invalid-token", "cannot be decoded")
})
{
    handler.Reset();
    var invalid = new ApimTransport(client, settings, () => Task.FromResult("synthetic-search-token"), () => Task.FromResult(token));
    await Fails(() => invalid.SendAsync(HttpMethod.Post, "knowledgebases/alice01-apim-kb/retrieve",
        ApimWorkshop.RetrievePayload(settings, new() { [settings.BlobSourceName] = "azureBlob", [settings.SharePointSourceName] = "searchIndex" },
            "question", "answerSynthesis", "web"), gateway: true), expected);
    Check(handler.Requests.Count == 0, "Invalid broker claims reached APIM");
}
ApimTransport.InspectToken(ApimOfflineHandler.Jwt(settings.BrokerResource, "other user_impersonation"), settings);
var start = ApimTransport.TokenStartInfo(settings.Tenant, settings.BrokerResource);
Check(start.ArgumentList.Contains("--resource") && start.ArgumentList.Contains(settings.BrokerResource) &&
    !start.ArgumentList.Any(a => a.Contains("/user_impersonation") || a.Contains("/.default")), "CLI broker resource semantics");
await Fails(() => Task.Run(() => ApimTransport.TokenStartInfo(settings.Tenant, settings.BrokerResource + "/user_impersonation")), "without a permission suffix");
handler.Reset("bad-rejection");
await Fails(() => workshop.ExecuteAsync("check-rejections"), "reject both probes");
Check(handler.Requests.Count == 1, "Unexpected extra rejection probes");
handler.Reset();
var failedAuth = new ApimTransport(client, settings, () => throw new InvalidOperationException("synthetic-key auth failure"));
await Fails(() => failedAuth.SendAsync(HttpMethod.Get, "knowledgesources/iq-workshop-webiq"), "<redacted> auth failure");
Check(handler.Requests.Count == 0, "Auth failure reached HTTP");
foreach (string[] invalid in new[]
{
    new[] { "unknown" }, new[] { "cleanup" }, new[] { "cleanup", "--confirm", "extra" }, new[] { "cleanup", "--help" },
    new[] { "knowledge-base", "extra" }, new[] { "run", "--bad" }, new[] { "run", "one", "two" }, new[] { "run", " " },
    new[] { "run", "--output-mode" }, new[] { "run", "--output-mode", "invalid" }, new[] { "run", "--output-mode", "--diagnostics" },
    new[] { "run", "--diagnostics", "--diagnostics" }, new[] { "run", "--output-mode", "extractiveData", "--output-mode", "extractiveData" },
    new[] { "help", "extra" }, new[] { "check-rejections", "--diagnostics" }
})
{
    bool rejected = false;
    try { ApimOptions.Parse(invalid); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Strict args " + string.Join(" ", invalid));
}
foreach (string[] alias in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" }, new[] { "-h" }, new[] { "HELP" } })
    Check(ApimOptions.Parse(alias).Command == "help", "Offline help aliases");
var references = ApimOfflineHandler.References().OfType<JsonObject>().ToList();
references.Add(new JsonObject { ["id"] = "string", ["type"] = "mcpServer", ["sourceData"] = """{"content":"{\"content\":\"{\\\"title\\\":\\\"Nested\\\",\\\"url\\\":\\\"https://example.test/nested\\\"}\"}"}""" });
references.Add(new JsonObject { ["id"] = "fallback", ["docKey"] = "opaque-key", ["sourceData"] = "not JSON" });
references.Add(new JsonObject { ["id"] = "badpages", ["sourceData"] = new JsonObject { ["page_from"] = 5, ["page_to"] = 2, ["url"] = "https://example.test/doc#section" } });
Check(ApimResults.FormatReference(references[3]).Contains("Nested") && ApimResults.FormatReference(references[3]).Contains("https://example.test/nested"), "Nested sourceData");
Check(ApimResults.FormatReference(references[4]).Contains("opaque-key") && !ApimResults.FormatReference(references[4]).Contains("https://"), "Fallback no fabricated link");
Check(ApimResults.FormatReference(references[5]).Contains("#section") && !ApimResults.FormatReference(references[5]).Contains("p. "), "No fabricated pages");
string escapedSecret = "quoted-\"key\\value";
Check(!ApimResults.Redact(JsonSerializer.Serialize(JsonSerializer.Serialize(escapedSecret)), escapedSecret).Contains("quoted-"), "Nested escaped secret");
Check(ApimResults.Redact("https://private.example.test/doc?sig=synthetic-signature&sv=2026")
    == "https://private.example.test/doc?sig=<redacted>&sv=2026", "URL credential");
var mixedCaseSettings = new ApimSettings("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", settings.SearchEndpoint,
    "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB", settings.Gateway, settings.Prefix);
ApimTransport.InspectToken(ApimOfflineHandler.Jwt("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    tenant: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), mixedCaseSettings);
Check(mixedCaseSettings.BrokerResource == "api://bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "GUID normalization");
if (args.Length == 2 && args[0] == "--wire")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new
    {
        Requests = wire,
        BaseKb = ApimOfflineHandler.BaseKb(),
        ReferenceCases = references.Select(r => new { Reference = r, Formatted = ApimResults.FormatReference(r) })
    }));
else if (args.Length != 0) throw new ArgumentException("Use --wire <owned-output-path> or no arguments.");
Console.SetOut(rootOutput);
Console.SetError(rootError);
Console.WriteLine($"PASS: {cases} offline APIM protocol/runtime cases.");
