using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class ApimSettings(string tenant, string searchEndpoint, string brokerId, string gateway, string prefix,
    string webIqSourceName = "iq-workshop-webiq")
{
    public string Tenant { get; } = NormalizeGuid(tenant);
    public string SearchEndpoint { get; } = searchEndpoint;
    public string BrokerId { get; } = NormalizeGuid(brokerId);
    public string Gateway { get; } = gateway;
    public string Prefix { get; } = prefix;
    public string WebIqSourceName { get; } = webIqSourceName;
    public string BlobSourceName => Prefix + "-kb-blob";
    public string SharePointSourceName => Prefix + "-kb-sharepoint";
    public string BaseName => Prefix + "-kb";
    public string ApimBaseName => Prefix + "-apim-kb";
    public string BrokerResource => "api://" + BrokerId;
    public string BrokerScope => BrokerResource + "/.default";
    static string NormalizeGuid(string value) => Guid.TryParseExact(value, "D", out var id) ? id.ToString("D") : value;

    public static ApimSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name + ". Complete gateway preparation; see .env.example.");
        var settings = new ApimSettings(Required("AZURE_TENANT_ID"), Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'),
            Required("BROKER_APP_CLIENT_ID"), Required("APIM_GATEWAY_URL").TrimEnd('/'), Required("WORKSHOP_PREFIX"),
            Environment.GetEnvironmentVariable("WEBIQ_APIM_SOURCE_NAME")?.Trim() ?? "iq-workshop-webiq");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Guid.TryParseExact(BrokerId, "D", out _) ||
            !Regex.IsMatch(Prefix, @"^[a-z][a-z0-9]{2,19}$") ||
            !Regex.IsMatch(WebIqSourceName, @"^[a-z][a-z0-9-]{1,127}$"))
            throw new InvalidOperationException("Use tenant/broker GUIDs, a 3-20 lowercase letters/digits prefix starting with a letter, and a valid shared source name.");
        bool Root(string value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            uri.Scheme == "https" && uri.UserInfo == "" && uri.IsDefaultPort && uri.AbsolutePath == "/" &&
            uri.Query == "" && uri.Fragment == "";
        if (!Root(SearchEndpoint, out var search) ||
            !Regex.IsMatch(search!.Host, @"^[a-z0-9][a-z0-9-]*\.search\.windows\.net$", RegexOptions.IgnoreCase) ||
            !Root(Gateway, out _))
            throw new InvalidOperationException("Use HTTPS Search and APIM service roots without credentials, ports, paths or queries.");
    }

    // Match the preceding labs: deployment dotenv values override .env and process values.
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
            string name = setting[..equals].Trim(), value = setting[(equals + 1)..].Trim();
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

