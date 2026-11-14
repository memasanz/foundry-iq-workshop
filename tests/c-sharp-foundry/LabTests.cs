using System.Net;
using System.Text;
using System.Text.Json.Nodes;

public static class LabTests
{
    private static readonly FoundrySettings Settings = new("synthetic-test-key", "alice01",
        "https://workshop.services.ai.azure.com/api/projects/workshop",
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/test/projects/workshop",
        "test-model", "00000000-0000-0000-0000-000000000000");

    public static async Task Main()
    {
        await Discovery();
        await AgentFlow();
        await RuntimeFailures();
        await ConsoleFlow();
        Console.WriteLine("PASS C# lab 02: MCP JSON/SSE/session/pagination, ARM connection, agent reuse, Responses, errors and follow-up (offline).");
    }

    private static async Task Discovery()
    {
        foreach (bool sse in new[] { false, true })
        {
            using var handler = new Handler { Sse = sse };
            using var client = new HttpClient(handler);
            JsonArray? tools = await new FoundryWorkshop(client, Settings).DiscoverToolsAsync();
            Check(tools?.Count == 2, "all MCP pages collected");
            Check(handler.Requests.Count == 5, "initialize, notification, two pages, session delete");
            Check(handler.Requests.All(request => request.Key == Settings.ApiKey), "MCP key header");
            Check(handler.Requests[0].Session == null, "initialization has no session");
            Check(handler.Requests.Skip(1).All(request => request.Session == "synthetic-session"
                && request.Protocol == "2025-06-18"), "negotiated headers forwarded");
            Check(handler.Requests.Take(4).All(request => request.Accept.Contains("application/json")
                && request.Accept.Contains("text/event-stream")), "both MCP response types accepted");
            Check(handler.Requests[1].Body!["id"] == null, "initialized notification has no request ID");
            Check((string?)handler.Requests[3].Body!["params"]!["cursor"] == "next-page", "pagination cursor");
        }
        foreach (int status in new[] { 401, 429 })
        {
            using var handler = new Handler { DiscoveryStatus = status };
            using var client = new HttpClient(handler);
            using var output = new OutputCapture();
            Check(await new FoundryWorkshop(client, Settings).DiscoverToolsAsync() == null, "HTTP discovery failure clears tools");
            Check(output.Text.Contains("HTTP: " + status), "HTTP status printed");
            Check(!output.Text.Contains(Settings.ApiKey), "echoed key redacted");
            Check(!output.Text.Contains(new string('x', 8001)), "body bounded");
            if (status == 429)
                Check(output.Text.Contains("retry-after: 2"), "MCP retry header printed");
        }
        using (var handler = new Handler { FailTransport = true })
        using (var client = new HttpClient(handler))
            Check(await new FoundryWorkshop(client, Settings).DiscoverToolsAsync() == null, "MCP transport failure handled");
        using (var handler = new Handler { BrokenStream = true })
        using (var client = new HttpClient(handler))
            Check(await new FoundryWorkshop(client, Settings).DiscoverToolsAsync() == null, "MCP interrupted SSE read handled");
        using (var handler = new Handler { MissingWeb = true })
        using (var client = new HttpClient(handler))
            await Throws(() => new FoundryWorkshop(client, Settings).DiscoverToolsAsync(), "missing web remains a configuration error");
    }

