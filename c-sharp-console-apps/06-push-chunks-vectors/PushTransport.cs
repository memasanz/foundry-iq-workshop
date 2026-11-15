using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed record PushSettings(string Root, string Tenant, string SearchEndpoint,
    string Account, string Deployment, string Model, string Prefix)
{
    public string Index => Prefix + "-push-chunks";
    public string EmbeddingEndpoint => $"https://{Account}.openai.azure.com";
    public string CacheDirectory => Path.Combine(Root, ".workshop", "lab06-" + Prefix);
    public string ChunksPath => Path.Combine(CacheDirectory, "chunks.json");
    public string VectorsPath => Path.Combine(CacheDirectory, "vectors.json");
    public string UploadedPath => Path.Combine(CacheDirectory, "uploaded.json");

    public static PushSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string key) => Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + key);
        var settings = new PushSettings(Path.GetFullPath(root), Required("AZURE_TENANT_ID"),
            Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'), Required("AZURE_AI_ACCOUNT_NAME"),
            Required("AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"),
            Environment.GetEnvironmentVariable("AZURE_AI_EMBEDDING_MODEL_NAME")?.Trim() is { Length: > 0 } model
                ? model : "text-embedding-3-small", Required("WORKSHOP_PREFIX"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"^[a-z][a-z0-9]{2,19}$"))
            throw new InvalidOperationException("Use a tenant GUID and a 3-20 lowercase letter/digit prefix starting with a letter.");
        if (!Uri.TryCreate(SearchEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.EndsWith(".search.windows.net", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length <= ".search.windows.net".Length || uri.AbsolutePath != "/" ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || !uri.IsDefaultPort)
            throw new InvalidOperationException("Expected a public Azure HTTPS Search endpoint without a path/query.");
        foreach (string value in new[] { Account, Deployment, Model })
            if (!Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$"))
                throw new InvalidOperationException("Invalid AI account, deployment or model.");
        if (!Regex.IsMatch(Account, @"^[a-zA-Z0-9][a-zA-Z0-9-]{1,62}$"))
            throw new InvalidOperationException("Invalid AI account name.");
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

public sealed class PushTransport(HttpClient client, string tenant, Func<string, Task<string>>? tokenProvider = null)
{
    public const string SearchAudience = "https://search.azure.com/";
    public const string EmbeddingAudience = "https://cognitiveservices.azure.com/";
    public const string SearchApiVersion = "2025-09-01";
    public const string EmbeddingApiVersion = "2024-10-21";
    readonly Func<string, Task<string>> getToken = tokenProvider ?? (resource => AzureCliTokenAsync(tenant, resource));

    public Task<JsonObject> SearchAsync(HttpMethod method, string endpoint, string path,
        JsonObject? body, params int[] expected) =>
        SendAsync(method, $"{endpoint}/{path}?api-version={SearchApiVersion}", SearchAudience, body, expected);

    public Task<JsonObject> EmbeddingsAsync(PushSettings settings, JsonObject body) =>
        SendAsync(HttpMethod.Post,
            $"{settings.EmbeddingEndpoint}/openai/deployments/{Uri.EscapeDataString(settings.Deployment)}/embeddings?api-version={EmbeddingApiVersion}",
            EmbeddingAudience, body, [200]);

    async Task<JsonObject> SendAsync(HttpMethod method, string uri, string audience, JsonObject? body, int[] expected)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(audience));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        if (!expected.Contains((int)response.StatusCode))
        {
            string message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}; expected {string.Join(", ", expected)}.";
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("Response body: " + text[..Math.Min(8000, text.Length)]);
            foreach (string name in new[] { "x-ms-request-id", "request-id", "retry-after", "x-ms-retry-after-ms" })
                if (response.Headers.TryGetValues(name, out var values))
                    Console.Error.WriteLine($"{name}: {string.Join(", ", values)}");
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        if ((int)response.StatusCode == 404) return new JsonObject { ["missing"] = true };
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
    }

    public static async Task<string> AzureCliTokenAsync(string tenant, string resource)
    {
        if (!Guid.TryParseExact(tenant, "D", out _) || resource is not (SearchAudience or EmbeddingAudience))
            throw new InvalidOperationException("Invalid Azure CLI tenant or audience.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
            foreach (string arg in new[] { "/d", "/c", "az" }) start.ArgumentList.Add(arg);
        foreach (string arg in new[] { "account", "get-access-token", "--resource", resource, "--tenant", tenant, "--output", "json" })
            start.ArgumentList.Add(arg);
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
            throw new InvalidOperationException("Azure CLI authentication timed out. Sign in to the workshop tenant.");
        }
        await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Azure CLI authentication failed. Run az login --tenant with AZURE_TENANT_ID.");
        return JsonNode.Parse(await output)?["accessToken"]?.GetValue<string>() is { Length: > 0 } token
            ? token : throw new InvalidOperationException("Azure CLI returned no access token.");
    }
}
