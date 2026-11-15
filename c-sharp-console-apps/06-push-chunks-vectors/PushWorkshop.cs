using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class PushWorkshop(PushSettings settings, PushTransport transport, Func<TimeSpan, Task>? pause = null)
{
    public const int Dimensions = 1536;
    public const int ChunkSize = 1000;
    public const int ChunkOverlap = 200;
    public const string SemanticConfiguration = "chunk-semantic";
    public const string Question = "What receipts do I need to claim travel expenses, and when must I submit them?";
    public static readonly string[] QueryModes = ["keyword", "vector", "hybrid", "semantic", "semantic-hybrid"];
    public static readonly string[] Commands = ["index", "chunk", "embed", "push", "verify",
        "keyword", "vector", "hybrid", "semantic", "semantic-hybrid", "run", "cleanup", "help"];
    readonly Func<TimeSpan, Task> delay = pause ?? (duration => Task.Delay(duration));

    public async Task ExecuteAsync(string command, string? question = null, bool confirmCleanup = false)
    {
        settings.Validate();
        switch (command)
        {
            case "index": await CreateIndexAsync(); break;
            case "chunk":
                Invalidate(settings.VectorsPath, settings.UploadedPath);
                Invalidate(settings.ChunksPath);
                Save(settings.ChunksPath, ChunkDocuments());
                break;
            case "embed":
                // Invalidate before reads or paid calls: failure must not leave old vectors usable.
                Invalidate(settings.VectorsPath, settings.UploadedPath);
                var chunks = Load(settings.ChunksPath, false);
                await EmbedAsync(chunks);
                Save(settings.VectorsPath, chunks);
                break;
            case "push":
                Invalidate(settings.UploadedPath);
                var vectors = Load(settings.VectorsPath, true);
                await PushAsync(vectors);
                Save(settings.UploadedPath, vectors);
                break;
            case "verify": await VerifyAsync(Load(settings.UploadedPath, true)); break;
            case "keyword" or "vector" or "hybrid" or "semantic" or "semantic-hybrid":
                await QueryAsync(command, question ?? Question); break;
            case "run":
                if (string.IsNullOrWhiteSpace(question ?? Question))
                    throw new ArgumentException("Use a non-empty question.");
                foreach (string step in Commands.Take(10)) await ExecuteAsync(step, question);
                break;
            case "cleanup": await CleanupAsync(confirmCleanup); break;
            case "help": Console.WriteLine(string.Join(", ", Commands)); break;
            default: throw new ArgumentException("Unknown lab command.");
        }
    }

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

    public async Task CreateIndexAsync()
    {
        await transport.SearchAsync(HttpMethod.Put, settings.SearchEndpoint, $"indexes/{settings.Index}",
            IndexPayload(), 200, 201, 204);
        Console.WriteLine("Index ready: " + settings.Index);
    }

    public static JsonArray Slice(string text, string fileName, string sourceUrl,
        int chunkSize = ChunkSize, int overlap = ChunkOverlap)
    {
        if (chunkSize <= 0 || overlap < 0 || overlap >= chunkSize)
            throw new ArgumentException("Chunk overlap must be non-negative and smaller than chunk size.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Empty source document: " + fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!Regex.IsMatch(stem, @"^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Filename stem must be Search-key-safe.");
        // Python slices Unicode characters, not UTF-16 code units. Normalize TXT newlines like read_text.
        var characters = text.Replace("\r\n", "\n").Replace('\r', '\n').EnumerateRunes().ToArray();
        var documents = new JsonArray();
        for (int start = 0, ordinal = 0; start < characters.Length; start += chunkSize - overlap, ordinal++)
        {
            int end = Math.Min(start + chunkSize, characters.Length);
            string chunk = string.Concat(characters[start..end].Select(rune => rune.ToString()));
            documents.Add(new JsonObject
            {
                ["@search.action"] = "upload",
                ["chunk_id"] = $"{stem}-{ordinal}",
                ["parent_id"] = stem,
                ["title"] = fileName,
                ["chunk"] = chunk,
                ["source"] = new JsonObject { ["file_name"] = fileName, ["url"] = sourceUrl }
            });
            if (end == characters.Length) break;
        }
        return documents;
    }

    public JsonArray ChunkDocuments()
    {
        string directory = Path.Combine(settings.Root, "data", "search");
        var files = Directory.GetFiles(directory, "*.txt").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Length != 2) throw new InvalidOperationException("Expected the two workshop sample documents under data/search.");
        var documents = new JsonArray();
        foreach (string path in files)
            foreach (var document in Slice(new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)), Path.GetFileName(path),
                Path.GetRelativePath(settings.Root, path).Replace('\\', '/')))
                documents.Add(document!.DeepClone());
        Console.WriteLine($"Files: {files.Length} | chunks: {documents.Count}");
        return documents;
    }

    public static void ValidateDocuments(JsonArray documents, bool requireVectors)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (documents.Count == 0) throw new InvalidOperationException("Chunk and embed every document before pushing.");
        foreach (var item in documents)
        {
            string? key = item?["chunk_id"]?.GetValue<string>();
            if (key is null || !Regex.IsMatch(key, @"^[a-zA-Z0-9_-]+$") || !keys.Add(key) ||
                item?["@search.action"]?.GetValue<string>() != "upload" ||
                string.IsNullOrWhiteSpace(item?["chunk"]?.GetValue<string>()))
                throw new InvalidOperationException("Invalid or duplicate chunk documents.");
            if (requireVectors) ValidateVector(item!["text_vector"]);
        }
    }

    public static void ValidateVector(JsonNode? node)
    {
        if (node is not JsonArray vector || vector.Count != Dimensions ||
            vector.Any(value => value is null || !double.IsFinite(value.GetValue<double>())))
            throw new InvalidOperationException("Embedding dimensions do not match the Search vector field or contain non-finite values.");
    }

    public async Task EmbedAsync(JsonArray documents)
    {
        foreach (var document in documents) document?.AsObject().Remove("text_vector");
        ValidateDocuments(documents, false);
        try
        {
            for (int start = 0; start < documents.Count; start += 16)
            {
                var batch = documents.Skip(start).Take(16).ToArray();
                var payload = new JsonObject
                {
                    ["input"] = new JsonArray(batch.Select(item => (JsonNode?)JsonValue.Create(item!["chunk"]!.GetValue<string>())).ToArray()),
                    ["dimensions"] = Dimensions
                };
                var response = await transport.EmbeddingsAsync(settings, payload);
                if (response["data"] is not JsonArray embeddings || embeddings.Count != batch.Length)
                    throw new InvalidOperationException("Embedding response did not contain exactly one vector per input.");
                var seen = new HashSet<int>();
                foreach (var embedding in embeddings)
                {
                    if (embedding?["index"] is not JsonValue value || !value.TryGetValue<int>(out int index) ||
                        index < 0 || index >= batch.Length || !seen.Add(index))
                        throw new InvalidOperationException("Embedding response did not contain exactly one vector per input.");
                    ValidateVector(embedding["embedding"]);
                    batch[index]!["text_vector"] = embedding["embedding"]!.DeepClone();
                }
            }
        }
        catch
        {
            foreach (var document in documents) document?.AsObject().Remove("text_vector");
            throw;
        }
        Console.WriteLine("Embedded chunks: " + documents.Count + " | dimensions: " + Dimensions);
    }

    public async Task PushAsync(JsonArray documents)
    {
        ValidateDocuments(documents, true);
        for (int start = 0; start < documents.Count; start += 100)
        {
            var batch = new JsonArray(documents.Skip(start).Take(100).Select(item => item!.DeepClone()).ToArray());
            var response = await transport.SearchAsync(HttpMethod.Post, settings.SearchEndpoint,
                $"indexes/{settings.Index}/docs/index", new JsonObject { ["value"] = batch }, 200, 207);
            var expected = batch.Select(item => item!["chunk_id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
            if (response["value"] is not JsonArray results ||
                !results.Select(item => item?["key"]?.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(expected) ||
                results.Any(item => item?["status"]?.GetValue<bool>() != true))
                throw new InvalidOperationException("Push was incomplete or some documents failed: " + response.ToJsonString());
            Console.WriteLine("Uploaded chunks: " + batch.Count);
        }
    }

    public async Task VerifyAsync(JsonArray documents)
    {
        ValidateDocuments(documents, true);
        var pending = new List<string>();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            pending.Clear();
            foreach (var document in documents)
            {
                string key = document!["chunk_id"]!.GetValue<string>();
                var stored = await transport.SearchAsync(HttpMethod.Get, settings.SearchEndpoint,
                    $"indexes/{settings.Index}/docs/{Uri.EscapeDataString(key)}", null, 200, 404);
                bool match = stored["missing"]?.GetValue<bool>() != true &&
                    stored["chunk"]?.GetValue<string>() == document["chunk"]!.GetValue<string>() &&
                    stored["text_vector"] is JsonArray { Count: Dimensions } actual &&
                    actual.Zip(document["text_vector"]!.AsArray()).All(pair =>
                        pair.First is not null && double.IsFinite(pair.First.GetValue<double>()) &&
                        Math.Abs(pair.First.GetValue<double>() - pair.Second!.GetValue<double>()) <= 1e-6);
                if (!match) pending.Add(key);
            }
            if (pending.Count == 0)
            {
                foreach (var document in documents)
                    Console.WriteLine($"{document!["chunk_id"]} | vector dimensions: {Dimensions}");
                return;
            }
            if (attempt < 29) await delay(TimeSpan.FromSeconds(2));
        }
        throw new TimeoutException("Pushed documents are not yet visible with the expected content: " + string.Join(", ", pending));
    }

    public static JsonObject QueryPayload(string mode, string question)
    {
        if (!QueryModes.Contains(mode)) throw new ArgumentException("Unknown query mode.");
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question.");
        var payload = new JsonObject { ["select"] = "chunk_id,title,chunk,source", ["top"] = 3 };
        if (mode != "vector")
        {
            payload["search"] = question;
            payload["searchFields"] = "chunk";
        }
        if (mode is "vector" or "hybrid" or "semantic-hybrid")
            payload["vectorQueries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "text",
                ["text"] = question,
                ["fields"] = "text_vector",
                ["k"] = 50
            });
        if (mode is "semantic" or "semantic-hybrid")
        {
            payload["queryType"] = "semantic";
            payload["semanticConfiguration"] = SemanticConfiguration;
            payload["captions"] = "extractive";
        }
        return payload;
    }

    public async Task QueryAsync(string mode, string question)
    {
        var response = await transport.SearchAsync(HttpMethod.Post, settings.SearchEndpoint,
            $"indexes/{settings.Index}/docs/search", QueryPayload(mode, question), 200);
        if (response["@search.semanticPartialResponseReason"]?.ToString() is { Length: > 0 })
            throw new InvalidOperationException("Semantic ranking did not complete: " + response.ToJsonString());
        Console.WriteLine(response.ToJsonString());
    }

    public async Task CleanupAsync(bool confirm)
    {
        if (!confirm) throw new InvalidOperationException("Cleanup disabled. Use cleanup --confirm to delete only your push index.");
        settings.Validate();
        await transport.SearchAsync(HttpMethod.Delete, settings.SearchEndpoint,
            $"indexes/{settings.Index}", null, 200, 204, 404);
        Invalidate(settings.UploadedPath);
        Console.WriteLine("Deleted or already absent: " + settings.Index);
    }

    JsonObject Compatibility() => new()
    {
        ["version"] = 1,
        ["root"] = Path.GetFullPath(settings.Root),
        ["searchEndpoint"] = settings.SearchEndpoint,
        ["index"] = settings.Index,
        ["embeddingEndpoint"] = settings.EmbeddingEndpoint,
        ["deployment"] = settings.Deployment,
        ["model"] = settings.Model,
        ["dimensions"] = Dimensions,
        ["chunkSize"] = ChunkSize,
        ["overlap"] = ChunkOverlap,
        ["sourceFingerprint"] = Fingerprint(ChunkDocuments())
    };

    static string Fingerprint(JsonArray documents) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documents.ToJsonString())));

    void Save(string path, JsonArray documents)
    {
        Directory.CreateDirectory(settings.CacheDirectory);
        var cache = new JsonObject
        {
            ["compatibility"] = Compatibility(),
            ["documents"] = documents.DeepClone(),
            ["documentFingerprint"] = Fingerprint(documents)
        };
        string staging = path + ".writing";
        try
        {
            File.WriteAllText(staging, cache.ToJsonString(), Encoding.UTF8);
            File.Move(staging, path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    JsonArray Load(string path, bool vectors)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Missing local step artifact: " + Path.GetFileName(path) + ". Run chunk, embed, push in order.");
        var cache = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (!JsonNode.DeepEquals(cache["compatibility"], Compatibility()) ||
            cache["documents"] is not JsonArray documents ||
            cache["documentFingerprint"]?.GetValue<string>() != Fingerprint(documents))
            throw new InvalidOperationException("Stale or incompatible local step artifact. Run chunk, embed, push in order.");
        ValidateDocuments(documents, vectors);
        var unembedded = new JsonArray(documents.Select(item =>
        {
            var copy = item!.DeepClone().AsObject();
            copy.Remove("text_vector");
            return (JsonNode)copy;
        }).ToArray());
        if (!JsonNode.DeepEquals(unembedded, ChunkDocuments()))
            throw new InvalidOperationException("Cached chunks do not match source files. Run chunk, embed, push in order.");
        return documents;
    }

    static void Invalidate(params string[] paths)
    {
        foreach (string path in paths)
            if (File.Exists(path)) File.Delete(path);
    }
}
