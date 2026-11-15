try
{
    string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine("source, knowledge-base, retrieve [query], run [query], cleanup --confirm, help");
        Console.WriteLine("Run from the repository root. Retrieval invokes the billable Microsoft 365 Copilot Retrieval API as your signed-in user.");
        Console.WriteLine("Each step does only its named action. Run never cleans up; cleanup requires exactly: cleanup --confirm.");
        return 0;
    }
    bool queryCommand = command is "retrieve" or "run";
    if (!RemoteWorkshop.Commands.Contains(command) ||
        (command == "cleanup" && !args.SequenceEqual(new[] { "cleanup", "--confirm" })) ||
        (!queryCommand && command != "cleanup" && args.Length > 1) ||
        (queryCommand && args.Length > 2))
        throw new ArgumentException("Use help for commands. Cleanup requires exactly: cleanup --confirm.");
    var settings = RemoteSettings.Load(Directory.GetCurrentDirectory());
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    var workshop = new RemoteWorkshop(settings, new RemoteTransport(client, settings.Tenant));
    await workshop.ExecuteAsync(command, queryCommand && args.Length == 2 ? args[1] : null,
        command == "cleanup" && args.Length == 2 && args[1] == "--confirm");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
