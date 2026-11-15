using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class SearchTests
{
    public const string StorageId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test/providers/Microsoft.Storage/storageAccounts/workshop";
    public static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static async Task Fails<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T error)
        {
            Check(error.Message.Contains(message), "Wrong failure: " + error.Message);
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static async Task<int> Main(string[] args)
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), ".csharp-search-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "search"));
        var settings = new SearchSettings(root, "00000000-0000-0000-0000-000000000000",
            "https://workshop.search.windows.net", "https://workshop.blob.core.windows.net", StorageId,
            "samples", "workshop", "workshop-embedding", "text-embedding-3-small", "alice01");
        int tests = 0;
        try
        {
            foreach (string name in new[] { "onboarding-guide.txt", "travel-policy.txt" })
                File.Copy(Path.Combine("data", "search", name), Path.Combine(root, "data", "search", name));
            File.WriteAllText(Path.Combine(root, "data", "search", "not-uploaded.txt"), "Do not upload arbitrary files.");
            settings.Validate();
            using var handler = new SearchOfflineHandler();
            using var client = new HttpClient(handler);
            var audiences = new List<string>();
            Func<string, Task<string>> tokens = audience =>
            {
                audiences.Add(audience);
                return Task.FromResult("synthetic-token");
            };
            var lab = new BlobWorkshop(settings, new SearchTransport(client, settings.Tenant, tokens),
                _ => Task.CompletedTask);
            await lab.ExecuteAsync("run");
            AssertWire(handler);
            Check(audiences.Count == handler.Requests.Count && audiences.Take(2).All(a => a == SearchTransport.StorageAudience)
                && audiences.Skip(2).All(a => a == SearchTransport.SearchAudience), "Token audiences/refresh differ");
            Check(JsonSerializer.Deserialize<IndexerRunState>(File.ReadAllText(settings.StatePath))!.Completed, "Success marker not completed");
            Check(!File.ReadAllText(settings.StatePath).Contains("synthetic-token") &&
                !File.ReadAllText(settings.StatePath).Contains("receipts"), "State stored sensitive data");
            if (args.Length == 2 && args[0] == "--wire")
                File.WriteAllText(args[1], JsonSerializer.Serialize(handler.Requests));
            tests++;

            // Every standalone action must avoid hidden writes, preceding actions and billable queries.
            foreach (string command in BlobWorkshop.Commands.Where(c => c != "run"))
            {
                handler.Reset();
                if (command == "monitor")
                    await lab.ExecuteAsync("indexer");
                handler.Requests.Clear();
                await lab.ExecuteAsync(command, command == "hybrid" ? "public custom question" : null);
                int expectedCount = command switch { "upload" => 2, "indexer" => 2, "monitor" => 2, _ => 1 };
                Check(handler.Requests.Count == expectedCount, "Hidden actions for " + command);
                if (command == "monitor")
                    Check(handler.Requests.All(r => r.Method == "GET"), "Monitor wrote");
                else if (command == "indexer")
                    Check(handler.Requests[0].Method == "GET" && handler.Requests[1].Method == "PUT", "Create should auto-run without POST");
                else if (command is "upload" or "data-source" or "index" or "skillset")
                    Check(handler.Requests.All(r => r.Method == "PUT"), "Unexpected method for " + command);
                else
                    Check(handler.Requests[0].Method == "POST", "Search command must only query");
                if (command == "hybrid")
                    Check(handler.Requests[0].Body!["search"]!.GetValue<string>() == "public custom question", "Custom question ignored");
                tests++;
            }
            handler.Reset("existing");
            await lab.ExecuteAsync("indexer");
            Check(handler.Requests.Select(r => r.Method).SequenceEqual(["GET", "PUT", "POST"]), "Existing indexer must explicitly POST run");
            await lab.ExecuteAsync("monitor");
            Check(handler.StatusCalls == 4, "Queued previous success was accepted");
            await Fails<InvalidOperationException>(() => lab.ExecuteAsync("monitor"), "No pending");
            tests++;

            handler.Reset("busy");
            await Fails<InvalidOperationException>(() => lab.ExecuteAsync("indexer"), "already running");
            Check(handler.Requests.Count == 1, "Busy indexer was written");
            tests++;

            foreach (string mode in new[] { "failed-items", "errors", "transientFailure", "persistentFailure", "error", "top-error" })
            {
                handler.Reset(mode);
                await lab.ExecuteAsync("indexer");
                await Fails<InvalidOperationException>(() => lab.ExecuteAsync("monitor"), mode == "top-error" ? "error" : mode == "failed-items" ? "itemsFailed" : mode == "errors" ? "synthetic error" : mode);
                Check(!JsonSerializer.Deserialize<IndexerRunState>(File.ReadAllText(settings.StatePath))!.Completed, "Failed run lost pending state");
                tests++;
            }
            handler.Reset("timeout");
            await lab.ExecuteAsync("indexer");
            await Fails<TimeoutException>(() => lab.ExecuteAsync("monitor"), "15 minutes");
            Check(handler.StatusCalls == 91, "Timeout must stop after 90 monitor reads");
            tests++;
            handler.Reset("timeout");
            handler.StatusCalls = 1;
            DateTimeOffset time = DateTimeOffset.UtcNow;
            var timed = new BlobWorkshop(settings, new SearchTransport(client, settings.Tenant, tokens),
                _ => { time = time.AddMinutes(16); return Task.CompletedTask; }, () => time);
            await Fails<TimeoutException>(() => timed.PollAsync(null), "15 minutes");
            Check(handler.StatusCalls == 2, "Wall-clock deadline ignored");
            tests++;
            foreach (string mode in new[] { "empty", "missing-vector", "short-vector" })
            {
                handler.Reset(mode);
                await Fails<InvalidOperationException>(() => lab.ExecuteAsync("verify-vectors"), "Missing or wrong-sized");
                tests++;
            }
            handler.Reset("empty");
            await Fails<InvalidOperationException>(() => lab.ExecuteAsync("inspect"), "No chunks");
            tests++;
            foreach (string mode in new[] { "partial200", "partial206" })
            {
                handler.Reset(mode);
                foreach (string command in new[] { "semantic", "semantic-hybrid" })
                {
                    if (mode == "partial200")
                        await Fails<InvalidOperationException>(() => lab.ExecuteAsync(command), "Semantic ranking did not complete");
                    else
                        await Fails<HttpRequestException>(() => lab.ExecuteAsync(command), "HTTP 206");
                    tests++;
                }
            }

            // Strict wire status expectations, bounded body and diagnostic header handling.
            TextWriter originalError = Console.Error;
            var diagnostics = new StringWriter();
            Console.SetError(diagnostics);
            try
            {
                foreach ((string command, int badStatus) in new[] { ("upload", 200), ("index", 202), ("indexer", 403), ("keyword", 204) })
                {
                    handler.Reset();
                    handler.ForceStatus = badStatus;
                    await Fails<HttpRequestException>(() => lab.ExecuteAsync(command), "HTTP " + badStatus);
                    Check(handler.Requests.Count == 1, "HTTP failure retried or continued");
                    tests++;
                }
                handler.Reset("existing");
                handler.RunStatus = 200;
                await Fails<HttpRequestException>(() => lab.ExecuteAsync("indexer"), "HTTP 200");
                Check(!JsonSerializer.Deserialize<IndexerRunState>(File.ReadAllText(settings.StatePath))!.Requested, "Rejected run marker accepted");
                await Fails<InvalidOperationException>(() => lab.ExecuteAsync("monitor"), "No pending");
                tests++;
            }
            finally { Console.SetError(originalError); }
            string errorText = diagnostics.ToString();
            Check(errorText.Contains("x-ms-request-id: offline-request") && errorText.Contains("retry-after: 1") &&
                !errorText.Contains("synthetic-token") && !errorText.Contains(new string('x', 8001)), "Diagnostics leaked/unbounded/missing request ID");
            foreach (int status in new[] { 200, 201, 204 })
            {
                handler.Reset();
                handler.PutStatus = status;
                await lab.ExecuteAsync("data-source");
                tests++;
            }
            handler.Reset();
            await new SearchTransport(client, settings.Tenant, tokens).UploadAsync(settings.StorageEndpoint, "samples",
                "alice01/blob/a #é.txt", Encoding.UTF8.GetBytes("sample"));
            Check(handler.Requests[0].Uri.Contains("a%20%23%C3%A9.txt"), "Storage path was not escaped preserving slash");
            tests++;
            foreach (var invalid in new[] {
                settings with { Tenant = "bad;echo" }, settings with { Prefix = "x\"bad" },
                settings with { SearchEndpoint = "http://workshop.search.windows.net" },
                settings with { StorageEndpoint = "https://workshop.blob.core.windows.net/path" },
                settings with { Deployment = "bad\"deployment" } })
                await Fails<InvalidOperationException>(() => { invalid.Validate(); return Task.CompletedTask; }, "");
            tests++;
            var environment = new Dictionary<string, string?>();
            foreach ((string key, string value) in new Dictionary<string, string>
            {
                ["AZURE_TENANT_ID"] = settings.Tenant,
                ["AZURE_SEARCH_ENDPOINT"] = settings.SearchEndpoint,
                ["AZURE_STORAGE_BLOB_ENDPOINT"] = settings.StorageEndpoint,
                ["AZURE_STORAGE_RESOURCE_ID"] = settings.StorageId,
                ["AZURE_STORAGE_CONTAINER_NAME"] = settings.Container,
                ["AZURE_AI_ACCOUNT_NAME"] = settings.Account,
                ["AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"] = settings.Deployment,
                ["AZURE_AI_EMBEDDING_MODEL_NAME"] = "",
                ["WORKSHOP_PREFIX"] = "wrong01"
            })
            {
                environment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
            try
            {
                File.WriteAllText(Path.Combine(root, ".env"), "WORKSHOP_PREFIX=first01\n");
                File.WriteAllText(Path.Combine(root, ".env.azure"), "WORKSHOP_PREFIX='alice01'\n");
                Check(SearchSettings.Load(root).Prefix == "alice01" && SearchSettings.Load(root).Model == "text-embedding-3-small", "Dotenv override/default failed");
                foreach (string key in environment.Keys) Environment.SetEnvironmentVariable(key, null);
                Check(await Program.Main(["help"]) == 0 && await Program.Main(["bad-command"]) == 1, "Help must not authenticate/read env");
            }
            finally { foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
            tests++;
            Console.WriteLine($"PASS: {tests} offline console protocol/runtime cases");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static void AssertWire(SearchOfflineHandler handler)
    {
        Check(handler.Requests.Count == 16, "Full sequence request count differs");
        foreach (var request in handler.Requests)
        {
            Check(request.Authorization == "Bearer synthetic-token", "Missing Entra Authorization");
            Check(!request.Headers.ContainsKey("api-key") && !request.Headers.ContainsKey("x-apikey"), "API key used");
            if (request.Uri.Contains(".search.windows.net"))
                Check(request.Uri.EndsWith("?api-version=2025-09-01"), "REST version differs");
        }
        foreach (var request in handler.Requests.Take(2))
        {
            Check(request.Method == "PUT" && request.Uri.Contains("/samples/alice01/blob/"), "Blob upload scope differs");
            Check(request.Headers["x-ms-version"] == "2023-11-03" && request.Headers["x-ms-blob-type"] == "BlockBlob", "Blob headers differ");
            Check(DateTimeOffset.TryParseExact(request.Headers["x-ms-date"], "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "Blob date not RFC1123");
            Check(request.ContentType == "text/plain; charset=utf-8" && request.Text!.Length > 100, "Blob content differs");
        }
        var source = handler.Requests[2].Body!;
        Check(source["type"]!.GetValue<string>() == "azureblob" && source["credentials"]!["connectionString"]!.GetValue<string>() == "ResourceId=" + StorageId + ";"
            && source["container"]!["query"]!.GetValue<string>() == "alice01/blob/", "Data source differs");
        var index = handler.Requests[3].Body!;
        Check(index["fields"]!.AsArray().Count == 6 && index["fields"]![4]!["dimensions"]!.GetValue<int>() == 1536, "Index fields/dimensions differ");
        Check(index["vectorSearch"]!["vectorizers"]![0]!["azureOpenAIParameters"]!["resourceUri"]!.GetValue<string>() == "https://workshop.openai.azure.com", "Vectorizer resource differs");
        var skills = handler.Requests[4].Body!;
        string skillJson = skills.ToJsonString();
        Check(skills["skills"]!.AsArray().Count == 3 && !skillJson.Contains("apiKey") && !skillJson.Contains("authIdentity") && !skillJson.Contains("cognitiveServices")
            && !skillJson.Contains("OcrSkill") && !skillJson.Contains("Vision"), "Wrong enrichment/auth");
        Check(skills["indexProjections"]!["parameters"]!["projectionMode"]!.GetValue<string>() == "skipIndexingParentDocuments", "Projection mode differs");
        Check(handler.Requests[5].Method == "GET" && handler.Requests[6].Method == "PUT" &&
            handler.Requests[6].Body!["parameters"]!["maxFailedItems"]!.GetValue<int>() == 0, "Create indexer protocol differs");
        Check(handler.Requests[7].Method == "GET" && handler.Requests[8].Method == "GET", "Monitor not polling");
        Check(handler.Requests[9].Body!["select"]!.GetValue<string>() == "chunk_id,parent_id,chunk,source" &&
            handler.Requests[10].Body!["select"]!.GetValue<string>() == "chunk_id,text_vector", "Inspection differs");
        // Full sequence contains five search modes; vector-only must not contain keyword search.
        for (int i = 0; i < 5; i++)
        {
            string mode = new[] { "keyword", "vector", "hybrid", "semantic", "semantic-hybrid" }[i];
            Check(JsonNode.DeepEquals(handler.Requests[11 + i].Body, BlobWorkshop.QueryPayload(mode, BlobWorkshop.Question)), "Query differs for " + mode);
        }
    }
}

public sealed record CapturedSearchRequest(string Method, string Uri, string Authorization,
    Dictionary<string, string> Headers, string? ContentType, string? Text, JsonObject? Body);

public sealed class SearchOfflineHandler : HttpMessageHandler
{
    public List<CapturedSearchRequest> Requests = new();
    public string Mode = "complete";
    public int StatusCalls;
    public int? ForceStatus;
    public int PutStatus = 201;
    public int RunStatus = 202;
    public void Reset(string mode = "complete")
    {
        Requests.Clear();
        Mode = mode;
        StatusCalls = 0;
        ForceStatus = null;
        PutStatus = 201;
        RunStatus = 202;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string? text = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
        JsonObject? payload = text?.StartsWith("{") == true ? JsonNode.Parse(text)!.AsObject() : null;
        Requests.Add(new CapturedSearchRequest(request.Method.Method, request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString() ?? "", headers, request.Content?.Headers.ContentType?.ToString(), text, payload));
        int code = request.Method == HttpMethod.Put ? PutStatus : 200;
        JsonObject body = new JsonObject();
        if (request.RequestUri.Host.Contains(".blob."))
            code = 201;
        if (request.RequestUri.AbsolutePath.EndsWith("/run"))
            code = RunStatus;
        if (request.RequestUri.AbsolutePath.EndsWith("/status"))
        {
            StatusCalls++;
            if (StatusCalls == 1 && Mode is not ("existing" or "busy"))
                code = 404;
            else
            {
                bool old = Mode == "existing" && StatusCalls <= 2;
                string state = Mode == "busy" ? "inProgress" : old ? "success" :
                    Mode == "timeout" || StatusCalls == 2 || (Mode == "existing" && StatusCalls == 3) ? "inProgress" :
                    Mode is "transientFailure" or "persistentFailure" or "error" ? Mode : "success";
                body["status"] = Mode == "top-error" ? "error" : "running";
                body["lastResult"] = new JsonObject
                {
                    ["status"] = state,
                    ["startTime"] = old || Mode == "busy" ? "2026-11-14T01:00:00Z" : "2026-11-14T02:00:00Z",
                    ["itemsProcessed"] = 2,
                    ["itemsFailed"] = Mode == "failed-items" ? 1 : 0,
                    ["errors"] = Mode == "errors" ? new JsonArray(new JsonObject { ["message"] = "synthetic error" }) : new JsonArray(),
                    ["warnings"] = new JsonArray(new JsonObject { ["message"] = "synthetic warning" })
                };
            }
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/docs/search"))
        {
            bool vectors = payload?["select"]?.GetValue<string>() == "chunk_id,text_vector";
            var item = new JsonObject
            {
                ["chunk_id"] = "synthetic-chunk",
                ["parent_id"] = "synthetic-parent",
                ["chunk"] = "Synthetic original text.",
                ["title"] = "travel-policy.txt",
                ["source"] = new JsonObject { ["file_name"] = "travel-policy.txt", ["url"] = "https://workshop.blob.core.windows.net/samples/alice01/blob/travel-policy.txt" }
            };
            if (vectors && Mode != "missing-vector")
                item["text_vector"] = new JsonArray(Enumerable.Range(0, Mode == "short-vector" ? 2 : 1536).Select(_ => JsonValue.Create(0.1)).Cast<JsonNode?>().ToArray());
            body["value"] = Mode == "empty" ? new JsonArray() : new JsonArray(item);
            body["@odata.count"] = Mode == "empty" ? 0 : 1;
            if (payload?["queryType"]?.GetValue<string>() == "semantic")
            {
                if (Mode == "partial206") code = 206;
                if (Mode is "partial206" or "partial200") body["@search.semanticPartialResponseReason"] = "CapacityOverloaded";
            }
        }
        if (ForceStatus is not null) code = ForceStatus.Value;
        var response = new HttpResponseMessage((HttpStatusCode)code)
        {
            Content = new StringContent(ForceStatus is not null ? new string('x', 9000) : code is 201 or 204 or 202 or 404 ? "" : body.ToJsonString())
        };
        response.Headers.Add("x-ms-request-id", "offline-request");
        response.Headers.Add("retry-after", "1");
        return response;
    }
}
