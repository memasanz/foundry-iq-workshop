using System;
using System.IO;
using System.Text.RegularExpressions;

public sealed record SharePointSettings(string Root, string Tenant, string SearchEndpoint, string SiteUrl,
    string Account, string Deployment, string Model, string Prefix, string AppClientId, string SearchMiClientId)
{
    public const string ApiVersion = "2026-08-01-preview";
    public const int Dimensions = 1536;
    public string Index => Prefix + "-nb7-index";
    public string DataSource => Prefix + "-nb7-ds";
    public string Skills => Prefix + "-nb7-skillset";
    public string Indexer => Prefix + "-nb7-indexer";
    public string FoundryEndpoint => "https://" + Account + ".services.ai.azure.com";
    public string EmbeddingEndpoint => "https://" + Account + ".openai.azure.com";
    public string StatePath => Path.Combine(Root, ".workshop", "lab07-" + Prefix + ".json");

    public static SharePointSettings Load(string root)
    {
        LoadEnvFile(Path.Combine(root, ".env"));
        LoadEnvFile(Path.Combine(root, ".env.azure"));
        string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing setting: " + name + ". See .env.example.");
        string Optional(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
        var settings = new SharePointSettings(Path.GetFullPath(root), Required("AZURE_TENANT_ID"),
            Required("AZURE_SEARCH_ENDPOINT").TrimEnd('/'), Required("SHAREPOINT_SITE_URL").TrimEnd('/'),
            Required("AZURE_AI_ACCOUNT_NAME"), Required("AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"),
            Optional("AZURE_AI_EMBEDDING_MODEL_NAME") is { Length: > 0 } model ? model : "text-embedding-3-small",
            Required("WORKSHOP_PREFIX"), Optional("SHAREPOINT_APP_CLIENT_ID"), Optional("SHAREPOINT_SEARCH_MI_CLIENT_ID"));
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Guid.TryParseExact(Tenant, "D", out _) || !Regex.IsMatch(Prefix, @"\A[a-z][a-z0-9]{2,19}\z"))
            throw new InvalidOperationException("Use a tenant GUID and a 3-20 lowercase letter/digit prefix starting with a letter.");
        if (!Regex.IsMatch(SearchEndpoint, @"\Ahttps://[a-z0-9][a-z0-9-]*\.search\.windows\.net\z"))
            throw new InvalidOperationException("Use a public-cloud HTTPS Search endpoint without path, query or credentials.");
        if (!Uri.TryCreate(SiteUrl, UriKind.Absolute, out var site) || site.Scheme != "https" ||
            !site.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || !site.IsDefaultPort ||
            site.UserInfo != "" || site.Query != "" || site.Fragment != "" ||
            SiteUrl.IndexOfAny(new[] { ';', '"', '\r', '\n', '\\' }) >= 0)
            throw new InvalidOperationException("Use a public-cloud SharePoint HTTPS site URL, not a file or sharing-link URL.");
        foreach (string value in new[] { Account, Deployment, Model })
            if (!Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z"))
                throw new InvalidOperationException("Use valid Foundry account, embedding deployment and model names.");
        if (!Regex.IsMatch(Account, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,62}\z"))
            throw new InvalidOperationException("Use a Foundry account name, not an endpoint.");
    }

    public string ConnectionString()
    {
        if (!Guid.TryParseExact(AppClientId, "D", out var app) || !Guid.TryParseExact(SearchMiClientId, "D", out var mi))
            throw new InvalidOperationException("Set SHAREPOINT_APP_CLIENT_ID and SHAREPOINT_SEARCH_MI_CLIENT_ID to client/application GUIDs after the existing setup script completes.");
        if (app == mi) throw new InvalidOperationException("The ingestion app and Search managed identity must have different client IDs.");
        return $"SharePointOnlineEndpoint={SiteUrl};ApplicationId={app};FederatedCredentialApplicationId={mi};TenantId={Tenant}";
    }

    public static void ConfirmSetup(bool confirmed)
    {
        if (!confirmed)
            throw new InvalidOperationException("Review app consent, selected-site Read grant and Search identity federation, then explicitly use --setup-confirmed. This acknowledges review, not an automated permissions test.");
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
}