    private static async Task AgentFlow()
    {
        foreach (string mode in new[] { "new", "legacy", "stable" })
        {
            using var handler = new Handler { AgentMode = mode };
            using var client = new HttpClient(handler);
            var resources = new List<string>();
            var lab = new FoundryWorkshop(client, Settings, resource =>
            {
                resources.Add(resource);
                return Task.FromResult("synthetic-azure-token");
            });
            Check(await lab.ConfigureConnectionAsync(), "ARM connection succeeds");
            JsonObject properties = handler.Requests[0].Body!["properties"]!.AsObject();
            Check((string?)properties["category"] == "RemoteTool" && (string?)properties["authType"] == "CustomKeys"
                && (string?)properties["target"] == FoundryWorkshop.McpUrl, "connection shape");
            Check((string?)properties["credentials"]!["keys"]!["x-apikey"] == Settings.ApiKey, "key only in ARM connection");
            JsonObject definition = FoundryWorkshop.CreateDefinition(Settings.Model);
            JsonObject? agent = await lab.CreateAgentVersionAsync(definition);
            string expectedName = mode == "legacy" ? "alice01-webiq-agent-old" : "alice01-webiq-agent";
            Check((string?)agent?["name"] == expectedName, "matching agent selected");
            var created = handler.Requests.Single(request => request.Path.Contains("/versions"));
            JsonNode tool = created.Body!["definition"]!["tools"]![0]!;
            Check((string?)tool["project_connection_id"] == Settings.ConnectionId, "project connection referenced");
            Check(JsonNode.DeepEquals(tool["allowed_tools"], new JsonArray("web", "browse"))
                && (string?)tool["require_approval"] == "never", "tool policy matches Python");
            Check(!created.Body.ToJsonString().Contains(Settings.ApiKey), "agent definition does not contain the key");
            Check(definition["tools"]![0]!["project_connection_id"] == null, "input definition not mutated");
            JsonObject? conversation = await lab.CreateConversationAsync();
            JsonObject? response = await lab.AskAsync(FoundryWorkshop.Question, conversation!, agent!);
            Check(lab.VerifyRetrieval(response), "completed response has genuine retrieval");
            Check(await lab.AskAsync(FoundryWorkshop.FollowUpQuestion, conversation!, agent!) != null, "follow-up succeeds");
            var requests = handler.Requests.Where(request => request.Path.EndsWith("/responses")).ToArray();
            Check(requests.Length == 2 && requests.All(request =>
                (string?)request.Body!["conversation"] == "conv_test"
                && (string?)request.Body!["agent_reference"]!["name"] == expectedName
                && (string?)request.Body!["agent_reference"]!["version"] == "2"), "same pinned version and conversation");
            Check(handler.Requests.Skip(1).All(request => request.Authorization == "Bearer synthetic-azure-token"
                && request.Key == null), "Azure token does not become an MCP key header");
            Check(resources[0] == "https://management.azure.com/" && resources.Skip(1).All(resource =>
                resource == "https://ai.azure.com/"), "correct token audiences");
            Check(handler.Requests.Any(request => request.Query.Contains("after=first-page")), "agent pages followed");
        }
        using (var handler = new Handler { AgentMode = "conflict" })
        using (var client = new HttpClient(handler))
        {
            await Throws(() => new FoundryWorkshop(client, Settings, _ => Task.FromResult("token"))
                .CreateAgentVersionAsync(FoundryWorkshop.CreateDefinition(Settings.Model)), "wrong-kind stable agent refused");
            Check(handler.Requests.All(request => request.Method == "GET"), "conflict makes no agent write");
        }
        using (var handler = new Handler { ConnectionStatus = 202 })
        using (var client = new HttpClient(handler))
            Check(!await new FoundryWorkshop(client, Settings, _ => Task.FromResult("token")).ConfigureConnectionAsync(),
                "asynchronous connection not treated as provisioned");
    }

    private static async Task RuntimeFailures()
    {
        var conversation = new JsonObject { ["id"] = "conv_test" };
        var agent = new JsonObject { ["name"] = "alice01-webiq-agent", ["version"] = "2" };
        foreach (string mode in new[] { "429", "failed", "incomplete", "transport", "timeout" })
        {
            using var handler = new Handler { ResponseMode = mode };
            using var client = new HttpClient(handler);
            var lab = new FoundryWorkshop(client, Settings, _ => Task.FromResult("synthetic-azure-token"));
            using var output = new OutputCapture();
            JsonObject? response = await lab.AskAsync("public question", conversation, agent);
            Check(response == null && !lab.VerifyRetrieval(response), "runtime failure does not become a stale answer");
            Check(handler.Requests.Count == 1, "no hidden runtime retry");
            if (mode == "429")
                Check(output.Text.Contains("retry-after: 2") && output.Text.Contains("HTTP: 429"), "runtime retry guidance");
        }
        using (var handler = new Handler { ResponseMode = "approval" })
        using (var client = new HttpClient(handler))
            await Throws(() => new FoundryWorkshop(client, Settings, _ => Task.FromResult("token"))
                .AskAsync("public question", conversation, agent), "unexpected approval is not suppressed");
        using (var handler = new Handler())
        using (var client = new HttpClient(handler))
        {
            var lab = new FoundryWorkshop(client, Settings);
            foreach (string mode in new[] { "no-tools", "tool-error", "no-answer" })
                Check(!lab.VerifyRetrieval(Completed(mode)), "unverified response not claimed as grounded");
        }
    }

