public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("Lab 04: dotnet run --project c-sharp-console-apps\\04-blob-indexer-skills -- <command> [question]");
            Console.WriteLine("Commands: " + string.Join(", ", BlobWorkshop.Commands) + ", help");
            Console.WriteLine("Each step performs only its named action. run executes the complete sequence.");
            Console.WriteLine("Questions are optional only for keyword/vector/hybrid/semantic/semantic-hybrid/run.");
            return 0;
        }
        if (!BlobWorkshop.Commands.Contains(command) ||
            (args.Length > 1 && command is not ("keyword" or "vector" or "hybrid" or "semantic" or "semantic-hybrid" or "run")))
        {
            Console.Error.WriteLine("Invalid command/arguments. Use help.");
            return 1;
        }
        try
        {
            var settings = SearchSettings.Load(Directory.GetCurrentDirectory());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            var lab = new BlobWorkshop(settings, new SearchTransport(client, settings.Tenant));
            await lab.ExecuteAsync(command, args.Length > 1 ? string.Join(" ", args.Skip(1)) : null);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
