public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("Lab 05: dotnet run --project c-sharp-console-apps\\05-blob-content-understanding -- <command> [question]");
            Console.WriteLine("Commands: " + string.Join(", ", ContentUnderstandingWorkshop.Commands) + ", help");
            Console.WriteLine("Each step performs only its named action. run executes the complete sequence.");
            Console.WriteLine("Questions are optional only for semantic-hybrid/run.");
            return 0;
        }
        if (!ContentUnderstandingWorkshop.Commands.Contains(command) ||
            (args.Length > 1 && command is not ("semantic-hybrid" or "run")))
        {
            Console.Error.WriteLine("Invalid command/arguments. Use help.");
            return 1;
        }
        try
        {
            var settings = SearchSettings.Load(Directory.GetCurrentDirectory());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            var lab = new ContentUnderstandingWorkshop(settings, new SearchTransport(client, settings.Tenant));
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