public sealed class ApimTransport(HttpClient client, ApimSettings settings,
    Func<Task<string>>? searchTokenProvider = null, Func<Task<string>>? brokerTokenProvider = null)
{
    public const string ApiVersion = "2026-08-01-preview";
    readonly Func<Task<string>> searchToken = searchTokenProvider ?? (() => AzureCliTokenAsync(settings.Tenant, "https://search.azure.com"));
    readonly Func<Task<string>> brokerToken = brokerTokenProvider ?? (() => AzureCliTokenAsync(settings.Tenant, settings.BrokerResource));

    // Decoding is inspection, not signature/issuer validation. APIM validate-jwt is the enforcement boundary.
    public static JsonObject InspectToken(string token, ApimSettings settings)
    {
        JsonObject claims;
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException();
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))?.AsObject()
                ?? throw new JsonException();
            string? audience = claims["aud"]?.GetValue<string>();
            if (audience != settings.BrokerId && audience != settings.BrokerResource)
                throw new InvalidOperationException("Token is not for the broker app. Request api://<broker-client-id>/.default (SDK) or --resource api://<broker-client-id> (CLI).");
            if (claims["tid"]?.GetValue<string>() != settings.Tenant)
                throw new InvalidOperationException("Broker token tenant differs from AZURE_TENANT_ID.");
            if (!(claims["scp"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("user_impersonation"))
                throw new InvalidOperationException("Broker token is missing delegated user_impersonation. Check exposed scope, Azure CLI pre-authorization and consent.");
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new InvalidOperationException("Broker token cannot be decoded for inspection; no raw token is printed.");
        }
        foreach (string field in new[] { "aud", "tid", "scp", "preferred_username", "upn", "oid", "azp", "appid" })
            if (claims[field] is JsonNode value) Console.WriteLine(field + ": " + ApimResults.Redact(value.ToString(), token));
        Console.WriteLine("Claims decoded for inspection only; APIM performs cryptographic validation.");
        return claims;
    }

    public async Task InspectTokenAsync() => InspectToken(await brokerToken(), settings);

    public async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body = null,
        bool gateway = false, bool allowMissing = false, bool diagnostics = false, int timeoutSeconds = 90)
    {
        settings.Validate();
        bool retrieve = path.EndsWith("/retrieve", StringComparison.Ordinal);
        if (retrieve != gateway || (gateway && method != HttpMethod.Post) ||
            (!gateway && !path.StartsWith("knowledgesources/", StringComparison.Ordinal) && !path.StartsWith("knowledgebases/", StringComparison.Ordinal)))
            throw new ArgumentException("Setup uses direct Search; retrieval uses only APIM.");
        if ((method == HttpMethod.Put || method == HttpMethod.Delete) && path != $"knowledgebases/{settings.ApimBaseName}")
            throw new ArgumentException("Only this lab's APIM knowledge base may be changed.");
        string token;
        try
        {
            token = await (gateway ? brokerToken() : searchToken());
            if (gateway) InspectToken(token, settings);
        }
        catch (Exception error) { throw new InvalidOperationException(ApimResults.Redact(error.Message)); }
        string Safe(string value) => ApimResults.Redact(value, token);
        using var request = new HttpRequestMessage(method,
            $"{(gateway ? settings.Gateway + "/iq" : settings.SearchEndpoint)}/{path}?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            string text = await response.Content.ReadAsStringAsync(timeout.Token);
            int status = (int)response.StatusCode;
            Console.WriteLine($"HTTP status: {status}");
            foreach (var header in response.Headers)
                if (header.Key.Contains("request", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("retry-after", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine(header.Key + ": " + Safe(string.Join(", ", header.Value)));
            if (status == 404 && allowMissing) return new JsonObject();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"HTTP {status}; UTC (Date): {response.Headers.Date:R}");
                Console.Error.WriteLine(status switch
                {
                    401 => "Check broker audience/scope, deployment tenant, pre-authorization and downstream Search delegated consent. No automatic re-login.",
                    403 when gateway => "Verify APIM system-assigned managed identity has Search Index Data Reader on Search; then check user document ACLs/network access.",
                    403 => "Setup needs direct user Search authoring/data roles; gateway access does not grant these.",
                    404 => "Complete lab11 with the same prefix and keep its resources; prepare the shared source with setup-apim-policy.ps1.",
                    429 => "Throttling/quota. Inspect Retry-After; no automatic billable retry.",
                    _ => "Inspect request IDs and redacted response details."
                });
                string safe = Safe(text);
                Console.Error.WriteLine("Response body: " + safe[..Math.Min(8000, safe.Length)]);
                throw new HttpRequestException($"HTTP {status}: request failed; inspect redacted diagnostics above.", null, response.StatusCode);
            }
            JsonObject result;
            try
            {
                result = string.IsNullOrWhiteSpace(text) && method != HttpMethod.Post ? new JsonObject()
                    : JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException) { throw new InvalidOperationException("Invalid JSON response; no complete result available."); }
            // Never echo a shared source definition: it may contain accidentally stored credentials.
            if (gateway) ApimResults.Print(result, status, diagnostics, token);
            else if (status == 206) throw new InvalidOperationException("Partial setup response; no complete result available.");
            return result;
        }
        catch (HttpRequestException error) { throw new HttpRequestException(Safe(error.Message), null, error.StatusCode); }
        catch (OperationCanceledException) { throw new TimeoutException("Request timed out; no complete result and no automatic retry."); }
    }

    // Uses the existing az login user, like AzureCliCredential; /.default becomes the resource, not /user_impersonation.
    public static ProcessStartInfo TokenStartInfo(string tenant, string resource)
    {
        if (!Guid.TryParseExact(tenant, "D", out _) ||
            (resource != "https://search.azure.com" && (!resource.StartsWith("api://", StringComparison.Ordinal) ||
                !Guid.TryParseExact(resource[6..], "D", out _))))
            throw new ArgumentException("Use a tenant GUID and Search or broker API resource, without a permission suffix.");
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
        foreach (string argument in new[] { "account", "get-access-token", "--resource", resource, "--tenant", tenant, "--output", "json" })
            start.ArgumentList.Add(argument);
        return start;
    }

    public static async Task<string> AzureCliTokenAsync(string tenant, string resource)
    {
        using var process = Process.Start(TokenStartInfo(tenant, resource)) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw new InvalidOperationException("Azure CLI authentication timed out. Check az login --tenant AZURE_TENANT_ID separately.");
        }
        await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Azure CLI authentication failed. Check tenant, broker exposed scope/pre-authorization/consent. No automatic re-login.");
        try
        {
            return JsonNode.Parse(await output)?["accessToken"]?.GetValue<string>() is { Length: > 0 } token
                ? token : throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new InvalidOperationException("Azure CLI returned no usable token."); }
    }

    // Intentional negative probes from Python12; they never print tokens or response bodies.
    public async Task CheckRejectionsAsync()
    {
        settings.Validate();
        string token;
        try { token = await searchToken(); }
        catch (Exception error) { throw new InvalidOperationException(ApimResults.Redact(error.Message)); }
        foreach (string? credential in new string?[] { null, token })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{settings.Gateway}/iq/knowledgebases/{settings.ApimBaseName}/retrieve?api-version={ApiVersion}");
            if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Content = new StringContent("""{"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}]}""",
                Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                using var response = await client.SendAsync(request, timeout.Token);
                Console.WriteLine((credential is null ? "No token: " : "Search token (wrong audience): ") + (int)response.StatusCode);
                if ((int)response.StatusCode != 401) throw new InvalidOperationException("Gateway must reject both probes with 401. Check validate-jwt policy.");
            }
            catch (HttpRequestException error) { throw new HttpRequestException(ApimResults.Redact(error.Message, token)); }
            catch (OperationCanceledException) { throw new TimeoutException("Gateway rejection probe timed out."); }
        }
    }
}

