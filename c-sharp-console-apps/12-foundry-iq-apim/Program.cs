try
{
    var options = ApimOptions.Parse(args);
    if (options.Command == "help")
    {
        Console.WriteLine(ApimOptions.Help);
        return 0;
    }
    var settings = ApimSettings.Load(Directory.GetCurrentDirectory());
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(150)
    };
    var workshop = new ApimWorkshop(settings, new ApimTransport(client, settings));
    await workshop.ExecuteAsync(options.Command, options.Question, options.OutputMode, options.Confirm, options.Diagnostics);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("APIM step failed: " + ApimResults.Redact(error.Message));
    return 1;
}
