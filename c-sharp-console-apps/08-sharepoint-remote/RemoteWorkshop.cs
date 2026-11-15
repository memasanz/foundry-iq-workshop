using System.Text.Json.Nodes;

public sealed class RemoteWorkshop(RemoteSettings settings, RemoteTransport transport)
{
    public static readonly string[] Commands = ["source", "knowledge-base", "retrieve", "run", "cleanup", "help"];
    public const string Question = "travel reimbursement receipts";

    public static JsonObject SourcePayload(RemoteSettings settings) => new()
    {
        ["name"] = settings.SourceName,
        ["kind"] = "remoteSharePoint",
        ["description"] = "Workshop SharePoint retrieval without copying content into Search.",
        ["remoteSharePointParameters"] = new JsonObject
        {
            ["filterExpression"] = $"Path:\"{settings.SiteUrl}\"",
            ["resourceMetadata"] = new JsonArray("Author", "Title")
        }
    };

    public static JsonObject BasePayload(RemoteSettings settings) => new()
    {
        ["name"] = settings.BaseName,
        ["description"] = "Retrieve SharePoint passages without LLM planning or synthesis.",
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = settings.SourceName }),
        ["models"] = new JsonArray(),
        ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "minimal" },
        ["outputMode"] = "extractiveData"
    };

    public static JsonObject RetrievePayload(RemoteSettings settings, string question)
    {
        ValidateQuestion(question);
        return new JsonObject
        {
            ["intents"] = new JsonArray(new JsonObject { ["type"] = "semantic", ["search"] = question }),
            ["knowledgeSourceParams"] = new JsonArray(new JsonObject
            {
                ["knowledgeSourceName"] = settings.SourceName,
                ["kind"] = "remoteSharePoint",
                ["includeReferences"] = true,
                ["includeReferenceSourceData"] = true
            })
        };
    }

    public static void ValidateQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.EnumerateRunes().Count() > 1500)
            throw new ArgumentException("Use a non-empty question of at most 1,500 characters.");
    }

    public async Task ExecuteAsync(string command, string? question = null, bool confirm = false)
    {
        if (command == "help")
        {
            Console.WriteLine(string.Join(", ", Commands));
            return;
        }
        if (!Commands.Contains(command)) throw new ArgumentException("Unknown command. Use help.");
        if (command == "cleanup" && !confirm) throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        settings.Validate();
        if (command is "retrieve" or "run") ValidateQuestion(question ?? Question);
        switch (command)
        {
            case "source":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgesources/{settings.SourceName}", SourcePayload(settings));
                Console.WriteLine("Remote knowledge source ready: " + settings.SourceName);
                break;
            case "knowledge-base":
                await transport.SendAsync(settings, HttpMethod.Put, $"knowledgebases/{settings.BaseName}", BasePayload(settings));
                Console.WriteLine("Extractive knowledge base ready: " + settings.BaseName);
                break;
            case "retrieve":
                await transport.SendAsync(settings, HttpMethod.Post, $"knowledgebases/{settings.BaseName}/retrieve",
                    RetrievePayload(settings, question ?? Question), queryUser: true);
                break;
            case "run":
                await ExecuteAsync("source");
                await ExecuteAsync("knowledge-base");
                await ExecuteAsync("retrieve", question);
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
        }
    }
}