public static class ApimResults
{
    static IEnumerable<string> CredentialValues(JsonNode? node, int depth = 0)
    {
        if (depth > 8) yield break;
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (Regex.IsMatch(property.Key, @"(?i)^(?:key|secret|token|x-apikey|(?:webiq[-_]?)?api[-_]?key|(?:broker[-_]?)?client[-_]?secret|access[-_]?token|refresh[-_]?token|password|authorization|.+-header-value)$") &&
                    property.Value is JsonValue value && value.TryGetValue<string>(out string? secret) && !string.IsNullOrEmpty(secret))
                    yield return secret;
                foreach (string nested in CredentialValues(property.Value, depth + 1)) yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                foreach (string nested in CredentialValues(item, depth + 1)) yield return nested;
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            JsonNode? parsed = null;
            try { parsed = JsonNode.Parse(text); } catch (JsonException) { }
            foreach (string nested in CredentialValues(parsed, depth + 1)) yield return nested;
        }
    }

    public static string Redact(string text, params string[] secrets)
    {
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(text); } catch (JsonException) { }
        var configured = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(e => Regex.IsMatch(e.Key.ToString()!, @"(?i)(key|secret|token|password)"))
            .Select(e => e.Value?.ToString() ?? "");
        foreach (string secret in secrets.Concat(configured).Concat(CredentialValues(parsed)).Where(s => !string.IsNullOrEmpty(s)).Distinct())
        {
            text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
            string escaped = secret;
            for (int level = 0; level < 3; level++)
            {
                escaped = JsonSerializer.Serialize(escaped)[1..^1];
                text = text.Replace(escaped, "<redacted>", StringComparison.Ordinal);
            }
            text = text.Replace(Uri.EscapeDataString(secret), "<redacted>", StringComparison.Ordinal);
        }
        // Also protect unconfigured echoed bearer/JWT credentials and secret-valued JSON/header/query fields.
        text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer <redacted>");
        text = Regex.Replace(text, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*", "<redacted>");
        text = Regex.Replace(text,
            """(?i)("?(?:x-apikey|api[-_]?key|webiq[-_]?api[-_]?key|broker[-_]?client[-_]?secret|client[-_]?secret|access[-_]?token|refresh[-_]?token|authorization|[a-z0-9-]+-header-value)"?\s*[:=]\s*)("(?:\\.|[^"\\])*"|[^\s,;&}]+)""",
            "$1\"<redacted>\"");
        text = Regex.Replace(text, @"(?i)([?&](?:sig|token|key|code|client_secret|access_token)=)[^&\s""<>]+", "$1<redacted>");
        return text;
    }

    public static void ValidateQuery(string question, string outputMode)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Use a non-empty question.");
        if (outputMode is not ("answerSynthesis" or "extractiveData"))
            throw new ArgumentException("Use answerSynthesis or extractiveData.");
    }

    public static JsonObject ObjectData(JsonNode? node)
    {
        if (node is JsonObject obj) return (JsonObject)obj.DeepClone();
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { }
        }
        return new JsonObject();
    }

    public static string FormatReference(JsonObject reference)
    {
        var data = ObjectData(reference["sourceData"]);
        for (int depth = 0; depth < 3; depth++)
        {
            var content = ObjectData(data["content"]);
            if (content.Count == 0) break;
            data.Remove("content");
            foreach (var property in content) data[property.Key] = property.Value?.DeepClone();
        }
        var source = ObjectData(data["source"]);
        string First(params JsonNode?[] values) => values.Select(v => v?.ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        string title = First(data["source/file_name"], source["file_name"], data["sourceFile"], data["title"], reference["docKey"]);
        if (title.Length == 0) title = "Source";
        string url = First(data["source/url"], source["url"], data["webUrl"], data["url"]);
        JsonNode? firstNode = data.ContainsKey("page_from") ? data["page_from"] : data["page"];
        JsonNode? lastNode = data.ContainsKey("page_to") ? data["page_to"] : data["pageTo"];
        string pages = "";
        if (firstNode is JsonValue firstValue && firstValue.TryGetValue<int>(out int first) &&
            lastNode is JsonValue lastValue && lastValue.TryGetValue<int>(out int last) && first >= 1 && first <= last)
        {
            pages = first == last ? $"p. {first}" : $"pp. {first}-{last}";
            if (url.Length > 0) url = url.Split('#')[0] + $"#page={first}";
        }
        return $"- [ref_id:{reference["id"]}] {title} {pages} {(url.Length > 0 ? url : "(No source URL returned.)")}";
    }

    public static void Print(JsonObject result, int status, bool diagnostics = false, params string[] secrets)
    {
        secrets = secrets.Concat(CredentialValues(result)).ToArray();
        void Write(string value) => Console.WriteLine(Redact(value, secrets));
        if (diagnostics)
        {
            Console.WriteLine("Raw activity:");
            Write((result["activity"] ?? new JsonArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Raw references:");
            Write((result["references"] ?? new JsonArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        if (result["activity"] is not JsonArray activity || activity.Any(s => s is not JsonObject) ||
            result["response"] is not JsonArray messages || messages.Count == 0 ||
            messages.Any(m => m is not JsonObject || m["content"] is not JsonArray content || content.Count == 0 ||
                content.Any(p => p is not JsonObject || p["text"] is not JsonValue text || !text.TryGetValue<string>(out _))) ||
            result["references"] is not null && (result["references"] is not JsonArray refs || refs.Any(r => r is not JsonObject)))
            throw new InvalidOperationException("Malformed retrieval response; no complete result available.");
        Console.WriteLine("\nAnswer or extractive output:");
        foreach (JsonObject message in messages.OfType<JsonObject>())
            foreach (JsonObject part in message["content"]!.AsArray().OfType<JsonObject>()) Write(part["text"]!.ToString());
        Console.WriteLine("\nReferences:");
        foreach (JsonObject reference in (result["references"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            Write(FormatReference(reference));
        Console.WriteLine("\nActivity:");
        foreach (JsonObject step in activity.OfType<JsonObject>())
            Write($"- {step["type"]} {step["knowledgeSourceName"]} {step["count"]} {step["error"]}");
        if (status == 206 || activity.OfType<JsonObject>().Any(s =>
            s["error"] is JsonNode error && error.ToJsonString() is not ("null" or "{}" or "[]" or "\"\"" or "false" or "0")))
            throw new InvalidOperationException("Retrieval was partial or a source failed. Inspect activity; --diagnostics shows the same response, without another request.");
        if (result["references"] is not JsonArray references || references.Count == 0)
            Console.WriteLine("No references returned. This alone does not establish an authentication failure.");
    }
}
