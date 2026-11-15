using System.Text.Json.Nodes;

public static class HostedAgentProgram
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
        Func<Task<string>>? getToken = null, Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, Task>? delay = null)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 13 - A C# hosted agent with one local function tool (.NET 10).
                Run from the repository root. .env then .env.azure override environment values.
                Commands:
                  write-agent        Write Program.cs, WorkshopTips.cs and WorkshopAgent.csproj locally.
                  package            Zip those existing files only; does not rewrite code or contact Azure.
                  deploy             Upload the existing zip and create one immutable hosted version.
                  status [version]   Read one version, or the existing latest version.
                  wait [version]     Poll existing version every 15 seconds, at most 15 minutes.
                  invoke [question]  Call the hosted Responses endpoint once (not a bare model).
                  invoke-tool        Ask for the indexer tip once and print output item types.
                  run                Write, package, deploy, wait and make the two example calls.
                  cleanup --confirm  Delete only this prefix's workshop agent; never force-delete sessions.
                  help / --help / -h Read no configuration, files, credentials or remote resources.
                Required: WORKSHOP_PREFIX, AZURE_TENANT_ID, AZURE_AI_PROJECT_ENDPOINT,
                AZURE_AI_MODEL_DEPLOYMENT_NAME; Foundry Owner on the parent resource and az login.
                Existing model/project and hosted code deployment availability are prerequisites.
                C# source folder: <prefix>-csharp-hosted-agent; Azure name: <prefix>-hosted-agent.
                No Python, Docker, azd, Search, Web IQ key, app registration or ingestion needed.
                The launcher is BCL-only; the generated agent uses NuGet packages restored by Foundry.
                Write-agent overwrites the three generated source files. Package preserves your edits.
                No implicit prerequisite writes, question retries or cleanup. Run never deletes resources.
                Hosted compute, model inference and sessions can incur charges. Use synthetic questions.
                """);
            return 0;
        }
        bool known = command is "write-agent" or "package" or "deploy" or "status" or "wait" or
            "invoke" or "invoke-tool" or "run" or "cleanup";
        bool valid = known && (command == "cleanup"
            ? args.Length == 2 && args[1] == "--confirm"
            : command is "status" or "wait" or "invoke" ? args.Length <= 2 : args.Length == 1);
        if (!valid) throw new ArgumentException("Use help for commands. Use one quoted question; cleanup requires exactly: cleanup --confirm.");
        if (args.Length == 2 && command is "status" or "wait") HostedWorkshop.ValidateVersionId(args[1]);
        if (args.Length == 2 && command == "invoke" && (string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--")))
            throw new ArgumentException("Use one non-empty quoted question.");
        var settings = HostedSettings.Load(root);
        var lab = new HostedWorkshop(settings, client, getToken, utcNow, delay);
        if (command == "write-agent") { lab.WriteFiles(root); return 0; }
        if (command == "package") { lab.Package(root); return 0; }
        if (command == "deploy") { await lab.DeployAsync(root); return 0; }
        if (command is "status" or "wait")
        {
            JsonObject? latest = args.Length == 1 ? await lab.LatestAsync() : null;
            string version = args.Length == 2 ? args[1] : (string)latest!["version"]!;
            if (command == "wait") await lab.WaitAsync(version);
            else if (latest != null) Console.WriteLine("Version " + version + " status: " + latest["status"]);
            else await lab.StatusAsync(version);
            return 0;
        }
        if (command == "cleanup") { await lab.CleanupAsync(true); return 0; }
        if (command == "invoke") { await lab.InvokeAsync(args.Length == 2 ? args[1] : HostedWorkshop.Question); return 0; }
        if (command == "invoke-tool") { await lab.InvokeAsync(HostedWorkshop.ToolQuestion, true); return 0; }
        lab.WriteFiles(root);
        lab.Package(root);
        JsonObject created = await lab.DeployAsync(root);
        await lab.WaitAsync((string)created["version"]!);
        await lab.InvokeAsync(HostedWorkshop.Question);
        await lab.InvokeAsync(HostedWorkshop.ToolQuestion, true);
        Console.WriteLine("Agent and local files retained. Successful invocation, not active status alone, verifies runtime readiness.");
        return 0;
    }
}
