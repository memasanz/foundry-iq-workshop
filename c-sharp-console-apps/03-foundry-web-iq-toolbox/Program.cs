using System.Text.Json.Nodes;

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
            or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
            or IOException or UnauthorizedAccessException)
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
                Commands (each step performs only its named action and necessary reads):
                webiq-connection: read/check the original lab 02 connection metadata.
                toolbox: publish/promote a toolbox version, using the existing lab 02 connection.
                discover: inspect the EXISTING toolbox default and select it after MCP discovery.
                toolbox-connection: create/update the Entra connection for the selected version.
                agent: publish the stable agent version; requires matching existing toolbox/connection.
                ask [public question]: validate pinned references, create a conversation, ask/verify once.
                run [public question]: existing complete flow, independent of local step references.
                References persist in ignored .workshop\lab03-<prefix>.json, without secrets or outputs.
                No WEBIQ_API_KEY is read or required. Help reads no configuration or state.
                Requires lab 02's existing connection, Foundry Owner on the parent resource,
                a chat model, toolbox preview access, WORKSHOP_PREFIX and Azure CLI tenant sign-in.
                Publishes stable toolbox/agent names, promotes the new default, discovers tools,
                retargets the stable Entra connection, pins the agent version and verifies retrieval.
                No interactive approvals or automatic follow-up. Resources are retained.
                Model/Web IQ calls incur charges. Use only public, non-sensitive questions.
                """);
            return 0;
        }
        if (command is not ("webiq-connection" or "toolbox" or "discover" or "toolbox-connection" or "agent" or "ask" or "run")
            || (command is not ("ask" or "run") && args.Length != 1))
        {
            Console.WriteLine("Use webiq-connection, toolbox, discover, toolbox-connection, agent, ask or run. See --help.");
            return 2;
        }
        FoundrySettings settings = FoundrySettings.Load(".", requireApiKey: false);
        var lab = new ToolboxWorkshop(client, settings, getToken);
        if (command != "run")
            return await RunStepAsync(command, args, settings, lab);
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

    private static async Task<int> RunStepAsync(string command, string[] args, FoundrySettings settings, ToolboxWorkshop lab)
    {
        if (command == "webiq-connection")
        {
            await lab.GetWebIqConnectionAsync();
            return 0;
        }
        WorkshopState state = WorkshopState.Load(settings, "lab03");
        if (command == "toolbox")
        {
            var connection = await lab.GetWebIqConnectionAsync();
            var version = await lab.PublishToolboxAsync(connection);
            state.ToolboxVersion = (string?)version["version"];
            state.ClearAgent();
            state.Save();
            return 0;
        }
        if (command == "discover")
        {
            var version = await lab.GetDefaultToolboxVersionAsync();
            await lab.ValidateToolboxVersionAsync(version);
            await lab.DiscoverToolsAsync(lab.ToolboxUrl(version));
            if (state.ToolboxVersion != (string?)version["version"]) state.ClearAgent();
            state.ToolboxVersion = (string?)version["version"];
            state.Save();
            return 0;
        }
        var selected = state.ToolboxReference();
        string url = lab.ToolboxUrl(selected);
        JsonObject? agent = command == "ask" ? state.AgentReference() : null;
        if (command == "ask")
        {
            // Clear any prior success even when validation of the newly requested question fails.
            state.ClearConversation();
            state.Save();
        }
        await lab.ValidateToolboxVersionAsync(selected);
        if (command == "toolbox-connection")
        {
            await lab.ConfigureConnectionAsync(url, selected);
            state.ClearAgent();
            state.Save();
            return 0;
        }
        await lab.ValidateToolboxConnectionAsync(url, selected);
        if (command == "agent")
        {
            state.SetAgent(await lab.CreateAgentVersionAsync(url));
            state.Save();
            return 0;
        }
        await lab.ValidateAgentVersionAsync(agent!, url);
        var conversation = await lab.CreateConversationAsync();
        string question = args.Length > 1 ? string.Join(" ", args.Skip(1)) : ToolboxWorkshop.Question;
        var response = await lab.AskAsync(question, conversation, agent!);
        lab.VerifyRetrieval(response);
        state.ConversationId = (string?)conversation["id"];
        state.InitialCompleted = true;
        state.Save();
        return 0;
    }
}
