using System.Text.Json.Nodes;

public sealed record WebIqOptions(string Command, string? Question = null,
    string OutputMode = "answerSynthesis", bool Confirm = false)
{
    public const string Help = """
        source, knowledge-base, retrieve [query] [--output-mode answerSynthesis|extractiveData],
        run [query] [--output-mode answerSynthesis|extractiveData], connectivity, diagnostics,
        cleanup --confirm, help / --help / -h / no arguments
        Run from the repository root. Each command performs only its named step.
        Source stores the Web IQ key; retrieval invokes billable Web IQ and model operations.
        Run creates source, creates knowledge base and retrieves once; never probes or cleans up.
        Connectivity is optional and unauthenticated: HTTP 401 is expected, not proof of permissions.
        Cleanup requires exactly: cleanup --confirm.
        """;

    public static WebIqOptions Parse(string[] args)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h") return new("help");
        if (command == "cleanup")
        {
            if (!args.SequenceEqual(new[] { "cleanup", "--confirm" }))
                throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
            return new(command, Confirm: true);
        }
        if (command is "source" or "knowledge-base" or "connectivity" or "diagnostics")
        {
            if (args.Length != 1) throw new ArgumentException("This step takes no arguments. Use help.");
            return new(command);
        }
        if (command is not ("retrieve" or "run")) throw new ArgumentException("Unknown command. Use help.");
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
            else if (!args[i].StartsWith("--") && question is null) question = args[i];
            else throw new ArgumentException("Use one quoted query and optional --output-mode. Use help.");
        }
        WebIqResults.ValidateQuery(question ?? WebIqWorkshop.Question, mode);
        return new(command, question, mode);
    }
}

public sealed class WebIqWorkshop(WebIqSettings settings, WebIqTransport transport)
{
    public static string Question => $"What is the weather forecast for Seattle, Washington, on {DateTime.UtcNow:yyyy-MM-dd}? Cite the forecast source and date.";

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
        ["description"] = "Standalone public web retrieval through basic Web IQ search only.",
        ["retrievalInstructions"] = $"Use {settings.SourceName} to retrieve public web evidence for the question. For every web call explicitly set contentFormat to passage. Use specific search terms, locations and dates from the question. For current software releases prefer the official release or downloads page. For weather, search for the requested location and forecast date.",
        ["answerInstructions"] = "Write a concise final answer from the retrieved passages and cite supporting reference IDs. Treat MCP web passages as evidence already retrieved for this request. If evidence does not establish the requested fact, identify the missing information. Distinguish forecast dates from page crawl dates and stable releases from prereleases. Do not infer the latest release from an incomplete list of older versions.",
        ["outputMode"] = "answerSynthesis",
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = settings.SourceName }),
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

    public static JsonObject RetrievePayload(WebIqSettings settings, string question, string outputMode)
    {
        WebIqResults.ValidateQuery(question, outputMode);
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
            ["knowledgeSourceParams"] = new JsonArray(new JsonObject
            {
                ["knowledgeSourceName"] = settings.SourceName,
                ["kind"] = "mcpServer",
                ["includeReferences"] = true,
                ["includeReferenceSourceData"] = true
            })
        };
    }

    public async Task ExecuteAsync(string command, string? question = null,
        string outputMode = "answerSynthesis", bool confirm = false)
    {
        if (command == "help") { Console.WriteLine(WebIqOptions.Help); return; }
        if (command == "cleanup" && !confirm)
            throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        if (command is "retrieve" or "run") WebIqResults.ValidateQuery(question ?? Question, outputMode);
        settings.Validate();
        switch (command)
        {
            case "source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.SourceName}", SourcePayload(settings));
                Console.WriteLine($"Web IQ source ready: {settings.SourceName} | authentication: storedHeaders | tool: web");
                break;
            case "knowledge-base":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgebases/{settings.BaseName}", BasePayload(settings));
                Console.WriteLine("Web IQ-only knowledge base ready: " + settings.BaseName);
                break;
            case "retrieve":
                Console.WriteLine(WebIqResults.Redact($"Question: {question ?? Question}\nOutput mode: {outputMode}", settings.WebIqKey));
                await transport.SendAsync(settings, HttpMethod.Post, $"knowledgebases/{settings.BaseName}/retrieve",
                    RetrievePayload(settings, question ?? Question, outputMode));
                break;
            case "run":
                await ExecuteAsync("source");
                await ExecuteAsync("knowledge-base");
                await ExecuteAsync("retrieve", question, outputMode);
                break;
            case "connectivity":
                await transport.ProbeAsync(settings);
                break;
            case "diagnostics":
                Console.WriteLine($"UTC: {DateTime.UtcNow:O}\n.NET: {Environment.Version}\nSearch: {settings.SearchEndpoint}");
                Console.WriteLine($"Web IQ source: {settings.SourceName}\nKnowledge base: {settings.BaseName}");
                Console.WriteLine("Proxy environment configured: " + new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }
                    .Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))));
                Console.WriteLine("Diagnostics are local; no token acquisition, Web IQ call or permission validation.");
                break;
            case "cleanup":
                foreach (var (collection, name) in new[]
                {
                    ("knowledgebases", settings.BaseName), ("knowledgesources", settings.SourceName)
                })
                {
                    await transport.SendAsync(settings, HttpMethod.Delete, $"{collection}/{name}", allowMissing: true);
                    Console.WriteLine("Deleted or already absent: " + name);
                }
                break;
            default:
                throw new ArgumentException("Unknown command. Use help.");
        }
    }
}
