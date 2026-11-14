"""One-shot generator for the fully inline optional C# lab 03 notebook."""
import ast
import json
from pathlib import Path

root = Path(__file__).resolve().parent
python = json.loads((root / "python-notebooks" / "03-foundry-web-iq-toolbox.ipynb").read_text(encoding="utf-8"))
template = json.loads((root / "c-sharp-notebooks" / "02-foundry-web-iq-agent.ipynb").read_text(encoding="utf-8"))
sources = {cell["id"]: "".join(cell["source"]) for cell in python["cells"]}
tree = ast.parse(sources["create-agent"])
definition = next(n for n in ast.walk(tree) if isinstance(n, ast.Call)
                  and isinstance(n.func, ast.Name) and n.func.id == "PromptAgentDefinition")
instructions = ast.literal_eval(next(k.value for k in definition.keywords if k.arg == "instructions"))
question = ast.literal_eval(ast.parse(sources["ask"]).body[0].value.args[0])

shared = (root / "c-sharp-console-apps" / "02-foundry-web-iq-agent" / "FoundryWorkshop.cs").read_text(encoding="utf-8")
env = (root / "c-sharp-console-apps" / "01-web-iq-apis" / "Workshop.cs").read_text(encoding="utf-8")
# Embed only the env loader and protocol/token/HTTP helpers actually used by this lab.
# The resulting notebook contains full code, never imports or reads console-app source files.
env_code = "public static class Workshop\n{\n" + env[
    env.index("    public static void LoadEnvFile"):env.index("    public static async Task<JsonObject> ReadResponseAsync")
] + "}\n"
settings_code = shared[shared.index("public sealed class FoundrySettings"):shared.index("public sealed class FoundryWorkshop")]
transport = "public sealed class FoundryWorkshop\n{\n    public const string McpUrl = \"https://api.microsoft.ai/v3/mcp\";\n"
transport += shared[shared.index("    private readonly HttpClient client;"):shared.index("    public static JsonObject CreateDefinition")]
transport += shared[shared.index("    public async Task<JsonArray?> DiscoverToolsAsync"):shared.index("    public async Task<bool> ConfigureConnectionAsync")]
transport += shared[shared.index("    public static string OutputText"): ]
using = shared[:shared.index("public sealed class FoundrySettings")]
setup = """// Self-contained setup: BCL only (no NuGet, no source imports, no console-app folder required).
// Load root settings in order; do not read or require WEBIQ_API_KEY in this toolbox lab.
""" + using + env_code + settings_code + transport + """
FoundrySettings settings = FoundrySettings.Load("..", requireApiKey: false);
HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
FoundryWorkshop lab = new FoundryWorkshop(client, settings);
Func<string, Task<string>> tokenProvider = lab.GetAzureCliTokenAsync;
string toolboxName = settings.Prefix + "-webiq-toolbox";
string toolboxConnectionName = toolboxName + "-connection";
string toolboxConnectionId = settings.ProjectResourceId + "/connections/" + toolboxConnectionName;
string agentName = settings.Prefix + "-webiq-toolbox-agent";

// A failed request throws so later cells cannot silently continue with an invalid result.
async Task<JsonObject> RequestAsync(HttpMethod method, string url, JsonObject body = null)
{
    return await lab.SendJsonAsync(method, url, "https://ai.azure.com/", body)
        ?? throw new InvalidOperationException("Foundry request failed. Resolve the error and rerun the failed section.");
}
string Required(JsonObject node, string property)
{
    string value = (string)node[property];
    if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("Service response omitted " + property);
    return value;
}
Console.WriteLine("Toolbox: " + toolboxName + " | connection: " + toolboxConnectionName);
"""
code = {
    "setup": setup,
    "get-webiq-connection": """// GET metadata only: never request credentials/listKeys, never overwrite the lab 02 connection.
JsonObject webIqConnection = await RequestAsync(HttpMethod.Get,
    settings.ProjectEndpoint + "/connections/" + settings.ConnectionName + "?api-version=v1");
if ((string)webIqConnection["target"] != FoundryWorkshop.McpUrl)
    throw new InvalidOperationException("The connection does not target Web IQ MCP. Check WORKSHOP_PREFIX and rerun lab 02 connection setup.");
string webIqConnectionId = Required(webIqConnection, "id");
Console.WriteLine("Using Web IQ connection: " + settings.ConnectionName);
""",
    "create-toolbox": """// MCPToolboxTool wire shape, checked against azure-ai-projects: type=mcp and snake_case fields.
// Publish a new version under the SAME stable name, using the original full connection ID.
string toolboxBaseUrl = settings.ProjectEndpoint + "/toolboxes/" + toolboxName;
JsonObject toolboxVersion = await RequestAsync(HttpMethod.Post, toolboxBaseUrl + "/versions?api-version=v1",
    new JsonObject
    {
        ["description"] = "Workshop Web IQ web and news search, reusable through a Foundry toolbox.",
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "mcp", ["server_label"] = "webiq", ["server_url"] = FoundryWorkshop.McpUrl,
            ["project_connection_id"] = webIqConnectionId,
            ["allowed_tools"] = new JsonArray("web", "news"), ["require_approval"] = "never",
        }),
    });
string toolboxVersionNumber = Required(toolboxVersion, "version");
JsonObject toolbox = await RequestAsync(HttpMethod.Get, toolboxBaseUrl + "?api-version=v1");
if ((string)toolbox["default_version"] != toolboxVersionNumber)
    toolbox = await RequestAsync(HttpMethod.Patch, toolboxBaseUrl + "?api-version=v1",
        new JsonObject { ["default_version"] = toolboxVersionNumber });
if ((string)toolbox["default_version"] != toolboxVersionNumber)
    throw new InvalidOperationException("Toolbox default promotion did not select the published version.");
// Pin the exact immutable version; promoting a default alone does not change this URL.
string toolboxMcpUrl = toolboxBaseUrl + "/versions/" + Uri.EscapeDataString(toolboxVersionNumber) + "/mcp?api-version=v1";
Console.WriteLine("Toolbox version: " + toolboxVersionNumber + " | default: " + (string)toolbox["default_version"]);
Console.WriteLine("Toolbox MCP endpoint: " + toolboxMcpUrl);
""",
    "discover-toolbox": """// First auth hop: Entra bearer to Foundry, not an x-apikey header to Web IQ.
// Azure CLI --resource https://ai.azure.com/ equals the SDK scope https://ai.azure.com/.default.
// The inline helper initializes MCP, forwards session/protocol headers, pages tools/list and closes the session.
string[] toolNames = new[] { "webiq___web", "webiq___news" };
async Task<JsonArray> DiscoverToolboxAsync()
{
    string token = await tokenProvider("https://ai.azure.com/");
    return await lab.DiscoverToolsAsync(toolboxMcpUrl, token, toolNames)
        ?? throw new InvalidOperationException("Toolbox discovery failed. Resolve the error before continuing.");
}
JsonArray availableTools = await DiscoverToolboxAsync();
""",
    "create-toolbox-connection": """// Second connection is for agent -> Foundry toolbox, NOT a new Web IQ connection.
// ARM UserEntraToken stores an audience, no API key and no pasted bearer token.
// Retargeting this stable connection means older mismatched agent versions must not be reused.
JsonObject toolboxConnection = await lab.SendJsonAsync(HttpMethod.Put,
    "https://management.azure.com" + toolboxConnectionId + "?api-version=2025-10-01-preview",
    "https://management.azure.com/", new JsonObject
    {
        ["properties"] = new JsonObject
        {
            ["category"] = "RemoteTool", ["authType"] = "UserEntraToken",
            ["target"] = toolboxMcpUrl, ["audience"] = "https://ai.azure.com", ["isSharedToAll"] = true,
            ["metadata"] = new JsonObject
            {
                ["type"] = "toolbox_MCP", ["toolboxName"] = toolboxName,
                ["toolboxVersion"] = toolboxVersionNumber,
            },
        },
    }) ?? throw new InvalidOperationException("Agent-to-toolbox connection was not provisioned (only HTTP 200/201 is accepted).");
Console.WriteLine("Agent-to-toolbox connection: " + toolboxConnectionName);
""",
    "create-agent": """// Publish a matching agent version at the stable name. Only the toolbox URL is attached.
// project_connection_id deliberately uses the NEW connection NAME, exactly like Python lab 03.
string instructions = """ + json.dumps(instructions) + """;
JsonObject agent = await RequestAsync(HttpMethod.Post,
    settings.ProjectEndpoint + "/agents/" + agentName + "/versions?api-version=v1",
    new JsonObject
    {
        ["definition"] = new JsonObject
        {
            ["kind"] = "prompt", ["model"] = settings.Model, ["instructions"] = instructions,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "mcp", ["server_label"] = "webiq_toolbox", ["server_url"] = toolboxMcpUrl,
                ["project_connection_id"] = toolboxConnectionName,
                ["allowed_tools"] = new JsonArray("webiq___web", "webiq___news"), ["require_approval"] = "never",
            }),
        },
    });
JsonObject conversation = await RequestAsync(HttpMethod.Post, settings.ProjectEndpoint + "/openai/v1/conversations",
    new JsonObject { ["metadata"] = new JsonObject { ["attendee"] = settings.Prefix } });
JsonObject agentReference = new JsonObject
{
    ["type"] = "agent_reference", ["name"] = Required(agent, "name"), ["version"] = Required(agent, "version"),
};
Console.WriteLine("Agent: " + (string)agent["name"] + " | version: " + (string)agent["version"]);
""",
    "agent-helper": """// No ReadLine/approval loop, silent approvals, automatic retries or follow-up question.
async Task<JsonObject> AskAgentAsync(string question)
{
    JsonObject result = await RequestAsync(HttpMethod.Post, settings.ProjectEndpoint + "/openai/v1/responses",
        new JsonObject
        {
            ["conversation"] = Required(conversation, "id"), ["input"] = question,
            ["agent_reference"] = agentReference.DeepClone(),
        });
    if ((result["output"] as JsonArray)?.Any(item => (string)item["type"] == "mcp_approval_request") == true)
        throw new InvalidOperationException("Unexpected tool approval request. Rerun sections 2-5 to apply require_approval=\\"never\\" at both layers and start a fresh conversation.");
    if ((string)result["status"] != "completed")
        throw new InvalidOperationException("Agent response " + (string)result["id"] + " ended with status " + (string)result["status"] + ".");
    return result;
}
""",
    "ask": """// Public research only: model and downstream Web IQ calls incur charges.
JsonObject response = await AskAgentAsync(""" + json.dumps(question) + """);
Console.WriteLine(FoundryWorkshop.OutputText(response));
""",
    "verify": """// Discovery is NOT retrieval. Require a successful web OR news call from the correct toolbox label.
JsonNode[] calls = (response["output"] as JsonArray ?? new JsonArray())
    .Where(item => (string)item["type"] == "mcp_call").ToArray();
foreach (JsonNode call in calls)
{
    Console.WriteLine("Tool: " + (string)call["name"] + " | Server: " + (string)call["server_label"] + " | Error: " + call["error"]);
    Console.WriteLine("Arguments: " + call["arguments"]);
    string preview = call["output"]?.ToString() ?? "";
    Console.WriteLine("Result preview: " + preview.Substring(0, Math.Min(preview.Length, 1000)));
}
bool IsEmpty(JsonNode node) => node == null || node.ToString() is "" or "false" or "[]" or "{}" or "0";
bool grounded = calls.Any(call => (string)call["server_label"] == "webiq_toolbox"
    && toolNames.Contains((string)call["name"]) && IsEmpty(call["error"]) && !IsEmpty(call["output"]));
if (!grounded)
    throw new InvalidOperationException("No successful Web IQ call through the toolbox was observed. Check tool errors and both connections.");
string answer = FoundryWorkshop.OutputText(response);
if (string.IsNullOrWhiteSpace(answer))
    throw new InvalidOperationException("Retrieval ran but no final answer was produced.");
// Extra guard: a source URL must appear. This does not prove citation accuracy.
if (!Regex.IsMatch(answer, @"https?://[^\\s<>]+"))
    throw new InvalidOperationException("The answer omitted source URLs. Review the claims and ask again with source-backed findings.");
Console.WriteLine("Confirmed Web IQ retrieval through the toolbox. Review claims and source URLs manually; inspect calls to confirm BOTH tools ran.");
""",
    "close-clients": """// Close only the local client; toolbox/agent versions, conversations and connections are retained.
client.Dispose();
""",
}
cells = []
code_metadata = next(cell["metadata"] for cell in template["cells"] if cell["cell_type"] == "code")
for original in python["cells"]:
    cell = {"cell_type": original["cell_type"], "id": original["id"], "metadata": {}}
    if cell["cell_type"] == "code":
        cell.update(source=code[cell["id"]].splitlines(keepends=True),
                    metadata=code_metadata, execution_count=None, outputs=[])
    else:
        text = "".join(original["source"]).replace("Notebook 02", "Lab 02").replace("notebook 02", "lab 02")
        text = text.replace("`python-notebooks`", "`c-sharp-notebooks`")
        text = text.replace("as an `MCPTool`", "as an MCP tool")
        text = text.replace("with `requests.put`", "with `HttpClient`")
        if cell["id"] == "intro":
            text = text.replace("# Lab 03 -", "# Optional C# Lab 03 -")
            text += "\n\n**Optional/deprecated runtime:** Polyglot Notebooks / .NET Interactive. The supported path is the [.NET 10 console app](../c-sharp-console-apps/03-foundry-web-iq-toolbox/README.md). This notebook is fully self-contained, with inline BCL HTTP/MCP/Azure CLI helpers and explanatory comments; no NuGet or console-app source imports are required. The C# URL guard also checks that the final answer contains a source URL.\n"
        cell["source"] = text.splitlines(keepends=True)
    cells.append(cell)
notebook = {"cells": cells, "metadata": template["metadata"], "nbformat": 4, "nbformat_minor": 5}
(root / "c-sharp-notebooks" / "03-foundry-web-iq-toolbox.ipynb").write_text(
    json.dumps(notebook, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
