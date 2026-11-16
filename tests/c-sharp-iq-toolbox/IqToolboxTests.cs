using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

int cases = 0;
void Check(bool condition, string message)
{
    cases++;
    if (!condition) throw new Exception(message);
}
async Task Fails(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
    {
        cases++;
        return;
    }
    throw new Exception("Expected failure: " + message);
}
string root = Path.GetFullPath(Path.Combine(".workshop", "iq14-test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    IqSettings settings = IqTestData.Settings;
    File.WriteAllText(Path.Combine(root, ".env"), "WORKSHOP_PREFIX=wrong01\n");
    File.WriteAllText(Path.Combine(root, ".env.azure"), IqTestData.EnvironmentText);
    var handler = new IqOfflineHandler();
    using var client = new HttpClient(handler);
    var clock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    int tokenCalls = 0;
    Task<string> Token(string resource) { tokenCalls++; return Task.FromResult(IqTestData.Token(resource)); }
    Task Delay(TimeSpan duration) { clock += duration; return Task.CompletedTask; }
    var lab = new IqWorkshop(settings, client, Token, () => clock, Delay);
    int result = await IqToolboxAgentProgram.RunAsync(new[] { "run" }, root, client, Token, () => clock, Delay);
    Check(result == 0 && handler.Requests.Count == 20, "Full flow hidden/missing operations");
    Check(handler.Requests.All(r => r.Method != "DELETE"), "Run performs cleanup");
    Check(tokenCalls == handler.Requests.Count, "Wrong credential acquisition count");
    Check(handler.Requests.Count(r => r.Url.Contains("/responses?")) == 2, "Billable retries or implicit question");
    foreach (var request in handler.Requests)
    {
        string resource = request.Url.StartsWith("https://management.azure.com") ? "https://management.azure.com" :
            request.Url.StartsWith(settings.SearchEndpoint) ? "https://search.azure.com" : "https://ai.azure.com";
        Check(request.Authorization == "Bearer " + IqTestData.Token(resource), "Wrong token audience");
        Check(!request.HasKey && !request.HasUserAuthorization && !request.HasElevatedRead, "Key/user forwarding/elevated read");
    }
    var kb = handler.Requests.Single(r => r.Method == "PUT" && r.Url.StartsWith(settings.SearchEndpoint));
    Check(kb.Body!["knowledgeSources"]!.AsArray().Count == 1 &&
        kb.Body["knowledgeSources"]![0]!["name"]!.ToString() == settings.SharePointSource, "Not SharePoint-only");
    Check(JsonNode.DeepEquals(kb.Body["models"], IqTestData.SharedBase()["models"]), "Models not copied");
    var connection = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/connections/"));
    Check(JsonNode.DeepEquals(connection.Body, lab.ConnectionDefinition()), "Wrong keyless agent connection");
    var toolbox = handler.Requests.Single(r => r.Method == "POST" && r.Url.Contains("/toolboxes/"));
    Check(JsonNode.DeepEquals(toolbox.Body, lab.ToolboxDefinition()), "Wrong MCP toolbox shape");
    Check(handler.Requests.Single(r => r.Method == "PATCH").Body!["default_version"]!.ToString() == "2", "Default not promoted");
    var upload = handler.Requests.Single(r => r.Code != null);
    Check(upload.Sha == Convert.ToHexString(SHA256.HashData(upload.Code!)).ToLowerInvariant(), "Bad upload SHA256");
    Check(JsonNode.DeepEquals(upload.Body, lab.CodeMetadata()), "Hosted deployment metadata");
    Check(upload.Body!["definition"]!["environment_variables"]!["TOOLBOX_NAME"]!.ToString() == settings.ToolboxName, "No stable toolbox name");
    using (var bundle = new ZipArchive(new MemoryStream(upload.Code!), ZipArchiveMode.Read))
        Check(bundle.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(new[] { "Program.cs", "WorkshopIqAgent.csproj" }), "Archive includes secrets/build files");
    var role = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/roleAssignments/"));
    Check(role.Body!["properties"]!["principalId"]!.ToString() == IqTestData.Principal, "Not project principal");
    Check(role.Url.StartsWith("https://management.azure.com" + settings.SearchResourceId + "/providers/"), "Wrong role scope");
    if (args.Length == 2 && args[0] == "--wire")
        File.WriteAllText(args[1], new JsonObject
        {
            ["knowledgeBase"] = kb.Body!.DeepClone(),
            ["connection"] = connection.Body!.DeepClone(),
            ["toolbox"] = toolbox.Body!.DeepClone(),
            ["hosted"] = upload.Body!.DeepClone(),
            ["role"] = role.Body!.DeepClone(),
            ["assignmentName"] = IqWorkshop.RoleAssignmentName(settings.SearchResourceId, IqTestData.Principal),
        }.ToJsonString());
    foreach (string command in new[] { "verify-sources", "knowledge-base", "connection", "toolbox", "check-toolbox", "deploy", "invoke", "invoke-unknown" })
    {
        handler.Reset();
        await IqToolboxAgentProgram.RunAsync(new[] { command }, root, client, Token, () => clock, Delay);
        int expected = command switch
        {
            "connection" => 2,
            "verify-sources" or "check-toolbox" => 3,
            "knowledge-base" => 4,
            "toolbox" or "deploy" => 4,
            _ => 1,
        };
        Check(handler.Requests.Count == expected, "Implicit actions: " + command);
        Check(handler.Requests.All(r => r.Method != "DELETE"), "Step deletes resources");
        if (command != "invoke" && command != "invoke-unknown")
            Check(handler.Requests.All(r => !r.Url.Contains("/responses?")), "Step runs a hidden model call");
    }
    handler.Reset("default-current");
    await lab.PublishToolboxAsync();
    Check(handler.Requests.Count == 3 && handler.Requests.All(r => r.Method != "PATCH"), "Unnecessary default promotion");
    handler.Reset("bad-default");
    await Fails(() => lab.PublishToolboxAsync(), "promotion");
    Check(handler.Requests.Count == 4, "Promotion retries");
    foreach (string mode in new[] { "missing-source", "missing-base", "bad-source", "no-models" })
    {
        handler.Reset(mode);
        await Fails(() => lab.CreateKnowledgeBaseAsync(), mode switch
        {
            "missing-source" or "missing-base" => "Complete lab 11",
            "bad-source" => "searchIndex",
            _ => "model settings",
        });
        Check(handler.Requests.All(r => r.Method == "GET"), "Writes before shared prerequisite checks");
    }
    foreach (string mode in new[] { "bad-connection", "bad-toolbox" })
    {
        handler.Reset(mode);
        await Fails(() => lab.DeployAsync(root), mode == "bad-connection" ? "connection" : "toolbox");
        Check(handler.Requests.All(r => r.Method == "GET"), "Deploy repairs mismatched resource implicitly");
    }
    foreach (string mode in new[] { "wrong-index", "missing-index", "no-acls", "bad-user-acls", "bad-group-acls", "missing-semantic", "missing-content" })
    {
        handler.Reset(mode);
        await Fails(() => lab.CreateKnowledgeBaseAsync(), mode == "wrong-index" ? "must target" :
            mode == "missing-index" ? "Complete lab 07" : "ACL schema");
        Check(handler.Requests.All(r => r.Method == "GET"), "Invalid ACL prerequisites allowed a write");
    }
    foreach (string mode in new[] { "no-project-identity", "wrong-project", "bad-project-principal" })
    {
        handler.Reset(mode);
        await Fails(() => lab.GrantSearchAsync(), mode == "wrong-project" ? "different resource" :
            mode == "bad-project-principal" ? "GUID" : "managed identity");
        Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "GET", "Invalid project identity allowed a grant");
    }
    handler.Reset();
    int callsBeforeFiles = tokenCalls;
    lab.WriteFiles(root);
    string folder = settings.Folder(root);
    File.AppendAllText(Path.Combine(folder, "Program.cs"), "\n// Synthetic edit preserved by package.\n");
    File.WriteAllText(Path.Combine(folder, ".env"), "TOKEN=must-not-upload");
    string zip = lab.Package(root);
    using (var bundle = ZipFile.OpenRead(zip))
    {
        using var reader = new StreamReader(bundle.GetEntry("Program.cs")!.Open());
        Check(reader.ReadToEnd().Contains("Synthetic edit"), "Package overwrites edits");
        Check(bundle.Entries.Count == 2, "Package uploads .env");
    }
    Check(tokenCalls == callsBeforeFiles && handler.Requests.Count == 0, "Local source/zip work authenticates");
    foreach (string mode in new[] { "failed", "deleting", "deleted" })
    {
        handler.Reset(mode);
        await Fails(() => lab.WaitAsync("1"), mode);
        Check(handler.Requests.Count == 1, "Terminal status retries");
    }
    handler.Reset("creating");
    await Fails(() => lab.WaitAsync("1", TimeSpan.FromSeconds(20)), "wait timeout");
    Check(handler.Requests.Count == 2 && handler.Requests.All(r => r.Method == "GET"), "Wait timeout/calls");
    handler.Reset();
    await lab.WaitAsync("1");
    Check(handler.Requests.Count == 2 && handler.Requests.All(r => r.Url.Contains("/versions/1?")), "Wait switches target");
    foreach (string mode in new[] { "role-existing", "role-denied", "role-conflict", "existing-wrong-scope" })
    {
        handler.Reset(mode);
        if (mode == "role-denied") Check(!await lab.GrantSearchAsync(), "Handoff claims success");
        else if (mode == "role-existing") Check(await lab.GrantSearchAsync(), "Existing exact grant not reused");
        else await Fails(() => lab.GrantSearchAsync(), mode == "role-conflict" ? "conflict" : "verify");
        Check(handler.Requests.All(r => !r.Url.Contains("/responses?")), "Grant asks a question");
        Check(handler.Requests.Count == (mode is "role-existing" or "existing-wrong-scope" ? 3 : 2), "Role conflict/handoff hidden calls");
    }
    handler.Reset("role-denied");
    Check(await IqToolboxAgentProgram.RunAsync(new[] { "run" }, root, client, Token, () => clock, Delay) == 3, "Run does not stop at admin handoff");
    Check(handler.Requests.All(r => !r.Url.Contains("/responses?")), "Run bills questions before successful grant");
    foreach (string mode in new[] { "denied", "partial", "incomplete", "invalid-json", "tool-error", "approval", "consent", "no-answer", "echo" })
    {
        handler.Reset(mode);
        var output = new StringWriter();
        var errors = new StringWriter();
        TextWriter savedOutput = Console.Out, savedError = Console.Error;
        try
        {
            Console.SetOut(output); Console.SetError(errors);
            if (mode == "echo") await lab.InvokeAsync(IqWorkshop.Question, true);
            else await Fails(() => lab.InvokeAsync(IqWorkshop.Question, true), mode switch
            {
                "denied" => "HTTP 403",
                "partial" => "HTTP 206",
                "incomplete" => "did not complete",
                "tool-error" => "retrieval failed",
                "approval" => "approval",
                "consent" => "consent",
                "no-answer" => "no answer",
                _ => "invalid JSON",
            });
        }
        finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
        Check(handler.Requests.Count == 1, "Question retried after failure");
        string logs = output + errors.ToString();
        Check(!logs.Contains("synthetic-ai-token") && !logs.Contains("synthetic-search-token") && !logs.Contains("synthetic-arm-token"), "Credential echo in diagnostics");
        Check(!logs.Contains(IqTestData.Token("https://ai.azure.com")), "Delegated token echo in diagnostics");
        if (mode == "denied")
            foreach (string text in new[] { "UTC (Date):", "synthetic-request", "Response body:", "<redacted>" })
                Check(logs.Contains(text), "Missing HTTP diagnostics");
    }
    handler.Reset();
    await lab.InvokeAsync(IqWorkshop.UnknownQuestion);
    Check(handler.Requests.Count == 1, "Negative control hidden followup");
    handler.Reset();
    await Fails(() => lab.CleanupAsync(false), "cleanup --confirm");
    Check(handler.Requests.Count == 0, "Cleanup guard after reads");
    await lab.CleanupAsync(true);
    Check(handler.Requests.Count == 9 && handler.Requests.Count(r => r.Method == "DELETE") == 4, "Cleanup resources/count");
    Check(handler.Requests.Where(r => r.Method == "DELETE").All(r => !r.Url.Contains(settings.SharedBase + "?") &&
        !r.Url.Contains("/knowledgesources/") && !r.Url.Contains("force") && !r.Url.Contains("/roleAssignments/")), "Shared/forced/role cleanup");
    foreach (string mode in new[] { "other-agent", "bad-toolbox", "bad-kb" })
    {
        handler.Reset(mode);
        await Fails(() => lab.CleanupAsync(true), mode switch { "other-agent" => "workshop", "bad-toolbox" => "toolbox", _ => "SharePoint-only" });
        Check(handler.Requests.All(r => r.Method == "GET"), "Unsafe delete before preflight");
    }
    handler.Reset("cleanup-conflict");
    await Fails(() => lab.CleanupAsync(true), "HTTP 409");
    Check(handler.Requests.Count(r => r.Method == "DELETE") == 1, "Dismantles toolchain after blocked agent deletion");
    handler.Reset("all-missing");
    await lab.CleanupAsync(true);
    Check(handler.Requests.Count == 4 && handler.Requests.All(r => r.Method == "GET"), "Missing cleanup objects not skipped");
    handler.Reset();
    var noAuth = new IqWorkshop(settings, client, _ => throw new InvalidOperationException("Synthetic authentication failure"));
    await Fails(() => noAuth.InvokeAsync("test"), "authentication failure");
    await Fails(() => lab.InvokeAsync(" "), "non-empty");
    Check(handler.Requests.Count == 0, "Auth/input failure reaches network");
    foreach (string mode in new[] { "app", "no-scope", "wrong-tenant", "wrong-audience", "expired", "no-user", "malformed" })
    {
        string token = IqTestData.UserToken(mode: mode);
        var invalidUser = new IqWorkshop(settings, client, _ => Task.FromResult(token), () => clock);
        await Fails(() => invalidUser.InvokeAsync("restricted fact"), "delegated Foundry user token");
        Check(handler.Requests.Count == 0, "Missing/app-only user context reached network");
    }
    string aliceToken = IqTestData.UserToken("44444444-4444-4444-4444-444444444444");
    string bobToken = IqTestData.UserToken("55555555-5555-5555-5555-555555555555");
    string currentUserToken = aliceToken;
    var delegated = new IqWorkshop(settings, client, _ => Task.FromResult(currentUserToken), () => clock);
    await delegated.InvokeAsync("same restricted fact");
    currentUserToken = bobToken;
    await delegated.InvokeAsync("same restricted fact");
    Check(handler.Requests.Select(r => r.Authorization).SequenceEqual(new[] { "Bearer " + aliceToken, "Bearer " + bobToken }),
        "Different users reused an invocation credential");
    Check(handler.Requests.All(r => r.Body!.Count == 2 && (bool?)r.Body["store"] == false &&
        r.Body["previous_response_id"] == null && r.Body["agent_session_id"] == null), "Cross-user conversation reuse");
    handler.Reset();
    string invalid = Path.Combine(root, "invalid");
    Directory.CreateDirectory(invalid);
    File.WriteAllText(Path.Combine(invalid, ".env"), "WORKSHOP_PREFIX=invalid!\n");
    foreach (string[] arguments in new[]
    {
        new[] { "cleanup" }, new[] { "cleanup", "--confirm", "extra" }, new[] { "wait", "../1" },
        new[] { "invoke", "" }, new[] { "invoke", "one", "two" }, new[] { "toolbox", "--diagnostics" },
        new[] { "grant-search", "--diagnostics" }, new[] { "unknown" },
    })
        await Fails(() => IqToolboxAgentProgram.RunAsync(arguments, invalid, client), arguments[0] == "wait"
            ? "identifier" : arguments[0] == "invoke" && arguments.Length == 2 ? "non-empty" : "help");
    foreach (string[] arguments in new[] { Array.Empty<string>(), new[] { "help" }, new[] { "--help" }, new[] { "-h" } })
        Check(await IqToolboxAgentProgram.RunAsync(arguments, invalid, client) == 0, "Help requires configuration");
    Check(handler.Requests.Count == 0, "Invalid args/help side effects");
    Console.WriteLine($"Passed {cases} offline IQ toolbox console cases.");
}
finally { Directory.Delete(root, recursive: true); }

