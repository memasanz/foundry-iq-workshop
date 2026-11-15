"""Offline execution of C#11 inline cells and current Python11 wire/citation parity."""
import ast
import contextlib
import copy
import io
import json
import os
import re
import shutil
import subprocess
import unittest
import uuid

import nbformat

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
NOTEBOOK = ROOT / "c-sharp-notebooks" / "11-foundry-iq-knowledge-base.ipynb"
PROJECT = ROOT / "c-sharp-console-apps" / "11-foundry-iq-knowledge-base"
PYTHON = ROOT / "python-notebooks" / "11-foundry-iq-knowledge-base.ipynb"


def python_payloads(output_mode="answerSynthesis"):
    values = {
        "blob_index_name": "alice01-cu-chunks", "sharepoint_index_name": "alice01-nb7-index",
        "blob_source_name": "alice01-kb-blob", "sharepoint_source_name": "alice01-kb-sharepoint",
        "webiq_source_name": "alice01-kb-webiq", "knowledge_base_name": "alice01-kb",
        "webiq_api_key": "synthetic-test-key", "aoai_endpoint": "https://workshop-ai.openai.azure.com",
        "chat_deployment": "workshop-chat", "chat_model": "gpt-5-mini", "webiq_tools": ["web"],
    }
    bodies = []
    for cell in nbformat.read(PYTHON, as_version=4).cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("blob_source", "sharepoint_source", "webiq_source", "knowledge_base", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<python11-payload>", "eval"),
                                        {"__builtins__": {}, **values})
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                expression = next((kw.value for kw in call.keywords if kw.arg == "json"), None)
                if expression is not None:
                    body = eval(compile(ast.Expression(expression), "<python11-payload>", "eval"),
                                {"__builtins__": {}, **values})
                    if "messages" in body and output_mode != "answerSynthesis":
                        body["outputMode"] = output_mode
                    bodies.append(body)
                    if body == {"search": "*", "count": True, "top": 0}:
                        bodies.append(copy.deepcopy(body))
    return bodies


def python_reference(reference):
    cell = next(c for c in nbformat.read(PYTHON, as_version=4).cells
                if c.cell_type == "code" and c.source.startswith("def print_reference"))
    namespace = {"json": json}
    exec(cell.source, namespace)
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        namespace["print_reference"](reference)
    return output.getvalue().strip()


def csharp_markdown(source):
    return (source.replace("python-notebooks", "c-sharp-notebooks")
            .replace("`blob_index_name` above", "the `BlobIndexName` property in Configuration")
            .replace("`result['activity']`", '`transport.LastResult["activity"]`')
            .replace("`response` and `result`", "HTTP status (`transport.LastStatus`) and parsed response (`transport.LastResult`)"))


