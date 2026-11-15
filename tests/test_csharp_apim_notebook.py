"""Current Python12 payload contracts and isolated, offline C#12 execution."""
import ast
import copy
import json
import os
import re
import shutil
import subprocess
import unittest
import uuid
from pathlib import Path

import nbformat
from jupyter_client import KernelManager
from jupyter_client.kernelspec import KernelSpecManager
from nbclient import NotebookClient
from nbclient.exceptions import CellExecutionError

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "c-sharp-console-apps" / "12-foundry-iq-apim"
HARNESS = ROOT / "tests" / "c-sharp-apim"
NOTEBOOK = ROOT / "c-sharp-notebooks" / "12-foundry-iq-apim.ipynb"
PYTHON = ROOT / "python-notebooks" / "12-foundry-iq-apim.ipynb"
TENANT = "00000000-0000-0000-0000-000000000000"
BROKER = "11111111-1111-1111-1111-111111111111"
BASE_KB = {
    "name": "alice01-kb",
    "retrievalInstructions": "Use alice01-kb-blob and alice01-kb-sharepoint internally, alice01-kb-webiq publicly. Do not send internal text to Web IQ.",
    "answerInstructions": "Cite evidence.",
    "models": [{"kind": "azureOpenAI", "azureOpenAIParameters": {
        "resourceUri": "https://offline.openai.azure.com", "deploymentId": "offline-chat", "modelName": "gpt-5-mini"}}],
}


def python_payloads(mode="answerSynthesis"):
    """Evaluate only wire payload expressions from CURRENT canonical code, without imports or requests."""
    values = {
        "prefix": "alice01", "blob_source_name": "alice01-kb-blob",
        "sharepoint_source_name": "alice01-kb-sharepoint", "webiq_source_name": "iq-workshop-webiq",
        "knowledge_base_name": "alice01-apim-kb", "base_kb": copy.deepcopy(BASE_KB),
        "source_kinds": {"alice01-kb-blob": "azureBlob", "alice01-kb-sharepoint": "searchIndex",
                         "iq-workshop-webiq": "mcpServer"},
    }
    bodies = []
    for cell in nbformat.read(PYTHON, as_version=4).cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("knowledge_base", "question", "body"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<Python12 wire>", "eval"),
                                        {"__builtins__": {}, **values})
        # Preserve lexical order of the two rejection requests.
        calls = sorted((node for node in ast.walk(tree) if isinstance(node, ast.Call) and
                        isinstance(node.func, ast.Attribute) and node.func.attr in ("put", "post")),
                       key=lambda node: (node.lineno, node.col_offset))
        for call in calls:
            expression = next((kw.value for kw in call.keywords if kw.arg == "json"), None)
            if expression is None:
                continue
            body = eval(compile(ast.Expression(expression), "<Python12 wire>", "eval"),
                        {"__builtins__": {}, **values})
            if "knowledgeSourceParams" in body and mode != "answerSynthesis":
                body["outputMode"] = mode
            bodies.append(body)
    return bodies


def literal(value):
    return json.dumps(json.dumps(value))


