using System.Text.Json.Nodes;

public sealed record ApimOptions(string Command, string? Question = null, string OutputMode = "answerSynthesis",
    bool Confirm = false, bool Diagnostics = false)
{
    public const string Help = """
        inspect-token, check-webiq-source, knowledge-base, check-rejections,
        retrieve-internal|retrieve-web|retrieve-mixed [question]
          [--output-mode answerSynthesis|extractiveData] [--diagnostics],
        run [mixed question] [--output-mode answerSynthesis|extractiveData] [--diagnostics],
        cleanup --confirm, help / --help / -h / no arguments
        Run from the repository root; .env then .env.azure override process settings.
        Reuses lab11 <prefix>-kb-blob, <prefix>-kb-sharepoint and <prefix>-kb model/routing.
        Shared WEBIQ_APIM_SOURCE_NAME defaults to iq-workshop-webiq and is READ ONLY.
        Setup reads Search directly and writes only <prefix>-apim-kb; retrieval uses APIM /iq with a broker token.
        Run inspects token, checks shared source, PUTs own KB, retrieves three questions and checks 401 rejection probes.
        Run never configures APIM/apps/roles or cleans up. Retrieval can incur Search/model/Web IQ costs.
        --diagnostics prints redacted raw activity/references from the same response, no extra request/cache.
        Diagnostics may contain internal passages/private URLs; do not share output.
        Cleanup deletes ONLY <prefix>-apim-kb; retains sources, lab11 KB, indexes, gateway and broker.
        """;

    public static ApimOptions Parse(string[] args)
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
        if (command is "inspect-token" or "check-webiq-source" or "knowledge-base" or "check-rejections")
        {
            if (args.Length != 1) throw new ArgumentException("This step takes no arguments. Use help.");
            return new(command);
        }
        if (command is not ("retrieve-internal" or "retrieve-web" or "retrieve-mixed" or "run"))
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
        ApimResults.ValidateQuery(question ?? "default question", mode);
        return new(command, question, mode, Diagnostics: diagnostics);
    }
}

public sealed class ApimWorkshop(ApimSettings settings, ApimTransport transport)
{
    public static string Question(string selection) => selection switch
    {
        "internal" => "What is our policy for booking international travel?",
        "web" => "What is the latest stable release of Python, and when was it released?",
        "mixed" => "Does our travel policy meet current public guidance on business travel? Compare them.",
        _ => throw new ArgumentException("Use internal, web or mixed.")
    };

    public async Task CheckWebIqSourceAsync()
    {
        var source = await transport.SendAsync(HttpMethod.Get, $"knowledgesources/{settings.WebIqSourceName}");
        if (source["kind"]?.ToString() != "mcpServer" ||
            source["mcpServerParameters"]?["serverURL"]?.ToString() != "https://api.microsoft.ai/v3/mcp")
            throw new InvalidOperationException("Shared source is not the expected Web IQ MCP source. Check WEBIQ_APIM_SOURCE_NAME and gateway preparation.");
        Console.WriteLine("Shared Web IQ source found (read-only): " + ApimResults.Redact(settings.WebIqSourceName));
    }

    public async Task<Dictionary<string, string>> ReadSourceKindsAsync()
    {
        var kinds = new Dictionary<string, string>();
        foreach (string name in new[] { settings.BlobSourceName, settings.SharePointSourceName })
        {
            var source = await transport.SendAsync(HttpMethod.Get, $"knowledgesources/{name}");
            kinds[name] = source["kind"]?.GetValue<string>() is { Length: > 0 } kind ? kind
                : throw new InvalidOperationException("Existing source has no kind. Complete lab11 first.");
        }
        kinds[settings.WebIqSourceName] = "mcpServer";
        return kinds;
    }

