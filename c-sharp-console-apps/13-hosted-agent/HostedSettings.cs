#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

public sealed record HostedSettings(string Tenant, string ProjectEndpoint, string Model, string Prefix)
{
    public string AgentName => Prefix + "-hosted-agent";
    public string Folder(string root) => Path.Combine(Path.GetFullPath(root), Prefix + "-csharp-hosted-agent");

    public static HostedSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name + ". See .env.example.");
        var settings = new HostedSettings(Required("AZURE_TENANT_ID"),
            Required("AZURE_AI_PROJECT_ENDPOINT").TrimEnd('/'),
            Required("AZURE_AI_MODEL_DEPLOYMENT_NAME"), Required("WORKSHOP_PREFIX"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _))
            throw new InvalidOperationException("AZURE_TENANT_ID must be a tenant GUID.");
        if (!Regex.IsMatch(Prefix, @"\A[a-z][a-z0-9]{2,19}\z"))
            throw new InvalidOperationException("Use 3-20 lowercase letters/digits for WORKSHOP_PREFIX, starting with a letter.");
        if (!Regex.IsMatch(ProjectEndpoint,
            @"\Ahttps://[a-z0-9][a-z0-9-]*\.services\.ai\.azure\.com/api/projects/[a-zA-Z0-9._-]+\z"))
            throw new InvalidOperationException("Use a public-cloud HTTPS Foundry project endpoint, without a query, fragment or credentials.");
        if (string.IsNullOrWhiteSpace(Model) || Model.Any(char.IsControl))
            throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME must be a non-empty deployment name.");
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
            string name = line.Substring(0, equals).Trim();
            if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")) continue;
            string value = line.Substring(equals + 1).Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                (value[0] == '\'' && value[value.Length - 1] == '\'')))
                value = value.Substring(1, value.Length - 2);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public async Task<string> GetTokenAsync()
    {
        Validate();
        const string command = "az account get-access-token --resource https://ai.azure.com " +
            "--query accessToken --output tsv --only-show-errors --tenant ";
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
            start.ArgumentList.Add(command + Tenant);
        }
        else
        {
            foreach (string argument in (command.Substring(3) + Tenant).Split(' '))
                start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Azure CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
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
