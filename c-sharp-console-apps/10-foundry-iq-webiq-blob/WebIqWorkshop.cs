using System.Text.Json.Nodes;

public sealed record WebIqOptions(string Command, string? Question = null,
    string OutputMode = "answerSynthesis", bool Confirm = false)
{
    public const string Help = """
        verify-index, blob-source, webiq-source, knowledge-base,
        retrieve-blob|retrieve-web|retrieve-mixed [query] [--output-mode answerSynthesis|extractiveData],
        run [mixed query] [--output-mode answerSynthesis|extractiveData], cleanup --confirm,
        help / --help / -h / no arguments
        Run from the repository root; each command performs only its named step.
        Requires lab 05's populated <prefix>-cu-chunks index; never creates or changes indexing resources.
        Run checks index, creates exactly two sources and one KB, then compares Blob/web/mixed retrieval.
        Retrieval invokes billable model planning/vectorizer/Web IQ operations; run never cleans up.
        Cleanup requires exactly: cleanup --confirm; deletes only this KB then its sources, never the CU index.
        """;

    public static WebIqOptions Parse(string[] args)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            if (args.Length > 1) throw new ArgumentException("Help takes no arguments.");
            return new("help");
        }
        if (command == "cleanup")
        {
            if (!args.SequenceEqual(new[] { "cleanup", "--confirm" }))
                throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
            return new(command, Confirm: true);
        }
        if (command is "verify-index" or "blob-source" or "webiq-source" or "knowledge-base")
        {
            if (args.Length != 1) throw new ArgumentException("This step takes no arguments. Use help.");
            return new(command);
        }
        if (command is not ("retrieve-blob" or "retrieve-web" or "retrieve-mixed" or "run"))
            throw new ArgumentException("Unknown command. Use help.");
        string? question = null;
        string mode = "answerSynthesis";
        bool suppliedMode = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--output-mode" && !suppliedMode && i + 1 < args.Length)
            {
                mode = args[++i];
                suppliedMode = true;
            }
            else if (!args[i].StartsWith('-') && question is null) question = args[i];
            else throw new ArgumentException("Use one quoted query and optional --output-mode. Use help.");
        }
        WebIqResults.ValidateQuery(question ?? "default question", mode);
        return new(command, question, mode);
    }
}

public sealed class WebIqWorkshop(WebIqSettings settings, WebIqTransport transport)
{
    public static string Question(WebIqSettings settings, string selection) => selection switch
    {
        "blob" => $"Using {settings.BlobSourceName}, what receipts do I need for travel reimbursement?",
        "web" => "What are the current public IRS guidelines for business travel receipt records?",
        "mixed" => $"Use {settings.BlobSourceName} for our internal travel receipt policy and {settings.SourceName} for public IRS travel receipt guidelines. Compare the requirements; do not send internal text to the web.",
        _ => throw new ArgumentException("Use blob, web or mixed.")
    };

    public static JsonObject BlobSourcePayload(WebIqSettings settings) => new()
    {
        ["name"] = settings.BlobSourceName,
        ["kind"] = "searchIndex",
        ["description"] = "Travel reimbursement and onboarding policy documents from the existing Blob index.",
        ["searchIndexParameters"] = new JsonObject
        {
            ["searchIndexName"] = settings.IndexName,
            ["semanticConfigurationName"] = "chunk-semantic",
            ["searchFields"] = new JsonArray(new JsonObject { ["name"] = "chunk" }),
            ["sourceDataFields"] = new JsonArray(
                new JsonObject { ["name"] = "chunk_id" }, new JsonObject { ["name"] = "chunk" },
                new JsonObject { ["name"] = "title" }, new JsonObject { ["name"] = "source/file_name" },
                new JsonObject { ["name"] = "source/url" })
        }
    };