    private static async Task ConsoleFlow()
    {
        string oldDirectory = Directory.GetCurrentDirectory();
        string folder = Path.Combine(Directory.GetCurrentDirectory(), ".foundry-csharp-tests-" + Guid.NewGuid().ToString("N"));
        string[] names = { "WEBIQ_API_KEY", "WORKSHOP_PREFIX", "AZURE_AI_PROJECT_ENDPOINT",
            "AZURE_AI_PROJECT_RESOURCE_ID", "AZURE_AI_MODEL_DEPLOYMENT_NAME", "AZURE_TENANT_ID" };
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Directory.CreateDirectory(folder);
        try
        {
            Directory.SetCurrentDirectory(folder);
            File.WriteAllText(".env", "WEBIQ_API_KEY=old-key\nWORKSHOP_PREFIX=alice01\n");
            File.WriteAllText(".env.azure", "WEBIQ_API_KEY=synthetic-test-key\n"
                + "AZURE_AI_PROJECT_ENDPOINT=" + Settings.ProjectEndpoint + "\n"
                + "AZURE_AI_PROJECT_RESOURCE_ID=" + Settings.ProjectResourceId + "\n"
                + "AZURE_AI_MODEL_DEPLOYMENT_NAME=test-model\nAZURE_TENANT_ID=" + Settings.TenantId + "\n");
            foreach (string mode in new[] { "complete", "429", "no-tools" })
            {
                using var handler = new Handler { ResponseMode = mode };
                using var client = new HttpClient(handler);
                int result = await FoundryWebIqProgram.RunAsync(new[] { "run", "custom public query" },
                    client, _ => Task.FromResult("synthetic-azure-token"));
                Check(result == (mode == "complete" ? 0 : 1), "console exit signals verified success/failure");
                var responses = handler.Requests.Where(request => request.Path.EndsWith("/responses")).ToArray();
                Check(responses.Length == (mode == "429" ? 1 : 2), "follow-up always runs after a completed first response");
                Check((string?)responses[0].Body!["input"] == "custom public query", "custom question passed");
            }
            using (var handler = new Handler { DiscoveryStatus = 401 })
            using (var client = new HttpClient(handler))
            {
                Check(await FoundryWebIqProgram.RunAsync(new[] { "run" }, client,
                    _ => throw new Exception("Azure must not be called after discovery fails")) == 1,
                    "full run stops on MCP failure");
                Check(handler.Requests.All(request => request.Path == "/v3/mcp"), "no resource creation after discovery failure");
            }
            File.Delete(".env.azure");
            foreach (string name in names.Skip(1))
                Environment.SetEnvironmentVariable(name, null);
            using (var handler = new Handler())
            using (var client = new HttpClient(handler))
            {
                Check(await FoundryWebIqProgram.RunAsync(new[] { "discover" }, client) == 0,
                    "discovery needs no Azure settings");
                Check(await FoundryWebIqProgram.RunAsync(new[] { "--help" }, client) == 0, "help works offline");
                Check(await FoundryWebIqProgram.RunAsync(new[] { "invalid" }, client) == 2, "invalid command rejected");
            }
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText("az.cmd", "@echo off\r\necho {\"accessToken\":\"synthetic-cli-token\"}\r\n");
                Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + originalPath);
                using var handler = new Handler();
                using var client = new HttpClient(handler);
                Check(await new FoundryWorkshop(client, Settings).ConfigureConnectionAsync(), "native Azure CLI adapter works");
                Check(handler.Requests[0].Authorization == "Bearer synthetic-cli-token", "CLI JSON token parsed without printing");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDirectory);
            foreach (var setting in original)
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            File.Delete(Path.Combine(folder, ".env"));
            File.Delete(Path.Combine(folder, ".env.azure"));
            File.Delete(Path.Combine(folder, "az.cmd"));
            Directory.Delete(folder);
        }
    }

    private static JsonObject Completed(string mode = "complete")
    {
        var output = new JsonArray();
        if (mode != "no-tools")
            output.Add(new JsonObject
            {
                ["type"] = "mcp_call", ["name"] = "web", ["server_label"] = "webiq",
                ["arguments"] = "{\"query\":\"public query\"}", ["output"] = "source evidence",
                ["error"] = mode == "tool-error" ? "retrieval failed" : null,
            });
        if (mode != "no-answer")
            output.Add(new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Answer https://example.test/source" }),
            });
        return new JsonObject { ["id"] = "resp_test", ["status"] = "completed", ["output"] = output };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception("FAIL: " + message);
    }

    private static async Task Throws(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("FAIL: " + message);
    }

    private sealed class OutputCapture : IDisposable
    {
        private readonly TextWriter original = Console.Out;
        private readonly StringWriter writer = new();
        public string Text => writer.ToString();
        public OutputCapture() => Console.SetOut(writer);
        public void Dispose() { Console.SetOut(original); writer.Dispose(); }
    }

    private sealed record Request(string Method, string Path, string Query, string? Key,
        string? Authorization, string? Session, string? Protocol, string Accept, JsonObject? Body);

    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<Request> Requests = new();
        public bool Sse, MissingWeb, FailTransport, BrokenStream;
        public int DiscoveryStatus = 200, ConnectionStatus = 200;
        public string AgentMode = "new", ResponseMode = "complete";
        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            JsonObject? body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!.AsObject();
            Requests.Add(new Request(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                Header(request, "x-apikey"), request.Headers.Authorization?.ToString(), Header(request, "Mcp-Session-Id"),
                Header(request, "MCP-Protocol-Version"), request.Headers.Accept.ToString(), body));
            string path = request.RequestUri.AbsolutePath;
            if (path == "/v3/mcp")
            {
                if (FailTransport)
                    throw new HttpRequestException("synthetic connection failure");
                if (BrokenStream)
                {
                    var failed = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) };
                    failed.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                    return failed;
                }
                if (DiscoveryStatus != 200)
                    return Reply(DiscoveryStatus, "synthetic-test-key " + new string('x', 9000));
                if (request.Method == HttpMethod.Delete)
                    return Reply(200, "{}");
                if ((string?)body!["method"] == "notifications/initialized")
                    return Reply(202, "");
                string method = (string)body["method"]!;
                JsonObject result = method == "initialize"
                    ? new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() }
                    : body["params"]?["cursor"] == null
                        ? new JsonObject { ["tools"] = new JsonArray(new JsonObject
                            { ["name"] = MissingWeb ? "news" : "web", ["description"] = "Find evidence", ["inputSchema"] = new JsonObject() }),
                            ["nextCursor"] = "next-page" }
                        : new JsonObject { ["tools"] = new JsonArray(new JsonObject
                            { ["name"] = "browse", ["description"] = "Read a URL", ["inputSchema"] = new JsonObject() }) };
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result };
                HttpResponseMessage reply = Sse
                    ? Reply(200, "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\n\n"
                        + "event: message\ndata: " + response.ToJsonString() + "\n\n", "text/event-stream")
                    : Reply(200, response.ToJsonString());
                if (method == "initialize")
                    reply.Headers.Add("Mcp-Session-Id", "synthetic-session");
                return reply;
            }
            if (path.Contains("/connections/"))
                return Reply(ConnectionStatus, "{\"id\":\"connection\"}");
            if (path.EndsWith("/agents"))
            {
                var agents = new JsonArray();
                if (request.RequestUri.Query.Contains("after="))
                {
                    if (AgentMode != "new")
                    {
                        string name = AgentMode == "legacy" ? "alice01-webiq-agent-old" : "alice01-webiq-agent";
                        agents.Add(new JsonObject { ["name"] = name,
                            ["versions"] = new JsonObject { ["latest"] = new JsonObject { ["definition"] =
                                AgentMode == "conflict" ? new JsonObject { ["kind"] = "hosted" } : FoundryWorkshop.CreateDefinition("old-model") } } });
                    }
                    return Reply(200, new JsonObject { ["data"] = agents, ["has_more"] = false }.ToJsonString());
                }
                agents.Add(new JsonObject { ["name"] = "unrelated-agent" });
                return Reply(200, new JsonObject { ["data"] = agents, ["last_id"] = "first-page", ["has_more"] = true }.ToJsonString());
            }
            if (path.EndsWith("/versions"))
                return Reply(200, new JsonObject { ["name"] = Uri.UnescapeDataString(path.Split('/')[^2]), ["version"] = "2" }.ToJsonString());
            if (path.EndsWith("/conversations"))
                return Reply(200, "{\"id\":\"conv_test\"}");
            if (path.EndsWith("/responses"))
            {
                if (ResponseMode == "transport")
                    throw new HttpRequestException("synthetic transport failure");
                if (ResponseMode == "timeout")
                    throw new TaskCanceledException("synthetic timeout");
                if (ResponseMode == "429")
                    return Reply(429, "{\"error\":\"synthetic rate limit\"}");
                if (ResponseMode == "approval")
                    return Reply(200, "{\"status\":\"completed\",\"output\":[{\"type\":\"mcp_approval_request\"}]}");
                if (ResponseMode is "failed" or "incomplete")
                    return Reply(200, new JsonObject { ["id"] = "resp_test", ["status"] = ResponseMode,
                        ["error"] = new JsonObject { ["message"] = "synthetic execution failure" } }.ToJsonString());
                return Reply(200, Completed(ResponseMode).ToJsonString());
            }
            throw new Exception("Unexpected offline request: " + path);
        }
        private static HttpResponseMessage Reply(int status, string body, string type = "application/json")
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type) };
            if (status == 429)
                response.Headers.Add("retry-after", "2");
            return response;
        }

        private sealed class FailingStream : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                throw new IOException("synthetic interrupted SSE stream");
        }
    }
}
