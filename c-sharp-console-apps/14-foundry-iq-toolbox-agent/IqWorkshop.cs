#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

public sealed record IqReply(int Status, JsonObject? Json);

public sealed partial class IqWorkshop
{
    public const string ReaderRole = "1407120a-92aa-4202-b7e9-c0e197c71c8f";
    public const string Question = "Using the SharePoint documents I can access, what are the main recommendations about sustainability and AI? Cite the documents.";
    public const string UnknownQuestion = "What is the capital of Australia?";
    private const string Features = "A2AAgents=V1Preview,WorkflowAgents=V1Preview,DraftAgents=V1Preview," +
        "VoiceAgents=V1Preview,DigitalWorker=V1Preview,GitHubCopilot=V1Preview,Skills=V1Preview," +
        "MicrosoftCopilot=V1Preview,ModelRouterControls=V1Preview";
    private readonly IqSettings settings;
    private readonly HttpClient client;
    private readonly Func<string, Task<string>> getToken;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, Task> delay;
    private readonly HashSet<string> knownTokens = new();

    public IqWorkshop(IqSettings settings, HttpClient client, Func<string, Task<string>>? getToken = null,
        Func<DateTimeOffset>? utcNow = null, Func<TimeSpan, Task>? delay = null)
    {
        settings.Validate();
        this.settings = settings;
        this.client = client;
        this.getToken = getToken ?? settings.GetTokenAsync;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? (duration => Task.Delay(duration));
    }

    public async Task<JsonObject> VerifySourcesAsync()
    {
        JsonObject source = await RequiredAsync(HttpMethod.Get, SearchUrl("knowledgesources", settings.SharePointSource),
            "https://search.azure.com", missing: "Complete lab 11 first and retain " + settings.SharePointSource + ".");
        if ((string?)source["name"] != settings.SharePointSource || (string?)source["kind"] != "searchIndex" ||
            (string?)source["searchIndexParameters"]?["searchIndexName"] != settings.SharePointIndex)
            throw new InvalidOperationException("The existing lab 11 SharePoint searchIndex source must target " + settings.SharePointIndex + ".");
        JsonObject index = await RequiredAsync(HttpMethod.Get, SearchUrl("indexes", settings.SharePointIndex),
            "https://search.azure.com", missing: "Complete lab 07 first and retain its permission-enabled SharePoint index.");
        ValidateSharePointIndex(index);
        JsonObject shared = await RequiredAsync(HttpMethod.Get, SearchUrl("knowledgebases", settings.SharedBase),
            "https://search.azure.com", missing: "Complete lab 11 first and retain " + settings.SharedBase + ".");
        if ((string?)shared["name"] != settings.SharedBase || shared["models"] is not JsonArray models || models.Count == 0)
            throw new InvalidOperationException("Lab 11 knowledge base is missing its model settings.");
        Console.WriteLine("Checked SharePoint source/index ACL schema and model settings from " + settings.SharedBase + ". No documents were queried.");
        return shared;
    }

    private void ValidateSharePointIndex(JsonObject index)
    {
        JsonArray fields = index["fields"] as JsonArray ?? new JsonArray();
        bool Permission(string name, string kind) => fields.OfType<JsonObject>().Any(field =>
            (string?)field["name"] == name && (string?)field["type"] == "Collection(Edm.String)" &&
            (string?)field["permissionFilter"] == kind && (bool?)field["filterable"] == true &&
            (bool?)field["retrievable"] == false);
        bool Retrievable(string name) => fields.OfType<JsonObject>().Any(field =>
            (string?)field["name"] == name && (bool?)field["retrievable"] != false);
        if ((string?)index["name"] != settings.SharePointIndex || (string?)index["permissionFilterOption"] != "enabled" ||
            !Permission("UserIds", "userIds") || !Permission("GroupIds", "groupIds") ||
            new[] { "id", "content", "sourceFile", "webUrl", "page", "pageTo" }.Any(name => !Retrievable(name)) ||
            !fields.OfType<JsonObject>().Any(field => (string?)field["name"] == "content" && (bool?)field["searchable"] == true) ||
            !(index["semantic"]?["configurations"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().Any(config => (string?)config["name"] == "semantic-config"))
            throw new InvalidOperationException("Lab 07 SharePoint index must retain its user/group ACL schema, retrievable source fields and semantic-config. No unfiltered fallback is used.");
    }

    public JsonObject KnowledgeBaseDefinition(JsonObject shared) => new JsonObject
    {
        ["name"] = settings.BaseName,
        ["description"] = "SharePoint documents with Entra user/group ACLs, for user-delegated hosted retrieval.",
        ["retrievalInstructions"] = "Use " + settings.SharePointSource + " for questions about SharePoint documents. Retrieve only evidence authorized for the signed-in user.",
        ["answerInstructions"] = shared["answerInstructions"]?.DeepClone(),
        ["outputMode"] = "answerSynthesis",
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = settings.SharePointSource }),
        ["models"] = shared["models"]?.DeepClone(),
        ["retrievalReasoningEffort"] = new JsonObject { ["kind"] = "low" },
    };

