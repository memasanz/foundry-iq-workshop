using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed record RemoteSettings(string Tenant, string SearchEndpoint, string SiteUrl, string Prefix)
{
    public string SourceName => Prefix + "-sp-remote-source";
    public string BaseName => Prefix + "-sp-remote-kb";

    public static RemoteSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name);
        var settings = new RemoteSettings(Required("AZURE_TENANT_ID"),
            Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'), Required("SHAREPOINT_SITE_URL").TrimEnd('/'),
            Required("WORKSHOP_PREFIX"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"^[a-z][a-z0-9]{2,19}$"))
            throw new InvalidOperationException("Use a tenant GUID and a 3-20 lowercase letter/digit prefix starting with a letter.");
        if (!SafeHttps(SearchEndpoint, out var search) ||
            !Regex.IsMatch(search!.Host, @"^[a-z0-9][a-z0-9-]*\.search\.windows\.net$", RegexOptions.IgnoreCase) ||
            search.AbsolutePath != "/")
            throw new InvalidOperationException("Use a public Azure HTTPS Search service root, without a path/query.");
        if (!SafeHttps(SiteUrl, out var site) ||
            !Regex.IsMatch(site!.Host, @"^[a-z0-9][a-z0-9-]*\.sharepoint\.com$", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(Uri.UnescapeDataString(site.AbsolutePath).TrimEnd('/'),
                @"^$|^/(sites|teams)/[a-zA-Z0-9 _.-]+$", RegexOptions.IgnoreCase) ||
            Uri.UnescapeDataString(site.AbsolutePath).Split('/').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Use a public-cloud SharePoint HTTPS site root or /sites/name or /teams/name, not a file or sharing-link URL.");
    }

    static bool SafeHttps(string value, out Uri? uri)
    {
        uri = null;
        if (value.Any(c => char.IsControl(c) || c is ';' or '"' or '\\') ||
            Uri.UnescapeDataString(value).Any(c => char.IsControl(c) || c is ';' or '"' or '\\'))
            return false;
        return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
            uri.UserInfo == "" && uri.IsDefaultPort && uri.Query == "" && uri.Fragment == "";
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

public sealed class RemoteTransport(HttpClient client, string tenant, Func<Task<string>>? tokenProvider = null)
{
    public const string Audience = "https://search.azure.com/";
    public const string ApiVersion = "2026-08-01-preview";
    readonly Func<Task<string>> getToken = tokenProvider ?? (() => AzureCliUserTokenAsync(tenant));

    public async Task<JsonObject> SendAsync(RemoteSettings settings, HttpMethod method, string path,
        JsonObject? body = null, bool queryUser = false, bool allowMissing = false)
    {
        settings.Validate();
        using var request = new HttpRequestMessage(method, $"{settings.SearchEndpoint}/{path}?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken());
        // Python08 sends the raw user token here, NOT another "Bearer " prefix.
        if (queryUser) request.Headers.Add("x-ms-query-source-authorization", await getToken());
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        int status = (int)response.StatusCode;
        if (allowMissing && status == 404) return new JsonObject();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Remote request failed. HTTP: {status} {response.ReasonPhrase}");
            Console.Error.WriteLine("UTC (Date): " + response.Headers.Date?.ToString("R"));
            foreach (var header in response.Headers)
                if (header.Key.Contains("request", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"{header.Key}: {string.Join(", ", header.Value)}");
            Console.Error.WriteLine("Response body: " + text[..Math.Min(8000, text.Length)]);
            throw new HttpRequestException($"HTTP {status}: remote request failed; inspect UTC/correlation details and response body above.", null, response.StatusCode);
        }
        JsonObject result = string.IsNullOrWhiteSpace(text) && method != HttpMethod.Post
            ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
        if (method == HttpMethod.Post)
        {
            Console.WriteLine(result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (status == 206 || result["activity"] is JsonArray activity && activity.Any(item => item?["error"] is not null))
                throw new InvalidOperationException("Retrieval was partial or a source failed. Inspect the activity output above.");
            RemoteResults.VerifyUrls(result, settings.SiteUrl);
            if (result["references"] is not JsonArray references || references.Count == 0)
                Console.WriteLine("No source references returned. Check query, source permissions, M365 indexing and eligibility.");
        }
        return result;
    }

    public static async Task<string> AzureCliUserTokenAsync(string tenant)
    {
        if (!Guid.TryParseExact(tenant, "D", out _)) throw new InvalidOperationException("Invalid tenant GUID.");
        JsonObject account = JsonNode.Parse(await RunCliAsync(["account", "show", "--output", "json"]))!.AsObject();
        if (account["user"]?["type"]?.GetValue<string>() != "user" ||
            !string.Equals(account["tenantId"]?.GetValue<string>(), tenant, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sign in as a USER with az login --tenant AZURE_TENANT_ID; service principals/managed identities are not this lab's query identity.");
        JsonObject token = JsonNode.Parse(await RunCliAsync(
            ["account", "get-access-token", "--resource", Audience, "--tenant", tenant, "--output", "json"]))!.AsObject();
        return token["accessToken"]?.GetValue<string>() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Azure CLI returned no access token.");
    }

    static async Task<string> RunCliAsync(string[] arguments)
    {
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
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
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
            throw new InvalidOperationException("Azure CLI authentication timed out. Sign in to your tenant.");
        }
        await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Azure CLI authentication failed. Run az login --tenant with AZURE_TENANT_ID.");
        return await output;
    }
}

public static class RemoteResults
{
    public static void VerifyUrls(JsonNode? node, string siteUrl)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.Equals("webUrl", StringComparison.OrdinalIgnoreCase) && property.Value is not null)
                {
                    string url = property.Value.GetValue<string>();
                    var site = new Uri(siteUrl);
                    string path = Uri.UnescapeDataString(site.AbsolutePath).TrimEnd('/');
                    bool allowed = Uri.TryCreate(url, UriKind.Absolute, out var result) &&
                        result.Scheme == "https" && result.UserInfo == "" && result.IsDefaultPort &&
                        result.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase) &&
                        (Uri.UnescapeDataString(result.AbsolutePath).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                         Uri.UnescapeDataString(result.AbsolutePath).StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine((allowed ? "Verified site webUrl: " : "WARNING: webUrl outside selected site: ") + url);
                    if (!allowed)
                        throw new InvalidOperationException("Returned webUrl is outside the selected site. Path KQL is not an authorization boundary; inspect the output.");
                }
                else VerifyUrls(property.Value, siteUrl);
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode? item in array) VerifyUrls(item, siteUrl);
    }
}
