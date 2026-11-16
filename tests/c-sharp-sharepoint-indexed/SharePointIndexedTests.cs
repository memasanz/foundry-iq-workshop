using System.Text;
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
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
    {
        cases++;
        return;
    }
    throw new Exception("Expected failure: " + message);
}
TextWriter originalOut = Console.Out, originalError = Console.Error;
using var logs = new StringWriter();
Console.SetOut(logs);
Console.SetError(logs);
string root = Path.GetFullPath(Path.Combine(".workshop", "sp07-test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    SharePointSettings settings = SpTestData.Settings with { Root = root };
    File.WriteAllText(Path.Combine(root, ".env"), "WORKSHOP_PREFIX=wrong01\n");
    File.WriteAllText(Path.Combine(root, ".env.azure"), SpTestData.EnvironmentText);
    var handler = new SpOfflineHandler();
    using var client = new HttpClient(handler);
    int tokens = 0, pauses = 0;
    Task<string> Token(string resource)
    {
        Check(resource == SharePointTransport.Audience, "Wrong token resource");
        tokens++;
        return Task.FromResult(SpTestData.Token());
    }
    Task Delay(TimeSpan duration) { Check(duration == TimeSpan.FromSeconds(10), "Polling interval"); pauses++; return Task.CompletedTask; }
    SharePointWorkshop Lab(bool persist = false, Func<string, Task<string>>? token = null) =>
        new(settings, new SharePointTransport(client, settings, token ?? Token), Delay, persist);
    SpTestData.Definitions["datasources"] = Lab().DataSourceDefinition();
    SpTestData.Definitions["indexes"] = Lab().IndexDefinition();
    SpTestData.Definitions["skillsets"] = Lab().SkillsetDefinition();
    SpTestData.Definitions["indexers"] = Lab().IndexerDefinition();
    int result = await SharePointIndexedProgram.RunAsync(new[] { "run", "--setup-confirmed" }, root, client, Token, Delay);
    Check(result == 0 && handler.Requests.Count == 8, "Full new-indexer flow hidden/missing operations");
    Check(tokens == 8 && pauses == 1, "Token refresh/polling count");
    Check(handler.Requests.All(r => r.Method != "DELETE"), "Run cleans up");
    foreach (var request in handler.Requests)
    {
        Check(request.Url.StartsWith(settings.SearchEndpoint + "/") && request.Url.EndsWith("?api-version=2026-08-01-preview"), "Endpoint/version");
        Check(request.Authorization == "Bearer " + SpTestData.Token() && !request.HasKey && !request.HasElevatedRead, "Key/elevated credential");
        Check(request.SourceAuthorization == (request.Url.Contains("/docs/search?") ? SpTestData.Token() : null), "Wrong user header placement");
    }
    var source = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/datasources/")).Body!;
    var index = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/indexes/")).Body!;
    var skills = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/skillsets/")).Body!;
    var indexer = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/indexers/")).Body!;
    var query = handler.Requests.Single(r => r.Url.Contains("/docs/search?")).Body!;
    Check(source["credentials"]!["connectionString"]!.ToString() == settings.ConnectionString(), "Federation IDs");
    Check(!source.ToJsonString().Contains("Secret") && source["container"]!["name"]!.ToString() == "defaultSiteLibrary", "Secret/crawl scope");
    Check(JsonNode.DeepEquals(source["indexerPermissionOptions"], new JsonArray("userIds", "groupIds")), "Source ACL options");
    Check(index["permissionFilterOption"]!.ToString() == "enabled", "Permissions disabled");
    foreach (string name in new[] { "UserIds", "GroupIds", "metadata_spo_item_path", "contentVector" })
        Check(index["fields"]!.AsArray().Single(f => f!["name"]!.ToString() == name)!["retrievable"]!.GetValue<bool>() == false, "Sensitive field returned");
    Check(skills["skills"]!.AsArray().Count == 2 && !skills.ToJsonString().Contains("Vision"), "Not text-only");
    Check(JsonNode.DeepEquals(skills["skills"]![0]!["chunkingProperties"],
        JsonNode.Parse("""{"method":"semantic","unit":"tokens","maximumLength":800,"overlapLength":0}""")), "Wrong CU chunking");
    foreach (string name in new[] { "UserIds", "GroupIds", "metadata_spo_item_path" })
        Check(skills["indexProjections"]!["selectors"]![0]!["mappings"]!.AsArray().Any(m => m!["name"]!.ToString() == name), "Chunk ACL/path missing");
    Check(!indexer.ContainsKey("schedule") && indexer["parameters"]!["batchSize"]!.GetValue<int>() == 1, "Implicit scheduled ingestion");
    Check(JsonNode.DeepEquals(query, SharePointWorkshop.QueryDefinition("*")), "Visibility query differs");
    var checkpoint = JsonNode.Parse(File.ReadAllText(settings.StatePath))!.AsObject();
    Check(checkpoint.Count == 3 && !checkpoint.ToJsonString().Contains(SpTestData.Token()), "Checkpoint leaks/persists content");

    JsonObject? resync = null;
    foreach (var (command, count) in new[] {
        ("configuration", 0), ("data-source", 1), ("index", 1), ("skillset", 1),
        ("status", 1), ("query", 1), ("run-indexer", 2), ("resync-permissions", 3) })
    {
        handler.Reset("existing");
        string[] arguments = command == "data-source" ? new[] { command, "--setup-confirmed" } :
            command == "resync-permissions" ? new[] { command, "--confirm" } : new[] { command };
        await SharePointIndexedProgram.RunAsync(arguments, root, client, Token, Delay);
        Check(handler.Requests.Count == count, "Implicit action: " + command);
        Check(handler.Requests.All(r => r.Method != "DELETE"), "Step deletes");
        if (command != "query") Check(handler.Requests.All(r => !r.Url.Contains("/docs/search?")), "Implicit query");
        if (command == "resync-permissions") resync = handler.Requests[1].Body!.DeepClone().AsObject();
    }
    foreach (string command in new[] { "data-source", "index", "skillset", "indexer" })
    {
        handler.Reset("put-no-content");
        string[] arguments = command is "data-source" or "indexer" ? new[] { command, "--setup-confirmed" } : new[] { command };
        await SharePointIndexedProgram.RunAsync(arguments, root, client, Token, Delay);
        Check(handler.Requests.Count == (command == "indexer" ? 3 : 1), "204 update retries or hidden operations");
        Check(handler.Requests.Count(r => r.Method == "PUT") == 1, "204 update not handled once");
    }
    handler.Reset("no-content");
    await Fails(() => Lab().QueryAsync(), "HTTP 204");
    Check(handler.Requests.Count == 1, "204 query retried");
    handler.Reset("no-content");
    await Fails(() => Lab().StatusAsync(), "HTTP 204");
    Check(handler.Requests.Count == 1, "204 GET retried");
    if (args.Length == 2 && args[0] == "--wire")
        File.WriteAllText(args[1], new JsonObject
        {
            ["dataSource"] = source.DeepClone(),
            ["index"] = index.DeepClone(),
            ["skillset"] = skills.DeepClone(),
            ["indexer"] = indexer.DeepClone(),
            ["query"] = query.DeepClone(),
            ["resync"] = resync,
        }.ToJsonString());

    foreach (string command in new[] { "data-source", "indexer", "run", "cleanup", "resync-permissions" })
    {
        handler.Reset();
        await Fails(() => SharePointIndexedProgram.RunAsync(new[] { command }, root, client, Token, Delay), "Use help");
        Check(handler.Requests.Count == 0, "Missing confirmation writes");
    }
    handler.Reset();
    await Fails(() => Lab().CreateDataSourceAsync(false), "acknowledges");
    await Fails(() => Lab().CreateIndexerAsync(false), "acknowledges");
    await Fails(() => Lab().CleanupAsync(false), "cleanup --confirm");
    Check(handler.Requests.Count == 0, "False opt-ins contact service");

    foreach (string site in new[] { "http://workshop.sharepoint.com", "https://evil.example/sites/test",
        "https://workshop.sharepoint.com.evil.example/sites/test", "https://user@workshop.sharepoint.com/sites/test",
        "https://workshop.sharepoint.com/sites/test?secret=1", "https://workshop.sharepoint.com/sites/test;ApplicationId=x" })
        await Fails(() => { (settings with { SiteUrl = site }).Validate(); return Task.CompletedTask; }, "SharePoint HTTPS");
    foreach (string prefix in new[] { "", "Aaa", "../site", "aa", "1abc", "abc\n" })
        await Fails(() => { (settings with { Prefix = prefix }).Validate(); return Task.CompletedTask; }, "prefix");
    foreach (var changed in new[] { settings with { AppClientId = "" }, settings with { SearchMiClientId = "bad" },
        settings with { SearchMiClientId = settings.AppClientId } })
        await Fails(() => { changed.ConnectionString(); return Task.CompletedTask; }, "client");

    foreach (string mode in new[] { "app", "wrong-tenant", "wrong-audience", "missing-user", "malformed" })
    {
        handler.Reset();
        await Fails(() => Lab(token: _ => Task.FromResult(SpTestData.Token(mode))).QueryAsync(), "delegated Search token");
        Check(handler.Requests.Count == 0, "Invalid identity queried");
    }
    handler.Reset();
    await Fails(() => Lab().QueryAsync(" "), "non-empty");
    Check(handler.Requests.Count == 0, "Empty query made a call");
    string escapedQuestion = "Quoted \"question\"\nC:\\sample";
    await Lab().QueryAsync(escapedQuestion);
    Check(handler.Requests.Single().Body!["search"]!.ToString() == escapedQuestion, "Unsafe question serialization");

    foreach (string mode in new[] { "http403", "http206", "bad-json", "run-conflict" })
    {
        handler.Reset(mode);
        await Fails(() => mode == "run-conflict" ? Lab().RunIndexerAsync() : Lab().QueryAsync(),
            mode == "bad-json" ? "invalid JSON" : mode == "run-conflict" ? "HTTP 409" : mode == "http403" ? "HTTP 403" : "HTTP 206");
        Check(handler.Requests.Count == (mode == "run-conflict" ? 2 : 1), "Failed action retries");
    }
    Check(!logs.ToString().Contains(SpTestData.Token()), "Credential leaked");

    foreach (string mode in new[] { "busy", "failure", "failed-docs", "overall-error", "timeout" })
    {
        handler.Reset(mode);
        await Fails(() => mode == "busy" ? Lab().CreateIndexerAsync(true) : Lab().WaitAsync(),
            mode == "busy" ? "already running" : mode == "timeout" ? "15 minutes" : mode == "failed-docs" ? "failed documents" : "Indexer failed");
        Check(handler.Requests.All(r => r.Method == "GET"), "Monitor/busy check writes/reruns");
        if (mode == "timeout") Check(handler.Requests.Count == 90, "Unbounded polling");
    }
    handler.Reset("existing");
    await Lab(true).RunIndexerAsync();
    handler.Mode = "stale-first";
    int before = handler.Requests.Count;
    await Lab(true).WaitAsync();
    Check(handler.Requests.Count - before == 3, "Fresh process accepted stale success");
    Check(handler.Requests.Skip(before).All(r => r.Method == "GET"), "Wait retries ingestion");
    handler.Reset("busy");
    await Fails(() => Lab().RunIndexerAsync(true), "already running");
    Check(handler.Requests.Count == 1, "Busy resync writes");
    handler.Reset("existing");
    await Lab().CreateIndexerAsync(true);
    Check(handler.Requests.Count == 3 && handler.Requests[2].Url.Contains("/run?"), "Existing indexer didn't explicitly run");

    foreach (string mode in new[] { "bad-source", "bad-index", "bad-skillset", "bad-indexer" })
    {
        handler.Reset(mode);
        await Fails(() => Lab().CleanupAsync(true), "before deletion");
        Check(handler.Requests.All(r => r.Method == "GET"), "Cleanup deletes before full preflight");
    }
    handler.Reset("all-missing");
    await Lab().CleanupAsync(true);
    Check(handler.Requests.Count == 4 && handler.Requests.All(r => r.Method == "GET"), "Missing cleanup");
    handler.Reset("existing");
    await Lab(true).CleanupAsync(true);
    Check(handler.Requests.Count == 8 && handler.Requests.Count(r => r.Method == "DELETE") == 4, "Cleanup scope");
    Check(!File.Exists(settings.StatePath), "Checkpoint not cleaned");
    Check(handler.Requests.All(r => !r.Url.Contains("/applications/") && !r.Url.Contains("/sites/")), "Tenant/source cleanup");
    handler.Reset("cleanup-denied");
    await Fails(() => Lab().CleanupAsync(true), "HTTP 403");
    Check(handler.Requests.Count(r => r.Method == "DELETE") == 1, "Continued after failed delete");
}
catch
{
    Console.SetOut(originalOut);
    Console.SetError(originalError);
    Console.Error.WriteLine(logs.ToString()[Math.Max(0, logs.ToString().Length - 8000)..]);
    throw;
}
finally
{
    Console.SetOut(originalOut);
    Console.SetError(originalError);
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"Passed {cases} offline SharePoint indexed console cases.");

public sealed record CapturedSpRequest(string Method, string Url, string? Authorization, string? SourceAuthorization,
    bool HasKey, bool HasElevatedRead, JsonObject? Body);

public static class SpTestData
{
    public static Dictionary<string, JsonObject> Definitions = new();
    public static SharePointSettings Settings => new("", "00000000-0000-0000-0000-000000000000",
        "https://workshop.search.windows.net", "https://workshop.sharepoint.com/sites/test",
        "workshop", "workshop-embedding", "text-embedding-3-small", "alice01",
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222");
    public static string EnvironmentText => "WORKSHOP_PREFIX=alice01\nAZURE_TENANT_ID=" + Settings.Tenant +
        "\nAZURE_SEARCH_ENDPOINT=" + Settings.SearchEndpoint + "\nSHAREPOINT_SITE_URL=" + Settings.SiteUrl +
        "\nAZURE_AI_ACCOUNT_NAME=workshop\nAZURE_AI_EMBEDDING_DEPLOYMENT_NAME=workshop-embedding" +
        "\nAZURE_AI_EMBEDDING_MODEL_NAME=text-embedding-3-small\nSHAREPOINT_APP_CLIENT_ID=" + Settings.AppClientId +
        "\nSHAREPOINT_SEARCH_MI_CLIENT_ID=" + Settings.SearchMiClientId + "\n";

    public static string Token(string mode = "")
    {
        if (mode == "malformed") return "synthetic-token";
        var claims = new JsonObject
        {
            ["aud"] = mode == "wrong-audience" ? "https://graph.microsoft.com" : SharePointTransport.Audience,
            ["tid"] = mode == "wrong-tenant" ? "33333333-3333-3333-3333-333333333333" : Settings.Tenant,
            ["oid"] = mode == "missing-user" ? null : mode == "restricted" ? "55555555-5555-5555-5555-555555555555" : "44444444-4444-4444-4444-444444444444",
            ["idtyp"] = mode == "app" ? "app" : "user",
            ["scp"] = mode == "app" ? null : "user_impersonation",
        };
        string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("""{"typ":"JWT","alg":"RS256"}""") + "." + Encode(claims.ToJsonString()) + ".synthetic-signature";
    }
}

public sealed class SpOfflineHandler : HttpMessageHandler
{
    public List<CapturedSpRequest> Requests = new();
    public string Mode = "new";
    bool created;
    bool requested;
    int polls;
    public void Reset(string mode = "new") { Requests.Clear(); Mode = mode; created = mode != "new"; requested = false; polls = 0; }
    JsonObject Definition(string collection)
    {
        var definition = SpTestData.Definitions[collection].DeepClone().AsObject();
        if (Mode == "bad-source" && collection == "datasources") definition["container"]!["name"] = "otherLibrary";
        if (Mode == "bad-index" && collection == "indexes") definition["permissionFilterOption"] = "disabled";
        if (Mode == "bad-skillset" && collection == "skillsets") definition["indexProjections"]!["selectors"]![0]!["targetIndexName"] = "other-index";
        if (Mode == "bad-indexer" && collection == "indexers") definition["targetIndexName"] = "other-index";
        return definition;
    }
    JsonObject Status()
    {
        polls++;
        bool stale = !requested || (Mode == "stale-first" && polls == 1);
        string state = Mode == "busy" ? "inProgress" : Mode == "failure" ? "persistentFailure" :
            Mode == "timeout" ? "inProgress" : stale ? "success" : polls <= (Mode == "stale-first" ? 2 : 1) ? "inProgress" : "success";
        return new JsonObject
        {
            ["status"] = Mode == "overall-error" ? "error" : "running",
            ["lastResult"] = new JsonObject
            {
                ["status"] = state,
                ["startTime"] = stale ? "2029-01-01T00:00:00Z" : "2030-01-01T00:00:00Z",
                ["itemsProcessed"] = 2,
                ["itemsFailed"] = Mode == "failed-docs" ? 1 : 0,
                ["errors"] = new JsonArray(),
                ["warnings"] = new JsonArray(),
            },
        };
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        JsonObject? body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation))!.AsObject();
        string path = request.RequestUri!.AbsolutePath;
        Requests.Add(new CapturedSpRequest(request.Method.Method, request.RequestUri.ToString(), request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("x-ms-query-source-authorization", out var user) ? user.Single() : null,
            request.Headers.Contains("api-key") || request.Headers.Contains("x-apikey"), request.Headers.Contains("x-ms-enable-elevated-read"), body));
        int status = 200;
        JsonObject response = new();
        if (Mode is "http403" or "http206")
        {
            status = Mode == "http403" ? 403 : 206;
            response["error"] = SpTestData.Token();
        }
        else if (Mode == "all-missing") status = 404;
        else if (Mode == "no-content") status = 204;
        else if (path.EndsWith("/status"))
        {
            if (!created) status = 404;
            else response = Status();
        }
        else if (path.EndsWith("/docs/search"))
        {
            bool restricted = Requests.Last().SourceAuthorization == SpTestData.Token("restricted");
            var documents = new JsonArray(new JsonObject { ["sourceFile"] = "public.docx", ["content"] = "Synthetic public text.", ["page"] = 1 });
            if (!restricted) documents.Add(new JsonObject { ["sourceFile"] = "restricted.docx", ["content"] = "Synthetic restricted text.", ["page"] = 2 });
            response["@odata.count"] = documents.Count;
            response["value"] = documents;
        }
        else if (path.EndsWith("/run"))
        {
            status = Mode == "run-conflict" ? 409 : 202;
            requested = true; polls = 0;
        }
        else if (path.EndsWith("/resync")) status = 202;
        else if (request.Method == HttpMethod.Delete) status = Mode == "cleanup-denied" ? 403 : 204;
        else if (request.Method == HttpMethod.Put)
        {
            status = Mode == "put-no-content" ? 204 : 201; response = body!.DeepClone().AsObject();
            if (path.Contains("/indexers/")) { created = true; requested = true; polls = 0; }
        }
        else response = Definition(path.Split('/')[1]);
        string text = Mode == "bad-json" ? "{invalid" : status is 202 or 204 ? "" : response.ToJsonString();
        var result = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        result.Headers.Date = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        result.Headers.Add("x-ms-request-id", "synthetic-request");
        return result;
    }
}