public sealed record CapturedIqRequest(string Method, string Url, string? Authorization, bool HasKey,
    bool HasUserAuthorization, bool HasElevatedRead, JsonObject? Body, byte[]? Code, string? Sha);

public static class IqTestData
{
    public const string Principal = "22222222-2222-2222-2222-222222222222";
    public const string AgentPrincipal = "33333333-3333-3333-3333-333333333333";
    public static IqSettings Settings => new IqSettings("00000000-0000-0000-0000-000000000000",
        "https://workshop.search.windows.net",
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/workshop/providers/Microsoft.Search/searchServices/workshop",
        "https://workshop.services.ai.azure.com/api/projects/project",
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/workshop/providers/Microsoft.CognitiveServices/accounts/workshop/projects/project",
        "workshop-chat", "alice01");
    public static string EnvironmentText => "WORKSHOP_PREFIX=alice01\nAZURE_TENANT_ID=" + Settings.Tenant +
        "\nAZURE_SEARCH_ENDPOINT=" + Settings.SearchEndpoint + "\nAZURE_SEARCH_RESOURCE_ID=" + Settings.SearchResourceId +
        "\nAZURE_AI_PROJECT_ENDPOINT=" + Settings.ProjectEndpoint + "\nAZURE_AI_PROJECT_RESOURCE_ID=" + Settings.ProjectResourceId +
        "\nAZURE_AI_MODEL_DEPLOYMENT_NAME=workshop-chat\n";
    public static string Token(string resource) => resource switch
    {
        "https://ai.azure.com" => UserToken(),
        "https://search.azure.com" => "synthetic-search-token",
        "https://management.azure.com" => "synthetic-arm-token",
        _ => throw new Exception("Unexpected token resource"),
    };
    public static string UserToken(string oid = "44444444-4444-4444-4444-444444444444", string mode = "")
    {
        if (mode == "malformed") return "synthetic-invalid-token";
        var claims = new JsonObject
        {
            ["oid"] = mode == "no-user" ? null : oid,
            ["tid"] = mode == "wrong-tenant" ? "99999999-9999-9999-9999-999999999999" : Settings.Tenant,
            ["aud"] = mode == "wrong-audience" ? "https://search.azure.com" : "https://ai.azure.com",
            ["scp"] = mode == "no-scope" ? null : "user_impersonation",
            ["idtyp"] = mode == "app" ? "app" : "user",
            ["exp"] = mode == "expired" ? 1 : 2208988800L,
        };
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".offline-signature";
    }
    public static JsonObject SharePointIndex() => new JsonObject
    {
        ["name"] = Settings.SharePointIndex,
        ["permissionFilterOption"] = "enabled",
        ["semantic"] = new JsonObject { ["configurations"] = new JsonArray(new JsonObject { ["name"] = "semantic-config" }) },
        ["fields"] = new JsonArray(
            new JsonObject { ["name"] = "UserIds", ["type"] = "Collection(Edm.String)", ["permissionFilter"] = "userIds", ["filterable"] = true, ["retrievable"] = false },
            new JsonObject { ["name"] = "GroupIds", ["type"] = "Collection(Edm.String)", ["permissionFilter"] = "groupIds", ["filterable"] = true, ["retrievable"] = false },
            new JsonObject { ["name"] = "id", ["retrievable"] = true },
            new JsonObject { ["name"] = "content", ["searchable"] = true, ["retrievable"] = true },
            new JsonObject { ["name"] = "sourceFile", ["retrievable"] = true },
            new JsonObject { ["name"] = "webUrl", ["retrievable"] = true },
            new JsonObject { ["name"] = "page", ["retrievable"] = true },
            new JsonObject { ["name"] = "pageTo", ["retrievable"] = true }),
    };
    public static JsonObject SharedBase() => new JsonObject
    {
        ["name"] = Settings.SharedBase,
        ["answerInstructions"] = "Cite the source documents.",
        ["models"] = new JsonArray(new JsonObject
        {
            ["kind"] = "azureOpenAI",
            ["azureOpenAIParameters"] = new JsonObject
            {
                ["resourceUri"] = "https://workshop.openai.azure.com",
                ["deploymentId"] = "workshop-chat",
                ["modelName"] = "gpt-5-mini",
            },
        }),
    };
}