    public async Task<JsonObject> CreateKnowledgeBaseAsync()
    {
        JsonObject shared = await VerifySourcesAsync();
        JsonObject created = await RequiredAsync(HttpMethod.Put, SearchUrl("knowledgebases", settings.BaseName),
            "https://search.azure.com", KnowledgeBaseDefinition(shared), accepted: new[] { 200, 201 });
        ValidateKnowledgeBase(created);
        Console.WriteLine("Knowledge base ready: " + settings.BaseName + " | MCP: " + settings.McpUrl);
        return created;
    }

    public JsonObject ConnectionDefinition() => new JsonObject
    {
        ["properties"] = new JsonObject
        {
            ["category"] = "RemoteTool",
            ["authType"] = "ProjectManagedIdentity",
            ["target"] = settings.McpUrl,
            ["audience"] = "https://search.azure.com",
            ["isSharedToAll"] = true,
            ["metadata"] = new JsonObject { ["ApiType"] = "Azure" },
        },
    };

    public async Task<JsonObject> CreateConnectionAsync()
    {
        ValidateKnowledgeBase(await RequiredAsync(HttpMethod.Get, SearchUrl("knowledgebases", settings.BaseName),
            "https://search.azure.com", missing: "Run knowledge-base first."));
        JsonObject connection = await RequiredAsync(HttpMethod.Put, ConnectionUrl(), "https://management.azure.com",
            ConnectionDefinition(), accepted: new[] { 200, 201 });
        ValidateConnection(connection);
        Console.WriteLine("Connection ready: " + settings.ConnectionName + " | project service identity; Foundry supplies per-user source authorization.");
        return connection;
    }

