using System.Net;
using System.Text;
using System.Text.Json.Nodes;

public static class LabTests
{
    public static async Task Main()
    {
        string originalDirectory = Directory.GetCurrentDirectory();
        string? originalKey = Environment.GetEnvironmentVariable("WEBIQ_API_KEY");
        string folder = Path.Combine(Path.GetTempPath(), $"webiq-lab-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var output = new StringWriter();
        TextWriter originalOutput = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(folder);
            Console.SetOut(output);
            Environment.SetEnvironmentVariable("WEBIQ_API_KEY", "process-key");
            File.WriteAllText(".env", "WEBIQ_API_KEY='local-key'\n");
            File.WriteAllText(".env.azure", "WEBIQ_API_KEY=synthetic-test-key # generated value\n");

            foreach (string api in new[] { "web", "localized", "browse", "news", "images", "videos" })
            {
                using var handler = new RecordingHandler(_ => Response(200, """{"webResults":[]}"""));
                using var client = Client(handler);
                string[] args = api == "browse" ? [api, "https://example.com/source"] : [api];
                Equal(0, await Program.RunAsync(args, client), $"{api} succeeds");
                Equal(1, handler.Requests.Count, $"{api} performs only the selected request");
                var request = handler.Requests[0];
                string route = api switch
                {
                    "localized" => "search/web", "browse" => "browse", _ => $"search/{api}",
                };
                Equal($"/v3/{route}", request.Path, $"{api} route");
                Equal("synthetic-test-key", request.Key, "generated env settings override local/process settings");
                Equal("application/json", request.ContentType, "JSON request content type");
                Check(!string.IsNullOrWhiteSpace(request.Payload["query"]?.GetValue<string>()), "public query is sent");
                if (api == "browse")
                    Equal("https://example.com/source", request.Payload["url"]?.GetValue<string>(), "explicit browse URL");
            }

            using (var handler = new RecordingHandler(_ => Response(200, """{"webResults":[]}""")))
            using (var client = Client(handler))
            {
                Equal(0, await Program.RunAsync(["web", "custom", "public query"], client), "custom query succeeds");
                Equal("custom public query", handler.Requests[0].Payload["query"]?.GetValue<string>(), "custom query joins arguments");
                Equal(2, await Program.RunAsync(["browse", "file:///private.txt"], client), "non-HTTP URL rejected");
                Equal(2, await Program.RunAsync(["browse"], client), "missing URL rejected");
                Equal(2, await Program.RunAsync(["unknown"], client), "unknown command rejected");
                Equal(0, await Program.RunAsync(["--help"], client), "help succeeds offline");
                Equal(1, handler.Requests.Count, "invalid commands and help make no calls");
            }

            using (var handler = new RecordingHandler(attempt =>
                attempt == 1 ? Response(202, """{"retryAfter":"0s"}""") : Response(200, """{"url":"https://example.com"}""")))
            using (var client = Client(handler))
            {
                Equal(0, await Program.RunAsync(["browse", "https://example.com"], client), "pending crawl completes");
                Equal(2, handler.Requests.Count, "pending crawl retried");
            }
            using (var handler = new RecordingHandler(_ => Response(202, """{"retryAfter":"0s"}""")))
            using (var client = Client(handler))
            {
                Equal(1, await Program.RunAsync(["browse", "https://example.com"], client), "exhausted crawl returns failure");
                Equal(4, handler.Requests.Count, "retry limit enforced");
            }
            foreach (string body in new[] { """{"state":"pending"}""", """{"retryAfter":"121s"}""",
                """{"retryAfter":"bad"}""", """{"retryAfter":20}""" })
            {
                using var handler = new RecordingHandler(_ => Response(202, body));
                using var client = Client(handler);
                Equal(1, await Program.RunAsync(["browse", "https://example.com"], client), "invalid pending delay stops cleanly");
                Equal(1, handler.Requests.Count, "invalid pending delay is not retried");
            }

            output.GetStringBuilder().Clear();
            using (var response = Response(403, new string('x', 9000)))
                Equal(0, (await Workshop.ReadResponseAsync(response)).Count, "HTTP failure clears result");
            Check(output.ToString().Contains("HTTP: 403"), "HTTP status printed");
            Check(output.ToString().Contains(new string('x', 8000)), "error body retained");
            Check(!output.ToString().Contains(new string('x', 8001)), "error body bounded");
            using (var response = Response(204, ""))
                Equal(0, (await Workshop.ReadResponseAsync(response, browse: true)).Count, "unexpected browse status stops cleanly");

            foreach (string delay in new[] { "-1s", "NaNs", "Infinitys", "121s", "invalid" })
                Check(!Workshop.TryGetRetryDelay(new JsonObject { ["retryAfter"] = delay }, out _), "invalid retry delay rejected");
            Check(Workshop.TryGetRetryDelay(new JsonObject { ["retryAfter"] = "0.5s" }, out double seconds)
                && seconds == 0.5, "duration delay parsed");
            string timestamp = DateTimeOffset.UtcNow.AddSeconds(30).ToString("O");
            Check(Workshop.TryGetRetryDelay(new JsonObject { ["retryAfter"] = timestamp }, out seconds)
                && seconds > 0 && seconds <= 30, "UTC timestamp delay parsed");

            File.Delete(".env");
            File.Delete(".env.azure");
            Environment.SetEnvironmentVariable("WEBIQ_API_KEY", null);
            using (var handler = new RecordingHandler(_ => throw new Exception("Must not call the service")))
            using (var client = Client(handler))
                Equal(2, await Program.RunAsync(["web"], client), "missing key fails before requesting");
        }
        finally
        {
            Console.SetOut(originalOutput);
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("WEBIQ_API_KEY", originalKey);
            File.Delete(Path.Combine(folder, ".env"));
            File.Delete(Path.Combine(folder, ".env.azure"));
            Directory.Delete(folder);
        }
        Console.WriteLine("PASS C# lab: API selection, settings, request payloads, clean errors and bounded Browse retries (offline).");
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.microsoft.ai/v3/"),
    };

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"FAIL: {message}");
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string? Key, string? ContentType, JsonObject Payload)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("x-apikey", out var values) ? values.Single() : null,
                request.Content!.Headers.ContentType?.MediaType,
                JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject()));
            return respond(Requests.Count);
        }
    }
}
