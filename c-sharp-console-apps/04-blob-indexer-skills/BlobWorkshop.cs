using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class BlobWorkshop(SearchSettings settings, SearchTransport transport,
    Func<TimeSpan, Task>? pause = null, Func<DateTimeOffset>? clock = null)
{
    public const int Dimensions = 1536;
    public const string SemanticConfiguration = "chunk-semantic";
    public const string Question = "What receipts do I need to claim travel expenses, and when must I submit them?";
    readonly Func<TimeSpan, Task> delay = pause ?? (duration => Task.Delay(duration));
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    public static readonly string[] Commands = ["upload", "data-source", "index", "skillset", "indexer", "monitor",
        "inspect", "verify-vectors", "keyword", "vector", "hybrid", "semantic", "semantic-hybrid", "run"];

    public async Task ExecuteAsync(string command, string? question = null)
    {
        switch (command)
        {
            case "upload": await UploadAsync(); break;
            case "data-source": await PutAsync("datasources", settings.DataSource, DataSourcePayload()); break;
            case "index": await PutAsync("indexes", settings.Index, IndexPayload()); break;
            case "skillset": await PutAsync("skillsets", settings.Skills, SkillsetPayload()); break;
            case "indexer": await IndexerAsync(); break;
            case "monitor": await MonitorAsync(); break;
            case "inspect": await InspectAsync(); break;
            case "verify-vectors": await VerifyVectorsAsync(); break;
            case "keyword" or "vector" or "hybrid" or "semantic" or "semantic-hybrid":
                await QueryAsync(command, question ?? Question); break;
            case "run":
                foreach (string step in Commands.Where(step => step != "run"))
                    await ExecuteAsync(step, question);
                break;
            default: throw new ArgumentException("Unknown lab command.");
        }
    }

    public async Task UploadAsync()
    {
        // Upload only these original synthetic samples, never a glob of arbitrary local documents.
        foreach (string name in new[] { "onboarding-guide.txt", "travel-policy.txt" })
            if (!File.Exists(Path.Combine(settings.Root, "data", "search", name)))
                throw new FileNotFoundException($"Expected workshop sample: {name}");
        foreach (string name in new[] { "onboarding-guide.txt", "travel-policy.txt" })
        {
            await transport.UploadAsync(settings.StorageEndpoint, settings.Container, settings.BlobPrefix + name,
                await File.ReadAllBytesAsync(Path.Combine(settings.Root, "data", "search", name)));
            Console.WriteLine("Uploaded: " + settings.BlobPrefix + name);
        }
    }

    public JsonObject DataSourcePayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.DataSource}}", "type": "azureblob",
          "credentials": { "connectionString": "ResourceId={{settings.StorageId}};" },
          "container": { "name": "{{settings.Container}}", "query": "{{settings.BlobPrefix}}" }
        }
        """)!.AsObject();

    public JsonObject IndexPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Index}}",
          "fields": [
            { "name": "chunk_id", "type": "Edm.String", "key": true, "searchable": true, "analyzer": "keyword" },
            { "name": "parent_id", "type": "Edm.String", "filterable": true },
            { "name": "chunk", "type": "Edm.String", "searchable": true },
            { "name": "title", "type": "Edm.String", "searchable": true },
            { "name": "text_vector", "type": "Collection(Edm.Single)", "searchable": true, "retrievable": true,
              "dimensions": {{Dimensions}}, "vectorSearchProfile": "chunk-vector-profile" },
            { "name": "source", "type": "Edm.ComplexType", "fields": [
              { "name": "file_name", "type": "Edm.String", "searchable": true },
              { "name": "url", "type": "Edm.String" }
            ] }
          ],
          "vectorSearch": {
            "algorithms": [ { "name": "chunk-hnsw", "kind": "hnsw", "hnswParameters": { "metric": "cosine" } } ],
            "profiles": [ { "name": "chunk-vector-profile", "algorithm": "chunk-hnsw", "vectorizer": "chunk-vectorizer" } ],
            "vectorizers": [ {
              "name": "chunk-vectorizer", "kind": "azureOpenAI",
              "azureOpenAIParameters": { "resourceUri": "{{settings.EmbeddingEndpoint}}",
                "deploymentId": "{{settings.Deployment}}", "modelName": "{{settings.Model}}" }
            } ]
          },
          "semantic": {
            "defaultConfiguration": "{{SemanticConfiguration}}",
            "configurations": [ {
              "name": "{{SemanticConfiguration}}",
              "prioritizedFields": {
                "titleField": { "fieldName": "title" },
                "prioritizedContentFields": [ { "fieldName": "chunk" } ]
              }
            } ]
          }
        }
        """)!.AsObject();

    public JsonObject SkillsetPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Skills}}",
          "description": "Split and embed blob text; preserve structured source provenance.",
          "skills": [
            {
              "@odata.type": "#Microsoft.Skills.Text.SplitSkill", "name": "split-text", "context": "/document",
              "textSplitMode": "pages", "maximumPageLength": 1000, "pageOverlapLength": 200,
              "inputs": [ { "name": "text", "source": "/document/content" } ],
              "outputs": [ { "name": "textItems", "targetName": "chunks" } ]
            },
            {
              "@odata.type": "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill", "name": "embed-chunks",
              "context": "/document/chunks/*", "resourceUri": "{{settings.EmbeddingEndpoint}}",
              "deploymentId": "{{settings.Deployment}}", "modelName": "{{settings.Model}}", "dimensions": {{Dimensions}},
              "inputs": [ { "name": "text", "source": "/document/chunks/*" } ],
              "outputs": [ { "name": "embedding", "targetName": "text_vector" } ]
            },
            {
              "@odata.type": "#Microsoft.Skills.Util.ShaperSkill", "name": "shape-provenance", "context": "/document",
              "inputs": [
                { "name": "file_name", "source": "/document/metadata_storage_name" },
                { "name": "url", "source": "/document/metadata_storage_path" }
              ],
              "outputs": [ { "name": "output", "targetName": "provenance" } ]
            }
          ],
          "indexProjections": {
            "selectors": [ {
              "targetIndexName": "{{settings.Index}}", "parentKeyFieldName": "parent_id",
              "sourceContext": "/document/chunks/*",
              "mappings": [
                { "name": "chunk", "source": "/document/chunks/*" },
                { "name": "text_vector", "source": "/document/chunks/*/text_vector" },
                { "name": "title", "source": "/document/metadata_storage_name" },
                { "name": "source", "source": "/document/provenance" }
              ]
            } ],
            "parameters": { "projectionMode": "skipIndexingParentDocuments" }
          }
        }
        """)!.AsObject();

    public JsonObject IndexerPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Indexer}}", "dataSourceName": "{{settings.DataSource}}",
          "targetIndexName": "{{settings.Index}}", "skillsetName": "{{settings.Skills}}",
          "parameters": {
            "maxFailedItems": 0, "maxFailedItemsPerBatch": 0,
            "configuration": { "dataToExtract": "contentAndMetadata" }
          }
        }
        """)!.AsObject();

    async Task PutAsync(string collection, string name, JsonObject body)
    {
        await transport.SearchAsync(HttpMethod.Put, settings.SearchEndpoint, $"{collection}/{name}", body, 200, 201, 204);
        Console.WriteLine("Ready: " + name);
    }

    public async Task IndexerAsync()
    {
        JsonObject status = await transport.SearchAsync(HttpMethod.Get, settings.SearchEndpoint,
            $"indexers/{settings.Indexer}/status", null, 200, 404);
        bool exists = status["missing"]?.GetValue<bool>() != true;
        JsonNode? previous = status["lastResult"];
        if (previous?["status"]?.GetValue<string>() == "inProgress")
            throw new InvalidOperationException("The indexer is already running. Wait before updating it.");
        // Persist only run metadata before any write; failed requests must not erase this marker.
        var state = new IndexerRunState(settings.SearchEndpoint, settings.Indexer,
            previous?["startTime"]?.GetValue<string>(), false, false);
        SaveState(state);
        await transport.SearchAsync(HttpMethod.Put, settings.SearchEndpoint,
            $"indexers/{settings.Indexer}", IndexerPayload(), 200, 201, 204);
        if (exists)
        {
            await transport.SearchAsync(HttpMethod.Post, settings.SearchEndpoint,
                $"indexers/{settings.Indexer}/run", null, 202);
            Console.WriteLine("New run requested for the existing indexer.");
        }
        SaveState(state with { Requested = true });
        Console.WriteLine("Indexer ready: " + settings.Indexer);
    }

    public async Task MonitorAsync()
    {
        if (!File.Exists(settings.StatePath))
            throw new InvalidOperationException("Run the indexer command first; monitor requires its local run marker.");
        var state = JsonSerializer.Deserialize<IndexerRunState>(await File.ReadAllTextAsync(settings.StatePath))
            ?? throw new InvalidOperationException("Invalid indexer run marker.");
        if (state.Endpoint != settings.SearchEndpoint || state.Indexer != settings.Indexer || !state.Requested || state.Completed)
            throw new InvalidOperationException("No pending accepted indexer request for this project/prefix. Run indexer first.");
        await PollAsync(state.PreviousStart);
        SaveState(state with { Completed = true });
    }

    public async Task PollAsync(string? previousStart)
    {
        DateTimeOffset deadline = now().AddMinutes(15);
        for (int attempt = 0; attempt < 90 && now() < deadline; attempt++)
        {
            var status = await transport.SearchAsync(HttpMethod.Get, settings.SearchEndpoint,
                $"indexers/{settings.Indexer}/status", null, 200);
            JsonNode? last = status["lastResult"];
            string? state = last?["status"]?.GetValue<string>();
            string? start = last?["startTime"]?.GetValue<string>();
            Console.WriteLine($"Run: {state} | processed: {last?["itemsProcessed"]} | failed: {last?["itemsFailed"]}");
            // An old successful run can remain visible while the accepted request is queued.
            bool requestedRun = !string.IsNullOrEmpty(start) && start != previousStart;
            if (status["status"]?.GetValue<string>() == "error" ||
                (requestedRun && state is "transientFailure" or "persistentFailure" or "error"))
                throw new InvalidOperationException(status.ToJsonString());
            if (requestedRun && state == "success")
            {
                if ((last?["itemsFailed"]?.GetValue<int>() ?? 0) != 0 || last?["errors"] is JsonArray { Count: > 0 })
                    throw new InvalidOperationException(last!.ToJsonString());
                if (now() >= deadline)
                    break;
                Console.WriteLine("Warnings: " + (last?["warnings"]?.ToJsonString() ?? "[]"));
                return;
            }
            if (attempt < 89 && now() < deadline)
                await delay(TimeSpan.FromSeconds(10));
        }
        throw new TimeoutException("Indexer did not complete within 15 minutes. Inspect its status before retrying.");
    }

    void SaveState(IndexerRunState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.StatePath)!);
        string staging = settings.StatePath + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(state));
            File.Move(staging, settings.StatePath, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public async Task InspectAsync()
    {
        var result = await SearchAsync(new JsonObject
        {
            ["search"] = "*",
            ["count"] = true,
            ["select"] = "chunk_id,parent_id,chunk,source",
            ["top"] = 100
        });
        if (result["value"] is not JsonArray { Count: > 0 })
            throw new InvalidOperationException("No chunks were indexed. Inspect indexer errors and the Blob folder prefix.");
        Console.WriteLine("Total chunks: " + result["@odata.count"]);
        Console.WriteLine(result["value"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task VerifyVectorsAsync()
    {
        var result = await SearchAsync(new JsonObject { ["search"] = "*", ["select"] = "chunk_id,text_vector", ["top"] = 3 });
        if (result["value"] is not JsonArray { Count: > 0 } samples ||
            samples.Any(item => item?["text_vector"] is not JsonArray { Count: Dimensions }))
            throw new InvalidOperationException("Missing or wrong-sized vectors. Inspect embedding errors.");
        foreach (var item in samples)
            Console.WriteLine($"{item!["chunk_id"]} | vector dimensions: {Dimensions}");
    }

    public static JsonObject QueryPayload(string mode, string question)
    {
        var body = new JsonObject { ["select"] = "chunk_id,title,chunk,source", ["top"] = 3 };
        if (mode != "vector")
        {
            body["search"] = question;
            body["searchFields"] = "chunk";
        }
        if (mode is "vector" or "hybrid" or "semantic-hybrid")
            body["vectorQueries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "text",
                ["text"] = question,
                ["fields"] = "text_vector",
                ["k"] = 50
            });
        if (mode is "semantic" or "semantic-hybrid")
        {
            body["queryType"] = "semantic";
            body["semanticConfiguration"] = SemanticConfiguration;
            body["captions"] = "extractive";
        }
        return body;
    }

    async Task QueryAsync(string mode, string question)
    {
        JsonObject result = await SearchAsync(QueryPayload(mode, question));
        if (mode is "semantic" or "semantic-hybrid" && result["@search.semanticPartialResponseReason"] is not null)
            throw new InvalidOperationException("Semantic ranking did not complete: " + result.ToJsonString());
        Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    Task<JsonObject> SearchAsync(JsonObject body) => transport.SearchAsync(HttpMethod.Post, settings.SearchEndpoint,
        $"indexes/{settings.Index}/docs/search", body, 200);
}

public sealed record IndexerRunState(string Endpoint, string Indexer, string? PreviousStart, bool Requested, bool Completed);
