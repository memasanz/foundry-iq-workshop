using System.Text.Json.Nodes;

public static class FoundryWebIqProgram
{
    public static async Task<int> Main(string[] args)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        try
        {
            return await RunAsync(args, client);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"Lab could not complete: {error.Message}");
            return 1;
        }
    }

    public static async Task<int> RunAsync(
        string[] args, HttpClient client, Func<string, Task<string>>? getToken = null)
    {
        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 02 - A Foundry agent grounded with Web IQ (.NET 10)
                Run from the repository root. Settings load from .env, then .env.azure.

                dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- discover
                dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- run
                Append an optional public question after run.

                discover: lists MCP tools using WEBIQ_API_KEY; no Azure sign-in or resource writes.
                run: requires the Foundry settings, WORKSHOP_PREFIX and Azure CLI sign-in.
                It updates the connection, creates an agent version and conversation, asks the
                question, verifies MCP retrieval and always follows up after a completed response.
                Resources are retained. Model and Web IQ calls incur charges; use public queries.
                """);
            return 0;
        }
        if (command is not ("discover" or "run") || (command == "discover" && args.Length != 1))
        {
            Console.WriteLine("Use discover or run [public question]. Run --help for setup guidance.");
            return 2;
        }

        FoundrySettings settings = FoundrySettings.Load(".", requireAzure: command == "run");
        var lab = new FoundryWorkshop(client, settings, getToken);
        JsonArray? tools = await lab.DiscoverToolsAsync();
        if (tools == null)
            return 1;
        if (command == "discover")
            return 0;
        if (!await lab.ConfigureConnectionAsync())
            return 1;

        JsonObject? agent = await lab.CreateAgentVersionAsync(FoundryWorkshop.CreateDefinition(settings.Model));
        if (agent == null)
            return 1;
        JsonObject? conversation = await lab.CreateConversationAsync();
        if (conversation == null)
            return 1;
        string question = args.Length > 1 ? string.Join(" ", args.Skip(1)) : FoundryWorkshop.Question;
        JsonObject? response = await lab.AskAsync(question, conversation, agent);
        bool grounded = lab.VerifyRetrieval(response);
        if (response == null)
        {
            Console.WriteLine("No initial agent response. Resolve the request error before asking a follow-up.");
            return 1;
        }
        JsonObject? followUp = await lab.AskAsync(FoundryWorkshop.FollowUpQuestion, conversation, agent);
        Console.WriteLine("Resources retained. Reuse the same WORKSHOP_PREFIX for lab 03.");
        return grounded && followUp != null ? 0 : 1;
    }
}
