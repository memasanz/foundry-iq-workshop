try
{
    string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    if (command is "help" or "--help" or "-h")
    {
        Console.WriteLine(string.Join(", ", PushWorkshop.Commands));
        Console.WriteLine("Query modes and run accept one optional quoted question. Cleanup requires --confirm.");
        return 0;
    }
    bool query = PushWorkshop.QueryModes.Contains(command) || command == "run";
    if (!PushWorkshop.Commands.Contains(command) ||
        (command == "cleanup" && !args.SequenceEqual(new[] { "cleanup", "--confirm" })) ||
        (!query && command != "cleanup" && args.Length > 1) ||
        (query && args.Length > 2))
        throw new ArgumentException("Use help for commands. Cleanup requires exactly: cleanup --confirm.");
    var settings = PushSettings.Load(Directory.GetCurrentDirectory());
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    var workshop = new PushWorkshop(settings, new PushTransport(client, settings.Tenant));
    await workshop.ExecuteAsync(command, query && args.Length == 2 ? args[1] : null,
        command == "cleanup" && args.Length == 2 && args[1] == "--confirm");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