    public static JsonObject SourcePayload(WebIqSettings settings) => new()
    {
        ["name"] = settings.SourceName,
        ["kind"] = "mcpServer",
        ["description"] = "Basic Web IQ web search for public information. For every web call, explicitly set contentFormat to passage; use query-dependent passages, not text, html or markdown.",
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
                ["maxOutputTokens"] = 4000
            })
        }
    };

    public static JsonObject BasePayload(WebIqSettings settings) => new()
    {
        ["name"] = settings.BaseName,
        ["description"] = "Existing Blob policies plus live public web evidence from Web IQ.",
        ["retrievalInstructions"] = $"Use {settings.BlobSourceName} for internal travel reimbursement and onboarding policies. Use {settings.SourceName} for current public information and external facts. For comparisons use both sources. Never send internal document text to Web IQ. For every web call explicitly set contentFormat to passage. Use specific public search terms, locations and dates from the question.",
        ["answerInstructions"] = "Answer concisely and cite supporting reference IDs. Distinguish internal policy facts from public web facts. If the evidence is missing, say you do not know.",
        ["outputMode"] = "answerSynthesis",
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = settings.BlobSourceName },
            new JsonObject { ["name"] = settings.SourceName }),
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
        ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "low" }
    };

    public static JsonObject RetrievePayload(WebIqSettings settings, string question, string outputMode, string selection)
    {
        WebIqResults.ValidateQuery(question, outputMode);
        if (selection is not ("blob" or "web" or "mixed")) throw new ArgumentException("Use blob, web or mixed.");
        var sources = new JsonArray();
        if (selection is "blob" or "mixed")
            sources.Add(new JsonObject
            {
                ["knowledgeSourceName"] = settings.BlobSourceName,
                ["kind"] = "searchIndex",
                ["includeReferences"] = true,
                ["includeReferenceSourceData"] = true
            });
        if (selection is "web" or "mixed")
            sources.Add(new JsonObject
            {
                ["knowledgeSourceName"] = settings.SourceName,
                ["kind"] = "mcpServer",
                ["includeReferences"] = true,
                ["includeReferenceSourceData"] = true
            });
        return new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = question })
            }),
            ["outputMode"] = outputMode,
            ["includeActivity"] = true,
            ["maxRuntimeInSeconds"] = 60,
            ["knowledgeSourceParams"] = sources
        };
    }

    public async Task VerifyIndexAsync()
    {
        JsonObject index = await transport.SendAsync(settings, HttpMethod.Get, $"indexes/{settings.IndexName}");
        var fields = new Dictionary<string, JsonNode>();
        foreach (JsonNode? field in index["fields"] as JsonArray ?? new JsonArray())
        {
            if (field?["name"] is not JsonValue name) continue;
            fields[name.ToString()] = field;
            foreach (JsonNode? child in field["fields"] as JsonArray ?? new JsonArray())
                if (child?["name"] is JsonValue childName) fields[$"{name}/{childName}"] = child;
        }
        string[] required = ["chunk_id", "chunk", "title", "source/file_name", "source/url"];
        if (required.Any(name => !fields.TryGetValue(name, out var field) ||
            field["retrievable"]?.ToString() == "false") ||
            fields["chunk"]["searchable"]?.ToString() != "true")
            throw new InvalidOperationException($"{settings.IndexName} does not have the expected retrievable content/source fields. Complete lab 05.");
        if (!(index["semantic"]?["configurations"] as JsonArray ?? new JsonArray())
            .Any(config => config?["name"]?.ToString() == "chunk-semantic"))
            throw new InvalidOperationException($"{settings.IndexName} is missing semantic configuration chunk-semantic. Complete lab 05.");
        JsonObject countResult = await transport.SendAsync(settings, HttpMethod.Post, $"indexes/{settings.IndexName}/docs/search",
            new JsonObject { ["search"] = "*", ["count"] = true, ["top"] = 0 });
        if (countResult["@odata.count"] is not JsonValue countValue ||
            !countValue.TryGetValue<long>(out long count) || count <= 0)
            throw new InvalidOperationException($"No chunks are visible in {settings.IndexName}. Check ingestion and document access in lab 05.");
        Console.WriteLine($"{settings.IndexName}: {count} visible chunks; existing schema unchanged.");
    }

    public async Task ExecuteAsync(string command, string? question = null,
        string outputMode = "answerSynthesis", bool confirm = false)
    {
        if (command == "help") { Console.WriteLine(WebIqOptions.Help); return; }
        if (command is not ("verify-index" or "blob-source" or "webiq-source" or "knowledge-base" or
            "retrieve-blob" or "retrieve-web" or "retrieve-mixed" or "run" or "cleanup"))
            throw new ArgumentException("Unknown command. Use help.");
        if (command == "cleanup" && !confirm)
            throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        if (command.StartsWith("retrieve-") || command == "run")
            WebIqResults.ValidateQuery(question ?? "default question", outputMode);
        settings.Validate();
        switch (command)
        {
            case "verify-index":
                await VerifyIndexAsync();
                break;
            case "blob-source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.BlobSourceName}", BlobSourcePayload(settings));
                Console.WriteLine($"Blob knowledge source ready: {settings.BlobSourceName} -> {settings.IndexName}");
                break;
            case "webiq-source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.SourceName}", SourcePayload(settings));
                Console.WriteLine($"Web IQ source ready: {settings.SourceName} | authentication: storedHeaders | tool: web");
                break;
            case "knowledge-base":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgebases/{settings.BaseName}", BasePayload(settings));
                Console.WriteLine("Web IQ + Blob knowledge base ready: " + settings.BaseName);
                break;
            case "retrieve-blob":
            case "retrieve-web":
            case "retrieve-mixed":
                string selection = command["retrieve-".Length..];
                await transport.SendAsync(settings, HttpMethod.Post, $"knowledgebases/{settings.BaseName}/retrieve",
                    RetrievePayload(settings, question ?? Question(settings, selection), outputMode, selection));
                break;
            case "run":
                await ExecuteAsync("verify-index");
                await ExecuteAsync("blob-source");
                await ExecuteAsync("webiq-source");
                await ExecuteAsync("knowledge-base");
                await ExecuteAsync("retrieve-blob", outputMode: outputMode);
                await ExecuteAsync("retrieve-web", outputMode: outputMode);
                await ExecuteAsync("retrieve-mixed", question, outputMode);
                break;
            case "cleanup":
                foreach (var (collection, name) in new[]
                {
                    ("knowledgebases", settings.BaseName), ("knowledgesources", settings.SourceName),
                    ("knowledgesources", settings.BlobSourceName)
                })
                {
                    await transport.SendAsync(settings, HttpMethod.Delete, $"{collection}/{name}", allowMissing: true);
                    Console.WriteLine("Deleted or already absent: " + name);
                }
                break;
        }
    }
}
