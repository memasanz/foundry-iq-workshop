import ast
import json
import shutil
import unittest

import nbformat

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional deprecated .NET Interactive tool is not installed.")
class CSharpFoundryNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        notebook_runner.CSharpNotebookTests.setUpClass.__func__(cls)
        (cls.path / ".env").write_text("WEBIQ_API_KEY=old-key\nWORKSHOP_PREFIX=alice01\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(
            "WEBIQ_API_KEY=synthetic-test-key\n"
            "AZURE_AI_PROJECT_ENDPOINT=https://workshop.services.ai.azure.com/api/projects/workshop\n"
            "AZURE_AI_PROJECT_RESOURCE_ID=/subscriptions/00000000-0000-0000-0000-000000000000/"
            "resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/test/projects/workshop\n"
            "AZURE_AI_MODEL_DEPLOYMENT_NAME=test-model\n"
            "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n",
            encoding="utf-8",
        )
        cls.notebook = nbformat.read(ROOT / "c-sharp-notebooks" / "02-foundry-web-iq-agent.ipynb", as_version=4)
        nbformat.validate(cls.notebook)

    def execute(self, mode, checks):
        handler = r'''
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text;

public class FoundryOfflineHandler : HttpMessageHandler
{
    public List<(string Path, string Query, string Key, string Auth, JsonObject Body)> Requests =
        new List<(string, string, string, string, JsonObject)>();
    public string Mode = "complete";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        JsonObject body = request.Content == null ? null :
            JsonNode.Parse(await request.Content.ReadAsStringAsync()).AsObject();
        string path = request.RequestUri.AbsolutePath;
        Requests.Add((path, request.RequestUri.Query,
            request.Headers.TryGetValues("x-apikey", out var keys) ? keys.Single() : null,
            request.Headers.Authorization?.ToString(), body));
        string text;
        int status = 200;
        if (path == "/v3/mcp")
        {
            if (Mode == "discovery-401")
                return Reply(401, "{\"error\":\"synthetic authentication failure\"}");
            if (request.Method == HttpMethod.Delete)
                return Reply(200, "{}");
            if ((string)body["method"] == "notifications/initialized")
                return Reply(202, "");
            JsonObject result = (string)body["method"] == "initialize"
                ? JsonNode.Parse("{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}").AsObject()
                : body["params"]["cursor"] == null
                    ? JsonNode.Parse("{\"tools\":[{\"name\":\"web\",\"description\":\"Search\",\"inputSchema\":{}}],\"nextCursor\":\"next\"}").AsObject()
                    : JsonNode.Parse("{\"tools\":[{\"name\":\"browse\",\"description\":\"Read\",\"inputSchema\":{}}]}").AsObject();
            text = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"].DeepClone(), ["result"] = result }.ToJsonString();
            var response = Reply(200, "event: message\ndata: " + text + "\n\n", "text/event-stream");
            if ((string)body["method"] == "initialize")
                response.Headers.Add("Mcp-Session-Id", "synthetic-session");
            return response;
        }
        if (path.Contains("/connections/"))
            text = "{\"id\":\"connection\"}";
        else if (path.EndsWith("/agents"))
            text = "{\"data\":[],\"has_more\":false}";
        else if (path.EndsWith("/versions"))
            text = "{\"name\":\"alice01-webiq-agent\",\"version\":\"7\"}";
        else if (path.EndsWith("/conversations"))
            text = "{\"id\":\"conv_offline\"}";
        else if (path.EndsWith("/responses"))
        {
            if (Mode == "runtime-429")
                return Reply(429, "{\"error\":\"synthetic rate limit\"}");
            text = "{\"id\":\"resp_offline\",\"status\":\"completed\",\"output\":["
                + "{\"type\":\"mcp_call\",\"name\":\"web\",\"server_label\":\"webiq\","
                + "\"arguments\":\"public query\",\"output\":\"source evidence\",\"error\":null},"
                + "{\"type\":\"message\",\"content\":[{\"type\":\"output_text\","
                + "\"text\":\"Answer https://example.test/source\"}]}]}";
        }
        else
            throw new Exception("Unexpected offline request: " + path);
        return Reply(status, text);
    }
    static HttpResponseMessage Reply(int status, string text, string type = "application/json")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(text, Encoding.UTF8, type) };
        if (status == 429) response.Headers.Add("retry-after", "2");
        return response;
    }
}
client.Dispose();
FoundryOfflineHandler handler = new FoundryOfflineHandler();
client = new HttpClient(handler);
lab = new FoundryWorkshop(client, settings, resource => Task.FromResult("synthetic-azure-token"));
'''
        handler += f'\nhandler.Mode = "{mode}";'
        return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)

    def test_actual_cells_match_python_agent_policy_and_always_follow_up(self):
        python = nbformat.read(ROOT / "python-notebooks" / "02-foundry-web-iq-agent.ipynb", as_version=4)
        source = next(cell.source for cell in python.cells if cell.id == "create-agent")
        definition = next(
            node for node in ast.walk(ast.parse(source))
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
            and node.func.id == "PromptAgentDefinition"
        )
        instructions = ast.literal_eval(next(keyword.value for keyword in definition.keywords if keyword.arg == "instructions"))
        literal = json.dumps(instructions)
        self.execute("complete", r'''
if (availableTools.Count != 2 || !connectionConfigured || !grounded || followUp == null)
    throw new Exception("Notebook flow did not complete");
if (settings.ApiKey != "synthetic-test-key") throw new Exception("Settings precedence failed");
var calls = handler.Requests.Where(item => item.Path.EndsWith("/responses")).ToArray();
if (calls.Length != 2 || calls.Any(item => (string)item.Body["conversation"] != "conv_offline" ||
    (string)item.Body["agent_reference"]["version"] != "7"))
    throw new Exception("Follow-up did not use the same conversation and pinned version");
var created = handler.Requests.Single(item => item.Path.EndsWith("/versions")).Body["definition"];
if (!JsonNode.DeepEquals(created["tools"][0]["allowed_tools"], new JsonArray("web", "browse")) ||
    (string)created["tools"][0]["require_approval"] != "never" ||
    (string)created["tools"][0]["project_connection_id"] != settings.ConnectionId)
    throw new Exception("Agent tool policy differs");
if (created.ToJsonString().Contains(settings.ApiKey)) throw new Exception("Key leaked into agent definition");
''' + f'''
if ((string)created["instructions"] != {literal})
    throw new Exception("Instructions differ from Python");
''')

    def test_discovery_401_clears_state_and_skips_azure_writes(self):
        self.execute("discovery-401", r'''
if (availableTools != null || connectionConfigured || agent != null ||
    conversation != null || response != null || followUp != null || grounded)
    throw new Exception("Failed discovery retained results");
if (handler.Requests.Count != 1 || handler.Requests[0].Path != "/v3/mcp")
    throw new Exception("Failed discovery caused Azure requests");
''')

    def test_runtime_429_clears_response_and_skips_follow_up(self):
        self.execute("runtime-429", r'''
if (response != null || followUp != null || grounded)
    throw new Exception("Rate limit retained an answer");
if (handler.Requests.Count(item => item.Path.EndsWith("/responses")) != 1)
    throw new Exception("Failed response was retried or followed up");
''')


if __name__ == "__main__":
    unittest.main()
