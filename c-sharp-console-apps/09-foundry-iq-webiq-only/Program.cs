try
{
    var options = WebIqOptions.Parse(args);
    if (options.Command == "help")
    {
        Console.WriteLine(WebIqOptions.Help);
        return 0;
    }
    var settings = WebIqSettings.Load(Directory.GetCurrentDirectory());
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };
    var workshop = new WebIqWorkshop(settings, new WebIqTransport(client, settings.Tenant));
    await workshop.ExecuteAsync(options.Command, options.Question, options.OutputMode, options.Confirm);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Web IQ-only step failed: " + WebIqResults.Redact(error.Message,
        Environment.GetEnvironmentVariable("WEBIQ_API_KEY") ?? ""));
    return 1;
}
