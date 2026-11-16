public static class IqToolboxAgentProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            return await RunAsync(args, Directory.GetCurrentDirectory(), client);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Lab could not complete: " + error.Message);
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, string root, HttpClient client,
        Func<string, Task<string>>? getToken = null, Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, Task>? delay = null)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 14 - A C# hosted agent using Foundry IQ through a toolbox (.NET 10).
                Run from the repository root; .env then .env.azure override environment values.
                Commands (only the named action plus necessary existing-resource reads):
                  verify-sources      Check lab07 SharePoint ACL index and lab11 source/model settings.
                  knowledge-base      Create/update <prefix>-toolbox-kb; SharePoint-only, low reasoning.
                  connection          Create/update the ProjectManagedIdentity Search connection.
                  toolbox             Publish one MCP toolbox version and promote its default.
                  check-toolbox       Read/check existing connection and toolbox default; no tool execution.
                  write-agent         Write real C# agent source locally; overwrites generated files.
                  package             Zip the existing two source files only; preserves edits.
                  deploy              Check existing toolbox, upload existing zip, create one hosted version.
                  status [version]    Read specified or latest existing agent version.
                  wait [version]      Poll fixed version every 15 seconds for at most 15 minutes.
                  grant-search        Assign Search Index Data Reader to the PROJECT identity (service access).
                  invoke [question] [--diagnostics] Ask once as the signed-in USER; SharePoint ACLs trim evidence.
                  invoke-unknown [--diagnostics] Ask the negative-control question once.
                  run [--diagnostics] Complete setup, deploy, wait, project grant and both example questions.
                  cleanup --confirm   Delete only this lab's agent, toolbox, connection and KB.
                Help reads no settings/state, acquires no credential and makes no request.
                No Python, Docker, azd, Web IQ key, new ingestion, app registration or APIM broker.
                Reuse lab07's permission-enabled index and lab11's SharePoint source/model settings.
                The project identity authenticates Search service calls; Foundry supplies USER source authorization.
                Invoke uses your delegated Foundry user token and a fresh conversation, never app-only fallback.
                Use separate non-admin Alice/Bob logins with the same restricted-fact question to verify live ACLs.
                No user tokens, call IDs or user IDs are stored on the shared toolbox/connection.
                Foundry Owner on the parent resource, Search authoring/read roles and az login are required.
                Grant-search now targets the PROJECT, not the agent; no version argument is accepted.
                It needs role-assignment rights; HTTP 403 prints an admin handoff and exits 3.
                Run stops before model calls if the Search grant could not be made. Role propagation can take time.
                No question retries, force-delete, shared-resource cleanup or secret persistence.
                Hosted compute, Search and two model layers can incur charges. Review evidence and clear outputs.
                """);
            return 0;
        }
        bool diagnostics = args.LastOrDefault() == "--diagnostics";
        string[] positional = diagnostics ? args.Take(args.Length - 1).ToArray() : args;
        bool questionCommand = command == "invoke", versionCommand = command is "status" or "wait";
        bool known = command is "verify-sources" or "knowledge-base" or "connection" or "toolbox" or "check-toolbox" or
            "write-agent" or "package" or "deploy" or "status" or "wait" or "grant-search" or "invoke" or "invoke-unknown" or "run" or "cleanup";
        bool valid = known && (!diagnostics || command is "invoke" or "invoke-unknown" or "run") &&
            (command == "cleanup" ? positional.Length == 2 && positional[1] == "--confirm" :
                questionCommand || versionCommand ? positional.Length is 1 or 2 : positional.Length == 1);
        if (!valid) throw new ArgumentException("Use help for commands, one quoted question, or exactly cleanup --confirm.");
        string? value = positional.Length == 2 ? positional[1] : null;
        if (versionCommand && value != null) IqWorkshop.ValidateVersionId(value);
        if (questionCommand && value != null && (string.IsNullOrWhiteSpace(value) || value.StartsWith("--")))
            throw new ArgumentException("Use a non-empty quoted question.");
        var settings = IqSettings.Load(root);
        var lab = new IqWorkshop(settings, client, getToken, utcNow, delay);
        if (command == "verify-sources") { await lab.VerifySourcesAsync(); return 0; }
        if (command == "knowledge-base") { await lab.CreateKnowledgeBaseAsync(); return 0; }
        if (command == "connection") { await lab.CreateConnectionAsync(); return 0; }
        if (command == "toolbox") { await lab.PublishToolboxAsync(); return 0; }
        if (command == "check-toolbox") { await lab.CheckToolboxAsync(); return 0; }
        if (command == "write-agent") { lab.WriteFiles(root); return 0; }
        if (command == "package") { lab.Package(root); return 0; }
        if (command == "deploy") { await lab.DeployAsync(root); return 0; }
        if (command is "status" or "wait")
        {
            var latest = value == null ? await lab.LatestAsync() : null;
            string version = value ?? (string)latest!["version"]!;
            if (command == "wait") await lab.WaitAsync(version);
            else if (latest == null) await lab.StatusAsync(version);
            else Console.WriteLine("Version " + version + " status: " + latest["status"]);
            return 0;
        }
        if (command == "grant-search") return await lab.GrantSearchAsync() ? 0 : 3;
        if (command == "invoke") { await lab.InvokeAsync(value ?? IqWorkshop.Question, diagnostics); return 0; }
        if (command == "invoke-unknown") { await lab.InvokeAsync(IqWorkshop.UnknownQuestion, diagnostics); return 0; }
        if (command == "cleanup") { await lab.CleanupAsync(true); return 0; }
        await lab.CreateKnowledgeBaseAsync();
        await lab.CreateConnectionAsync();
        await lab.PublishToolboxAsync();
        lab.WriteFiles(root);
        lab.Package(root);
        var created = await lab.DeployAsync(root);
        string selected = (string)created["version"]!;
        await lab.WaitAsync(selected);
        if (!await lab.GrantSearchAsync()) return 3;
        await lab.InvokeAsync(IqWorkshop.Question, diagnostics);
        await lab.InvokeAsync(IqWorkshop.UnknownQuestion, diagnostics);
        Console.WriteLine("Resources retained. Review tool results, document citations and negative-control behavior.");
        return 0;
    }
}