    public JsonObject ToolboxDefinition() => new JsonObject
    {
        ["description"] = "Foundry IQ SharePoint knowledge base for user-delegated hosted retrieval.",
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "mcp",
            ["server_label"] = "knowledge_base",
            ["server_url"] = settings.McpUrl,
            ["require_approval"] = "never",
            ["project_connection_id"] = settings.ConnectionId,
        }),
    };

    public async Task<JsonObject> PublishToolboxAsync()
    {
        ValidateConnection(await RequiredAsync(HttpMethod.Get, ConnectionUrl(), "https://management.azure.com",
            missing: "Run connection first."));
        JsonObject version = await RequiredAsync(HttpMethod.Post, ToolboxUrl("/versions"), "https://ai.azure.com", ToolboxDefinition());
        ValidateToolboxVersion(version);
        string number = RequiredText(version, "version");
        JsonObject toolbox = await RequiredAsync(HttpMethod.Get, ToolboxUrl(""), "https://ai.azure.com");
        if ((string?)toolbox["name"] != settings.ToolboxName)
            throw new InvalidOperationException("Foundry returned another toolbox.");
        if ((string?)toolbox["default_version"] != number)
            toolbox = await RequiredAsync(HttpMethod.Patch, ToolboxUrl(""), "https://ai.azure.com",
                new JsonObject { ["default_version"] = number });
        if ((string?)toolbox["name"] != settings.ToolboxName || (string?)toolbox["default_version"] != number)
            throw new InvalidOperationException("Toolbox default promotion did not select the new version.");
        Console.WriteLine("Toolbox " + settings.ToolboxName + " | new/default version " + number);
        return version;
    }

    public async Task<JsonObject> CheckToolboxAsync()
    {
        ValidateConnection(await RequiredAsync(HttpMethod.Get, ConnectionUrl(), "https://management.azure.com",
            missing: "Run connection first."));
        JsonObject toolbox = await RequiredAsync(HttpMethod.Get, ToolboxUrl(""), "https://ai.azure.com",
            missing: "Run toolbox first.");
        if ((string?)toolbox["name"] != settings.ToolboxName) throw new InvalidOperationException("Wrong toolbox name.");
        string number = RequiredText(toolbox, "default_version");
        ValidateVersionId(number);
        JsonObject version = await RequiredAsync(HttpMethod.Get, ToolboxUrl("/versions/" + Uri.EscapeDataString(number)), "https://ai.azure.com");
        ValidateToolboxVersion(version);
        if ((string?)version["version"] != number) throw new InvalidOperationException("Wrong toolbox default version returned.");
        Console.WriteLine("Checked existing toolbox default " + number + " and project-identity Search connection. No tools were executed.");
        return version;
    }

    public async Task<bool> GrantSearchAsync()
    {
        JsonObject project = await RequiredAsync(HttpMethod.Get,
            "https://management.azure.com" + settings.ProjectResourceId + "?api-version=" + IqSettings.ConnectionApi,
            "https://management.azure.com");
        if (!string.Equals((string?)project["id"], settings.ProjectResourceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project lookup returned a different resource.");
        string principal = RequiredText(project["identity"] as JsonObject ??
            throw new InvalidOperationException("The Foundry project needs an existing managed identity. This step does not enable one."), "principalId");
        if (!Guid.TryParseExact(principal, "D", out _)) throw new InvalidOperationException("Project principal ID is not a GUID.");
        Console.WriteLine("Project principal ID: " + principal + " | Search scope: " + settings.SearchResourceId);
        string assignment = RoleAssignmentName(settings.SearchResourceId, principal);
        string url = "https://management.azure.com" + settings.SearchResourceId +
            "/providers/Microsoft.Authorization/roleAssignments/" + assignment + "?api-version=2022-04-01";
        IqReply reply = await SendAsync(HttpMethod.Put, url, "https://management.azure.com", new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["roleDefinitionId"] = settings.SearchResourceId + "/providers/Microsoft.Authorization/roleDefinitions/" + ReaderRole,
                ["principalId"] = principal,
                ["principalType"] = "ServicePrincipal",
            },
        }, accepted: new[] { 200, 201, 403, 409 });
        if (reply.Status == 403)
        {
            Console.WriteLine("Search role was NOT granted. Ask an administrator to run:");
            Console.WriteLine("  Search service -> Access control (IAM) -> Add role assignment -> Search Index Data Reader");
            Console.WriteLine("  Assign it to the PROJECT principal printed above at the printed Search scope.");
            return false;
        }
        if (reply.Status == 409)
        {
            if ((string?)reply.Json?["error"]?["code"] != "RoleAssignmentExists")
                throw new InvalidOperationException("Role assignment conflict is not proof of existing Search access.");
            string listUrl = "https://management.azure.com" + settings.SearchResourceId +
                "/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter=" +
                Uri.EscapeDataString("principalId eq '" + principal + "'");
            JsonObject roles = await RequiredAsync(HttpMethod.Get, listUrl, "https://management.azure.com");
            bool matched = (roles["value"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(role =>
                string.Equals((string?)role["properties"]?["principalId"], principal, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)role["properties"]?["scope"], settings.SearchResourceId, StringComparison.OrdinalIgnoreCase) &&
                ((string?)role["properties"]?["roleDefinitionId"] ?? "").EndsWith("/" + ReaderRole, StringComparison.OrdinalIgnoreCase));
            if (!matched) throw new InvalidOperationException("Could not verify an existing Search reader assignment on this service. Ask an administrator.");
            Console.WriteLine("Verified existing Search Index Data Reader for this project identity on this Search service.");
        }
        else Console.WriteLine("Granted project service access to Search. This does not grant users access to SharePoint documents.");
        Console.WriteLine("Role propagation can take time. Invoke explicitly when ready; no automatic model-call retries.");
        return true;
    }

    public static string RoleAssignmentName(string scope, string principal)
    {
        byte[] ns = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8").ToByteArray();
        Array.Reverse(ns, 0, 4); Array.Reverse(ns, 4, 2); Array.Reverse(ns, 6, 2);
        byte[] digest = SHA1.HashData(ns.Concat(Encoding.UTF8.GetBytes(scope + "/" + principal + "/" + ReaderRole)).ToArray());
        byte[] id = digest.Take(16).ToArray();
        id[6] = (byte)((id[6] & 15) | 80);
        id[8] = (byte)((id[8] & 63) | 128);
        Array.Reverse(id, 0, 4); Array.Reverse(id, 4, 2); Array.Reverse(id, 6, 2);
        return new Guid(id).ToString("D");
    }

    public async Task CleanupAsync(bool confirm)
    {
        if (!confirm) throw new ArgumentException("Cleanup requires exactly: cleanup --confirm.");
        IqReply agent = await SendAsync(HttpMethod.Get, AgentUrl(""), "https://ai.azure.com", accepted: new[] { 200, 404 });
        if (agent.Status == 200)
        {
            JsonObject latest = LatestVersion(agent.Json!);
            if ((string?)latest["metadata"]?["workshop"] != "iq" || (string?)latest["metadata"]?["attendee"] != settings.Prefix)
                throw new InvalidOperationException("Refusing cleanup of an agent not marked as this prefix's workshop agent.");
        }
        IqReply toolbox = await SendAsync(HttpMethod.Get, ToolboxUrl(""), "https://ai.azure.com", accepted: new[] { 200, 404 });
        if (toolbox.Status == 200)
        {
            if ((string?)toolbox.Json?["name"] != settings.ToolboxName) throw new InvalidOperationException("Refusing cleanup of another toolbox.");
            string number = RequiredText(toolbox.Json!, "default_version");
            ValidateVersionId(number);
            ValidateToolboxVersion(await RequiredAsync(HttpMethod.Get, ToolboxUrl("/versions/" + Uri.EscapeDataString(number)), "https://ai.azure.com"));
        }
        IqReply connection = await SendAsync(HttpMethod.Get, ConnectionUrl(), "https://management.azure.com", accepted: new[] { 200, 404 });
        if (connection.Status == 200) ValidateConnection(connection.Json!);
        IqReply kb = await SendAsync(HttpMethod.Get, SearchUrl("knowledgebases", settings.BaseName), "https://search.azure.com", accepted: new[] { 200, 404 });
        if (kb.Status == 200) ValidateKnowledgeBase(kb.Json!);
        if (agent.Status == 200)
            await SendAsync(HttpMethod.Delete, AgentUrl(""), "https://ai.azure.com", accepted: new[] { 200, 404 });
        if (toolbox.Status == 200)
            await SendAsync(HttpMethod.Delete, ToolboxUrl(""), "https://ai.azure.com", accepted: new[] { 204, 404 });
        if (connection.Status == 200)
            await SendAsync(HttpMethod.Delete, ConnectionUrl(), "https://management.azure.com", accepted: new[] { 200, 202, 204, 404 });
        if (kb.Status == 200)
            await SendAsync(HttpMethod.Delete, SearchUrl("knowledgebases", settings.BaseName), "https://search.azure.com", accepted: new[] { 200, 204, 404 });
        Console.WriteLine("Cleanup requested only for this lab's agent, toolbox, connection and knowledge base.");
        Console.WriteLine("Lab 07 index, lab 11 sources/base, shared resources, Search role assignments and local code retained.");
    }

    private void ValidateKnowledgeBase(JsonObject kb)
    {
        if ((string?)kb["name"] != settings.BaseName || kb["knowledgeSources"] is not JsonArray sources ||
            sources.Count != 1 || (string?)sources[0]?["name"] != settings.SharePointSource)
            throw new InvalidOperationException("Expected this prefix's SharePoint-only toolbox knowledge base. Run knowledge-base first.");
    }

    private void ValidateConnection(JsonObject connection)
    {
        JsonObject? p = connection["properties"] as JsonObject;
        if ((string?)p?["category"] != "RemoteTool" || (string?)p?["authType"] != "ProjectManagedIdentity" ||
            (string?)p?["target"] != settings.McpUrl || (string?)p?["audience"] != "https://search.azure.com")
            throw new InvalidOperationException("Existing connection does not match this KB and ProjectManagedIdentity Search authentication. Run connection first.");
    }

    private void ValidateToolboxVersion(JsonObject version)
    {
        ValidateVersionId(RequiredText(version, "version"));
        JsonArray? tools = version["tools"] as JsonArray;
        JsonObject? tool = tools?.Count == 1 ? tools[0] as JsonObject : null;
        if ((string?)version["name"] != settings.ToolboxName || (string?)tool?["type"] != "mcp" ||
            (string?)tool?["server_label"] != "knowledge_base" || (string?)tool?["server_url"] != settings.McpUrl ||
            (string?)tool?["project_connection_id"] != settings.ConnectionId || (string?)tool?["require_approval"] != "never")
            throw new InvalidOperationException("Toolbox version's target, connection or approval policy differs. Run toolbox first.");
    }

    private string SearchUrl(string collection, string name) => settings.SearchEndpoint + "/" + collection + "/" +
        Uri.EscapeDataString(name) + "?api-version=" + IqSettings.SearchApi;
    private string ConnectionUrl() => "https://management.azure.com" + settings.ConnectionId + "?api-version=" + IqSettings.ConnectionApi;
    private string ToolboxUrl(string suffix) => settings.ProjectEndpoint + "/toolboxes/" + settings.ToolboxName + suffix + "?api-version=v1";
    private string AgentUrl(string suffix) => settings.ProjectEndpoint + "/agents/" + settings.AgentName + suffix + "?api-version=v1";
    private static string RequiredText(JsonObject obj, string field) => (string?)obj[field] is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing " + field + " in the service response.");

    private async Task<JsonObject> RequiredAsync(HttpMethod method, string url, string resource, JsonObject? body = null,
        int[]? accepted = null, string? missing = null)
    {
        IqReply reply = await SendAsync(method, url, resource, body, accepted: missing == null ? accepted : new[] { 200, 404 });
        if (reply.Status == 404) throw new InvalidOperationException(missing);
        return reply.Json ?? throw new InvalidOperationException("Service returned no JSON object.");
    }

    private async Task<IqReply> SendAsync(HttpMethod method, string url, string resource, JsonObject? body = null,
        int[]? accepted = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendRequestAsync(request, resource, accepted ?? new[] { 200 }, TimeSpan.FromSeconds(90));
    }

    private async Task<IqReply> SendRequestAsync(HttpRequestMessage request, string resource, int[] accepted, TimeSpan timeout,
        bool requireUser = false)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        string token = await getToken(resource).WaitAsync(cancellation.Token);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Invalid bearer token from token acquisition.");
        knownTokens.Add(token);
        if (requireUser) settings.ValidateUserToken(token, utcNow());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (resource == "https://ai.azure.com") request.Headers.Add("Foundry-Features", Features);
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);
        string body = Redact(await response.Content.ReadAsStringAsync(cancellation.Token));
        int status = (int)response.StatusCode;
        if (!accepted.Contains(status))
        {
            Console.Error.WriteLine("HTTP " + status + " " + response.ReasonPhrase + " | UTC (Date): " + response.Headers.Date);
            foreach (string header in new[] { "x-ms-request-id", "apim-request-id", "x-ms-correlation-request-id" })
                if (response.Headers.TryGetValues(header, out var values))
                    Console.Error.WriteLine(header + ": " + SafeText(Redact(string.Join(", ", values)), 300));
            Console.Error.WriteLine("Response body: " + SafeText(body, 4000));
            if (status == 409 && request.Method == HttpMethod.Delete)
                Console.Error.WriteLine("Close active hosted sessions in Foundry, then rerun cleanup. No force-delete is attempted.");
            throw new InvalidOperationException("Request failed with HTTP " + status + ". No automatic retry was made.");
        }
        if (string.IsNullOrWhiteSpace(body)) return new IqReply(status, null);
        try
        {
            JsonObject json = JsonNode.Parse(body) as JsonObject ?? throw new JsonException();
            return new IqReply(status, json);
        }
        catch (JsonException)
        {
            if (status >= 400) return new IqReply(status, null);
            throw new InvalidOperationException("Service returned invalid JSON instead of an object.");
        }
    }

    private string Redact(string text)
    {
        foreach (string token in knownTokens) text = text.Replace(token, "<redacted>");
        return text;
    }

    private static string SafeText(string text, int max) =>
        new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').Take(max).ToArray());
}
