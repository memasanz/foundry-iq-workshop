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
string root = Path.Combine(Directory.GetCurrentDirectory(), ".csharp-push-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "data", "search"));
try
{
    foreach (string name in new[] { "onboarding-guide.txt", "travel-policy.txt" })
        File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "data", "search", name), Path.Combine(root, "data", "search", name));
    var settings = new PushSettings(root, "00000000-0000-0000-0000-000000000000",
        "https://workshop.search.windows.net", "workshop", "workshop-embedding", "text-embedding-3-small", "alice01");
    var handler = new PushOfflineHandler();
    using var client = new HttpClient(handler);
    var audiences = new List<string>();
    var transport = new PushTransport(client, settings.Tenant, resource =>
    {
        audiences.Add(resource);
        return Task.FromResult("synthetic-token");
    });
    var delays = new List<TimeSpan>();
    var workshop = new PushWorkshop(settings, transport, duration => { delays.Add(duration); return Task.CompletedTask; });

    foreach (int length in new[] { 1, 799, 800, 999, 1000, 1001, 1600, 1800, 1801, 2600 })
    {
        string text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
        var chunks = PushWorkshop.Slice(text, "sample.txt", "data/search/sample.txt");
        int expected = length <= 1000 ? 1 : 1 + (int)Math.Ceiling((length - 1000) / 800.0);
        Check(chunks.Count == expected, "Redundant tail or missing chunk");
        string rebuilt = chunks[0]!["chunk"]!.GetValue<string>();
        for (int i = 1; i < chunks.Count; i++)
        {
            string previous = chunks[i - 1]!["chunk"]!.GetValue<string>();
            string current = chunks[i]!["chunk"]!.GetValue<string>();
            Check(previous[^200..] == current[..200], "Lost overlap");
            rebuilt += current[200..];
        }
        Check(rebuilt == text, "Lost source characters");
        Check(JsonNode.DeepEquals(chunks, PushWorkshop.Slice(text, "sample.txt", "data/search/sample.txt")), "Unstable IDs");
        Check(chunks[^1]!["chunk_id"]!.GetValue<string>() == $"sample-{expected - 1}", "Wrong key");
    }
    Check(PushWorkshop.Slice(string.Concat(Enumerable.Repeat("😀", 1001)), "unicode.txt", "unicode.txt").Count == 2, "UTF16 slicing");
    Check(PushWorkshop.Slice("a\r\nb\rc", "sample.txt", "sample.txt")[0]!["chunk"]!.GetValue<string>() == "a\nb\nc", "Newline parity");
    await Fails(() => Task.FromResult(PushWorkshop.Slice(" ", "sample.txt", "sample.txt")), "Empty source");
    await Fails(() => Task.FromResult(PushWorkshop.Slice("a", "sample.txt", "sample.txt", 2, 2)), "overlap");

    JsonArray Documents(int count) => new(Enumerable.Range(0, count).Select(i =>
        (JsonNode)new JsonObject
        {
            ["@search.action"] = "upload",
            ["chunk_id"] = "sample-" + i,
            ["parent_id"] = "sample",
            ["title"] = "sample.txt",
            ["chunk"] = "text " + i,
            ["source"] = new JsonObject { ["file_name"] = "sample.txt", ["url"] = "data/search/sample.txt" }
        }).ToArray());

    var documents = Documents(33);
    await workshop.EmbedAsync(documents);
    Check(handler.Requests.Select(r => r.Body!["input"]!.AsArray().Count).SequenceEqual(new[] { 16, 16, 1 }), "Embedding batch size");
    Check(documents[0]!["text_vector"]![0]!.GetValue<double>() == 0.01 &&
        documents[15]!["text_vector"]![0]!.GetValue<double>() == 0.16, "Embedding response order mapping");
    foreach (string mode in new[] { "missing-embedding", "duplicate-embedding", "out-of-range", "wrong-vector", "late-embedding-failure" })
    {
        handler.Reset(mode);
        await Fails(() => workshop.EmbedAsync(documents), mode == "wrong-vector" ? "dimensions" : mode == "late-embedding-failure" ? "HTTP 429" : "exactly one vector");
        Check(documents.All(item => item!["text_vector"] is null), "Failure retained partial/stale vectors");
    }
    handler.Reset();
    documents = Documents(201);
    await workshop.EmbedAsync(documents);
    handler.Reset();
    await workshop.PushAsync(documents);
    Check(handler.Requests.Select(r => r.Body!["value"]!.AsArray().Count).SequenceEqual(new[] { 100, 100, 1 }), "Push batch size");
    foreach (string mode in new[] { "partial-push", "incomplete-push", "wrong-key", "duplicate-key" })
    {
        handler.Reset(mode);
        await Fails(() => workshop.PushAsync(documents), "Push was incomplete");
        Check(handler.Requests.Count == 1, "Automatic push retry");
    }
    handler.Reset("success207");
    await workshop.PushAsync(documents);
    handler.Reset();
    await Fails(() => workshop.PushAsync(Documents(1)), "dimensions");
    Check(handler.Requests.Count == 0, "Push secretly embedded");

    documents = Documents(2);
    await workshop.EmbedAsync(documents);
    await workshop.PushAsync(documents);
    handler.Reset("pending");
    delays.Clear();
    await workshop.VerifyAsync(documents);
    Check(handler.Requests.Count == 6 && delays.SequenceEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }), "Async verification polling");
    handler.Reset("precision");
    await workshop.VerifyAsync(documents);
    Check(handler.Requests.Count == 2, "Float precision tolerance");
    foreach (string mode in new[] { "old-text", "old-vector", "missing-stored-vector", "missing" })
    {
        handler.Reset(mode);
        delays.Clear();
        await Fails(() => workshop.VerifyAsync(documents), "not yet visible");
        Check(handler.Requests.Count == 60 && delays.Count == 29, "Verification did not bound all-key polling");
    }
    handler.Reset("http403");
    await Fails(() => workshop.VerifyAsync(documents), "HTTP 403");
    Check(handler.Requests.Count == 1, "Verification retried HTTP error");

    foreach (string mode in PushWorkshop.QueryModes)
    {
        handler.Reset();
        await workshop.QueryAsync(mode, "Quoted \"question\"\nC:\\sample");
        Check(handler.Requests.Count == 1 && handler.Requests[0].Uri.Contains("/docs/search?api-version=2025-09-01"), "Hidden query calls");
        var body = handler.Requests[0].Body!;
        Check((body["vectorQueries"] is not null) == (mode is "vector" or "hybrid" or "semantic-hybrid"), "Query vector contract");
        Check((body["search"] is not null) == (mode != "vector"), "Vector query has keyword candidates");
        Check((body["queryType"] is not null) == mode.StartsWith("semantic", StringComparison.Ordinal), "Semantic contract");
    }
    foreach (string mode in new[] { "partial200", "partial206", "http403" })
    {
        handler.Reset(mode);
        await Fails(() => workshop.QueryAsync("semantic-hybrid", PushWorkshop.Question),
            mode == "partial200" ? "Semantic ranking did not complete" : mode == "partial206" ? "HTTP 206" : "HTTP 403");
    }
    handler.Reset();
    await Fails(() => workshop.QueryAsync("vector", " "), "non-empty");
    Check(handler.Requests.Count == 0, "Invalid question made paid call");

    handler.Reset();
    await Fails(() => workshop.ExecuteAsync("cleanup"), "Cleanup disabled");
    Check(handler.Requests.Count == 0, "Cleanup not opt-in");
    await Fails(() => new PushWorkshop(settings with { Prefix = "../bad" }, transport).CleanupAsync(true), "prefix");
    Check(handler.Requests.Count == 0, "Invalid cleanup made request");
    handler.Reset("missing");
    await workshop.ExecuteAsync("cleanup", confirmCleanup: true);
    Check(handler.Requests.Single().Method == "DELETE" && handler.Requests.Single().Uri ==
        "https://workshop.search.windows.net/indexes/alice01-push-chunks?api-version=2025-09-01", "Cleanup scope");

    handler.Reset();
    await Fails(() => workshop.ExecuteAsync("embed"), "Missing local step");
    Check(handler.Requests.Count == 0, "Embed secretly chunked");
    await workshop.ExecuteAsync("chunk");
    Check(handler.Requests.Count == 0 && File.Exists(settings.ChunksPath), "Chunk must be offline");
    await Fails(() => workshop.ExecuteAsync("push"), "Missing local step");
    await Fails(() => workshop.ExecuteAsync("verify"), "Missing local step");
    await workshop.ExecuteAsync("embed");
    Check(handler.Requests.Count == 1 && handler.Requests[0].Uri.Contains("/embeddings?"), "Embed step hidden earlier actions");
    handler.Reset();
    await workshop.ExecuteAsync("push");
    Check(handler.Requests.Count == 1 && handler.Requests[0].Uri.Contains("/docs/index?"), "Push step hidden earlier actions");
    handler.Reset("partial-push");
    await Fails(() => workshop.ExecuteAsync("push"), "Push was incomplete");
    Check(!File.Exists(settings.UploadedPath) && File.Exists(settings.VectorsPath), "Failed push kept accepted marker or removed reusable vectors");
    handler.Reset();
    await workshop.ExecuteAsync("push");
    handler.Reset();
    await workshop.ExecuteAsync("verify");
    Check(handler.Requests.All(r => r.Method == "GET"), "Verify step writes");
    foreach (var changed in new[]
    {
        settings with { Deployment = "changed" }, settings with { Model = "changed" },
        settings with { SearchEndpoint = "https://other.search.windows.net" }, settings with { Account = "other" }
    })
    {
        handler.Reset();
        await Fails(() => new PushWorkshop(changed, transport).ExecuteAsync("push"), "Stale or incompatible");
        Check(handler.Requests.Count == 0, "Stale cache made request");
    }
    var cache = JsonNode.Parse(File.ReadAllText(settings.VectorsPath))!.AsObject();
    cache["compatibility"]!["dimensions"] = 42;
    File.WriteAllText(settings.VectorsPath, cache.ToJsonString());
    await Fails(() => workshop.ExecuteAsync("push"), "Stale or incompatible");
    foreach (string key in new[] { "version", "chunkSize", "overlap" })
    {
        await workshop.ExecuteAsync("embed");
        cache = JsonNode.Parse(File.ReadAllText(settings.VectorsPath))!.AsObject();
        cache["compatibility"]![key] = -1;
        File.WriteAllText(settings.VectorsPath, cache.ToJsonString());
        handler.Reset();
        await Fails(() => workshop.ExecuteAsync("push"), "Stale or incompatible");
        Check(handler.Requests.Count == 0, "Incompatible chunk algorithm made request");
    }
    await workshop.ExecuteAsync("embed");
    cache = JsonNode.Parse(File.ReadAllText(settings.VectorsPath))!.AsObject();
    cache["documents"]![0]!["chunk"] = "Tampered cached text";
    File.WriteAllText(settings.VectorsPath, cache.ToJsonString());
    await Fails(() => workshop.ExecuteAsync("push"), "Stale or incompatible");
    await workshop.ExecuteAsync("embed");
    handler.Reset("http403");
    await Fails(() => workshop.ExecuteAsync("embed"), "HTTP 403");
    Check(!File.Exists(settings.VectorsPath) && !File.Exists(settings.UploadedPath), "Failed embed reused stale vectors");
    handler.Reset();
    await workshop.ExecuteAsync("embed");
    await workshop.ExecuteAsync("push");
    await workshop.ExecuteAsync("chunk");
    Check(!File.Exists(settings.VectorsPath) && !File.Exists(settings.UploadedPath), "Changed chunks did not invalidate later artifacts");
    await workshop.ExecuteAsync("embed");
    await workshop.ExecuteAsync("push");
    File.AppendAllText(Path.Combine(root, "data", "search", "travel-policy.txt"), "\nChanged content.");
    handler.Reset();
    await Fails(() => workshop.ExecuteAsync("verify"), "Stale or incompatible");
    await Fails(() => workshop.ExecuteAsync("push"), "Stale or incompatible");
    Check(handler.Requests.Count == 0, "Changed source used stale embeddings");

    // Restore exact baseline before exporting a full normal workflow's captured wire payloads.
    File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "data", "search", "travel-policy.txt"),
        Path.Combine(root, "data", "search", "travel-policy.txt"), true);
    handler.Reset();
    audiences.Clear();
    await workshop.ExecuteAsync("run");
    Check(handler.Requests.First().Method == "PUT" && handler.Requests.All(r => r.Method != "DELETE"), "Run not independent or cleaned up");
    Check(handler.Requests.Count(r => r.Uri.Contains("/embeddings?")) == 1, "Run reused previous vector cache");
    Check(handler.Requests.All(r => r.Authorization == "Bearer synthetic-token" && !r.HasApiKey), "Wrong authentication");
    Check(audiences.Count(a => a == PushTransport.EmbeddingAudience) == 1 &&
        audiences.Count(a => a == PushTransport.SearchAudience) == handler.Requests.Count - 1, "Token audience mismatch");
    if (args.Length == 2 && args[0] == "--wire")
        File.WriteAllText(args[1], JsonSerializer.Serialize(handler.Requests));
    Console.WriteLine($"PASS: {cases} offline console protocol/runtime cases");
}
finally { Directory.Delete(root, true); }

