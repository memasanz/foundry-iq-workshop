using System.Net;
using System.Text;
using System.Text.Json.Nodes;

public static class ToolboxTests
{
    public static readonly FoundrySettings Settings = new("", "alice01",
        "https://workshop.services.ai.azure.com/api/projects/workshop",
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/test/projects/workshop",
        "test-model", "00000000-0000-0000-0000-000000000000");

    public static async Task Main()
    {
        foreach (bool sse in new[] { false, true })
            foreach (bool defaultAlreadySelected in new[] { false, true })
            {
                using var handler = new ToolboxOfflineHandler { Sse = sse, DefaultAlreadySelected = defaultAlreadySelected };
                using var client = new HttpClient(handler);
                var resources = new List<string>();
                var lab = new ToolboxWorkshop(client, Settings, resource =>
                {
                    resources.Add(resource);
                    return Task.FromResult("synthetic-token");
                });
                JsonObject connection = await lab.GetWebIqConnectionAsync();
                JsonObject version = await lab.PublishToolboxAsync(connection);
                string url = lab.ToolboxUrl(version);
                Check((await lab.DiscoverToolsAsync(url)).Count == 2, "discovery pagination");
                await lab.ConfigureConnectionAsync(url, version);
                JsonObject agent = await lab.CreateAgentVersionAsync(url);
                JsonObject conversation = await lab.CreateConversationAsync();
                Check(lab.VerifyRetrieval(await lab.AskAsync(ToolboxWorkshop.Question, conversation, agent)), "retrieval");
                Check(handler.Requests.Count(r => r.Method == "PATCH") == (defaultAlreadySelected ? 0 : 1), "conditional default promotion");
                Check(resources.Contains("https://management.azure.com/")
                    && resources.All(r => r is "https://ai.azure.com/" or "https://management.azure.com/"), "token audiences");
                AssertWire(handler);
            }
        foreach (string protocol in new[] { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" })
            foreach (bool sse in new[] { false, true })
            {
                using var handler = new ToolboxOfflineHandler { Protocol = protocol, Sse = sse };
                using var client = new HttpClient(handler);
                var lab = new ToolboxWorkshop(client, Settings, _ => Task.FromResult("synthetic-token"));
                Check((await lab.DiscoverToolsAsync(Settings.ProjectEndpoint + "/toolboxes/alice01-webiq-toolbox/versions/7/mcp?api-version=v1")).Count == 2,
                    "negotiated discovery " + protocol);
                Check((string?)handler.Requests[0].Body!["params"]!["protocolVersion"] == "2025-06-18", "existing lab 02 request version retained");
                Check(handler.Requests.Skip(1).All(r => r.Protocol == protocol && r.Session == "synthetic-session"),
                    "negotiated protocol, not requested version, is forwarded");
            }
        using (var handler = new ToolboxOfflineHandler { Protocol = "2099-01-01" })
        using (var client = new HttpClient(handler))
        {
            try
            {
                await new ToolboxWorkshop(client, Settings, _ => Task.FromResult("synthetic-token")).DiscoverToolsAsync("https://example.test/mcp");
                throw new Exception("FAIL: unknown protocol accepted");
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("2099-01-01"), "unsupported error identifies actual server version");
                Check(!handler.Requests.Any(r => (string?)r.Body?["method"] == "tools/list"), "unknown protocol stops before tools/list");
            }
        }
        foreach (string mode in new[] { "wrong-target", "missing-news", "discovery-401", "rpc-error",
            "repeated-cursor", "connection-202", "approval", "failed", "runtime-429", "promotion-failed" })
        {
            using var handler = new ToolboxOfflineHandler { Mode = mode };
            using var client = new HttpClient(handler);
            var lab = new ToolboxWorkshop(client, Settings, _ => Task.FromResult("synthetic-token"));
            await Throws(async () =>
            {
                JsonObject connection = await lab.GetWebIqConnectionAsync();
                JsonObject version = await lab.PublishToolboxAsync(connection);
                string url = lab.ToolboxUrl(version);
                await lab.DiscoverToolsAsync(url);
                await lab.ConfigureConnectionAsync(url, version);
                JsonObject agent = await lab.CreateAgentVersionAsync(url);
                JsonObject conversation = await lab.CreateConversationAsync();
                await lab.AskAsync(ToolboxWorkshop.Question, conversation, agent);
            }, mode);
            if (mode == "wrong-target")
                Check(handler.Requests.Count == 1, "metadata failure makes no writes");
            if (mode is "missing-news" or "discovery-401" or "rpc-error" or "repeated-cursor" or "promotion-failed")
                Check(!handler.Requests.Any(r => r.Path.Contains("/agents/") || r.Method == "PUT"), "failed setup stops downstream writes");
            if (mode == "connection-202")
                Check(!handler.Requests.Any(r => r.Path.Contains("/agents/")), "only 200/201 provision connections");
            Check(handler.Requests.Count(r => r.Path.EndsWith("/responses")) <= 1, "no hidden follow-up/retry");
        }
        using (var client = new HttpClient(new ToolboxOfflineHandler()))
        {
            var lab = new ToolboxWorkshop(client, Settings);
            foreach (string mode in new[] { "wrong-label", "wrong-tool", "tool-error", "empty-output", "no-answer", "no-url" })
                await Throws(() => { lab.VerifyRetrieval(ToolboxOfflineHandler.Completed(mode)); return Task.CompletedTask; }, mode);
            Check(lab.VerifyRetrieval(ToolboxOfflineHandler.Completed("news-only")), "news OR web is sufficient");
            JsonObject stringEvidence = ToolboxOfflineHandler.Completed("complete");
            stringEvidence["output"]![0]!["output"] = "false";
            Check(lab.VerifyRetrieval(stringEvidence), "non-empty string evidence follows Python truthiness");
            stringEvidence["status"] = "incomplete";
            await Throws(() => { lab.VerifyRetrieval(stringEvidence); return Task.CompletedTask; }, "incomplete response cannot verify");
        }
        await ConsoleFlow();
        Console.WriteLine("PASS C# lab 03: wire shapes, auth hops, stable names/version pins, MCP JSON/SSE/pagination/errors, no approvals/follow-up, retrieval guards (offline).");
    }

    private static async Task StepCommands()
    {
        foreach (string command in new[] { "ask", "agent", "toolbox-connection" })
        {
            using var handler = new ToolboxOfflineHandler();
            using var client = new HttpClient(handler);
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { command }, client, _ => Task.FromResult("synthetic-token")), "missing " + command + " prerequisites");
            Check(handler.Requests.Count == 0, "missing cached prerequisites prevent all remote calls");
        }
        using (var handler = new ToolboxOfflineHandler())
        using (var client = new HttpClient(handler))
        {
            Check(await FoundryToolboxProgram.RunAsync(new[] { "webiq-connection" }, client, _ => Task.FromResult("synthetic-token")) == 0, "original metadata command");
            Check(handler.Requests.Count == 1 && handler.Requests[0].Method == "GET", "metadata command reads only");
        }
        foreach (string command in new[] { "toolbox", "discover", "toolbox-connection", "agent", "ask" })
        {
            using var handler = new ToolboxOfflineHandler { DefaultAlreadySelected = command != "toolbox" };
            using var client = new HttpClient(handler);
            Check(await FoundryToolboxProgram.RunAsync(new[] { command }, client, _ => Task.FromResult("synthetic-token")) == 0, command + " across independent invocations");
            var writes = handler.Requests.Where(r => r.Method != "GET" && !r.Path.EndsWith("/mcp")).ToArray();
            if (command == "toolbox")
                Check(writes.Length == 2 && writes[0].Path.EndsWith("/toolboxes/alice01-webiq-toolbox/versions")
                    && writes[1].Method == "PATCH", "toolbox only publishes/promotes");
            if (command == "discover")
                Check(writes.Length == 0 && handler.Requests.Any(r => r.Path.EndsWith("/versions/7/mcp")), "discovery reads existing default without publishing");
            if (command == "toolbox-connection")
                Check(writes.Length == 1 && writes[0].Method == "PUT", "connection only writes its named connection");
            if (command == "agent")
                Check(writes.Length == 1 && writes[0].Path.EndsWith("/agents/alice01-webiq-toolbox-agent/versions"), "agent never silently writes connection/toolbox");
            if (command == "ask")
            {
                Check(writes.Length == 2 && writes[0].Path.EndsWith("/conversations") && writes[1].Path.EndsWith("/responses"), "ask only creates conversation and invokes once");
                Check((string?)writes[1].Body!["agent_reference"]!["version"] == "9", "selected agent version pinned");
            }
            Check(handler.Requests.All(r => r.Key == null), "all step commands ignore API key");
        }
        string statePath = Path.Combine(".workshop", "lab03-alice01.json");
        string state = File.ReadAllText(statePath);
        Check(!state.Contains("synthetic-token") && !state.Contains("must-not-be-read") && !state.Contains(ToolboxWorkshop.Question)
            && !state.Contains("source evidence"), "no tokens/keys/prompts/results saved");
        foreach (string mode in new[] { "stale-connection", "stale-agent" })
        {
            using var handler = new ToolboxOfflineHandler { Mode = mode };
            using var client = new HttpClient(handler);
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "ask" }, client, _ => Task.FromResult("synthetic-token")), "stale refs " + mode);
            Check(handler.Requests.All(r => r.Method == "GET"), "mismatched references skip billable requests");
        }
        using (var handler = new ToolboxOfflineHandler { Mode = "stale-connection" })
        using (var client = new HttpClient(handler))
        {
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "agent" }, client, _ => Task.FromResult("synthetic-token")), "agent requires matching existing connection");
            Check(handler.Requests.All(r => r.Method == "GET"), "agent does not repair connection implicitly");
        }
        using (var handler = new ToolboxOfflineHandler { Mode = "runtime-429" })
        using (var client = new HttpClient(handler))
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "ask" }, client, _ => Task.FromResult("synthetic-token")), "failed new question");
        Check((bool?)JsonNode.Parse(File.ReadAllText(statePath))!["initialCompleted"] == false, "failed question invalidates old completion");
        string before = File.ReadAllText(statePath);
        using (var handler = new ToolboxOfflineHandler { Mode = "connection-202" })
        using (var client = new HttpClient(handler))
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "toolbox-connection" }, client, _ => Task.FromResult("synthetic-token")), "unsuccessful step not saved");
        Check(File.ReadAllText(statePath) == before, "failed connection step leaves selected references unchanged");
        string azure = File.ReadAllText(".env.azure");
        try
        {
            File.AppendAllText(".env.azure", "\nAZURE_AI_PROJECT_ENDPOINT=https://other.services.ai.azure.com/api/projects/other\n");
            using var handler = new ToolboxOfflineHandler();
            using var client = new HttpClient(handler);
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "ask" }, client, _ => Task.FromResult("synthetic-token")), "project scope mismatch");
            Check(handler.Requests.Count == 0, "other project never uses saved references for billable calls");
        }
        finally
        {
            File.WriteAllText(".env.azure", azure);
        }
        JsonObject olderSelection = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        olderSelection["toolboxVersion"] = "6";
        File.WriteAllText(statePath, olderSelection.ToJsonString());
        using (var handler = new ToolboxOfflineHandler { DefaultAlreadySelected = true })
        using (var client = new HttpClient(handler))
            Check(await FoundryToolboxProgram.RunAsync(new[] { "discover" }, client, _ => Task.FromResult("synthetic-token")) == 0, "select existing new default without publishing");
        using (var handler = new ToolboxOfflineHandler())
        using (var client = new HttpClient(handler))
        {
            await Throws(() => FoundryToolboxProgram.RunAsync(new[] { "ask" }, client, _ => Task.FromResult("synthetic-token")), "new selection clears older agent");
            Check(handler.Requests.Count == 0, "selecting another toolbox version requires a matching agent first");
        }
        File.WriteAllText(statePath, "malformed refs");
        Environment.SetEnvironmentVariable("WORKSHOP_PREFIX", "help-must-not-read-env");
        using (var handler = new ToolboxOfflineHandler())
        using (var client = new HttpClient(handler))
        {
            Check(await FoundryToolboxProgram.RunAsync(new[] { "--help" }, client) == 0
                && Environment.GetEnvironmentVariable("WORKSHOP_PREFIX") == "help-must-not-read-env", "help reads no env or cache");
            Check(await FoundryToolboxProgram.RunAsync(new[] { "run" }, client, _ => Task.FromResult("synthetic-token")) == 0, "full run ignores malformed step state");
            Check(handler.Requests.Count(r => r.Path.EndsWith("/responses")) == 1, "full run still asks once");
        }
    }

    public static void AssertWire(ToolboxOfflineHandler handler)
    {
        var requests = handler.Requests;
        Check(requests.All(r => r.Key == null && r.Auth == "Bearer synthetic-token"), "bearer only, no API key");
        var original = requests.Where(r => r.Path.EndsWith("/connections/alice01-webiq-mcp")).ToArray();
        Check(original.Length == 1 && original[0].Method == "GET" && original[0].Query == "?api-version=v1", "original metadata-only GET");
        var toolbox = requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/toolboxes/alice01-webiq-toolbox/versions"));
        JsonNode source = toolbox.Body!["tools"]![0]!;
        Check(source.AsObject().Count == 6 && (string?)source["type"] == "mcp"
            && (string?)source["server_label"] == "webiq" && (string?)source["server_url"] == FoundryWorkshop.McpUrl
            && (string?)source["project_connection_id"] == Settings.ConnectionId
            && (string?)source["require_approval"] == "never"
            && JsonNode.DeepEquals(source["allowed_tools"], new JsonArray("web", "news")), "SDK toolbox serialization");
        string url = Settings.ProjectEndpoint + "/toolboxes/alice01-webiq-toolbox/versions/7/mcp?api-version=v1";
        Check(requests.Where(r => r.Method == "PATCH").All(r => r.Body!.Count == 1
            && (string?)r.Body["default_version"] == "7" && r.Query == "?api-version=v1"), "PATCH default_version snake_case");
        var mcp = requests.Where(r => r.Path.EndsWith("/mcp")).ToArray();
        Check(mcp.Length == 5 && mcp.All(r => r.Url == url), "exact immutable toolbox endpoint");
        Check(mcp[0].Session == null && mcp.Skip(1).All(r => r.Session == "synthetic-session"
            && r.Protocol == handler.Protocol), "MCP session/protocol forwarding");
        Check(mcp[1].Body!["id"] == null && (string?)mcp[3].Body!["params"]!["cursor"] == "next", "notification and cursor");
        var arm = requests.Single(r => r.Method == "PUT");
        Check(arm.Path.EndsWith("/connections/alice01-webiq-toolbox-connection")
            && arm.Query == "?api-version=2025-10-01-preview", "stable ARM connection");
        JsonNode p = arm.Body!["properties"]!;
        Check(p.AsObject().Count == 6 && (string?)p["category"] == "RemoteTool"
            && (string?)p["authType"] == "UserEntraToken" && (string?)p["audience"] == "https://ai.azure.com"
            && (string?)p["target"] == url && (bool?)p["isSharedToAll"] == true
            && JsonNode.DeepEquals(p["metadata"], new JsonObject
            {
                ["type"] = "toolbox_MCP",
                ["toolboxName"] = "alice01-webiq-toolbox",
                ["toolboxVersion"] = "7"
            }), "two-hop auth metadata, no credentials");
        var agent = requests.Single(r => r.Path.EndsWith("/agents/alice01-webiq-toolbox-agent/versions"));
        JsonNode d = agent.Body!["definition"]!;
        JsonNode tool = d["tools"]![0]!;
        Check((string?)d["kind"] == "prompt" && (string?)d["model"] == Settings.Model
            && (string?)d["instructions"] == ToolboxWorkshop.Instructions
            && (string?)tool["server_label"] == "webiq_toolbox" && (string?)tool["server_url"] == url
            && (string?)tool["project_connection_id"] == "alice01-webiq-toolbox-connection"
            && (string?)tool["require_approval"] == "never"
            && JsonNode.DeepEquals(tool["allowed_tools"], new JsonArray("webiq___web", "webiq___news")), "agent attachment");
        var response = requests.Single(r => r.Path.EndsWith("/responses")).Body!;
        Check((string?)response["conversation"] == "conv_offline" && (string?)response["input"] == ToolboxWorkshop.Question
            && JsonNode.DeepEquals(response["agent_reference"], new JsonObject
            {
                ["type"] = "agent_reference",
                ["name"] = "alice01-webiq-toolbox-agent",
                ["version"] = "9"
            }), "pinned agent and no follow-up");
        Check(requests.Where(r => r.Body != null).All(r => !r.Body!.ToJsonString().Contains("synthetic-token")
            && !r.Body.ToJsonString().Contains("WEBIQ_API_KEY")), "tokens/keys not in definitions");
    }

    private static async Task ConsoleFlow()
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), ".toolbox-test-fixture-" + Guid.NewGuid().ToString("N"));
        string cwd = Directory.GetCurrentDirectory();
        string[] names = { "WEBIQ_API_KEY", "WORKSHOP_PREFIX", "AZURE_AI_PROJECT_ENDPOINT",
            "AZURE_AI_PROJECT_RESOURCE_ID", "AZURE_AI_MODEL_DEPLOYMENT_NAME", "AZURE_TENANT_ID" };
        var original = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Directory.CreateDirectory(folder);
        try
        {
            Directory.SetCurrentDirectory(folder);
            Environment.SetEnvironmentVariable("WEBIQ_API_KEY", null);
            File.WriteAllText(".env", "WORKSHOP_PREFIX=wrong01\n");
            File.WriteAllText(".env.azure", "WORKSHOP_PREFIX=alice01\nAZURE_AI_PROJECT_ENDPOINT=" + Settings.ProjectEndpoint
                + "\nAZURE_AI_PROJECT_RESOURCE_ID=" + Settings.ProjectResourceId
                + "\nAZURE_AI_MODEL_DEPLOYMENT_NAME=test-model\nAZURE_TENANT_ID=" + Settings.TenantId + "\n");
            Check(FoundrySettings.Load(".", requireApiKey: false).ApiKey == "", "no key required or read");
            Environment.SetEnvironmentVariable("WEBIQ_API_KEY", "must-not-be-read");
            Check(FoundrySettings.Load(".", requireApiKey: false).ApiKey == "", "existing key ignored");
            using var handler = new ToolboxOfflineHandler();
            using var client = new HttpClient(handler);
            Check(await FoundryToolboxProgram.RunAsync(new[] { "run" }, client, _ => Task.FromResult("synthetic-token")) == 0, "console run");
            AssertWire(handler);
            await StepCommands();
            Check(await FoundryToolboxProgram.RunAsync(new[] { "--help" }, client) == 0, "offline help");
            Check(await FoundryToolboxProgram.RunAsync(new[] { "invalid" }, client) == 2, "invalid command");
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
            foreach (var item in original) Environment.SetEnvironmentVariable(item.Key, item.Value);
            Directory.Delete(folder, true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
    private static async Task Throws(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: expected failure: " + message);
    }
}

public sealed class ToolboxOfflineHandler : HttpMessageHandler
{
    public record Request(string Method, string Url, string Path, string Query, string? Key, string? Auth,
        string? Session, string? Protocol, JsonObject? Body);
    public readonly List<Request> Requests = new();
    public string Mode = "complete";
    public string Protocol = "2025-06-18";
    public bool Sse, DefaultAlreadySelected;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        JsonObject? body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject();
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        string path = request.RequestUri!.AbsolutePath;
        Requests.Add(new(request.Method.Method, request.RequestUri.ToString(), path, request.RequestUri.Query,
            Header("x-apikey"), request.Headers.Authorization?.ToString(), Header("Mcp-Session-Id"), Header("MCP-Protocol-Version"), body));
        if (path.EndsWith("/connections/alice01-webiq-mcp"))
            return Reply(200, new JsonObject
            {
                ["id"] = ToolboxTests.Settings.ConnectionId,
                ["target"] = Mode == "wrong-target" ? "https://example.test/mcp" : FoundryWorkshop.McpUrl
            }.ToJsonString());
        if (path.EndsWith("/mcp"))
        {
            if (Mode == "discovery-401") return Reply(401, "{\"error\":\"synthetic-token\"}");
            if (request.Method == HttpMethod.Delete) return Reply(200, "{}");
            if ((string?)body!["method"] == "notifications/initialized") return Reply(202, "");
            JsonObject result = (string?)body["method"] == "initialize"
                ? new JsonObject { ["protocolVersion"] = Protocol, ["capabilities"] = new JsonObject() }
                : body["params"]?["cursor"] == null
                    ? new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "webiq___web" }), ["nextCursor"] = "next" }
                    : new JsonObject
                    {
                        ["tools"] = Mode == "missing-news" ? new JsonArray()
                        : new JsonArray(new JsonObject { ["name"] = "webiq___news" })
                    };
            if (Mode == "repeated-cursor" && (string?)body["method"] == "tools/list") result["nextCursor"] = "next";
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
            if (Mode == "rpc-error") message["error"] = new JsonObject { ["code"] = -32000, ["message"] = "synthetic-token" };
            else message["result"] = result;
            var reply = Sse ? Reply(200, "id: prime\ndata:\n\n" + "event: message\ndata: " + message.ToJsonString() + "\n\n", "text/event-stream")
                : Reply(200, message.ToJsonString());
            if ((string?)body["method"] == "initialize") reply.Headers.Add("Mcp-Session-Id", "synthetic-session");
            return reply;
        }
        if (path.EndsWith("/toolboxes/alice01-webiq-toolbox/versions")) return Reply(200, "{\"version\":\"7\"}");
        if (request.Method == HttpMethod.Get && path.EndsWith("/toolboxes/alice01-webiq-toolbox/versions/7"))
            return Reply(200, new JsonObject
            {
                ["version"] = "7",
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "mcp",
                    ["server_label"] = "webiq",
                    ["server_url"] = FoundryWorkshop.McpUrl,
                    ["project_connection_id"] = ToolboxTests.Settings.ConnectionId,
                    ["allowed_tools"] = new JsonObject { ["tool_names"] = new JsonArray("news", "web") },
                    ["require_approval"] = "never",
                })
            }.ToJsonString());
        if (path.EndsWith("/toolboxes/alice01-webiq-toolbox"))
            return Reply(200, "{\"default_version\":\"" + (Mode == "promotion-failed" ? "6"
                : request.Method == HttpMethod.Patch || DefaultAlreadySelected ? "7" : "6") + "\"}");
        if (path.EndsWith("/connections/alice01-webiq-toolbox-connection"))
        {
            if (request.Method == HttpMethod.Get)
                return Reply(200, new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["category"] = "RemoteTool",
                        ["authType"] = "UserEntraToken",
                        ["audience"] = "https://ai.azure.com",
                        ["target"] = "https://workshop.services.ai.azure.com/api/projects/workshop/toolboxes/alice01-webiq-toolbox/versions/"
                        + (Mode == "stale-connection" ? "8" : "7") + "/mcp?api-version=v1",
                        ["isSharedToAll"] = false,
                        ["metadata"] = new JsonObject
                        { ["type"] = "toolbox_MCP", ["toolboxName"] = "alice01-webiq-toolbox", ["toolboxVersion"] = "7" },
                    }
                }.ToJsonString());
            return Reply(Mode == "connection-202" ? 202 : 201, "{}");
        }
        if (path.EndsWith("/agents/alice01-webiq-toolbox-agent/versions"))
            return Reply(200, "{\"name\":\"alice01-webiq-toolbox-agent\",\"version\":\"9\"}");
        if (request.Method == HttpMethod.Get && path.EndsWith("/agents/alice01-webiq-toolbox-agent/versions/9"))
            return Reply(200, new JsonObject
            {
                ["name"] = "alice01-webiq-toolbox-agent",
                ["version"] = "9",
                ["definition"] = new JsonObject
                {
                    ["kind"] = "prompt",
                    ["model"] = "test-model",
                    ["instructions"] = ToolboxWorkshop.Instructions,
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "mcp",
                        ["server_label"] = "webiq_toolbox",
                        ["server_url"] = "https://workshop.services.ai.azure.com/api/projects/workshop/toolboxes/alice01-webiq-toolbox/versions/7/mcp?api-version=v1",
                        ["project_connection_id"] = "alice01-webiq-toolbox-connection",
                        ["allowed_tools"] = new JsonObject { ["tool_names"] = new JsonArray("webiq___news", "webiq___web") },
                        ["require_approval"] = Mode == "stale-agent" ? "always" : "never",
                    }),
                }
            }.ToJsonString());
        if (path.EndsWith("/conversations")) return Reply(200, "{\"id\":\"conv_offline\"}");
        if (path.EndsWith("/responses"))
        {
            if (Mode == "runtime-429") return Reply(429, "{\"error\":\"synthetic failure\"}");
            if (Mode == "approval") return Reply(200, "{\"status\":\"completed\",\"output\":[{\"type\":\"mcp_approval_request\"}]}");
            if (Mode == "failed") return Reply(200, "{\"status\":\"failed\",\"output\":[]}");
            return Reply(200, Completed(Mode).ToJsonString());
        }
        throw new Exception("Unexpected fake HTTP request: " + request.Method + " " + path);
    }

    public static JsonObject Completed(string mode)
    {
        var output = new JsonArray(new JsonObject
        {
            ["type"] = "mcp_call",
            ["name"] = mode == "wrong-tool" ? "webiq___browse" : mode == "news-only" ? "webiq___news" : "webiq___web",
            ["server_label"] = mode == "wrong-label" ? "webiq" : "webiq_toolbox",
            ["arguments"] = "public query",
            ["output"] = mode == "empty-output" ? "" : "source evidence",
            ["error"] = mode == "tool-error" ? "failed" : null,
        });
        if (mode != "no-answer")
            output.Add(new JsonObject
            {
                ["type"] = "message",
                ["content"] = new JsonArray(new JsonObject
                { ["type"] = "output_text", ["text"] = mode == "no-url" ? "uncited answer" : "Answer https://example.test/source" })
            });
        return new JsonObject { ["status"] = "completed", ["output"] = output };
    }

    private static HttpResponseMessage Reply(int status, string text, string type = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, type) };
}