class ApimConsoleTests(unittest.TestCase):
    def test_runtime_harness_and_current_python12_wire_parity(self):
        path = HARNESS / (".wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(["dotnet", "run", "--project", str(HARNESS / "ApimTests.csproj"),
                                 "--", "--wire", str(path)], cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout[-8000:] + result.stderr[-8000:])
        self.assertRegex(result.stdout, r"PASS: \d+ offline APIM protocol/runtime cases")
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual(wire["BaseKb"], BASE_KB)
        requests = wire["Requests"]
        self.assertEqual([r["Body"] for r in requests if r["Method"] in ("PUT", "POST")], python_payloads())
        for request in requests:
            if request["Method"] in ("PUT", "DELETE"):
                self.assertIn("/knowledgebases/alice01-apim-kb?", request["Uri"])
            if request["Method"] == "POST":
                self.assertTrue(request["Uri"].startswith("https://offline.azure-api.net/iq/knowledgebases/"))
            else:
                self.assertTrue(request["Uri"].startswith("https://offline.search.windows.net/"))
        formatted = [c["Formatted"] for c in wire["ReferenceCases"]]
        self.assertIn("https://example.test/public#section", formatted[2])
        self.assertIn("https://example.test/nested", formatted[3])
        self.assertIn("pp. 2-4", formatted[0])
        self.assertIn("report.pdf p. 7", formatted[1])
        self.assertIn("(No source URL returned.)", formatted[4])

    def test_real_help_aliases_strict_args_and_setup_failure_are_offline(self):
        result = subprocess.run(["dotnet", "build", str(PROJECT / "Apim.csproj"), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        fixture = HARNESS / (".help-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        (fixture / ".env").write_text("AZURE_TENANT_ID=invalid\nWORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        env = {**os.environ, "PATH": "", "WORKSHOP_PREFIX": "", "AZURE_TENANT_ID": "",
               "AZURE_SEARCH_ENDPOINT": "", "BROKER_APP_CLIENT_ID": "", "APIM_GATEWAY_URL": ""}
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "Apim.dll"
        output = []
        for args in ([], ["help"], ["--help"], ["-h"], ["HELP"]):
            with self.subTest(alias=args):
                result = subprocess.run([shutil.which("dotnet"), str(dll), *args], cwd=fixture, env=env,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("inspect-token", result.stdout)
                self.assertIn("cleanup --confirm", result.stdout)
                self.assertEqual(result.stderr, "")
                output.append(result.stdout)
        self.assertEqual(len(set(output)), 1)
        for args in (["unknown"], ["cleanup"], ["cleanup", "--confirm", "extra"], ["cleanup", "--help"],
                     ["run", "--output-mode"], ["run", "--bad"], ["run", "one", "two"], ["help", "extra"],
                     ["check-webiq-source", "--diagnostics"], ["run", "--output-mode", "--diagnostics"]):
            with self.subTest(invalid=args):
                result = subprocess.run([shutil.which("dotnet"), str(dll), *args], cwd=fixture, env=env,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 1)
                self.assertNotIn("Missing setting", result.stderr)
        result = subprocess.run([shutil.which("dotnet"), str(dll), "knowledge-base"], cwd=fixture, env=env,
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Missing setting", result.stderr)
        self.assertNotIn("HTTP status", result.stdout)
        self.assertEqual([p.name for p in fixture.iterdir()], [".env"])

    def test_notebook_schema_teaching_and_all_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        text = "\n".join(c.source for c in notebook.cells if c.cell_type == "markdown")
        self.assertIn('Path.GetFullPath("..")', code)
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps", "Azure.Identity", "ReadLine(", "azd "):
            self.assertNotIn(forbidden, code)
        for required in ('"-kb-blob"', '"-kb-sharepoint"', '"-apim-kb"', '"iq-workshop-webiq"',
                         '"kind"] = "medium"', '"--resource"', "client.Dispose();"):
            self.assertIn(required, code)
        for cell in notebook.cells:
            if cell.cell_type == "code":
                self.assertEqual(cell.outputs, [])
                self.assertIsNone(cell.execution_count)
            if cell.id.startswith("retrieve-"):
                self.assertIn("var payload = new JsonObject", cell.source)
                self.assertEqual(cell.source.count('["includeReferenceSourceData"] = true'), 3)
                self.assertIn("gateway: true", cell.source)
        for required in ("04b07795-8ddb-461a-bbee-02f9e1bf7b46", "Assignment required = No",
                         "not an allowlist", "Search Index Data Reader", "separately consented",
                         "not used for APIM's OBO exchange", "medium", "Claims"):
            self.assertIn(required.lower(), (text + code).lower())
        canonical_markdown = "\n".join(c.source for c in nbformat.read(PYTHON, as_version=4).cells if c.cell_type == "markdown")
        for link in re.findall(r"\]\(([^)]+)\)", canonical_markdown):
            self.assertIn(link, text)
        for path, content in ((NOTEBOOK, text), (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            for link in re.findall(r"\]\(([^)]+)\)", content) + re.findall(r'<img[^>]+src="([^"]+)"', content):
                if "://" not in link:
                    self.assertTrue((path.parent / link.split("#", 1)[0]).is_file(), f"Broken local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive kernel is not already installed.")
class ApimNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = HARNESS / (".kernel-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\nAZURE_TENANT_ID=invalid\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(
            f"AZURE_TENANT_ID={TENANT}\nBROKER_APP_CLIENT_ID={BROKER}\nWORKSHOP_PREFIX=alice01\n"
            "AZURE_SEARCH_ENDPOINT=https://offline.search.windows.net\nAPIM_GATEWAY_URL=https://offline.azure-api.net\n"
            "WEBIQ_APIM_SOURCE_NAME=iq-workshop-webiq\n"
            "WEBIQ_API_KEY=synthetic-key\nBROKER_CLIENT_SECRET=synthetic-broker-secret\n", encoding="utf-8")
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
                                capture_output=True, text=True, timeout=90,
                                env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1"})
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook = nbformat.read(NOTEBOOK, as_version=4)
        cls.original_bytes = NOTEBOOK.read_bytes()

    def execute(self, cells=None, checks="", mode="", failure_path="", output_mode="answerSynthesis",
                cleanup=False, broker_expression="ApimOfflineHandler.Jwt()", diagnostics=False):
        notebook = copy.deepcopy(self.notebook)
        if cells is not None:
            notebook.cells = [c for c in notebook.cells if c.cell_type != "code" or c.id == "setup" or c.id in cells]
        for cell in notebook.cells:
            if cell.id == "output-mode":
                cell.source = cell.source.replace('string outputMode = "answerSynthesis";',
                                                  f"string outputMode = {json.dumps(output_mode)};").replace(
                                                      "bool diagnostics = false;", f"bool diagnostics = {str(diagnostics).lower()};")
            if cleanup:
                cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
        handler = (HARNESS / "ApimOfflineHandler.cs").read_text(encoding="utf-8")
        inject = handler + """
client.Dispose();
ApimOfflineHandler handler = new ApimOfflineHandler();
client = new HttpClient(handler);
int searchCalls = 0, brokerCalls = 0;
searchTokenProvider = () => { searchCalls++; return Task.FromResult("synthetic-search-token"); };
brokerTokenProvider = () => { brokerCalls++; return Task.FromResult(""" + broker_expression + """); };
transport = new ApimTransport(client, settings, () => searchTokenProvider(), () => brokerTokenProvider());
handler.Mode = """ + json.dumps(mode) + ";\nhandler.FailurePath = " + json.dumps(failure_path) + ";\n"
        setup = next(i for i, c in enumerate(notebook.cells) if c.id == "setup")
        metadata = copy.deepcopy(notebook.cells[setup].metadata)
        notebook.cells.insert(setup + 1, nbformat.v4.new_code_cell(inject, metadata=metadata))
        if checks:
            notebook.cells.append(nbformat.v4.new_code_cell(checks, metadata=metadata))
        kernel = KernelManager(kernel_name=notebook.metadata.kernelspec.name,
                               kernel_spec_manager=KernelSpecManager(kernel_dirs=[str(self.kernel_directory)]))
        client = NotebookClient(notebook, km=kernel, timeout=90,
                                resources={"metadata": {"path": str(self.working_directory)}})
        try:
            client.execute(env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1"})
        finally:
            if client.kc is not None:
                client.kc.stop_channels()
            if kernel.has_kernel:
                kernel.shutdown_kernel(now=True)
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)
        return notebook

    def test_actual_full_notebook_current_python12_wire_parity(self):
        checks = [
            'if (handler.Requests.Count != 10 || searchCalls != 6 || brokerCalls != 4) throw new Exception("Hidden requests/auth");',
            'if (settings.Prefix != "alice01" || settings.Tenant != ApimOfflineHandler.Tenant) throw new Exception("dotenv override");',
            'if (handler.Requests.Take(5).Any(r => !r.Uri.StartsWith("https://offline.search.windows.net/"))) throw new Exception("Setup URL");',
            'if (handler.Requests.Skip(5).Any(r => !r.Uri.StartsWith("https://offline.azure-api.net/iq/"))) throw new Exception("Retrieval URL");',
            'if (handler.Requests.Any(r => r.Headers.Any(h => h != "Authorization"))) throw new Exception("Caller injected gateway headers");',
            'if (handler.Requests.Take(5).Any(r => r.Authorization != "Bearer synthetic-search-token")) throw new Exception("Setup token");',
            'if (handler.Requests.Skip(5).Take(3).Any(r => r.Authorization != "Bearer " + ApimOfflineHandler.Jwt())) throw new Exception("Broker token");',
            'if (handler.Requests[8].Authorization != null || handler.Requests[9].Authorization != "Bearer synthetic-search-token") throw new Exception("Negative probes");',
        ]
        for i, body in zip(range(4, 10), python_payloads()):
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{i}].Body, JsonNode.Parse({literal(body)}))) throw new Exception("Current Python12 payload {i}");')
        notebook = self.execute(checks="\n".join(checks))
        output = "".join(o.get("text", "") for c in notebook.cells for o in c.get("outputs", []))
        self.assertIn("Cleanup disabled.", output)
        self.assertIn("https://example.test/public#section", output)
        self.assertIn("travel.pdf pp. 2-4", output)
        self.assertNotIn("private passage", output)

    def test_actual_setup_has_no_calls_and_inline_token_contracts(self):
        self.execute(cells=set(), checks="""
if (handler.Requests.Count != 0 || searchCalls != 0 || brokerCalls != 0) throw new Exception("Setup calls");
if (settings.BrokerScope != "api://" + ApimOfflineHandler.Broker + "/.default") throw new Exception("SDK scope");
var start = ApimTransport.TokenStartInfo(settings.Tenant, settings.BrokerResource);
if (!start.ArgumentList.Contains("--resource") || !start.ArgumentList.Contains(settings.BrokerResource) ||
    start.ArgumentList.Any(a => a.Contains("/user_impersonation"))) throw new Exception("Wrong CLI resource");
foreach (string token in new[] { ApimOfflineHandler.Jwt("https://search.azure.com"),
    ApimOfflineHandler.Jwt(scope: "not_user_impersonation"), ApimOfflineHandler.Jwt(tenant: ApimOfflineHandler.Broker), "invalid-token" })
{
    bool failed = false;
    try { ApimTransport.InspectToken(token, settings); } catch (InvalidOperationException) { failed = true; }
    if (!failed) throw new Exception("Wrong token accepted");
}
client.Dispose();
""")

    def test_actual_authoring_and_retrieval_mode_parity(self):
        cells = {"check-webiq-source", "knowledge-base", "output-mode", "retrieve-mixed"}
        bodies = python_payloads("extractiveData")
        self.execute(cells=cells, output_mode="extractiveData", checks=f"""
if (handler.Requests.Count != 6 || handler.Requests.Count(r => r.Method == "PUT") != 1) throw new Exception("Wrong writes");
if (!JsonNode.DeepEquals(handler.Requests[4].Body, JsonNode.Parse({literal(bodies[0])}))) throw new Exception("KB parity");
if (!JsonNode.DeepEquals(handler.Requests[5].Body, JsonNode.Parse({literal(bodies[3])}))) throw new Exception("Extractive mixed parity");
client.Dispose();
""")

    def test_actual_prerequisite_and_broker_failures_stop_before_writes_or_retrieval(self):
        self.execute(cells=set(), checks="""
async Task MustFail(Func<Task> action)
{
    bool failed = false;
    try { await action(); } catch (Exception) { failed = true; }
    if (!failed) throw new Exception("Expected offline failure");
}
handler.Mode = "missing";
await MustFail(() => transport.SendAsync(HttpMethod.Get, "knowledgesources/iq-workshop-webiq"));
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "GET") throw new Exception("Setup failure mutation");
handler.Reset();
brokerTokenProvider = () => Task.FromResult(ApimOfflineHandler.Jwt("https://search.azure.com"));
await MustFail(() => transport.SendAsync(HttpMethod.Post, "knowledgebases/alice01-apim-kb/retrieve", gateway: true));
if (handler.Requests.Count != 0) throw new Exception("Wrong broker sent to gateway");
client.Dispose();
""")
        with self.assertRaisesRegex(CellExecutionError, "Shared source"):
            self.execute(cells={"check-webiq-source", "knowledge-base"}, mode="wrong-source")

    def test_actual_partial_source_errors_and_redacted_same_response_diagnostics(self):
        self.execute(cells={"check-webiq-source", "knowledge-base", "output-mode"}, checks="""
var payload = new JsonObject { ["messages"] = new JsonArray(new JsonObject {
    ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "offline" }) }) };
foreach (string mode in new[] { "partial", "activity-error", "denied", "shape", "malformed", "network", "timeout" })
{
    handler.Reset(mode, "/iq/");
    bool failed = false;
    try { await transport.SendAsync(HttpMethod.Post, "knowledgebases/alice01-apim-kb/retrieve", payload, gateway: true, diagnostics: true); }
    catch (Exception error)
    {
        failed = true;
        if (error.Message.Contains("synthetic-key") || error.Message.Contains(ApimOfflineHandler.Jwt())) throw new Exception("Exception secret");
    }
    if (!failed || handler.Requests.Count != 1) throw new Exception("Partial/error accepted or retried");
}
handler.Reset("echo");
var output = new StringWriter();
var errors = new StringWriter();
var originalOut = Console.Out; var originalError = Console.Error;
try
{
    Console.SetOut(output); Console.SetError(errors);
    await transport.SendAsync(HttpMethod.Post, "knowledgebases/alice01-apim-kb/retrieve", payload, gateway: true, diagnostics: true);
}
finally { Console.SetOut(originalOut); Console.SetError(originalError); }
string logs = output.ToString() + errors.ToString();
foreach (string secret in new[] { "synthetic-key", "synthetic-broker-secret", "unconfigured-key", "unconfigured-secret", ApimOfflineHandler.Jwt() })
    if (logs.Contains(secret)) throw new Exception("Output secret leaked");
if (handler.Requests.Count != 1 || !logs.Contains("Raw references:") ||
    !logs.Contains("https://example.test/public#section")) throw new Exception("Diagnostics caused another request or lost URL");
client.Dispose();
""")

    def test_actual_cleanup_only_own_apim_kb_and_reference_fallback(self):
        self.execute(cells={"cleanup"}, cleanup=True, checks="""
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "DELETE" ||
    handler.Requests[0].Uri != "https://offline.search.windows.net/knowledgebases/alice01-apim-kb?api-version=2026-08-01-preview")
    throw new Exception("Cleanup exceeded own KB");
var reference = new JsonObject { ["id"] = "nested", ["sourceData"] =
    new JsonObject { ["content"] = "{\\"title\\":\\"Nested\\",\\"url\\":\\"https://example.test/nested\\"}" }.ToJsonString() };
if (!ApimResults.FormatReference(reference).Contains("https://example.test/nested")) throw new Exception("String sourceData lost");
reference = new JsonObject { ["id"] = "opaque", ["docKey"] = "opaque-key", ["sourceData"] = "not JSON" };
string formatted = ApimResults.FormatReference(reference);
if (!formatted.Contains("opaque-key") || formatted.Contains("https://") || formatted.Contains("p. ")) throw new Exception("Fabricated URL/page");
""")


if __name__ == "__main__":
    unittest.main()