public sealed record CapturedPushRequest(string Method, string Uri, JsonObject? Body, string? Authorization, bool HasApiKey);

public sealed class PushOfflineHandler : HttpMessageHandler
{
    public List<CapturedPushRequest> Requests { get; } = [];
    public string Mode = "";
    public Dictionary<string, JsonObject> Stored { get; } = [];
    int embeddings;
    int reads;
    public void Reset(string mode = "")
    {
        Requests.Clear();
        Mode = mode;
        embeddings = 0;
        reads = 0;
    }
    public static JsonArray Vector(double number) => new(Enumerable.Range(0, 1536)
        .Select(_ => (JsonNode?)JsonValue.Create(number)).ToArray());
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonObject? body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        string uri = request.RequestUri!.ToString();
        Requests.Add(new CapturedPushRequest(request.Method.Method, uri, body, request.Headers.Authorization?.ToString(), request.Headers.Contains("api-key")));
        var response = new JsonObject();
        int status = 200;
        if (Mode == "http403") return Response(403, new JsonObject { ["error"] = "synthetic forbidden" });
        if (request.Method == HttpMethod.Delete) return Response(Mode == "missing" ? 404 : 204, response);
        if (uri.Contains("/embeddings?", StringComparison.Ordinal))
        {
            embeddings++;
            if (Mode == "late-embedding-failure" && embeddings == 2) return Response(429, response);
            int count = body!["input"]!.AsArray().Count;
            var data = new JsonArray();
            foreach (int index in Enumerable.Range(0, count).Reverse())
                data.Add(new JsonObject { ["index"] = index, ["embedding"] = Vector((index + 1) / 100.0) });
            switch (Mode)
            {
                case "missing-embedding": data.RemoveAt(0); break;
                case "duplicate-embedding": data[0]!["index"] = 0; break;
                case "out-of-range": data[0]!["index"] = count; break;
                case "wrong-vector": data[0]!["embedding"]!.AsArray().RemoveAt(0); break;
            }
            response["data"] = data;
        }
        else if (uri.Contains("/docs/index?", StringComparison.Ordinal))
        {
            var results = new JsonArray();
            foreach (var document in body!["value"]!.AsArray())
            {
                string key = document!["chunk_id"]!.GetValue<string>();
                Stored[key] = document.DeepClone().AsObject();
                results.Add(new JsonObject { ["key"] = key, ["status"] = true, ["statusCode"] = 201 });
            }
            switch (Mode)
            {
                case "partial-push": results[0]!["status"] = false; status = 207; break;
                case "success207": status = 207; break;
                case "incomplete-push": results.RemoveAt(0); break;
                case "wrong-key": results[0]!["key"] = "other-0"; break;
                case "duplicate-key": results[1]!["key"] = results[0]!["key"]!.DeepClone(); break;
            }
            response["value"] = results;
        }
        else if (request.Method == HttpMethod.Get)
        {
            reads++;
            string key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/').Last());
            if (Mode == "missing" || (Mode == "pending" && reads <= 2)) return Response(404, response);
            response = Stored[key].DeepClone().AsObject();
            if (Mode == "old-text" || (Mode == "pending" && reads <= 4)) response["chunk"] = "old text";
            if (Mode == "old-vector") response["text_vector"]![0] = 0.9;
            if (Mode == "missing-stored-vector") response.Remove("text_vector");
            if (Mode == "precision") response["text_vector"]![0] =
                (double)(float)response["text_vector"]![0]!.GetValue<double>() + 0.0000001;
        }
        else if (uri.Contains("/docs/search?", StringComparison.Ordinal))
        {
            var result = Stored.Values.FirstOrDefault()?.DeepClone().AsObject() ?? new JsonObject();
            result.Remove("text_vector");
            response["value"] = new JsonArray(result);
            if (Mode is "partial200" or "partial206")
            {
                response["@search.semanticPartialResponseReason"] = "CapacityOverloaded";
                status = Mode == "partial206" ? 206 : 200;
            }
        }
        return Response(status, response);
    }
    static HttpResponseMessage Response(int status, JsonObject body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString()) };
}
