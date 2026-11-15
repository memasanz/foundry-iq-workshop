using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed record SearchSettings(string Root, string Tenant, string SearchEndpoint,
    string StorageEndpoint, string StorageId, string Container, string Account,
    string Deployment, string Model, string Prefix)
{
    public string Index => Prefix + "-blob-chunks";
    public string DataSource => Prefix + "-blob-source";
    public string Skills => Prefix + "-blob-skills";
    public string Indexer => Prefix + "-blob-indexer";
    public string BlobPrefix => Prefix + "/blob/";
    public string EmbeddingEndpoint => $"https://{Account}.openai.azure.com";
    public string StatePath => Path.Combine(Root, ".workshop", $"lab04-{Prefix}.json");

    public static SearchSettings Load(string root)
    {
        Workshop.LoadEnvFile(Path.Combine(root, ".env"));
        Workshop.LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing setting: {name}");
        var settings = new SearchSettings(Path.GetFullPath(root), Required("AZURE_TENANT_ID"),
            Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'), Required("AZURE_STORAGE_BLOB_ENDPOINT").TrimEnd('/'),
            Required("AZURE_STORAGE_RESOURCE_ID"), Required("AZURE_STORAGE_CONTAINER_NAME"),
            Required("AZURE_AI_ACCOUNT_NAME"), Required("AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"),
            Environment.GetEnvironmentVariable("AZURE_AI_EMBEDDING_MODEL_NAME")?.Trim() is { Length: > 0 } model
                ? model : "text-embedding-3-small", Required("WORKSHOP_PREFIX"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"^[a-z][a-z0-9]{2,19}$"))
            throw new InvalidOperationException("Use a tenant GUID and a 3-20 lowercase letter/digit prefix starting with a letter.");
        ValidateEndpoint(SearchEndpoint, ".search.windows.net");
        ValidateEndpoint(StorageEndpoint, ".blob.core.windows.net");
        if (!Regex.IsMatch(StorageId, @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[a-zA-Z0-9_.()-]+/providers/Microsoft\.Storage/storageAccounts/[a-z0-9]{3,24}$") ||
            !Regex.IsMatch(Container, @"^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$") ||
            !Regex.IsMatch(Account, @"^[a-zA-Z0-9][a-zA-Z0-9-]{1,62}$") ||
            !Regex.IsMatch(Deployment, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$") ||
            !Regex.IsMatch(Model, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$"))
            throw new InvalidOperationException("Invalid Storage resource ID, container, AI account, deployment or model.");
    }

    static void ValidateEndpoint(string value, string suffix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || uri.Host.Length <= suffix.Length ||
            uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || !uri.IsDefaultPort)
            throw new InvalidOperationException($"Expected an HTTPS Azure endpoint ending in {suffix}, without a path/query.");
    }
}

public sealed class SearchTransport(HttpClient client, string tenant, Func<string, Task<string>>? tokenProvider = null)
{
    public const string SearchAudience = "https://search.azure.com/";
    public const string StorageAudience = "https://storage.azure.com/";
    public const string ApiVersion = "2025-09-01";
    readonly Func<string, Task<string>> getToken = tokenProvider ?? (resource => AzureCliTokenAsync(tenant, resource));

    public async Task<JsonObject> SearchAsync(HttpMethod method, string endpoint, string path,
        JsonObject? body, params int[] expected)
    {
        using var request = new HttpRequestMessage(method, $"{endpoint}/{path}?api-version={ApiVersion}");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(request, SearchAudience, expected);
    }

    public async Task UploadAsync(string endpoint, string container, string blobName, byte[] bytes)
    {
        string storagePath = string.Join("/", blobName.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/{Uri.EscapeDataString(container)}/{storagePath}");
        request.Headers.Add("x-ms-version", "2023-11-03");
        request.Headers.Add("x-ms-date", DateTimeOffset.UtcNow.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
        await SendAsync(request, StorageAudience, [201]);
    }

    async Task<JsonObject> SendAsync(HttpRequestMessage request, string audience, int[] expected)
    {
        // Obtain a current token for each request; never display tokens or Authorization headers.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(audience));
        using var response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        CheckResponse(response, text, expected);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new JsonObject { ["missing"] = true };
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
    }

    public static void CheckResponse(HttpResponseMessage response, string text, params int[] expected)
    {
        if (expected.Contains((int)response.StatusCode))
            return;
        string message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}; expected {string.Join(", ", expected)}.";
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Response body: " + (text.Length == 0 ? "(empty)" : text[..Math.Min(8000, text.Length)]));
        foreach (string name in new[] { "x-ms-request-id", "request-id", "retry-after", "x-ms-retry-after-ms" })
            if (response.Headers.TryGetValues(name, out var values))
                Console.Error.WriteLine($"{name}: {string.Join(", ", values)}");
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public static async Task<string> AzureCliTokenAsync(string tenant, string resource)
    {
        if (!Guid.TryParseExact(tenant, "D", out _) || resource is not (SearchAudience or StorageAudience))
            throw new InvalidOperationException("Invalid Azure CLI tenant or audience.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
            foreach (string arg in new[] { "/d", "/c", "az" })
                start.ArgumentList.Add(arg);
        foreach (string arg in new[] { "account", "get-access-token", "--resource", resource, "--tenant", tenant, "--output", "json" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
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
