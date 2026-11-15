try
{
    var options = KnowledgeBaseOptions.Parse(args);
    if (options.Command == "help")
    {
        Console.WriteLine(KnowledgeBaseOptions.Help);
        return 0;
    }
    var settings = KnowledgeBaseSettings.Load(Directory.GetCurrentDirectory());
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(150)
    };
    var workshop = new KnowledgeBaseWorkshop(settings, new KnowledgeBaseTransport(client, settings.Tenant));
    await workshop.ExecuteAsync(options.Command, options.Question, options.OutputMode, options.Confirm, options.Diagnostics);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Knowledge base step failed: " + KnowledgeBaseResults.Redact(error.Message,
        Environment.GetEnvironmentVariable("WEBIQ_API_KEY") ?? ""));
    return 1;
}
