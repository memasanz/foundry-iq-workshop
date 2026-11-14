using System.Net.Http.Json;
using System.Text.Json.Nodes;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.microsoft.ai/v3/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
        try
        {
            return await RunAsync(args, client);
        }
        catch (HttpRequestException error)
        {
            Console.WriteLine($"Request failed: {error.Message}");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Request timed out. Retry later.");
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, HttpClient client)
    {
        const string question = "What official OFAC guidance explains how to review a potential sanctions name match?";
        string api = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (api is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Lab 01 - Web IQ APIs (.NET 10)
                Run from the repository root; settings load from .env, then .env.azure.
                Choose one API per run; requests incur Web IQ usage charges.

                dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- web
                Commands: web | localized | news | images | videos
                Add an optional public search query after the command.
                Browse: browse <public-http(s)-URL> [public question]
                Browse makes no hidden search call; it retries pending crawls up to four times.
                """);
            return 0;
        }
        if (api is not ("web" or "localized" or "browse" or "news" or "images" or "videos"))
        {
            Console.WriteLine("Unknown API. Run with --help to see the commands.");
            return 2;
        }
        if (api == "browse" &&
            (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out Uri? url) ||
             (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)))
        {
            Console.WriteLine("Browse requires a public HTTP(S) source URL. Run Web Search first and copy a result URL.");
            return 2;
        }

        Workshop.LoadEnvFile(".env");
        Workshop.LoadEnvFile(".env.azure");
        string? apiKey = Environment.GetEnvironmentVariable("WEBIQ_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("Set WEBIQ_API_KEY in the repository's .env. The API key will not be displayed.");
            return 2;
        }
        client.DefaultRequestHeaders.Remove("x-apikey");
        client.DefaultRequestHeaders.Add("x-apikey", apiKey);

        string? customQuery = args.Length > 1 && api != "browse" ? string.Join(" ", args.Skip(1)) : null;
        string endpoint;
        object payload;
        switch (api)
        {
            case "web":
                endpoint = "search/web";
                payload = new
                {
                    query = customQuery ?? question, maxResults = 10,
                    contentFormat = "passage", maxLength = 3000,
                    language = "en", region = "US", safeSearch = "strict",
                };
                break;
            case "localized":
                endpoint = "search/web";
                payload = new
                {
                    query = customQuery ?? "inteligencia artificial generativa proteccion de datos personales site:aepd.es",
                    language = "es", region = "ES", safeSearch = "strict",
                    maxResults = 10, contentFormat = "passage", maxLength = 3000,
                };
                break;
            case "browse":
                endpoint = "browse";
                payload = new
                {
                    url = args[1], query = args.Length > 2 ? string.Join(" ", args.Skip(2)) : question,
                    contentFormat = "passage", maxLength = 3000, liveCrawl = "fallback",
                };
                Console.WriteLine($"Reading page: {args[1]}");
                break;
            case "news":
                endpoint = "search/news";
                payload = new
                {
                    query = customQuery ?? "OFAC sanctions enforcement developments", maxResults = 5,
                    language = "en", region = "US", contentFormat = "passage", maxLength = 3000,
                };
                break;
            case "images":
                endpoint = "search/images";
                payload = new
                {
                    query = customQuery ?? "OFAC sanctions screening process flowchart", maxResults = 3,
                    aspectRatio = "wide", imageSize = "large", safeSearch = "strict",
                };
                break;
            default:
                endpoint = "search/videos";
                payload = new
                {
                    query = customQuery ?? "OFAC sanctions compliance training", maxResults = 3,
                    duration = "medium", resolution = "720p", freshness = "year", safeSearch = "strict",
                };
                break;
        }

        int attempts = api == "browse" ? 4 : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, payload);
            JsonObject result = await Workshop.ReadResponseAsync(response, browse: api == "browse");
            if (result.Count == 0)
                return 1;
            if (api != "browse" || (int)response.StatusCode == 200)
                return 0;
            if (attempt == 3)
            {
                Console.WriteLine("Browse did not complete after four requests. Retry later.");
                return 1;
            }
            if (!Workshop.TryGetRetryDelay(result, out double seconds))
                return 1;
            Console.WriteLine($"Crawl pending. Waiting {seconds:g}s...");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        return 1;
    }
}
