using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class WebIqSettings(string tenant, string searchEndpoint, string account,
    string deployment, string model, string prefix, string webIqKey)
{
    public string Tenant { get; } = tenant;
    public string SearchEndpoint { get; } = searchEndpoint;
    public string Account { get; } = account;
    public string Deployment { get; } = deployment;
    public string Model { get; } = model;
    public string Prefix { get; } = prefix;
    public string WebIqKey { get; } = webIqKey;
    public string ModelEndpoint => $"https://{Account}.openai.azure.com";
    public string IndexName => Prefix + "-cu-chunks";
    public string BlobSourceName => Prefix + "-nb10-blob";
    public string SourceName => Prefix + "-nb10-webiq";
    public string BaseName => Prefix + "-nb10-kb";

    public static WebIqSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name + ". See .env.example.");
        var settings = new WebIqSettings(Required("AZURE_TENANT_ID"), Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'),
            Required("AZURE_AI_ACCOUNT_NAME"), Required("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
            Environment.GetEnvironmentVariable("AZURE_AI_MODEL_NAME") ?? "gpt-5-mini",
            Required("WORKSHOP_PREFIX"), Required("WEBIQ_API_KEY"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"^[a-z][a-z0-9]{2,19}$"))
            throw new InvalidOperationException("Use a tenant GUID and 3-20 lowercase letters/digits prefix starting with a letter.");
        if (!Uri.TryCreate(SearchEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo != "" || !endpoint.IsDefaultPort || endpoint.AbsolutePath != "/" ||
            endpoint.Query != "" || endpoint.Fragment != "" ||
            !Regex.IsMatch(endpoint.Host, @"^[a-z0-9][a-z0-9-]*\.search\.windows\.net$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Use a public Azure HTTPS Search service root without credentials, path or query.");
        if (!Regex.IsMatch(Account, @"^[a-zA-Z0-9][a-zA-Z0-9-]*$") ||
            string.IsNullOrWhiteSpace(Deployment) || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(WebIqKey))
            throw new InvalidOperationException("Use a Foundry account name, chat deployment/model and non-empty WEBIQ_API_KEY.");
    }

    public static void LoadEnvFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
        {
            string setting = line.Trim();
            if (setting.Length == 0 || setting.StartsWith('#')) continue;
            if (setting.StartsWith("export ", StringComparison.Ordinal)) setting = setting[7..].TrimStart();
            int equals = setting.IndexOf('=');
            if (equals <= 0) continue;
            string name = setting[..equals].Trim();
            string value = setting[(equals + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            else
            {
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

public sealed class WebIqTransport(HttpClient client, string tenant, Func<Task<string>>? tokenProvider = null)
{
    public const string Scope = "https://search.azure.com/.default";
    public const string ApiVersion = "2026-08-01-preview";
    readonly Func<Task<string>> getToken = tokenProvider ?? (() => AzureCliTokenAsync(tenant));

    public async Task<JsonObject> SendAsync(WebIqSettings settings, HttpMethod method, string path,
        JsonObject? body = null, bool allowMissing = false)
    {
        settings.Validate();
        string token;
        try { token = await getToken(); }
        catch (Exception error)
        {
            throw new InvalidOperationException(WebIqResults.Redact(error.Message, settings.WebIqKey));
        }
        using var request = new HttpRequestMessage(method, $"{settings.SearchEndpoint}/{path}?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;
            string Safe(string value) => WebIqResults.Redact(value, settings.WebIqKey, token);
            Console.WriteLine($"HTTP status: {status}");
            Console.WriteLine("Request ID: " + Safe(response.Headers.TryGetValues("x-ms-request-id", out var ids)
                ? string.Join(", ", ids) : response.Headers.TryGetValues("request-id", out ids) ? string.Join(", ", ids) : ""));
            if (allowMissing && status == 404) return new JsonObject();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"HTTP {status}: Search request failed. UTC (Date): {response.Headers.Date:R}");
                Console.Error.WriteLine(status switch
                {
                    401 => "Check Azure CLI sign-in and tenant; this is Search authorization, not proof of Web IQ key failure.",
                    403 => "Check Search RBAC/network access (index reads need a data role); model/vectorizer errors need Search managed-identity inference access.",
                    429 => "Quota or throttling; inspect Retry-After and service/profile capacity. No automatic billable retry.",
                    404 when path.StartsWith("indexes/") => "Complete lab 05 Content Understanding with the same prefix; do not create an index here.",
                    _ => "Inspect source activity and request identifiers; auth_missing_token concerns the stored Web IQ profile header."
                });
                foreach (var header in response.Headers)
                    if (header.Key.Contains("request", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
                        header.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase))
                        Console.Error.WriteLine(header.Key + ": " + Safe(string.Join(", ", header.Value)));
                string safe = Safe(text);
                Console.Error.WriteLine("Response body: " + safe[..Math.Min(8000, safe.Length)]);
                throw new HttpRequestException($"HTTP {status}: Search request failed; inspect redacted diagnostics above.", null, response.StatusCode);
            }
            JsonObject result;
            try
            {
                result = string.IsNullOrWhiteSpace(text) && method != HttpMethod.Post ? new JsonObject()
                    : JsonNode.Parse(Safe(text))?.AsObject() ?? throw new JsonException();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException("Search returned invalid JSON; no complete result available.");
            }
            if (path.EndsWith("/retrieve", StringComparison.Ordinal))
                WebIqResults.Print(result, status, settings.WebIqKey, token);
            else if (status == 206)
                throw new InvalidOperationException("Search returned a partial response; no complete result available.");
            return result;
        }
        catch (HttpRequestException error)
        {
            throw new HttpRequestException(WebIqResults.Redact(error.Message, settings.WebIqKey, token), null, error.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Search request timed out before a complete response; inspect network/service health. No automatic retry.");
        }
    }

    public static async Task<string> AzureCliTokenAsync(string tenant)
    {
        if (!Guid.TryParseExact(tenant, "D", out _)) throw new InvalidOperationException("Invalid tenant GUID.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
            foreach (string argument in new[] { "/d", "/c", "az" }) start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "account", "get-access-token", "--scope", Scope, "--tenant", tenant, "--output", "json" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw new InvalidOperationException("Azure CLI authentication timed out before Search/Web IQ. Run az login --tenant AZURE_TENANT_ID.");
        }
        await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Azure CLI authentication failed before Search/Web IQ. Run az login --tenant AZURE_TENANT_ID.");
        try
        {
            return JsonNode.Parse(await output)?["accessToken"]?.GetValue<string>() is { Length: > 0 } token
                ? token : throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Azure CLI returned no usable access token.");
        }
    }
}

public static class WebIqResults
{
    public static string Redact(string text, params string[] secrets)
    {
        foreach (string secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
            string escaped = JsonSerializer.Serialize(secret)[1..^1];
            text = text.Replace(escaped, "<redacted>", StringComparison.Ordinal);
            text = text.Replace(Uri.EscapeDataString(secret), "<redacted>", StringComparison.Ordinal);
        }
        return text;
    }

    public static void ValidateQuery(string question, string outputMode)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question.");
        if (outputMode is not ("answerSynthesis" or "extractiveData"))
            throw new ArgumentException("Use answerSynthesis or extractiveData.");
    }

    public static void Print(JsonObject result, int status, params string[] secrets)
    {
        void Write(string value) => Console.WriteLine(Redact(value, secrets));
        Console.WriteLine("\nActivity:");
        Write((result["activity"] ?? new JsonArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("\nAnswer or extractive output:");
        foreach (JsonNode? message in result["response"] as JsonArray ?? new JsonArray())
            foreach (JsonNode? part in message?["content"] as JsonArray ?? new JsonArray())
                Write(part?["text"]?.ToString() ?? "");
        Console.WriteLine("\nReferences and retrieved passages:");
        foreach (JsonNode? reference in result["references"] as JsonArray ?? new JsonArray())
        {
            var data = reference?["sourceData"]?.DeepClone() as JsonObject ?? new JsonObject();
            try
            {
                if (JsonNode.Parse(data["content"]?.ToString() ?? "{}") is JsonObject parsed)
                    foreach (var property in parsed) data[property.Key] = property.Value?.DeepClone();
            }
            catch (JsonException) { }
            Write($"- {reference?["id"]} {data["title"]} {data["url"] ?? data["source/url"] ?? data["source"]?["url"]}");
            string content = (data["chunk"] ?? data["content"])?.ToString() ?? "";
            string safeContent = Redact(content, secrets);
            Write(safeContent[..Math.Min(2000, safeContent.Length)]);
        }
        if (status == 206 || result["activity"] is JsonArray activity &&
            activity.Any(step => step?["error"] is JsonNode error && error.ToJsonString() is not ("null" or "{}" or "\"\"" or "false")))
            throw new InvalidOperationException("Retrieval was partial or a source failed. Inspect the activity above.");
        if (result["references"] is not JsonArray references || references.Count == 0)
            Console.WriteLine("No references returned. Inspect activity; this alone does not establish an authentication failure.");
    }
}
