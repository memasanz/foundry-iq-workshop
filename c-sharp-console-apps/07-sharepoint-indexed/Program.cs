public static class SharePointIndexedProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            return await RunAsync(args, Directory.GetCurrentDirectory(), client);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Lab could not complete: " + error.Message);
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, string root, HttpClient client,
        Func<string, Task<string>>? tokenProvider = null, Func<TimeSpan, Task>? delay = null)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 07 - SharePoint indexing, Content Understanding, embeddings and Entra ACLs (.NET 10).
                Run from the repository root; .env then .env.azure override environment values.
                Commands:
                  configuration                    Validate settings and ingestion IDs; fully offline.
                  data-source --setup-confirmed    Create/update the secretless SharePoint source.
                  index                            Create/update the Entra ACL-aware text/vector index.
                  skillset                         Create/update Content Understanding, embedding and ACL projections.
                  indexer --setup-confirmed        Create/start ingestion, or update/request an existing idle indexer.
                  status                           Read current indexer status; no new run.
                  wait                             Monitor the requested run/current last result; no new run.
                  query [question]                 Search as the signed-in user; default '*', indented results.
                  run-indexer                      Explicitly request another billable run; does not wait/query.
                  resync-permissions --confirm     Resync inherited ACLs, then request a run; does not wait/query.
                  run --setup-confirmed            Create all four objects, wait and query visibility once.
                  cleanup --confirm                Preflight/delete only this lab's four named Search objects.
                Reuse scripts\setup-sharepoint-app.ps1; no new app registration, secret, consent or site grant.
                Setup confirmation acknowledges review, not a permissions test. Preview enrollment is required.
                Search's system identity federates to the ingestion app; its client ID is not its principal ID.
                Graph Files.Read.All is tenant-wide; Sites.Selected does not narrow that separate grant.
                Your Search token authenticates both Search access and document ACL identity, for different purposes.
                Query rejects app-only tokens; no elevated-read, native SharePoint-group expansion or automatic retries.
                CU/embeddings and Search incur charges. Use a non-sensitive test site/library and two member users.
                Cleanup retains the app, federation, consent, site grant, source documents and other indexes.
                Help reads no settings, acquires no token and has no side effects.
                """);
            return 0;
        }
        bool setupCommand = command is "data-source" or "indexer" or "run";
        bool confirmedCommand = command is "cleanup" or "resync-permissions";
        bool known = command is "configuration" or "data-source" or "index" or "skillset" or "indexer" or
            "status" or "wait" or "query" or "run-indexer" or "resync-permissions" or "run" or "cleanup";
        bool valid = known && (setupCommand ? args.Length == 2 && args[1] == "--setup-confirmed" :
            confirmedCommand ? args.Length == 2 && args[1] == "--confirm" :
            command == "query" ? args.Length is 1 or 2 : args.Length == 1);
        if (!valid) throw new ArgumentException("Use help: one quoted query, --setup-confirmed for ingestion setup, and exactly --confirm for resync/cleanup.");
        string question = command == "query" && args.Length == 2 ? args[1] : "*";
        if (command == "query") SharePointWorkshop.QueryDefinition(question);
        SharePointSettings settings = SharePointSettings.Load(root);
        var transport = new SharePointTransport(client, settings, tokenProvider);
        var lab = new SharePointWorkshop(settings, transport, delay, persistCheckpoint: true);
        if (command == "configuration")
        {
            settings.ConnectionString();
            Console.WriteLine("Settings validated. Index: " + settings.Index + " | site: " + settings.SiteUrl);
            Console.WriteLine("IDs are distinct client/application IDs. Consent, federation and site access are not tested.");
        }
        else if (command == "data-source") await lab.CreateDataSourceAsync(true);
        else if (command == "index") await lab.CreateIndexAsync();
        else if (command == "skillset") await lab.CreateSkillsetAsync();
        else if (command == "indexer") await lab.CreateIndexerAsync(true);
        else if (command == "status") await lab.StatusAsync();
        else if (command == "wait") await lab.WaitAsync();
        else if (command == "query") await lab.QueryAsync(question);
        else if (command == "run-indexer") await lab.RunIndexerAsync();
        else if (command == "resync-permissions") await lab.RunIndexerAsync(resyncPermissions: true);
        else if (command == "cleanup") await lab.CleanupAsync(true);
        else
        {
            settings.ConnectionString();
            await lab.CreateDataSourceAsync(true);
            await lab.CreateIndexAsync();
            await lab.CreateSkillsetAsync();
            await lab.CreateIndexerAsync(true);
            await lab.WaitAsync();
            await lab.QueryAsync();
        }
        return 0;
    }
}
