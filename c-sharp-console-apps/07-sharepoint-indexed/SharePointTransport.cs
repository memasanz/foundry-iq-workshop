using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed class SharePointTransport(HttpClient client, SharePointSettings settings,
    Func<string, Task<string>>? tokenProvider = null)
{
    public const string Audience = "https://search.azure.com";
    readonly Func<string, Task<string>> getToken = tokenProvider ?? (resource => AzureCliTokenAsync(settings.Tenant, resource));
    readonly HashSet<string> knownTokens = new(StringComparer.Ordinal);

    public async Task<JsonObject?> RequestAsync(HttpMethod method, string path, JsonObject? body = null,
        bool asUser = false, bool allowMissing = false)
    {
        string token = await getToken(Audience);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new InvalidOperationException("Azure CLI returned no usable Search token.");
        knownTokens.Add(token);
        if (asUser) RequireDelegatedUser(token, settings.Tenant);
        using var request = new HttpRequestMessage(method, settings.SearchEndpoint + "/" + path +
            "?api-version=" + SharePointSettings.ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Both headers identify the same local CLI user; no app-only or elevated-read substitution.
        if (asUser) request.Headers.Add("x-ms-query-source-authorization", token);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        string text = Redact(await response.Content.ReadAsStringAsync());
        int status = (int)response.StatusCode;
        if (allowMissing && status == 404) return null;
        bool accepted = method == HttpMethod.Get ? status == 200 :
            method == HttpMethod.Put ? status is 200 or 201 or 204 :
            method == HttpMethod.Delete ? status is 200 or 204 :
            path.EndsWith("/docs/search", StringComparison.Ordinal) ? status == 200 : status is 200 or 202 or 204;
        if (!accepted)
        {
            string message = $"HTTP {status} {response.ReasonPhrase}: {method} {path}.";
            Console.Error.WriteLine(message);
            if (response.Headers.Date != null) Console.Error.WriteLine("Date (UTC): " + response.Headers.Date.Value.UtcDateTime.ToString("O"));
            foreach (string header in new[] { "x-ms-request-id", "request-id", "x-ms-correlation-request-id", "retry-after" })
                if (response.Headers.TryGetValues(header, out var values))
                    Console.Error.WriteLine(header + ": " + Redact(string.Join(", ", values)));
            Console.Error.WriteLine("Response body: " + (text.Length == 0 ? "(empty)" : text.Substring(0, Math.Min(2000, text.Length))));
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        if (status is 202 or 204) return new JsonObject();
        try
        {
            return JsonNode.Parse(text)?.AsObject() ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"HTTP {status}: {method} {path} returned invalid JSON.");
        }
    }

    public string Redact(string text)
    {
        foreach (string token in knownTokens) text = text.Replace(token, "[REDACTED]", StringComparison.Ordinal);
        return Regex.Replace(text, @"(?i)\bBearer\s+[^\s""<>]+", "Bearer [REDACTED]");
    }

    public static void RequireDelegatedUser(string token, string tenant)
    {
        try
        {
            string[] pieces = token.Split('.');
            if (pieces.Length != 3) throw new FormatException();
            string payload = pieces[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            JsonObject claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))!.AsObject();
            if (claims["idtyp"]?.ToString() == "app" || string.IsNullOrWhiteSpace(claims["scp"]?.ToString()) ||
                !Guid.TryParse(claims["oid"]?.ToString(), out _) ||
                !Guid.TryParse(claims["tid"]?.ToString(), out var actualTenant) ||
                actualTenant != Guid.Parse(tenant) ||
                claims["aud"]?.ToString().TrimEnd('/') != Audience)
                throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or NullReferenceException)
        {
            throw new InvalidOperationException("Query requires a delegated Search token for a signed-in member user in AZURE_TENANT_ID. Sign out/in with Azure CLI; do not use an application-only token.");
        }
        // This is a local claim sanity check, not signature validation; Search enforces authorization.
    }

    public static async Task<string> AzureCliTokenAsync(string tenant, string resource)
    {
        if (!Guid.TryParseExact(tenant, "D", out _) || resource != Audience)
            throw new InvalidOperationException("Invalid Azure CLI tenant or token audience.");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "az",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
            foreach (string argument in new[] { "/d", "/c", "az" }) start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "account", "get-access-token", "--resource", resource, "--tenant", tenant,
            "--query", "accessToken", "--output", "tsv", "--only-show-errors" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
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
        return (await output).Trim();
    }
}
