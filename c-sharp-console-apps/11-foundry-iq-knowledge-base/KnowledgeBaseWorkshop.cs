using System.Text.Json.Nodes;

public sealed record KnowledgeBaseOptions(string Command, string? Question = null,
    string OutputMode = "answerSynthesis", bool Confirm = false, bool Diagnostics = false)
{
    public const string Help = """
        verify-indexes, blob-source, sharepoint-source, webiq-source, knowledge-base,
        retrieve-internal|retrieve-web|retrieve-sharepoint|retrieve-mixed [question]
          [--output-mode answerSynthesis|extractiveData] [--diagnostics],
        run [mixed question] [--output-mode answerSynthesis|extractiveData] [--diagnostics],
        cleanup --confirm, help / --help / -h / no arguments
        Run from the repository root; each command performs only its named step.
        Requires lab05 <prefix>-cu-chunks and lab07 ACL <prefix>-nb7-index; never creates/changes indexes.
        Run verifies indexes, PUTs three sources and one KB, then retrieves four documented questions.
        Retrieval invokes billable Search/model/vectorizer/Web IQ operations; run never cleans up.
        --diagnostics prints raw activity, MCP activity and references for that same response, no extra call/cache.
        Caution: diagnostics may contain internal passages, query text and private URLs. Do not share outputs.
        Cleanup deletes only <prefix>-kb then its three <prefix>-kb-* sources, never indexes or lab09/10.
        """;

    public static KnowledgeBaseOptions Parse(string[] args)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            if (args.Length > 1) throw new ArgumentException("Help takes no arguments.");
            return new("help");
        }
        if (command == "cleanup")
        {
            if (args.Length != 2 || args[1] != "--confirm")
                throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
            return new(command, Confirm: true);
        }
        if (command is "verify-indexes" or "blob-source" or "sharepoint-source" or "webiq-source" or "knowledge-base")
        {
            if (args.Length != 1) throw new ArgumentException("This step takes no arguments. Use help.");
            return new(command);
        }
        if (command is not ("retrieve-internal" or "retrieve-web" or "retrieve-sharepoint" or "retrieve-mixed" or "run"))
            throw new ArgumentException("Unknown command. Use help.");
        string? question = null;
        string mode = "answerSynthesis";
        bool suppliedMode = false, diagnostics = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--output-mode" && !suppliedMode && i + 1 < args.Length)
            {
                mode = args[++i];
                suppliedMode = true;
            }
            else if (args[i] == "--diagnostics" && !diagnostics) diagnostics = true;
            else if (!args[i].StartsWith('-') && question is null) question = args[i];
            else throw new ArgumentException("Use one quoted question, optional --output-mode and --diagnostics. Use help.");
        }
        KnowledgeBaseResults.ValidateQuery(question ?? "default question", mode);
        return new(command, question, mode, Diagnostics: diagnostics);
    }
}

public sealed class KnowledgeBaseWorkshop(KnowledgeBaseSettings settings, KnowledgeBaseTransport transport)
{
    public static string Question(KnowledgeBaseSettings settings, string selection) => selection switch
    {
        "internal" => $"Using {settings.BlobSourceName}, what receipts do I need to submit for travel reimbursement?",
        "web" => "What is the weather in St. Paul MN today?",
        "sharepoint" => $"Using {settings.SharePointSourceName}, what are the main recommendations about sustainability and AI?",
        "mixed" => $"Using {settings.BlobSourceName} and {settings.WebIqSourceName}, how does our travel reimbursement policy compare with common corporate travel expense best practices on the public web?",
        _ => throw new ArgumentException("Use internal, web, sharepoint or mixed.")
    };

    public static JsonObject BlobSourcePayload(KnowledgeBaseSettings settings) => new()
    {
        ["name"] = settings.BlobSourceName,
        ["kind"] = "searchIndex",
        ["description"] = "Travel reimbursement and onboarding policy documents from the existing Blob index.",
        ["searchIndexParameters"] = new JsonObject
        {
            ["searchIndexName"] = settings.BlobIndexName,
            ["semanticConfigurationName"] = "chunk-semantic",
            ["searchFields"] = new JsonArray(new JsonObject { ["name"] = "chunk" }),
            ["sourceDataFields"] = new JsonArray(
                new JsonObject { ["name"] = "chunk_id" }, new JsonObject { ["name"] = "chunk" },
                new JsonObject { ["name"] = "title" }, new JsonObject { ["name"] = "source/file_name" },
                new JsonObject { ["name"] = "source/url" }, new JsonObject { ["name"] = "page_from" },
                new JsonObject { ["name"] = "page_to" })
        }
    };

    public static JsonObject SharePointSourcePayload(KnowledgeBaseSettings settings) => new()
    {
        ["name"] = settings.SharePointSourceName,
        ["kind"] = "searchIndex",
        ["description"] = "Reports and documents from the existing SharePoint library index, with Entra user/group permissions.",
        ["searchIndexParameters"] = new JsonObject
        {
            ["searchIndexName"] = settings.SharePointIndexName,
            ["semanticConfigurationName"] = "semantic-config",
            ["searchFields"] = new JsonArray(new JsonObject { ["name"] = "content" }),
            ["sourceDataFields"] = new JsonArray(
                new JsonObject { ["name"] = "id" }, new JsonObject { ["name"] = "content" },
                new JsonObject { ["name"] = "sourceFile" }, new JsonObject { ["name"] = "webUrl" },
                new JsonObject { ["name"] = "page" }, new JsonObject { ["name"] = "pageTo" })
        }
    };