public sealed class IqOfflineHandler : HttpMessageHandler
{
    public List<CapturedIqRequest> Requests = new();
    public string Mode = "";
    private string defaultVersion = "1";
    private int polls;
    private readonly IqSettings settings = IqTestData.Settings;
    public void Reset(string mode = "") { Requests.Clear(); Mode = mode; defaultVersion = mode == "default-current" ? "2" : "1"; polls = 0; }
    public JsonObject Version(string status = "active") => new JsonObject
    {
        ["name"] = settings.AgentName,
        ["version"] = "1",
        ["status"] = status,
        ["definition"] = new JsonObject { ["kind"] = "hosted" },
        ["instance_identity"] = new JsonObject { ["principal_id"] = IqTestData.AgentPrincipal },
        ["metadata"] = new JsonObject { ["workshop"] = Mode == "other-agent" ? "other" : "iq", ["attendee"] = settings.Prefix },
    };
    private JsonObject Base() => new JsonObject
    {
        ["name"] = settings.BaseName,
        ["knowledgeSources"] = new JsonArray(new JsonObject { ["name"] = Mode == "bad-kb" ? "other-source" : settings.SharePointSource }),
    };
    private JsonObject Connection() => new JsonObject
    {
        ["properties"] = new JsonObject
        {
            ["category"] = "RemoteTool",
            ["authType"] = Mode == "bad-connection" ? "AgenticIdentityToken" : "ProjectManagedIdentity",
            ["target"] = settings.McpUrl,
            ["audience"] = "https://search.azure.com",
            ["isSharedToAll"] = true,
            ["metadata"] = new JsonObject { ["ApiType"] = "Azure" },
        },
    };
    private JsonObject ToolboxVersion(string number) => new JsonObject
    {
        ["name"] = settings.ToolboxName,
        ["version"] = number,
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "mcp",
            ["server_label"] = "knowledge_base",
            ["server_url"] = Mode == "bad-toolbox" ? "https://wrong.example/mcp" : settings.McpUrl,
            ["require_approval"] = "never",
            ["project_connection_id"] = settings.ConnectionId,
        }),
    };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        JsonObject? body = null;
        byte[]? code = null;
        if (request.Content is MultipartFormDataContent multipart)
            foreach (HttpContent part in multipart)
            {
                string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "metadata") body = JsonNode.Parse(await part.ReadAsStringAsync(cancellation))!.AsObject();
                else if (name == "code") code = await part.ReadAsByteArrayAsync(cancellation);
                else throw new Exception("Unexpected multipart part");
            }
        else if (request.Content != null) body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation))!.AsObject();
        string path = request.RequestUri!.AbsolutePath;
        Requests.Add(new CapturedIqRequest(request.Method.Method, request.RequestUri.ToString(), request.Headers.Authorization?.ToString(),
            request.Headers.Contains("api-key") || request.Headers.Contains("x-apikey"), request.Headers.Contains("x-ms-query-source-authorization"),
            request.Headers.Contains("x-ms-enable-elevated-read"), body, code,
            request.Headers.TryGetValues("x-ms-code-zip-sha256", out var sha) ? sha.Single() : null));
        int status = 200;
        JsonObject result = new JsonObject();
        if (Mode == "denied") { status = 403; result["error"] = IqTestData.Token("https://ai.azure.com") + " synthetic-search-token synthetic-arm-token"; }
        else if (Mode == "partial") status = 206;
        else if (Mode == "all-missing") status = 404;
        else if (request.Method == HttpMethod.Delete)
        {
            status = path.Contains("/agents/") ? Mode == "cleanup-conflict" ? 409 : 200 :
                path.Contains("/connections/") ? 202 : 204;
        }
        else if (path.Contains("/roleAssignments"))
        {
            if (request.Method == HttpMethod.Put)
            {
                status = Mode == "role-denied" ? 403 : Mode is "role-existing" or "role-conflict" or "existing-wrong-scope" ? 409 : 201;
                result = status == 409
                    ? new JsonObject { ["error"] = new JsonObject { ["code"] = Mode == "role-conflict" ? "Conflict" : "RoleAssignmentExists" } }
                    : body!.DeepClone().AsObject();
            }
            else result["value"] = new JsonArray(new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["principalId"] = IqTestData.Principal,
                    ["scope"] = Mode == "existing-wrong-scope" ? "/subscriptions/other" : settings.SearchResourceId,
                    ["roleDefinitionId"] = settings.SearchResourceId + "/providers/Microsoft.Authorization/roleDefinitions/" + IqWorkshop.ReaderRole,
                },
            });
        }
        else if (path.Contains("/connections/")) { result = Connection(); if (request.Method == HttpMethod.Put) status = 201; }
        else if (path.Equals(settings.ProjectResourceId, StringComparison.OrdinalIgnoreCase))
        {
            result = new JsonObject
            {
                ["id"] = Mode == "wrong-project" ? "/subscriptions/other" : settings.ProjectResourceId,
                ["identity"] = Mode == "no-project-identity" ? null : new JsonObject
                {
                    ["principalId"] = Mode == "bad-project-principal" ? "not-a-guid" : IqTestData.Principal,
                },
            };
        }
        else if (path.EndsWith("/indexes/" + settings.SharePointIndex))
        {
            result = IqTestData.SharePointIndex();
            status = Mode == "missing-index" ? 404 : 200;
            if (Mode == "no-acls") result["permissionFilterOption"] = "disabled";
            if (Mode == "bad-user-acls") result["fields"]![0]!["permissionFilter"] = null;
            if (Mode == "bad-group-acls") result["fields"]![1]!["retrievable"] = true;
            if (Mode == "missing-semantic") result.Remove("semantic");
            if (Mode == "missing-content") result["fields"]!.AsArray().RemoveAt(3);
        }
        else if (path.EndsWith("/knowledgesources/" + settings.SharePointSource))
        {
            status = Mode == "missing-source" ? 404 : 200;
            result = new JsonObject
            {
                ["name"] = settings.SharePointSource,
                ["kind"] = Mode == "bad-source" ? "mcpServer" : "searchIndex",
                ["searchIndexParameters"] = new JsonObject { ["searchIndexName"] = Mode == "wrong-index" ? "alice01-cu-chunks" : settings.SharePointIndex },
            };
        }
        else if (path.EndsWith("/knowledgebases/" + settings.SharedBase))
        {
            status = Mode == "missing-base" ? 404 : 200;
            result = IqTestData.SharedBase();
            if (Mode == "no-models") result.Remove("models");
        }
        else if (path.EndsWith("/knowledgebases/" + settings.BaseName)) result = request.Method == HttpMethod.Put ? body!.DeepClone().AsObject() : Base();
        else if (path.Contains("/toolboxes/"))
        {
            if (request.Method == HttpMethod.Post) result = ToolboxVersion("2");
            else if (path.Contains("/versions/")) result = ToolboxVersion(path.Split('/').Last());
            else
            {
                if (request.Method == HttpMethod.Patch && Mode != "bad-default") defaultVersion = body!["default_version"]!.ToString();
                result = new JsonObject { ["name"] = settings.ToolboxName, ["default_version"] = defaultVersion };
            }
        }
        else if (path.EndsWith("/responses"))
        {
            bool unknown = (string?)body?["input"] == IqWorkshop.UnknownQuestion;
            var output = new JsonArray();
            if (!unknown)
                output.Add(new JsonObject
                {
                    ["type"] = Mode == "consent" ? "oauth_consent_request" : Mode == "approval" ? "mcp_approval_request" : "mcp_call",
                    ["name"] = "knowledge_base_retrieve",
                    ["server_label"] = "knowledge_base",
                    ["output"] = "Synthetic policy evidence",
                    ["error"] = Mode == "tool-error" ? "synthetic tool failure" : null,
                });
            output.Add(new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = Mode == "no-answer" ? "" : Mode == "echo" ? IqTestData.Token("https://ai.azure.com") : unknown ? "I don't know from the SharePoint documents." : "Synthetic SharePoint answer.",
                    ["annotations"] = new JsonArray(new JsonObject { ["type"] = "url_citation", ["url"] = "https://workshop.sharepoint.com/sites/demo/documents/report.pdf#page=2" }),
                }),
            });
            result = new JsonObject { ["status"] = Mode is "incomplete" or "consent" ? "incomplete" : "completed", ["output"] = output, ["agent_session_id"] = "synthetic-session" };
        }
        else if (request.Method == HttpMethod.Post) result = Version("creating");
        else if (path.Contains("/versions/"))
        {
            polls++;
            result = Version(Mode is "failed" or "deleting" or "deleted" or "creating" ? Mode : polls == 1 ? "creating" : "active");
        }
        else result = new JsonObject { ["name"] = settings.AgentName, ["versions"] = new JsonObject { ["latest"] = Version() } };
        string text = Mode == "invalid-json" ? "{invalid" : status is 204 or 202 ? "" : result.ToJsonString();
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        response.Headers.Date = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        response.Headers.Add("x-ms-request-id", "synthetic-request");
        return response;
    }
}