class KnowledgeBaseConsoleTests(unittest.TestCase):
    def test_console_runtime_current_python11_wire_and_reference_parity(self):
        path = ROOT / (".csharp-kb11-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-knowledge-base"),
                                 "--", "--wire", str(path)], cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual([r["Body"] for r in wire["Requests"] if r["Body"] is not None], python_payloads())
        for case in wire["ReferenceCases"]:
            with self.subTest(reference=case["Reference"]):
                self.assertEqual(case["Formatted"], python_reference(case["Reference"]))

    def test_real_entrypoint_help_and_argument_validation_are_offline(self):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        fixture = ROOT / (".csharp-kb11-help-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        (fixture / ".env").write_text("AZURE_TENANT_ID=invalid\nWORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        env = {**os.environ, "PATH": "", "WORKSHOP_PREFIX": "", "WEBIQ_API_KEY": "",
               "AZURE_TENANT_ID": "", "AZURE_SEARCH_ENDPOINT": ""}
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "KnowledgeBase.dll"
        outputs = []
        for args, success in (
            ([], True), (["help"], True), (["--help"], True), (["-h"], True), (["HELP"], True),
            (["cleanup"], False), (["cleanup", "--help"], False), (["cleanup", "--confirm", "extra"], False),
            (["unknown"], False), (["help", "extra"], False), (["run", "--bad"], False),
            (["run", "--output-mode", "low"], False), (["run", "--diagnostics", "--diagnostics"], False),
            (["retrieve-sharepoint", "one", "two"], False), (["sharepoint-source", "--diagnostics"], False),
        ):
            with self.subTest(args=args):
                result = subprocess.run([shutil.which("dotnet"), str(dll), *args], cwd=fixture, env=env,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertIn("verify-indexes", result.stdout)
                    self.assertIn("cleanup --confirm", result.stdout)
                    self.assertEqual(result.stderr, "")
                    outputs.append(result.stdout)
                else:
                    self.assertNotIn("Missing setting", result.stderr)
                self.assertEqual([p.name for p in fixture.iterdir()], [".env"])
        self.assertEqual(len(set(outputs)), 1)

    def test_notebook_schema_inline_teaching_and_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        source = "\n".join(c.source for c in notebook.cells)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps", "Azure.Identity"):
            self.assertNotIn(forbidden, code)
        for expected in ('Path.GetFullPath("..")', 'bool CLEAN_UP = false;', '"-cu-chunks"', '"-nb7-index"',
                         '"-kb-blob"', '"-kb-sharepoint"', '"-kb-webiq"', '"-kb"',
                         '"https://search.azure.com/.default"', '"storedHeaders"', '"x-apikey"',
                         '"maxOutputTokens"] = 2000', '"name"] = "web"', '"kind"] = "medium"',
                         '"page_from"', '"page_to"', "client.Dispose();"):
            self.assertIn(expected, code)
        for forbidden in ("facilitator", "attendee", "blob-chunks", "cu-blob"):
            self.assertNotIn(forbidden, source.lower())
        for cell in notebook.cells:
            if cell.cell_type == "code":
                self.assertIsNone(cell.execution_count)
                self.assertEqual(cell.outputs, [])
        ids = [c.id for c in notebook.cells]
        self.assertLess(ids.index("retrieve-web"), ids.index("diagnostics-guide"))
        self.assertEqual(ids.index("diagnostics-guide") + 1, ids.index("diagnostics"))
        canonical = nbformat.read(PYTHON, as_version=4)
        self.assertEqual(next(c.source for c in notebook.cells if c.id == "diagnostics-guide"),
                         csharp_markdown(canonical.cells[20].source))
        # Preserve every explanation/reference, adapting language-specific names.
        text = "\n".join(c.source for c in notebook.cells if c.cell_type == "markdown")
        for cell in canonical.cells:
            if cell.cell_type == "markdown":
                self.assertIn(csharp_markdown(cell.source), text)
        for path, content in ((NOTEBOOK, source), (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            for link in re.findall(r"\]\(([^)]+)\)", content):
                if "://" not in link:
                    self.assertTrue((path.parent / link.split("#", 1)[0]).is_file(), f"Missing local link: {link}")
        for cell in notebook.cells:
            if cell.id.startswith("retrieve-"):
                self.assertEqual(cell.source.count('["includeReferenceSourceData"] = true'), 3)
                self.assertIn("var payload = new JsonObject", cell.source)


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class KnowledgeBaseNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-kb11-notebook-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        cls.settings_text = (
            "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
            "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
            "AZURE_AI_ACCOUNT_NAME=workshop-ai\nAZURE_AI_MODEL_DEPLOYMENT_NAME=workshop-chat\n"
            "AZURE_AI_MODEL_NAME=gpt-5-mini\nWORKSHOP_PREFIX=alice01\nWEBIQ_API_KEY=synthetic-test-key\n"
        )
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\nWEBIQ_API_KEY=wrong-key\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(cls.settings_text, encoding="utf-8")
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
                                capture_output=True, text=True, timeout=90,
                                env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1"})
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook = nbformat.read(NOTEBOOK, as_version=4)
        cls.original_bytes = NOTEBOOK.read_bytes()

    def execute(self, cells=None, checks="", mode="", cleanup=False, output_mode="answerSynthesis"):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if cells is not None:
            self.notebook.cells = [c for c in self.notebook.cells if c.cell_type != "code" or
                                   c.id in {"setup", "output-mode"} or c.id in cells]
        for cell in self.notebook.cells:
            if cell.id == "output-mode":
                cell.source = cell.source.replace('string outputMode = "answerSynthesis";',
                                                  f"string outputMode = {json.dumps(output_mode)};")
            if cleanup:
                cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
        tests = (ROOT / "tests" / "c-sharp-knowledge-base" / "KnowledgeBaseTests.cs").read_text(encoding="utf-8")
        handler = "using System.Net;\n" + tests[tests.index("public sealed record CapturedKnowledgeBaseRequest"):] + """
client.Dispose();
KnowledgeBaseOfflineHandler handler = new KnowledgeBaseOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
tokenProvider = () => { tokenCalls++; return Task.FromResult("synthetic-search-token"); };
transport = new KnowledgeBaseTransport(client, settings.Tenant, () => tokenProvider());
handler.Mode = """ + json.dumps(mode) + ";\n"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)

    def cell(self, id):
        return next(c.source for c in self.notebook.cells if c.id == id)

    def test_actual_full_notebook_current_python11_wire_parity(self):
        checks = [
            'if (settings.Prefix != "alice01" || settings.WebIqKey != "synthetic-test-key") throw new Exception("dotenv precedence");',
            'if (handler.Requests.Count != 12 || tokenCalls != 12) throw new Exception("Hidden calls");',
            'for (int i = 0; i < handler.Requests.Count; i++) { var r = handler.Requests[i];'
            'if (r.Authorization != "Bearer synthetic-search-token" || r.HasApiKey ||'
            'r.QueryAuthorization != (i == 3 || i >= 8 ? "synthetic-search-token" : null)) throw new Exception("Wrong auth headers"); }',
        ]
        bodies = python_payloads()
        for request, payload in zip([1, 3, 4, 5, 6, 7, 8, 9, 10, 11], bodies):
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{request}].Body, JsonNode.Parse({json.dumps(json.dumps(payload))}))) throw new Exception("Python11 payload {request}");')
        self.execute(checks="\n".join(checks))

    def test_actual_isolated_setup_and_authoring_steps(self):
        self.execute(cells=set(), checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Setup made calls"); client.Dispose();')
        for cell, index in (("create-blob-source", 2), ("create-sharepoint-source", 3),
                            ("create-webiq-source", 4), ("create-knowledge-base", 5)):
            with self.subTest(cell=cell):
                expected = json.dumps(json.dumps(python_payloads()[index]))
                self.execute(cells={cell}, checks=f"""
if (handler.Requests.Count != 1 || tokenCalls != 1 || handler.Requests[0].QueryAuthorization != null ||
    !JsonNode.DeepEquals(handler.Requests[0].Body, JsonNode.Parse({expected}))) throw new Exception("Isolated authoring parity");
client.Dispose();
""")

    def test_actual_isolated_all_queries_both_modes_and_rich_mixed_references(self):
        for index, selection in enumerate(("internal", "web", "sharepoint", "mixed"), start=6):
            source = self.cell("retrieve-" + selection)
            default = json.dumps(json.dumps(python_payloads()[index]))
            extractive = json.dumps(json.dumps(python_payloads("extractiveData")[index]))
            with self.subTest(selection=selection):
                self.execute(cells=set(), checks="""
StringWriter capturedOutput = new StringWriter();
TextWriter savedOutput = Console.Out;
async Task QueryStep() {
""" + source + f"""
}}
try {{
    Console.SetOut(capturedOutput);
    await QueryStep();
    outputMode = "extractiveData";
    await QueryStep();
}} finally {{ Console.SetOut(savedOutput); }}
if (handler.Requests.Count != 2 || tokenCalls != 2 ||
    !JsonNode.DeepEquals(handler.Requests[0].Body, JsonNode.Parse({default})) ||
    !JsonNode.DeepEquals(handler.Requests[1].Body, JsonNode.Parse({extractive})) ||
    handler.Requests.Any(r => r.QueryAuthorization != "synthetic-search-token")) throw new Exception("Isolated query parity");
string logs = capturedOutput.ToString();
foreach (string expected in new[] {{ "[ref_id:0] travel.pdf pp. 2-4", "travel.pdf#page=2", "[ref_id:1] report.pdf p. 7",
    "[ref_id:2] Public evidence", "https://example.test/public#section" }})
    if (!logs.Contains(expected)) throw new Exception("Missing rich mixed reference: " + expected);
int before = handler.Requests.Count;
""" + self.cell("diagnostics") + """
if (handler.Requests.Count != before || tokenCalls != before) throw new Exception("Diagnostics made a call");
client.Dispose();
""")

    def test_actual_schema_acl_visible_counts_and_error_guards(self):
        source = self.cell("verify-indexes")
        self.execute(cells=set(), checks="""
async Task VerifyStep() {
""" + source + """
}
await VerifyStep();
if (handler.Requests.Count != 4 || tokenCalls != 4 || handler.Requests[3].QueryAuthorization != "synthetic-search-token" ||
    handler.Requests.Take(3).Any(r => r.QueryAuthorization != null)) throw new Exception("Read-only verification");
foreach (string index in new[] { "blob", "sharepoint" })
foreach (var failure in new[] { ("missing", "HTTP 404"), ("page-schema", "retrievable content/source"),
    ("unretrievable", "retrievable content/source"), ("unsearchable", "retrievable content/source"),
    ("semantic", "missing semantic configuration"), ("empty", "No chunks are visible"),
    ("bool-count", "No chunks are visible"), ("string-count", "No chunks are visible"),
    ("float-count", "No chunks are visible"), ("missing-count", "No chunks are visible"),
    ("partial", "partial response") })
{
    handler.Reset(failure.Item1, index);
    string errorText = "";
    try { await VerifyStep(); } catch (Exception error) { errorText = error.Message; }
    if (!errorText.Contains(failure.Item2) || handler.Requests.Any(r => r.Method == "PUT")) throw new Exception("Schema/count failure swallowed");
}
foreach (string mode in new[] { "acl-disabled", "acl-user", "acl-group", "acl-missing" })
{
    handler.Reset(mode, "sharepoint");
    string errorText = "";
    try { await VerifyStep(); } catch (Exception error) { errorText = error.Message; }
    if (!errorText.Contains("user/group permission filtering") || handler.Requests.Count != 3) throw new Exception("ACL check failed");
}
client.Dispose();
""")

    def test_actual_partial_malformed_errors_redaction_and_raw_diagnostics(self):
        source = self.cell("retrieve-mixed")
        self.execute(cells=set(), checks="""
async Task QueryStep() {
""" + source + """
}
foreach (var failure in new[] { ("denied", "HTTP 403"), ("unauthorized", "HTTP 401"), ("throttled", "HTTP 429"),
    ("partial", "partial or a source failed"), ("activity-error", "partial or a source failed"),
    ("malformed", "invalid JSON"), ("malformed-shape", "Malformed retrieval response"),
    ("malformed-activity", "Malformed retrieval response"), ("malformed-references", "Malformed retrieval response"),
    ("network", "network failure"), ("timeout", "timed out"), ("echo", ""), ("no-references", "") })
{
    handler.Reset(failure.Item1);
    StringWriter captured = new StringWriter();
    TextWriter savedOut = Console.Out, savedErr = Console.Error;
    string errorText = "";
    try { Console.SetOut(captured); Console.SetError(captured); await QueryStep(); }
    catch (Exception error) { errorText = error.Message; }
    finally { Console.SetOut(savedOut); Console.SetError(savedErr); }
    if (!errorText.Contains(failure.Item2) || handler.Requests.Count != 1) throw new Exception("Failed query swallowed/retried: " + failure.Item1);
    string logs = captured.ToString() + errorText;
    if (logs.Contains("synthetic-test-key") || logs.Contains("synthetic-search-token")) throw new Exception("Credential leaked");
    if (failure.Item1 == "no-references" && !logs.Contains("alone does not establish an authentication failure"))
        throw new Exception("False auth inference");
}
handler.Reset("echo");
await QueryStep();
StringWriter diagnosticOutput = new StringWriter();
TextWriter priorOutput = Console.Out;
int before = handler.Requests.Count, beforeTokens = tokenCalls;
try { Console.SetOut(diagnosticOutput);
""" + self.cell("diagnostics") + """
} finally { Console.SetOut(priorOutput); }
string diagnosticText = diagnosticOutput.ToString();
if (diagnosticText.Contains("synthetic-test-key") || diagnosticText.Contains("synthetic-search-token") ||
    !diagnosticText.Contains("Full activity:") || !diagnosticText.Contains("Web IQ activity only:") ||
    !diagnosticText.Contains("Raw references:") || handler.Requests.Count != before || tokenCalls != beforeTokens)
    throw new Exception("Raw existing response diagnostics/redaction");
client.Dispose();
""")

    def test_actual_cleanup_disabled_and_exact_opt_in_scope(self):
        self.execute(cells={"cleanup"}, checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Cleanup enabled"); client.Dispose();')
        self.execute(cells={"cleanup"}, cleanup=True, mode="missing", checks="""
if (handler.Requests.Count != 4 || tokenCalls != 4 || handler.Requests.Any(r => r.Method != "DELETE" || r.QueryAuthorization != null))
    throw new Exception("Cleanup scope");
foreach (var pair in new[] { (0, "/knowledgebases/alice01-kb?"), (1, "/knowledgesources/alice01-kb-webiq?"),
    (2, "/knowledgesources/alice01-kb-sharepoint?"), (3, "/knowledgesources/alice01-kb-blob?") })
    if (!handler.Requests[pair.Item1].Uri.Contains(pair.Item2)) throw new Exception("Cleanup names/order");
client.Dispose();
""")

    def test_actual_configuration_prefix_required_and_query_validation_before_calls(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        setup = next(c for c in self.notebook.cells if c.id == "setup")
        setup.source = setup.source.replace('KnowledgeBaseSettings settings = KnowledgeBaseSettings.Load(Path.GetFullPath(".."));', """
string configurationFailure = "";
KnowledgeBaseSettings settings = new KnowledgeBaseSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "workshop-ai", "workshop-chat", "gpt-5-mini", "alice01", "synthetic-test-key");
try { settings = KnowledgeBaseSettings.Load(Path.GetFullPath("..")); }
catch (Exception error) { configurationFailure = error.Message; }
""")
        (self.path / ".env.azure").write_text(self.settings_text.replace("WORKSHOP_PREFIX=alice01", "WORKSHOP_PREFIX="), encoding="utf-8")
        try:
            self.execute(cells=set(), checks="""
if (!configurationFailure.Contains("Missing setting: WORKSHOP_PREFIX") || tokenCalls != 0 || handler.Requests.Count != 0)
    throw new Exception("Missing prefix prompted or made calls");
client.Dispose();
""")
        finally:
            self.notebook = original
            (self.path / ".env.azure").write_text(self.settings_text, encoding="utf-8")
        source = self.cell("retrieve-internal")
        source = re.sub(r'string question = .*?;', 'string question = " ";', source, count=1)
        self.execute(cells=set(), checks="""
string queryFailure = "";
try {
""" + source + """
} catch (Exception error) { queryFailure = error.Message; }
if (!queryFailure.Contains("non-empty") || tokenCalls != 0 || handler.Requests.Count != 0) throw new Exception("Invalid query reached service");
client.Dispose();
""")


if __name__ == "__main__":
    unittest.main()