    public static JsonObject WebIqSourcePayload(KnowledgeBaseSettings settings) => new()
    {
        ["name"] = settings.WebIqSourceName,
        ["kind"] = "mcpServer",
        ["description"] = "Basic Web IQ web search for public information. Always set contentFormat to passage; do not request full-document text, html or markdown.",
        ["resultsProcessing"] = "rerank",
        ["mcpServerParameters"] = new JsonObject
        {
            ["serverURL"] = "https://api.microsoft.ai/v3/mcp",
            ["authentication"] = new JsonObject
            {
                ["kind"] = "storedHeaders",
                ["storedHeadersParameters"] = new JsonObject
                {
                    ["headers"] = new JsonObject { ["x-apikey"] = settings.WebIqKey }
                }
            },
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "web",
                ["outputParsing"] = new JsonObject { ["kind"] = "auto" },
                ["maxOutputTokens"] = 2000
            })
        }
    };

    public static JsonObject BasePayload(KnowledgeBaseSettings settings) => new()
    {
        ["name"] = settings.BaseName,
        ["description"] = "Workshop policies (Blob and SharePoint) plus live public web information from Web IQ.",
        ["retrievalInstructions"] = $"Use {settings.BlobSourceName} for travel reimbursement and onboarding policy documents from Blob. " +
            $"Use {settings.SharePointSourceName} for reports and other documents from the SharePoint library. " +
            "Consider both indexed sources when an internal question does not identify its document source. " +
            $"Use {settings.WebIqSourceName} for current public information and external facts. " +
            "For every Web IQ web call, explicitly set contentFormat to passage. " +
            "Never request text, html or markdown content formats; use only query-dependent search passages. " +
            "For internal-versus-public comparisons, query the relevant internal source and Web IQ. " +
            "Do not send internal document text to Web IQ.",
        ["answerInstructions"] = "Answer concisely and cite every source. Say which facts come from internal documents and which " +
            "come from the web. If the sources do not contain the answer, say you do not know.",
        ["outputMode"] = "answerSynthesis",
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = settings.BlobSourceName },
            new JsonObject { ["name"] = settings.SharePointSourceName }, new JsonObject { ["name"] = settings.WebIqSourceName }),
        ["models"] = new JsonArray(new JsonObject
        {
            ["kind"] = "azureOpenAI",
            ["azureOpenAIParameters"] = new JsonObject
            {
                ["resourceUri"] = settings.ModelEndpoint,
                ["deploymentId"] = settings.Deployment,
                ["modelName"] = settings.Model
            }
        }),
        ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "medium" }
    };

    public static JsonObject RetrievePayload(KnowledgeBaseSettings settings, string question, string outputMode, string selection)
    {
        KnowledgeBaseResults.ValidateQuery(question, outputMode);
        if (selection is not ("internal" or "web" or "sharepoint" or "mixed"))
            throw new ArgumentException("Use internal, web, sharepoint or mixed.");
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = question })
            }),
            ["includeActivity"] = true,
            ["maxRuntimeInSeconds"] = selection == "mixed" ? 90 : 60,
            ["knowledgeSourceParams"] = new JsonArray(
                new JsonObject
                {
                    ["knowledgeSourceName"] = settings.BlobSourceName,
                    ["kind"] = "searchIndex",
                    ["includeReferences"] = true,
                    ["includeReferenceSourceData"] = true
                },
                new JsonObject
                {
                    ["knowledgeSourceName"] = settings.SharePointSourceName,
                    ["kind"] = "searchIndex",
                    ["includeReferences"] = true,
                    ["includeReferenceSourceData"] = true
                },
                new JsonObject
                {
                    ["knowledgeSourceName"] = settings.WebIqSourceName,
                    ["kind"] = "mcpServer",
                    ["includeReferences"] = true,
                    ["includeReferenceSourceData"] = true
                })
        };
        // Python uses the stored answerSynthesis default. Override only when requested.
        if (outputMode != "answerSynthesis") payload["outputMode"] = outputMode;
        return payload;
    }

    public async Task VerifyIndexesAsync()
    {
        foreach (var (indexName, lab, semanticName, contentField, required) in new[]
        {
            (settings.BlobIndexName, "05", "chunk-semantic", "chunk",
                new[] { "chunk_id", "chunk", "title", "source/file_name", "source/url", "page_from", "page_to" }),
            (settings.SharePointIndexName, "07", "semantic-config", "content",
                new[] { "id", "content", "sourceFile", "webUrl", "page", "pageTo" })
        })
        {
            JsonObject index = await transport.SendAsync(settings, HttpMethod.Get, $"indexes/{indexName}");
            var fields = new Dictionary<string, JsonNode>();
            foreach (JsonObject field in (index["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (field["name"] is not JsonValue name) continue;
                fields[name.ToString()] = field;
                foreach (JsonObject child in (field["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (child["name"] is JsonValue childName) fields[$"{name}/{childName}"] = child;
            }
            if (required.Any(name => !fields.TryGetValue(name, out var field) || field["retrievable"]?.ToString() == "false") ||
                fields[contentField]["searchable"]?.ToString() != "true")
                throw new InvalidOperationException($"{indexName} does not have the expected retrievable content/source fields. See notebook {lab}.");
            if (!(index["semantic"]?["configurations"] as JsonArray ?? new JsonArray())
                .Any(config => config?["name"]?.ToString() == semanticName))
                throw new InvalidOperationException($"{indexName} is missing semantic configuration {semanticName}. See notebook {lab}.");
            bool sharePoint = indexName == settings.SharePointIndexName;
            if (sharePoint && (index["permissionFilterOption"]?.ToString() != "enabled" ||
                fields.GetValueOrDefault("UserIds")?["permissionFilter"]?.ToString() != "userIds" ||
                fields.GetValueOrDefault("GroupIds")?["permissionFilter"]?.ToString() != "groupIds"))
                throw new InvalidOperationException("The SharePoint index must retain notebook 07 user/group permission filtering.");
            JsonObject countResult = await transport.SendAsync(settings, HttpMethod.Post, $"indexes/{indexName}/docs/search",
                new JsonObject { ["search"] = "*", ["count"] = true, ["top"] = 0 }, userContext: sharePoint);
            if (countResult["@odata.count"] is not JsonValue countValue ||
                !countValue.TryGetValue<long>(out long count) || count <= 0)
                throw new InvalidOperationException($"No chunks are visible in {indexName}. Check ingestion and document access in notebook {lab}.");
            Console.WriteLine($"{indexName}: {count} visible chunks; existing schema unchanged.");
        }
    }

    public async Task ExecuteAsync(string command, string? question = null,
        string outputMode = "answerSynthesis", bool confirm = false, bool diagnostics = false)
    {
        if (command == "help") { Console.WriteLine(KnowledgeBaseOptions.Help); return; }
        if (command is not ("verify-indexes" or "blob-source" or "sharepoint-source" or "webiq-source" or "knowledge-base" or
            "retrieve-internal" or "retrieve-web" or "retrieve-sharepoint" or "retrieve-mixed" or "run" or "cleanup"))
            throw new ArgumentException("Unknown command. Use help.");
        if (command == "cleanup" && !confirm) throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        if (command.StartsWith("retrieve-") || command == "run")
            KnowledgeBaseResults.ValidateQuery(question ?? "default question", outputMode);
        settings.Validate();
        switch (command)
        {
            case "verify-indexes":
                await VerifyIndexesAsync();
                break;
            case "blob-source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.BlobSourceName}", BlobSourcePayload(settings));
                Console.WriteLine($"Blob source ready: {settings.BlobSourceName} -> {settings.BlobIndexName}");
                break;
            case "sharepoint-source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.SharePointSourceName}", SharePointSourcePayload(settings));
                Console.WriteLine($"SharePoint source ready: {settings.SharePointSourceName} -> {settings.SharePointIndexName}");
                break;
            case "webiq-source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.WebIqSourceName}", WebIqSourcePayload(settings), timeoutSeconds: 120);
                Console.WriteLine($"Web IQ source ready: {settings.WebIqSourceName} | storedHeaders | tool: web");
                break;
            case "knowledge-base":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgebases/{settings.BaseName}", BasePayload(settings));
                Console.WriteLine("Knowledge base ready: " + settings.BaseName);
                break;
            case "retrieve-internal":
            case "retrieve-web":
            case "retrieve-sharepoint":
            case "retrieve-mixed":
                string selection = command["retrieve-".Length..];
                await transport.SendAsync(settings, HttpMethod.Post, $"knowledgebases/{settings.BaseName}/retrieve",
                    RetrievePayload(settings, question ?? Question(settings, selection), outputMode, selection),
                    userContext: true, diagnostics: diagnostics, timeoutSeconds: selection == "mixed" ? 150 : 120);
                break;
            case "run":
                await ExecuteAsync("verify-indexes");
                await ExecuteAsync("blob-source");
                await ExecuteAsync("sharepoint-source");
                await ExecuteAsync("webiq-source");
                await ExecuteAsync("knowledge-base");
                foreach (string source in new[] { "internal", "web", "sharepoint", "mixed" })
                    await ExecuteAsync("retrieve-" + source, source == "mixed" ? question : null, outputMode, diagnostics: diagnostics);
                break;
            case "cleanup":
                foreach (var (collection, name) in new[]
                {
                    ("knowledgebases", settings.BaseName), ("knowledgesources", settings.WebIqSourceName),
                    ("knowledgesources", settings.SharePointSourceName), ("knowledgesources", settings.BlobSourceName)
                })
                {
                    await transport.SendAsync(settings, HttpMethod.Delete, $"{collection}/{name}", allowMissing: true);
                    Console.WriteLine("Deleted or already absent: " + name);
                }
                break;
        }
    }
}
