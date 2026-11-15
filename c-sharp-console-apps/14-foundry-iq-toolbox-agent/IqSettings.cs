#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public sealed record IqSettings(string Tenant, string SearchEndpoint, string SearchResourceId,
    string ProjectEndpoint, string ProjectResourceId, string Model, string Prefix)
{
    public const string SearchApi = "2026-08-01-preview";
    public const string ConnectionApi = "2025-04-01-preview";
    public string BlobSource => Prefix + "-kb-blob";
    public string SharedBase => Prefix + "-kb";
    public string BaseName => Prefix + "-toolbox-kb";
    public string ConnectionName => Prefix + "-iq-search-mcp";
    public string ConnectionId => ProjectResourceId + "/connections/" + ConnectionName;
    public string ToolboxName => Prefix + "-iq-toolbox";
    public string AgentName => Prefix + "-iq-agent";
    public string McpUrl => SearchEndpoint + "/knowledgebases/" + BaseName + "/mcp?api-version=" + SearchApi;
    public string Folder(string root) => Path.Combine(Path.GetFullPath(root), Prefix + "-csharp-hosted-agent-iq");

    public static IqSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name + ". See .env.example.");
        var settings = new IqSettings(Required("AZURE_TENANT_ID"), Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'),
            Required("AZURE_SEARCH_RESOURCE_ID").TrimEnd('/'), Required("AZURE_AI_PROJECT_ENDPOINT").TrimEnd('/'),
            Required("AZURE_AI_PROJECT_RESOURCE_ID").TrimEnd('/'), Required("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
            Required("WORKSHOP_PREFIX"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"\A[a-z][a-z0-9]{2,19}\z"))
            throw new InvalidOperationException("Use a tenant GUID and a 3-20 lowercase letter/digit prefix starting with a letter.");
        Match search = Regex.Match(SearchEndpoint, @"\Ahttps://(?<name>[a-z0-9][a-z0-9-]*)\.search\.windows\.net\z");
        Match project = Regex.Match(ProjectEndpoint,
            @"\Ahttps://(?<account>[a-z0-9][a-z0-9-]*)\.services\.ai\.azure\.com/api/projects/(?<project>[a-zA-Z0-9._-]+)\z");
        const string scope = @"/subscriptions/(?<subscription>[0-9a-fA-F-]{36})/resourceGroups/[a-zA-Z0-9_().-]+/providers/";
        Match searchId = Regex.Match(SearchResourceId, @"\A" + scope + @"Microsoft\.Search/searchServices/(?<name>[a-z0-9-]+)\z", RegexOptions.IgnoreCase);
        Match projectId = Regex.Match(ProjectResourceId, @"\A" + scope +
            @"Microsoft\.CognitiveServices/accounts/(?<account>[a-z0-9-]+)/projects/(?<project>[a-zA-Z0-9._-]+)\z", RegexOptions.IgnoreCase);
        if (!search.Success || !project.Success || !searchId.Success || !projectId.Success ||
            !Guid.TryParseExact(searchId.Groups["subscription"].Value, "D", out _) ||
            !Guid.TryParseExact(projectId.Groups["subscription"].Value, "D", out _) ||
            !string.Equals(search.Groups["name"].Value, searchId.Groups["name"].Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.Groups["account"].Value, projectId.Groups["account"].Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.Groups["project"].Value, projectId.Groups["project"].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use matching public-cloud HTTPS Search/project endpoints and their full ARM resource IDs, without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsControl))
            throw new InvalidOperationException("Use a non-empty model deployment name.");
    }

    public static void LoadEnvFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line.Substring(7).TrimStart();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string name = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
            if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")) continue;
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                (value[0] == '\'' && value[value.Length - 1] == '\'')))
                value = value.Substring(1, value.Length - 2);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public async Task<string> GetTokenAsync(string resource)
    {
        Validate();
        if (resource is not ("https://ai.azure.com" or "https://search.azure.com" or "https://management.azure.com"))
            throw new ArgumentException("Unexpected token audience.");
        string command = "az account get-access-token --resource " + resource + " --tenant " + Tenant +
            " --query accessToken --output tsv --only-show-errors";
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "az",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command);
        }
        else foreach (string argument in command.Substring(3).Split(' ')) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Azure CLI token acquisition timed out.");
        }
        string token = (await output).Trim();
        await error;
        if (process.ExitCode != 0 || token.Length == 0 || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Azure CLI could not obtain a token. Sign in with az login in AZURE_TENANT_ID.");
        return token;
    }
}
