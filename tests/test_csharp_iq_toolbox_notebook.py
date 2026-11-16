"""Offline checks for lab14's real C# runtime, protocol parity and inline cells."""
import ast
import copy
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import unittest
import uuid

import nbformat

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
PROJECT = ROOT / "c-sharp-console-apps" / "14-foundry-iq-toolbox-agent"
HARNESS = ROOT / "tests" / "c-sharp-iq-toolbox"
NOTEBOOK = ROOT / "c-sharp-notebooks" / "14-foundry-iq-toolbox-agent.ipynb"
SEARCH_ID = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/workshop/providers/Microsoft.Search/searchServices/workshop"
PROJECT_ID = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/workshop/providers/Microsoft.CognitiveServices/accounts/workshop/projects/project"
SETTINGS = (
    "WORKSHOP_PREFIX=alice01\n"
    "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
    "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
    f"AZURE_SEARCH_RESOURCE_ID={SEARCH_ID}\n"
    "AZURE_AI_PROJECT_ENDPOINT=https://workshop.services.ai.azure.com/api/projects/project\n"
    f"AZURE_AI_PROJECT_RESOURCE_ID={PROJECT_ID}\n"
    "AZURE_AI_MODEL_DEPLOYMENT_NAME=workshop-chat\n"
)


def python_payloads(shared_base):
    from azure.ai.projects.models import CodeConfiguration, HostedAgentDefinition, MCPToolboxTool, ProtocolVersionRecord
    notebook = nbformat.read(ROOT / "python-notebooks" / "14-foundry-iq-toolbox-agent.ipynb", as_version=4)
    sources = {c.id: ast.parse(c.source) for c in notebook.cells if c.cell_type == "code"}
    context = {
        "__builtins__": {}, "base_kb": shared_base, "prefix": "alice01",
        "knowledge_base_name": "alice01-toolbox-kb", "sharepoint_source_name": "alice01-kb-sharepoint",
        "sharepoint_index_name": "alice01-nb7-index",
        "knowledge_base_mcp_url": "https://workshop.search.windows.net/knowledgebases/alice01-toolbox-kb/mcp?api-version=2026-08-01-preview",
        "connection_id": PROJECT_ID + "/connections/alice01-iq-search-mcp",
        "model_name": "workshop-chat", "toolbox_name": "alice01-iq-toolbox",
        "search_resource_id": SEARCH_ID, "principal_id": "22222222-2222-2222-2222-222222222222",
        "search_index_data_reader": "1407120a-92aa-4202-b7e9-c0e197c71c8f",
        "CodeConfiguration": CodeConfiguration, "HostedAgentDefinition": HostedAgentDefinition,
        "ProtocolVersionRecord": ProtocolVersionRecord, "MCPToolboxTool": MCPToolboxTool,
    }

    def evaluate(node):
        return eval(compile(ast.Expression(node), "<python14-payload>", "eval"), context)

    def assignment(cell, name):
        return next(n.value for n in sources[cell].body if isinstance(n, ast.Assign) and
                    any(isinstance(t, ast.Name) and t.id == name for t in n.targets))

    def keyword(cell, function, name):
        call = next(n for n in ast.walk(sources[cell]) if isinstance(n, ast.Call) and
                    isinstance(n.func, ast.Attribute) and n.func.attr == function and
                    any(k.arg == name for k in n.keywords))
        return next(k.value for k in call.keywords if k.arg == name)

    definition = evaluate(assignment("deploy", "definition")).as_dict()
    definition["code_configuration"]["runtime"] = "dotnet_10"
    definition["code_configuration"]["entry_point"] = ["dotnet", "WorkshopIqAgent.dll"]
    role_id = context["search_index_data_reader"]
    return {
        "knowledgeBase": evaluate(assignment("create-knowledge-base", "knowledge_base")),
        "connection": evaluate(keyword("create-toolbox", "put", "json")),
        "toolbox": {
            "description": evaluate(keyword("create-toolbox", "create_version", "description")),
            "tools": [t.as_dict() for t in evaluate(keyword("create-toolbox", "create_version", "tools"))],
        },
        "hosted": {
            "definition": definition,
            "description": evaluate(keyword("deploy", "create_version_from_code", "description")),
            "metadata": evaluate(keyword("deploy", "create_version_from_code", "metadata")),
        },
        "role": evaluate(keyword("grant-search-access", "put", "json")),
        "assignmentName": str(uuid.uuid5(uuid.NAMESPACE_URL,
                                       f"{SEARCH_ID}/{context['principal_id']}/{role_id}")),
    }


class IqToolboxConsoleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def fixture(self):
        path = ROOT / ".workshop" / ("iq14-python-" + uuid.uuid4().hex)
        path.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, path)
        return path

    def test_console_harness_matches_current_python_payloads_and_role_uuid(self):
        wire = self.fixture() / "wire.json"
        result = subprocess.run(["dotnet", "run", "--project", str(HARNESS), "--", "--wire", str(wire)],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        actual = json.loads(wire.read_text(encoding="utf-8"))
        shared = {"models": actual["knowledgeBase"]["models"],
                  "answerInstructions": actual["knowledgeBase"]["answerInstructions"]}
        self.assertEqual(actual, python_payloads(shared))
        self.assertIn("offline", result.stdout)

    def test_actual_generated_csharp_toolbox_host_builds_without_azure_signin(self):
        path = self.fixture()
        (path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (path / ".env.azure").write_text(SETTINGS, encoding="utf-8")
        result = subprocess.run(["dotnet", str(PROJECT / "bin" / "Debug" / "net10.0" / "IqToolboxAgent.dll"),
                                 "write-agent"], cwd=path, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        generated = path / "alice01-csharp-hosted-agent-iq"
        self.assertEqual(sorted(p.name for p in generated.iterdir()), ["Program.cs", "WorkshopIqAgent.csproj"])
        result = subprocess.run(["dotnet", "build", str(generated / "WorkshopIqAgent.csproj"),
                                 "--nologo", "--verbosity", "minimal", "--disable-build-servers"], cwd=path,
                                capture_output=True, text=True, timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((generated / "bin" / "Debug" / "net10.0" / "WorkshopIqAgent.dll").is_file())
        host_source = (generated / "Program.cs").read_text(encoding="utf-8")
        guard = host_source[host_source.index("app.App.Use("):host_source.index("app.Run();")]
        checks = path / "context-guard"
        checks.mkdir()
        (checks / "Guard.csproj").write_text("""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
</Project>
""", encoding="utf-8")
        (checks / "Program.cs").write_text("""
using Microsoft.AspNetCore.Http;
var app = new Capture();
""" + guard + """
foreach (var value in new[] {
    ("POST", "", "", false), ("POST", "call", "", false),
    ("POST", "", "user", false), ("POST", "call", "user", true),
    ("GET", "", "", true) })
{
    var context = new DefaultHttpContext();
    context.Request.Method = value.Item1;
    context.Request.Headers["x-agent-foundry-call-id"] = value.Item2;
    context.Request.Headers["x-agent-user-id"] = value.Item3;
    bool called = false;
    await app.Handler!(context, _ => { called = true; return Task.CompletedTask; });
    if (called != value.Item4 || (!called && context.Response.StatusCode != 401))
        throw new Exception("Missing context allowed model/tool execution or blocked a readiness probe");
}
Console.WriteLine("Passed five actual generated request-context guard cases");
public sealed class Capture {
    public Capture App => this;
    public Func<HttpContext, RequestDelegate, Task>? Handler;
    public void Use(Func<HttpContext, RequestDelegate, Task> handler) => Handler = handler;
}
""", encoding="utf-8")
        result = subprocess.run(["dotnet", "run", "--project", str(checks), "--disable-build-servers"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Passed five", result.stdout)

    def test_real_help_and_argument_guards_without_configuration_or_azure_cli(self):
        path = self.fixture()
        (path / ".env").write_text("WORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "IqToolboxAgent.dll"
        env = {**os.environ, "PATH": ""}
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["cleanup"], False), (["cleanup", "--confirm", "other"], False),
                              (["grant-search", "../1"], False), (["invoke", ""], False),
                              (["invoke", "one", "two"], False), (["connection", "--diagnostics"], False),
                              (["unknown"], False)):
            with self.subTest(args=args):
                result = subprocess.run([shutil.which("dotnet"), str(dll), *args], cwd=path, env=env,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                self.assertNotIn("Missing setting", result.stderr)
                if success:
                    self.assertEqual(result.stderr, "")
                    self.assertIn("cleanup --confirm", result.stdout)
                    outputs.append(result.stdout)
                self.assertEqual([p.name for p in path.iterdir()], [".env"])
        self.assertEqual(len(set(outputs)), 1)

    def test_notebook_schema_inline_sources_blank_outputs_and_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps"):
            self.assertNotIn(forbidden, code)
        self.assertIn('Path.GetFullPath("..")', code)
        self.assertIn("bool CLEAN_UP = false;", code)
        self.assertTrue(all(c.execution_count is None and not c.outputs for c in notebook.cells if c.cell_type == "code"))
        for cell in notebook.cells:
            if cell.cell_type == "code":
                self.assertEqual(cell.metadata["vscode"]["languageId"], "polyglot-notebook")
                self.assertEqual(cell.metadata["polyglot_notebook"]["kernelName"], "csharp")
        for source in ("IqSettings.cs", "IqSources.cs", "IqWorkshop.cs", "IqHostedRuntime.cs"):
            body = "public " + (PROJECT / source).read_text(encoding="utf-8").split("public ", 1)[1].strip()
            self.assertIn(body, code)
        for path, text in ((NOTEBOOK, "\n".join(c.source for c in notebook.cells)),
                           (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            links = re.findall(r"\]\(([^)]+)\)|src=\"([^\"]+)\"", text)
            for markdown, image in links:
                link = markdown or image
                if "://" not in link:
                    self.assertTrue((path.parent / link).is_file(), f"Missing local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class IqToolboxNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / ".workshop" / ("iq14-notebook-" + uuid.uuid4().hex)
        cls.path.mkdir(parents=True)
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(SETTINGS, encoding="utf-8")
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
                                capture_output=True, text=True, timeout=90,
                                env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1"})
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook = nbformat.read(NOTEBOOK, as_version=4)
        cls.original_bytes = NOTEBOOK.read_bytes()

    def execute(self, ids=None, mode="", checks="", cleanup=False):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if ids is not None:
            self.notebook.cells = [c for c in self.notebook.cells if c.cell_type != "code" or
                                   c.id in {"setup", "dispose"} or c.id in ids]
        if cleanup:
            cell = next(c for c in self.notebook.cells if c.id == "cleanup")
            cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
        tests = (HARNESS / "IqToolboxTests.cs").read_text(encoding="utf-8")
        handler = tests[tests.index("public sealed record CapturedIqRequest"):] + """
client.Dispose();
IqOfflineHandler handler = new IqOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
DateTimeOffset testClock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
tokenProvider = resource => { tokenCalls++; return Task.FromResult(IqTestData.Token(resource)); };
clockProvider = () => testClock;
delayProvider = duration => { testClock += duration; return Task.CompletedTask; };
lab = new IqWorkshop(settings, client, resource => tokenProvider(resource),
    () => clockProvider(), duration => delayProvider(duration));
handler.Mode = """ + json.dumps(mode) + ";"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)

    def test_actual_full_notebook_chain_uses_project_transport_and_source_only_archive(self):
        self.execute(checks="""
if (settings.Prefix != "alice01" || selectedVersion != "1") throw new Exception("Settings/version");
if (handler.Requests.Count != 20 || tokenCalls != 20 || handler.Requests.Any(r => r.Method == "DELETE"))
    throw new Exception("Hidden call/retry/cleanup");
foreach (CapturedIqRequest request in handler.Requests)
{
    string audience = request.Url.StartsWith("https://management.azure.com") ? "https://management.azure.com" :
        request.Url.StartsWith(settings.SearchEndpoint) ? "https://search.azure.com" : "https://ai.azure.com";
    if (request.Authorization != "Bearer " + IqTestData.Token(audience) ||
        request.HasKey || request.HasUserAuthorization || request.HasElevatedRead)
        throw new Exception("Wrong credential/user forwarding");
}
CapturedIqRequest upload = handler.Requests.Single(r => r.Code != null);
if (!JsonNode.DeepEquals(upload.Body, lab.CodeMetadata())) throw new Exception("Hosted definition");
CapturedIqRequest role = handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/roleAssignments/"));
if (role.Body["properties"]["principalId"].ToString() != IqTestData.Principal) throw new Exception("Not project principal");
if (handler.Requests.Count(r => r.Url.Contains("/responses?")) != 2) throw new Exception("Model retries");
using (MemoryStream bytes = new MemoryStream(upload.Code))
using (ZipArchive bundle = new ZipArchive(bytes, ZipArchiveMode.Read))
    if (!bundle.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(new[] { "Program.cs", "WorkshopIqAgent.csproj" }))
        throw new Exception("Not CSharp source-only archive");
""")

    def test_actual_independent_cells_and_scoped_opt_in_cleanup(self):
        for ids, count, methods in (
                ({"create-connection"}, 2, {"GET", "PUT"}),
                ({"wait"}, 3, {"GET"}),
                ({"invoke"}, 1, {"POST"}),
                ({"invoke-unknown"}, 1, {"POST"}),
                ({"cleanup"}, 0, set())):
            allowed = json.dumps(json.dumps(sorted(methods)))
            self.execute(ids=ids, checks=f"""
if (handler.Requests.Count != {count} ||
    handler.Requests.Any(r => !JsonNode.Parse({allowed}).AsArray().Any(m => m.ToString() == r.Method)))
    throw new Exception("Implicit action or cleanup");
""")
        self.execute(ids={"cleanup"}, cleanup=True, checks="""
if (handler.Requests.Count(r => r.Method == "DELETE") != 4 ||
    handler.Requests.Where(r => r.Method == "DELETE").Any(r => r.Url.Contains("force") || r.Url.Contains("/knowledgesources/")))
    throw new Exception("Cleanup scope/forcing");
""")

    def test_actual_grant_handoff_and_failed_invocations_stop_without_paid_retries(self):
        original = self.notebook
        for cell_id, mode, expected, count in (
                ("grant-search-access", "role-denied", "administrator handoff", 2),
                ("invoke", "denied", "HTTP 403", 1),
                ("invoke", "tool-error", "tool", 1),
                ("invoke", "incomplete", "did not complete", 1)):
            self.notebook = copy.deepcopy(original)
            cell = next(c for c in self.notebook.cells if c.id == cell_id)
            cell.source = """
string failure = "";
try {
""" + cell.source + """
} catch (Exception error) { failure = error.Message; }
"""
            try:
                self.execute(ids={cell_id}, mode=mode, checks=f"""
if (!failure.Contains({json.dumps(expected)}, StringComparison.OrdinalIgnoreCase) || handler.Requests.Count != {count})
    throw new Exception("Swallowed/retried failure");
if (failure.Contains("synthetic-ai-token")) throw new Exception("Credential leak");
""")
            finally:
                self.notebook = original


if __name__ == "__main__":
    unittest.main()
