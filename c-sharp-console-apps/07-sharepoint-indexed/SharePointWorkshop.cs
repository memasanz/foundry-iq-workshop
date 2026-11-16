using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

public sealed class SharePointWorkshop(SharePointSettings settings, SharePointTransport transport,
    Func<TimeSpan, Task>? pause = null, bool persistCheckpoint = false)
{
    readonly Func<TimeSpan, Task> delay = pause ?? (duration => Task.Delay(duration));
    public string? PreviousRunStart { get; private set; }

    public JsonObject DataSourceDefinition() => new JsonObject
    {
        ["name"] = settings.DataSource,
        ["type"] = "sharepoint",
        ["credentials"] = new JsonObject { ["connectionString"] = settings.ConnectionString() },
        ["container"] = new JsonObject { ["name"] = "defaultSiteLibrary" },
        ["indexerPermissionOptions"] = new JsonArray("userIds", "groupIds"),
    };

    public JsonObject IndexDefinition()
    {
        JsonObject index = JsonNode.Parse("""
            {
              "permissionFilterOption": "enabled",
              "fields": [
                {"name":"id","type":"Edm.String","key":true,"searchable":true,"analyzer":"keyword"},
                {"name":"parent_id","type":"Edm.String","filterable":true},
                {"name":"content","type":"Edm.String","searchable":true},
                {"name":"contentVector","type":"Collection(Edm.Single)","searchable":true,"retrievable":false,"dimensions":1536,"vectorSearchProfile":"text-profile"},
                {"name":"sourceFile","type":"Edm.String","searchable":true,"filterable":true},
                {"name":"webUrl","type":"Edm.String"},
                {"name":"page","type":"Edm.Int32","filterable":true},
                {"name":"pageTo","type":"Edm.Int32","filterable":true},
                {"name":"metadata_spo_item_id","type":"Edm.String","filterable":true},
                {"name":"metadata_spo_item_path","type":"Edm.String","searchable":false,"filterable":true,"retrievable":false},
                {"name":"lastModified","type":"Edm.DateTimeOffset","filterable":true},
                {"name":"UserIds","type":"Collection(Edm.String)","permissionFilter":"userIds","filterable":true,"retrievable":false},
                {"name":"GroupIds","type":"Collection(Edm.String)","permissionFilter":"groupIds","filterable":true,"retrievable":false}
              ],
              "vectorSearch": {
                "algorithms":[{"name":"hnsw","kind":"hnsw","hnswParameters":{"metric":"cosine"}}],
                "profiles":[{"name":"text-profile","algorithm":"hnsw","vectorizer":"aoai-vectorizer"}],
                "vectorizers":[{"name":"aoai-vectorizer","kind":"azureOpenAI","azureOpenAIParameters":{}}]
              },
              "semantic": {
                "defaultConfiguration":"semantic-config",
                "configurations":[{"name":"semantic-config","prioritizedFields":{
                  "titleField":{"fieldName":"sourceFile"},
                  "prioritizedContentFields":[{"fieldName":"content"}]
                }}]
              }
            }
            """)!.AsObject();
        index["name"] = settings.Index;
        index["vectorSearch"]!["vectorizers"]![0]!["azureOpenAIParameters"] = new JsonObject
        {
            ["resourceUri"] = settings.EmbeddingEndpoint,
            ["deploymentId"] = settings.Deployment,
            ["modelName"] = settings.Model,
        };
        return index;
    }

    public JsonObject SkillsetDefinition()
    {
        JsonObject skillset = JsonNode.Parse("""
            {
              "description":"Content Understanding semantic text sections, embeddings and Entra ACLs.",
              "cognitiveServices":{"@odata.type":"#Microsoft.Azure.Search.AIServicesByIdentity"},
              "skills":[
                {
                  "@odata.type":"#Microsoft.Skills.Util.ContentUnderstandingSkill",
                  "name":"content-understanding","context":"/document",
                  "extractionOptions":["locationMetadata"],
                  "chunkingProperties":{"method":"semantic","unit":"tokens","maximumLength":800,"overlapLength":0},
                  "inputs":[{"name":"file_data","source":"/document/file_data"}],
                  "outputs":[{"name":"text_sections","targetName":"text_sections"}]
                },
                {
                  "@odata.type":"#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
                  "name":"text-embed","context":"/document/text_sections/*","dimensions":1536,
                  "inputs":[{"name":"text","source":"/document/text_sections/*/content"}],
                  "outputs":[{"name":"embedding","targetName":"content_vector"}]
                }
              ],
              "indexProjections":{
                "selectors":[{
                  "parentKeyFieldName":"parent_id","sourceContext":"/document/text_sections/*",
                  "mappings":[
                    {"name":"content","source":"/document/text_sections/*/content"},
                    {"name":"contentVector","source":"/document/text_sections/*/content_vector"},
                    {"name":"page","source":"/document/text_sections/*/locationMetadata/pageNumberFrom"},
                    {"name":"pageTo","source":"/document/text_sections/*/locationMetadata/pageNumberTo"},
                    {"name":"sourceFile","source":"/document/metadata_spo_item_name"},
                    {"name":"metadata_spo_item_id","source":"/document/metadata_spo_item_id"},
                    {"name":"webUrl","source":"/document/metadata_spo_item_weburi"},
                    {"name":"metadata_spo_item_path","source":"/document/metadata_spo_item_path"},
                    {"name":"lastModified","source":"/document/metadata_spo_item_last_modified"},
                    {"name":"UserIds","source":"/document/metadata_user_ids"},
                    {"name":"GroupIds","source":"/document/metadata_group_ids"}
                  ]
                }],
                "parameters":{"projectionMode":"skipIndexingParentDocuments"}
              }
            }
            """)!.AsObject();
        skillset["name"] = settings.Skills;
        skillset["cognitiveServices"]!["subdomainUrl"] = settings.FoundryEndpoint;
        JsonObject embedding = skillset["skills"]![1]!.AsObject();
        embedding["resourceUri"] = settings.EmbeddingEndpoint;
        embedding["deploymentId"] = settings.Deployment;
        embedding["modelName"] = settings.Model;
        skillset["indexProjections"]!["selectors"]![0]!["targetIndexName"] = settings.Index;
        return skillset;
    }

    public JsonObject IndexerDefinition() => new JsonObject
    {
        ["name"] = settings.Indexer,
        ["dataSourceName"] = settings.DataSource,
        ["targetIndexName"] = settings.Index,
        ["skillsetName"] = settings.Skills,
        ["parameters"] = new JsonObject
        {
            ["batchSize"] = 1,
            ["maxFailedItems"] = 0,
            ["maxFailedItemsPerBatch"] = 0,
            ["configuration"] = new JsonObject { ["dataToExtract"] = "contentAndMetadata", ["allowSkillsetToReadFileData"] = true },
        },
    };

    public static JsonObject QueryDefinition(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question or '*'.");
        return new JsonObject
        {
            ["search"] = question,
            ["count"] = true,
            ["top"] = 5,
            ["select"] = "sourceFile,webUrl,page,pageTo,content",
        };
    }

    public async Task CreateDataSourceAsync(bool setupConfirmed)
    {
        SharePointSettings.ConfirmSetup(setupConfirmed);
        JsonObject definition = DataSourceDefinition();
        await transport.RequestAsync(HttpMethod.Put, "datasources/" + settings.DataSource, definition);
        Console.WriteLine("Secretless SharePoint data source ready: " + settings.DataSource);
    }

    public async Task CreateIndexAsync()
    {
        await transport.RequestAsync(HttpMethod.Put, "indexes/" + settings.Index, IndexDefinition());
        Console.WriteLine("ACL-aware index ready: " + settings.Index);
    }

    public async Task CreateSkillsetAsync()
    {
        await transport.RequestAsync(HttpMethod.Put, "skillsets/" + settings.Skills, SkillsetDefinition());
        Console.WriteLine("ACL-preserving Content Understanding skillset ready: " + settings.Skills);
    }

    public async Task CreateIndexerAsync(bool setupConfirmed)
    {
        SharePointSettings.ConfirmSetup(setupConfirmed);
        JsonObject? status = await transport.RequestAsync(HttpMethod.Get, "indexers/" + settings.Indexer + "/status", allowMissing: true);
        EnsureIdle(status);
        PreviousRunStart = status?["lastResult"]?["startTime"]?.ToString();
        await transport.RequestAsync(HttpMethod.Put, "indexers/" + settings.Indexer, IndexerDefinition());
        SaveCheckpoint();
        Console.WriteLine("Indexer ready: " + settings.Indexer + ". Creation starts ingestion; no schedule is set.");
        if (status != null)
        {
            await transport.RequestAsync(HttpMethod.Post, "indexers/" + settings.Indexer + "/run");
            Console.WriteLine("New run requested for the existing indexer.");
        }
    }

    public async Task<JsonObject> StatusAsync()
    {
        JsonObject status = (await transport.RequestAsync(HttpMethod.Get, "indexers/" + settings.Indexer + "/status"))!;
        Print(status);
        return status;
    }

    public async Task RunIndexerAsync(bool resyncPermissions = false)
    {
        JsonObject status = (await transport.RequestAsync(HttpMethod.Get, "indexers/" + settings.Indexer + "/status"))!;
        EnsureIdle(status);
        PreviousRunStart = status["lastResult"]?["startTime"]?.ToString();
        SaveCheckpoint();
        if (resyncPermissions)
            await transport.RequestAsync(HttpMethod.Post, "indexers/" + settings.Indexer + "/resync",
                new JsonObject { ["options"] = new JsonArray("permissions") });
        await transport.RequestAsync(HttpMethod.Post, "indexers/" + settings.Indexer + "/run");
        Console.WriteLine("Run requested. Use wait, then query. Permissions resync: " + resyncPermissions);
    }

    public async Task<JsonObject> WaitAsync()
    {
        LoadCheckpoint();
        for (int attempt = 0; attempt < 90; attempt++)
        {
            JsonObject status = (await transport.RequestAsync(HttpMethod.Get, "indexers/" + settings.Indexer + "/status"))!;
            JsonObject last = status["lastResult"]?.AsObject() ?? new JsonObject();
            string? state = last["status"]?.ToString();
            bool requestedRun = PreviousRunStart == null || last["startTime"]?.ToString() != PreviousRunStart;
            Console.WriteLine($"Run: {state ?? "(not started)"} | processed: {last["itemsProcessed"]} | failed: {last["itemsFailed"]}");
            if (status["status"]?.ToString() == "error" ||
                (requestedRun && state is "transientFailure" or "persistentFailure" or "error"))
                throw new InvalidOperationException("Indexer failed: " + Pretty(last.Count == 0 ? status : last));
            if (state == "success" && requestedRun)
            {
                if ((last["itemsFailed"]?.GetValue<int>() ?? 0) != 0 || (last["errors"]?.AsArray().Count ?? 0) != 0)
                    throw new InvalidOperationException("Indexer reported failed documents: " + Pretty(last));
                Console.WriteLine("Warnings: " + Pretty(last["warnings"] ?? new JsonArray()));
                Console.WriteLine("Ingestion completed. This is not proof of two-user permission trimming.");
                return status;
            }
            if (attempt < 89) await delay(TimeSpan.FromSeconds(10));
        }
        throw new TimeoutException("Indexer did not complete within 15 minutes. Inspect status before requesting another billable run.");
    }

    public async Task<JsonObject> QueryAsync(string question = "*")
    {
        JsonObject body = QueryDefinition(question);
        JsonObject result = (await transport.RequestAsync(HttpMethod.Post, "indexes/" + settings.Index + "/docs/search",
            body, asUser: true))!;
        Console.WriteLine("Visible chunks: " + result["@odata.count"]);
        Print(result);
        Console.WriteLine("Empty results can be valid. Compare the same query using two non-admin member accounts.");
        return result;
    }

    public async Task CleanupAsync(bool confirm)
    {
        if (!confirm) throw new InvalidOperationException("Cleanup requires exactly: cleanup --confirm.");
        LoadCheckpoint();
        var objects = new[]
        {
            (Collection: "indexers", Name: settings.Indexer), (Collection: "skillsets", Name: settings.Skills),
            (Collection: "indexes", Name: settings.Index), (Collection: "datasources", Name: settings.DataSource),
        };
        var definitions = new JsonObject?[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            var item = objects[i];
            definitions[i] = await transport.RequestAsync(HttpMethod.Get, item.Collection + "/" + item.Name, allowMissing: true);
            if (definitions[i] != null) ValidateCleanupTarget(item.Collection, definitions[i]!);
        }
        for (int i = 0; i < objects.Length; i++)
        {
            var item = objects[i];
            if (definitions[i] != null)
                await transport.RequestAsync(HttpMethod.Delete, item.Collection + "/" + item.Name, allowMissing: true);
            Console.WriteLine("Deleted or already absent: " + item.Name);
        }
        if (persistCheckpoint && File.Exists(settings.StatePath))
        {
            LoadCheckpoint();
            File.Delete(settings.StatePath);
        }
        Console.WriteLine("App, federation, consent, selected-site grant, SharePoint files/site and other indexes are retained.");
    }

    void ValidateCleanupTarget(string collection, JsonObject definition)
    {
        bool safe = collection switch
        {
            "indexers" => (string?)definition["name"] == settings.Indexer &&
                (string?)definition["dataSourceName"] == settings.DataSource &&
                (string?)definition["targetIndexName"] == settings.Index && (string?)definition["skillsetName"] == settings.Skills,
            "skillsets" => (string?)definition["name"] == settings.Skills &&
                (string?)definition["cognitiveServices"]?["subdomainUrl"] == settings.FoundryEndpoint &&
                definition["indexProjections"]?["selectors"]?.AsArray() is { Count: 1 } selectors &&
                (string?)selectors[0]?["targetIndexName"] == settings.Index &&
                (string?)definition["indexProjections"]?["parameters"]?["projectionMode"] == "skipIndexingParentDocuments",
            "indexes" => (string?)definition["name"] == settings.Index && (string?)definition["permissionFilterOption"] == "enabled" &&
                definition["fields"]?.AsArray().Any(f => (string?)f?["name"] == "UserIds" && (string?)f?["permissionFilter"] == "userIds") == true &&
                definition["fields"]?.AsArray().Any(f => (string?)f?["name"] == "GroupIds" && (string?)f?["permissionFilter"] == "groupIds") == true,
            "datasources" => (string?)definition["name"] == settings.DataSource && (string?)definition["type"] == "sharepoint" &&
                (string?)definition["container"]?["name"] == "defaultSiteLibrary" && SafeConnection(definition),
            _ => false,
        };
        if (!safe) throw new InvalidOperationException("Cleanup stopped before deletion: " + collection + " has been repurposed or does not match this lab.");
    }

    bool SafeConnection(JsonObject definition)
    {
        string connection = definition["credentials"]?["connectionString"]?.ToString() ?? "";
        string[] parts = connection.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? Value(string name) => parts.FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal))?.Substring(name.Length + 1);
        return Value("SharePointOnlineEndpoint") == settings.SiteUrl && Value("TenantId") == settings.Tenant &&
            Guid.TryParse(Value("ApplicationId"), out var app) && Guid.TryParse(Value("FederatedCredentialApplicationId"), out var mi) &&
            app != mi && (settings.AppClientId.Length == 0 || app == Guid.Parse(settings.AppClientId)) &&
            (settings.SearchMiClientId.Length == 0 || mi == Guid.Parse(settings.SearchMiClientId));
    }

    static void EnsureIdle(JsonObject? status)
    {
        if (status?["lastResult"]?["status"]?.ToString() == "inProgress")
            throw new InvalidOperationException("The indexer is already running. Wait before updating, rerunning or resyncing it.");
    }

    void SaveCheckpoint()
    {
        if (!persistCheckpoint) return;
        Directory.CreateDirectory(Path.GetDirectoryName(settings.StatePath)!);
        File.WriteAllText(settings.StatePath, new JsonObject
        {
            ["searchEndpoint"] = settings.SearchEndpoint,
            ["indexer"] = settings.Indexer,
            ["previousRunStart"] = PreviousRunStart,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    void LoadCheckpoint()
    {
        if (!persistCheckpoint || !File.Exists(settings.StatePath)) return;
        JsonObject state = JsonNode.Parse(File.ReadAllText(settings.StatePath))!.AsObject();
        if ((string?)state["searchEndpoint"] != settings.SearchEndpoint || (string?)state["indexer"] != settings.Indexer ||
            !state.ContainsKey("previousRunStart"))
            throw new InvalidOperationException("Local indexer checkpoint does not match this Search/indexer. Inspect it before monitoring.");
        PreviousRunStart = (string?)state["previousRunStart"];
    }

    string Pretty(JsonNode value) => transport.Redact(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    void Print(JsonNode value) => Console.WriteLine(Pretty(value));
}
