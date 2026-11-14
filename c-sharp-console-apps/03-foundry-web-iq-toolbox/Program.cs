public static class FoundryToolboxProgram
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
            Console.WriteLine("Lab could not complete: " + error.Message);
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, HttpClient client, Func<string, Task<string>>? getToken = null)
    {
        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 03 - Web IQ through a Foundry toolbox (.NET 10)
                Run from the repository root; .env then .env.azure override environment values.
                dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- run
                Append an optional public question. No WEBIQ_API_KEY is read or required.
                Requires lab 02's existing connection, Foundry Owner on the parent resource,
                a chat model, toolbox preview access, WORKSHOP_PREFIX and Azure CLI tenant sign-in.
                Publishes stable toolbox/agent names, promotes the new default, discovers tools,
                retargets the stable Entra connection, pins the agent version and verifies retrieval.
                No interactive approvals or automatic follow-up. Resources are retained.
                Model/Web IQ calls incur charges. Use only public, non-sensitive questions.
                """);
            return 0;
        }
        if (command != "run")
        {
            Console.WriteLine("Use run [public question] or --help.");
            return 2;
        }
        FoundrySettings settings = FoundrySettings.Load(".", requireApiKey: false);
        var lab = new ToolboxWorkshop(client, settings, getToken);
        var connection = await lab.GetWebIqConnectionAsync();
        var version = await lab.PublishToolboxAsync(connection);
        string url = lab.ToolboxUrl(version);
        await lab.DiscoverToolsAsync(url);
        await lab.ConfigureConnectionAsync(url, version);
        var agent = await lab.CreateAgentVersionAsync(url);
        var conversation = await lab.CreateConversationAsync();
        string question = args.Length > 1 ? string.Join(" ", args.Skip(1)) : ToolboxWorkshop.Question;
        var response = await lab.AskAsync(question, conversation, agent);
        lab.VerifyRetrieval(response);
        Console.WriteLine("Resources retained. Use this new agent version after retargeting the toolbox connection.");
        return 0;
    }
}
