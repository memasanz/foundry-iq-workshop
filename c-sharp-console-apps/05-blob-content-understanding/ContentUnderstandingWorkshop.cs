using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class ContentUnderstandingWorkshop(SearchSettings settings, SearchTransport transport,
    Func<TimeSpan, Task>? pause = null, Func<DateTimeOffset>? clock = null)
{
    public const int Dimensions = 1536;
    public const string SemanticConfiguration = "chunk-semantic";
    public const string Question = "Which travel expenses always need itemized receipts, and what if a hotel receipt is missing?";
    readonly Func<TimeSpan, Task> delay = pause ?? (duration => Task.Delay(duration));
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    public static readonly string[] Commands = ["upload", "data-source", "index", "skillset", "indexer", "monitor",
        "inspect", "semantic-hybrid", "run"];

    public async Task ExecuteAsync(string command, string? question = null)
    {
        if (command is "semantic-hybrid" or "run" && question is not null && string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Provide a non-empty semantic-hybrid question.");
        switch (command)
        {
            case "upload": await UploadAsync(); break;
            case "data-source": await PutAsync("datasources", settings.DataSource, DataSourcePayload()); break;
            case "index": await PutAsync("indexes", settings.Index, IndexPayload()); break;
            case "skillset": await PutAsync("skillsets", settings.Skills, SkillsetPayload()); break;
            case "indexer": await IndexerAsync(); break;
            case "monitor": await MonitorAsync(); break;
            case "inspect": await InspectAsync(); break;
            case "semantic-hybrid":
                await QueryAsync(question ?? Question); break;
            case "run":
                foreach (string step in Commands.Where(step => step != "run"))
                    await ExecuteAsync(step, question);
                break;
            default: throw new ArgumentException("Unknown lab command.");
        }
    }

    public async Task UploadAsync()
    {
        string path = Path.Combine(settings.Root, "data", "search", "travel-handbook.pdf");
        byte[] data = await File.ReadAllBytesAsync(path);
        if (!data.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidOperationException("Expected a PDF file: travel-handbook.pdf");
        await transport.UploadAsync(settings.StorageEndpoint, settings.Container,
            settings.BlobPrefix + "travel-handbook.pdf", data);
        Console.WriteLine("Uploaded: " + settings.BlobPrefix + "travel-handbook.pdf");
    }

    public JsonObject DataSourcePayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.DataSource}}",
          "type": "azureblob",
          "credentials": {
            "connectionString": "ResourceId={{settings.StorageId}};"
          },
          "container": {
            "name": "{{settings.Container}}",
            "query": "{{settings.BlobPrefix}}"
          }
        }
        """)!.AsObject();

    public JsonObject IndexPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Index}}",
          "fields": [
            {
              "name": "chunk_id",
              "type": "Edm.String",
              "key": true,
              "searchable": true,
              "analyzer": "keyword"
            },
            {
              "name": "parent_id",
              "type": "Edm.String",
              "filterable": true
            },
            {
              "name": "chunk",
              "type": "Edm.String",
              "searchable": true
            },
            {
              "name": "title",
              "type": "Edm.String",
              "searchable": true
            },
            {
              "name": "page_from",
              "type": "Edm.Int32",
              "filterable": true
            },
            {
              "name": "page_to",
              "type": "Edm.Int32",
              "filterable": true
            },
            {
              "name": "ordinal_position",
              "type": "Edm.Int32",
              "sortable": true
            },
            {
              "name": "location_source",
              "type": "Edm.String"
            },
            {
              "name": "text_vector",
              "type": "Collection(Edm.Single)",
              "searchable": true,
              "retrievable": true,
              "dimensions": {{Dimensions}},
              "vectorSearchProfile": "chunk-vector-profile"
            },
            {
              "name": "source",
              "type": "Edm.ComplexType",
              "fields": [
                {
                  "name": "file_name",
                  "type": "Edm.String",
                  "searchable": true
                },
                {
                  "name": "url",
                  "type": "Edm.String"
                }
              ]
            }
          ],
          "vectorSearch": {
            "algorithms": [
              {
                "name": "chunk-hnsw",
                "kind": "hnsw",
                "hnswParameters": {
                  "metric": "cosine"
                }
              }
            ],
            "profiles": [
              {
                "name": "chunk-vector-profile",
                "algorithm": "chunk-hnsw",
                "vectorizer": "chunk-vectorizer"
              }
            ],
            "vectorizers": [
              {
                "name": "chunk-vectorizer",
                "kind": "azureOpenAI",
                "azureOpenAIParameters": {
                  "resourceUri": "{{settings.EmbeddingEndpoint}}",
                  "deploymentId": "{{settings.Deployment}}",
                  "modelName": "{{settings.Model}}"
                }
              }
            ]
          },
          "semantic": {
            "defaultConfiguration": "{{SemanticConfiguration}}",
            "configurations": [
              {
                "name": "{{SemanticConfiguration}}",
                "prioritizedFields": {
                  "titleField": {
                    "fieldName": "title"
                  },
                  "prioritizedContentFields": [
                    {
                      "fieldName": "chunk"
                    }
                  ]
                }
              }
            ]
          }
        }
        """)!.AsObject();

    // Analyze the original PDF; embed each Markdown content string, not its section object.
    public JsonObject SkillsetPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Skills}}",
          "description": "Content Understanding PDF extraction, chunking, vectors and page provenance.",
          "cognitiveServices": {
            "@odata.type": "#Microsoft.Azure.Search.AIServicesByIdentity",
            "subdomainUrl": "{{settings.FoundryEndpoint}}",
            "identity": null
          },
          "skills": [
            {
              "@odata.type": "#Microsoft.Skills.Util.ContentUnderstandingSkill",
              "name": "understand-pdf",
              "context": "/document",
              "extractionOptions": [
                "locationMetadata"
              ],
              "chunkingProperties": {
                "unit": "characters",
                "maximumLength": 1000,
                "overlapLength": 200
              },
              "inputs": [
                {
                  "name": "file_data",
                  "source": "/document/file_data"
                }
              ],
              "outputs": [
                {
                  "name": "text_sections",
                  "targetName": "text_sections"
                }
              ]
            },
            {
              "@odata.type": "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
              "name": "embed-sections",
              "context": "/document/text_sections/*",
              "resourceUri": "{{settings.EmbeddingEndpoint}}",
              "deploymentId": "{{settings.Deployment}}",
              "modelName": "{{settings.Model}}",
              "dimensions": {{Dimensions}},
              "inputs": [
                {
                  "name": "text",
                  "source": "/document/text_sections/*/content"
                }
              ],
              "outputs": [
                {
                  "name": "embedding",
                  "targetName": "text_vector"
                }
              ]
            },
            {
              "@odata.type": "#Microsoft.Skills.Util.ShaperSkill",
              "name": "shape-provenance",
              "context": "/document",
              "inputs": [
                {
                  "name": "file_name",
                  "source": "/document/metadata_storage_name"
                },
                {
                  "name": "url",
                  "source": "/document/metadata_storage_path"
                }
              ],
              "outputs": [
                {
                  "name": "output",
                  "targetName": "provenance"
                }
              ]
            }
          ],
          "indexProjections": {
            "selectors": [
              {
                "targetIndexName": "{{settings.Index}}",
                "parentKeyFieldName": "parent_id",
                "sourceContext": "/document/text_sections/*",
                "mappings": [
                  {
                    "name": "chunk",
                    "source": "/document/text_sections/*/content"
                  },
                  {
                    "name": "text_vector",
                    "source": "/document/text_sections/*/text_vector"
                  },
                  {
                    "name": "title",
                    "source": "/document/metadata_storage_name"
                  },
                  {
                    "name": "source",
                    "source": "/document/provenance"
                  },
                  {
                    "name": "page_from",
                    "source": "/document/text_sections/*/locationMetadata/pageNumberFrom"
                  },
                  {
                    "name": "page_to",
                    "source": "/document/text_sections/*/locationMetadata/pageNumberTo"
                  },
                  {
                    "name": "ordinal_position",
                    "source": "/document/text_sections/*/locationMetadata/ordinalPosition"
                  },
                  {
                    "name": "location_source",
                    "source": "/document/text_sections/*/locationMetadata/source"
                  }
                ]
              }
            ],
            "parameters": {
              "projectionMode": "skipIndexingParentDocuments"
            }
          }
        }
        """)!.AsObject();

    // Expose original file_data while leaving extraction and chunking to Content Understanding.
    public JsonObject IndexerPayload() => JsonNode.Parse($$"""
        {
          "name": "{{settings.Indexer}}",
          "dataSourceName": "{{settings.DataSource}}",
          "targetIndexName": "{{settings.Index}}",
          "skillsetName": "{{settings.Skills}}",
          "parameters": {
            "maxFailedItems": 0,
            "maxFailedItemsPerBatch": 0,
            "configuration": {
              "dataToExtract": "allMetadata",
              "allowSkillsetToReadFileData": true,
              "indexedFileNameExtensions": ".pdf"
            }
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
        var result = await SearchAsync(InspectPayload());
        if (result["value"] is not JsonArray { Count: > 0 } chunks)
            throw new InvalidOperationException("No chunks were indexed. Inspect the indexer errors and source folder.");
        ValidatePageRanges(chunks);
        Console.WriteLine("Total chunks: " + result["@odata.count"]);
        Console.WriteLine(chunks.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ValidatePageRanges(JsonArray chunks)
    {
        // This validation is specific to the original two-page workshop sample.
        foreach (JsonNode? item in chunks)
        {
            if (item?["page_from"] is not JsonValue from || !from.TryGetValue<int>(out int first) ||
                item?["page_to"] is not JsonValue to || !to.TryGetValue<int>(out int last) ||
                first < 1 || first > last || last > 2)
                throw new InvalidOperationException("Expected valid page ranges from the two-page sample; inspect locationMetadata.");
        }
    }

    public static JsonObject InspectPayload() => JsonNode.Parse($$"""
        {
          "search": "*",
          "count": true,
          "top": 100,
          "orderby": "ordinal_position",
          "select": "chunk_id,parent_id,title,chunk,source,page_from,page_to,ordinal_position"
        }
        """)!.AsObject();

    public static JsonObject QueryPayload(string question)
    {
        return new JsonObject
        {
            ["search"] = question,
            ["searchFields"] = "chunk",
            ["vectorQueries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "text",
                ["text"] = question,
                ["fields"] = "text_vector",
                ["k"] = 50
            }),
            ["queryType"] = "semantic",
            ["semanticConfiguration"] = SemanticConfiguration,
            ["captions"] = "extractive",
            ["select"] = "chunk_id,title,chunk,source,page_from,page_to",
            ["top"] = 3
        };
    }

    async Task QueryAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Provide a non-empty semantic-hybrid question.");
        JsonObject result = await SearchAsync(QueryPayload(question));
        if (result["@search.semanticPartialResponseReason"] is not null)
            throw new InvalidOperationException("Semantic ranking did not complete: " + result.ToJsonString());
        if (result["value"] is not JsonArray { Count: > 0 })
            throw new InvalidOperationException("No evidence returned. Inspect the indexed chunks before continuing.");
        Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    Task<JsonObject> SearchAsync(JsonObject body) => transport.SearchAsync(HttpMethod.Post, settings.SearchEndpoint,
        $"indexes/{settings.Index}/docs/search", body, 200);
}

public sealed record IndexerRunState(string Endpoint, string Indexer, string? PreviousStart, bool Requested, bool Completed);