    public static JsonObject BasePayload(ApimSettings settings, JsonObject baseKb, Dictionary<string, string> kinds)
    {
        if (baseKb["models"] is not JsonArray models || models.Count == 0 ||
            baseKb["retrievalInstructions"] is not JsonValue instructions || !instructions.TryGetValue<string>(out string? routing))
            throw new InvalidOperationException("Lab11 knowledge base needs its model and routing instructions. Complete lab11 first.");
        return new JsonObject
        {
            ["name"] = settings.ApimBaseName,
            ["description"] = "Workshop policies (Blob and SharePoint) plus Web IQ, reached through APIM.",
            ["retrievalInstructions"] = routing.Replace(settings.Prefix + "-kb-webiq", settings.WebIqSourceName, StringComparison.Ordinal),
            ["answerInstructions"] = (baseKb["answerInstructions"]?.ToString() ?? "") + " Answer from the retrieved evidence, not from memory. " +
                "Do not claim external access is unavailable when web results were retrieved. " +
                "If evidence is insufficient, explain what it does not establish; do not offer to fetch sources later " +
                "or ask permission to search, because retrieval has already run. " +
                "Do not infer the latest release from an incomplete list of older versions.",
            ["outputMode"] = "answerSynthesis",
            ["knowledgeSources"] = new JsonArray(kinds.Keys.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray()),
            ["models"] = models.DeepClone(),
            ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "medium" }
        };
    }

    public static JsonObject RetrievePayload(ApimSettings settings, Dictionary<string, string> kinds,
        string question, string outputMode, string selection)
    {
        ApimResults.ValidateQuery(question, outputMode);
        if (selection is not ("internal" or "web" or "mixed")) throw new ArgumentException("Use internal, web or mixed.");
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
                    ["kind"] = kinds[settings.BlobSourceName],
                    ["includeReferences"] = true,
                    ["includeReferenceSourceData"] = true
                },
                new JsonObject
                {
                    ["knowledgeSourceName"] = settings.SharePointSourceName,
                    ["kind"] = kinds[settings.SharePointSourceName],
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
        // Default is stored on the KB, as in Python12; override only for an explicitly selected mode.
        if (outputMode != "answerSynthesis") payload["outputMode"] = outputMode;
        return payload;
    }

    public async Task ExecuteAsync(string command, string? question = null, string outputMode = "answerSynthesis",
        bool confirm = false, bool diagnostics = false)
    {
        if (command == "help") { Console.WriteLine(ApimOptions.Help); return; }
        if (command is not ("inspect-token" or "check-webiq-source" or "knowledge-base" or "check-rejections" or
            "retrieve-internal" or "retrieve-web" or "retrieve-mixed" or "run" or "cleanup"))
            throw new ArgumentException("Unknown command. Use help.");
        if (command == "cleanup" && !confirm) throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        if (command.StartsWith("retrieve-", StringComparison.Ordinal) || command == "run")
            ApimResults.ValidateQuery(question ?? "default question", outputMode);
        settings.Validate();
        switch (command)
        {
            case "inspect-token":
                await transport.InspectTokenAsync();
                break;
            case "check-webiq-source":
                await CheckWebIqSourceAsync();
                break;
            case "knowledge-base":
                await CheckWebIqSourceAsync();
                var kinds = await ReadSourceKindsAsync();
                var baseKb = await transport.SendAsync(HttpMethod.Get, $"knowledgebases/{settings.BaseName}");
                await transport.SendAsync(HttpMethod.Put, $"knowledgebases/{settings.ApimBaseName}", BasePayload(settings, baseKb, kinds));
                Console.WriteLine("Knowledge base ready: " + ApimResults.Redact(settings.ApimBaseName));
                break;
            case "retrieve-internal":
            case "retrieve-web":
            case "retrieve-mixed":
                string selection = command["retrieve-".Length..];
                var sourceKinds = await ReadSourceKindsAsync();
                await transport.SendAsync(HttpMethod.Post, $"knowledgebases/{settings.ApimBaseName}/retrieve",
                    RetrievePayload(settings, sourceKinds, question ?? Question(selection), outputMode, selection),
                    gateway: true, diagnostics: diagnostics, timeoutSeconds: selection == "mixed" ? 150 : 120);
                break;
            case "check-rejections":
                await transport.CheckRejectionsAsync();
                break;
            case "run":
                await ExecuteAsync("inspect-token");
                await ExecuteAsync("knowledge-base");
                foreach (string selectionName in new[] { "internal", "web", "mixed" })
                    await ExecuteAsync("retrieve-" + selectionName, selectionName == "mixed" ? question : null,
                        outputMode, diagnostics: diagnostics);
                await ExecuteAsync("check-rejections");
                break;
            case "cleanup":
                await transport.SendAsync(HttpMethod.Delete, $"knowledgebases/{settings.ApimBaseName}", allowMissing: true);
                Console.WriteLine("Deleted or already absent: " + ApimResults.Redact(settings.ApimBaseName));
                break;
        }
    }
}
