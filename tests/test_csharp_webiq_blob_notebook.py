"""Offline C#10 inline cell execution and exact current Python10 wire parity."""
import ast
import copy
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
NOTEBOOK = ROOT / "c-sharp-notebooks" / "10-foundry-iq-webiq-blob.ipynb"
PROJECT = ROOT / "c-sharp-console-apps" / "10-foundry-iq-webiq-blob"


def python_payloads(mode="answerSynthesis"):
    notebook = nbformat.read(ROOT / "python-notebooks" / "10-foundry-iq-webiq-blob.ipynb", as_version=4)
    values = {
        "blob_index_name": "alice01-cu-chunks", "blob_source_name": "alice01-nb10-blob",
        "webiq_source_name": "alice01-nb10-webiq", "knowledge_base_name": "alice01-nb10-kb",
        "webiq_api_key": "synthetic-test-key", "aoai_endpoint": "https://workshop-ai.openai.azure.com",
        "chat_deployment": "workshop-chat", "chat_model": "gpt-5-mini", "output_mode": mode,
    }
    bodies = []
    for cell in notebook.cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("blob_source", "webiq_source", "knowledge_base", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<python10-payload>", "eval"),
                                        {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                body = next((item.value for item in call.keywords if item.arg == "json"), None)
                if body is not None:
                    bodies.append(eval(compile(ast.Expression(body), "<python10-payload>", "eval"),
                                       {"__builtins__": {}}, values))
    return bodies


class WebIqBlobConsoleTests(unittest.TestCase):
    def test_console_harness_and_current_python10_wire_parity(self):
        path = ROOT / (".csharp-webiq10-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-webiq-blob"),
                                 "--", "--wire", str(path)], cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual([request["Body"] for request in wire if request["Body"] is not None], python_payloads())

    def test_real_entrypoint_offline_help_and_unknown_argument_guards(self):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        fixture = ROOT / (".csharp-webiq10-help-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        (fixture / ".env").write_text("WORKSHOP_PREFIX=invalid!\nAZURE_TENANT_ID=invalid\n", encoding="utf-8")
        environment = {**os.environ, "PATH": "", "WORKSHOP_PREFIX": "", "WEBIQ_API_KEY": "",
                       "AZURE_TENANT_ID": "", "AZURE_SEARCH_ENDPOINT": ""}
        dotnet = shutil.which("dotnet")
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "WebIqBlob.dll"
        outputs = []
        cases = (([], True), (["help"], True), (["--help"], True), (["-h"], True), (["HELP"], True),
                 (["cleanup"], False), (["cleanup", "--help"], False), (["cleanup", "--confirm", "extra"], False),
                 (["unknown"], False), (["run", "-h"], False), (["run", "--bad"], False),
                 (["run", "--output-mode", "wrong"], False), (["help", "extra"], False),
                 (["blob-source", "extra"], False), (["retrieve-mixed", "one", "two"], False))
        for args, success in cases:
            with self.subTest(args=args):
                result = subprocess.run([dotnet, str(dll), *args], cwd=fixture, env=environment,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertEqual(result.stderr, "")
                    self.assertIn("verify-index", result.stdout)
                    self.assertIn("cleanup --confirm", result.stdout)
                    outputs.append(result.stdout)
                else:
                    self.assertNotIn("Missing setting", result.stderr)
                self.assertEqual([p.name for p in fixture.iterdir()], [".env"])
        self.assertEqual(len(set(outputs)), 1)

    def test_notebook_schema_standalone_conventions_and_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        source = "\n".join(c.source for c in notebook.cells)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps", "Azure.Identity"):
            self.assertNotIn(forbidden, code)
        for expected in ('Path.GetFullPath("..")', 'bool CLEAN_UP = false;', '"-cu-chunks"',
                         '"-nb10-blob"', '"-nb10-webiq"', '"-nb10-kb"',
                         '"https://search.azure.com/.default"', '"storedHeaders"', '"x-apikey"',
                         '"maxOutputTokens"] = 4000', '"name"] = "web"', "client.Dispose();"):
            self.assertIn(expected, code)
        for forbidden in ("facilitator", "attendee", "blob-chunks", "cu-blob"):
            self.assertNotIn(forbidden, source.lower())
        self.assertTrue(all(c.execution_count is None and not c.outputs for c in notebook.cells if c.cell_type == "code"))
        ids = [c.id for c in notebook.cells]
        self.assertLess(ids.index("retrieve-mixed"), ids.index("diagnostics-guide"))
        self.assertLess(ids.index("diagnostics-guide"), ids.index("portal-screenshot"))
        python = nbformat.read(ROOT / "python-notebooks" / "10-foundry-iq-webiq-blob.ipynb", as_version=4)
        self.assertEqual(next(c.source for c in notebook.cells if c.id == "portal-screenshot"),
                         python.cells[16].source)
        for path, text in ((NOTEBOOK, source), (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            for link in re.findall(r"\]\(([^)]+)\)", text):
                if "://" not in link:
                    self.assertTrue((path.parent / link).is_file(), f"Missing local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class WebIqBlobNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-webiq10-notebook-" + uuid.uuid4().hex)
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
                                   c.id in {"setup", "output-mode", "dispose"} or c.id in cells]
        for cell in self.notebook.cells:
            if cell.id == "output-mode":
                cell.source = cell.source.replace('string outputMode = "answerSynthesis";',
                                                  f"string outputMode = {json.dumps(output_mode)};")
            if cleanup:
                cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
        tests = (ROOT / "tests" / "c-sharp-webiq-blob" / "WebIqBlobTests.cs").read_text(encoding="utf-8")
        handler = "using System.Net;\n" + tests[tests.index("public sealed record CapturedWebIqBlobRequest"):] + """
client.Dispose();
WebIqBlobOfflineHandler handler = new WebIqBlobOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
tokenProvider = () => { tokenCalls++; return Task.FromResult("synthetic-search-token"); };
transport = new WebIqTransport(client, settings.Tenant, () => tokenProvider());
handler.Mode = """ + json.dumps(mode) + ";\n"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)

    def test_actual_full_notebook_current_python10_parity(self):
        checks = [
            'if (settings.Prefix != "alice01" || settings.WebIqKey != "synthetic-test-key") throw new Exception("dotenv precedence");',
            'if (handler.Requests.Count != 8 || tokenCalls != 8) throw new Exception("Hidden calls");',
            'if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-search-token" || r.HasApiKey || r.HasQueryAuthorization)) throw new Exception("Wrong auth headers");',
            'if (!handler.Requests[0].Uri.Contains("/indexes/alice01-cu-chunks?")) throw new Exception("Wrong CU index");',
        ]
        for index, payload in enumerate(python_payloads(), start=1):
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse({json.dumps(json.dumps(payload))}))) throw new Exception("Python10 payload {index}");')
        self.execute(checks="\n".join(checks))

    def test_actual_isolated_setup_source_base_and_retrieval_modes(self):
        self.execute(cells=set(), checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Setup has HTTP");')
        for cell, index in (("verify-index", 0), ("create-blob-source", 1), ("create-webiq-source", 2),
                            ("create-knowledge-base", 3), ("retrieve-blob", 4), ("retrieve-web", 5), ("retrieve-mixed", 6)):
            for mode in (("answerSynthesis", "extractiveData") if cell.startswith("retrieve-") else ("answerSynthesis",)):
                with self.subTest(cell=cell, mode=mode):
                    count = 2 if cell == "verify-index" else 1
                    request = 1 if cell == "verify-index" else 0
                    payload = python_payloads(mode)[index]
                    self.execute(cells={cell}, output_mode=mode, checks=f"""
if (handler.Requests.Count != {count} || tokenCalls != {count}) throw new Exception("Hidden actions");
if (!JsonNode.DeepEquals(handler.Requests[{request}].Body, JsonNode.Parse({json.dumps(json.dumps(payload))})))
    throw new Exception("Isolated step parity");
""")

    def test_actual_index_schema_empty_and_partial_guards(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        cell = next(c for c in self.notebook.cells if c.id == "verify-index")
        cell.source = 'string failureMessage = ""; try {\n' + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
"""
        try:
            for mode, expected, count in (
                ("missing", "HTTP 404", 1), ("schema", "retrievable content/source", 1),
                ("unretrievable", "retrievable content/source", 1), ("unsearchable", "retrievable content/source", 1),
                ("semantic", "missing semantic configuration", 1), ("empty", "No chunks are visible", 2),
                ("bool-count", "No chunks are visible", 2), ("string-count", "No chunks are visible", 2),
                ("float-count", "No chunks are visible", 2), ("missing-count", "No chunks are visible", 2),
                ("partial", "partial response", 1),
            ):
                with self.subTest(mode=mode):
                    self.execute(cells={"verify-index"}, mode=mode, checks=f"""
if (!failureMessage.Contains({json.dumps(expected)}) || handler.Requests.Count != {count} ||
    handler.Requests.Any(r => r.Method == "PUT")) throw new Exception("Prerequisite failure swallowed");
""")
        finally:
            self.notebook = original

    def test_actual_errors_redaction_and_no_false_auth_inference(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        cell = next(c for c in self.notebook.cells if c.id == "retrieve-mixed")
        cell.source = """
string failureMessage = "";
StringWriter capturedOutput = new StringWriter(), capturedError = new StringWriter();
TextWriter savedOutput = Console.Out, savedError = Console.Error;
try { Console.SetOut(capturedOutput); Console.SetError(capturedError);
""" + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
"""
        try:
            for mode, expected in (("denied", "HTTP 403"), ("unauthorized", "HTTP 401"), ("throttled", "HTTP 429"),
                                   ("partial", "partial or a source failed"), ("activity-error", "partial or a source failed"),
                                   ("malformed", "invalid JSON"), ("echo", ""), ("no-references", "")):
                with self.subTest(mode=mode):
                    self.execute(cells={"retrieve-mixed"}, mode=mode, checks=f"""
if (!failureMessage.Contains({json.dumps(expected)}) || handler.Requests.Count != 1) throw new Exception("Failure swallowed/retried");
string logs = capturedOutput.ToString() + capturedError.ToString();
if (logs.Contains("synthetic-test-key") || logs.Contains("synthetic-search-token")) throw new Exception("Credential leak");
""" + ('if (!logs.Contains("synthetic-request") || !logs.Contains("UTC (Date):") || !logs.Contains("<redacted>")) throw new Exception("Missing diagnostics");'
                       if mode in ("denied", "unauthorized", "throttled") else "") +
                        ('if (!logs.Contains("alone does not establish an authentication failure")) throw new Exception("False auth inference");'
                         if mode == "no-references" else ""))
        finally:
            self.notebook = original

    def test_actual_cleanup_disabled_and_exact_opt_in_scope(self):
        self.execute(cells={"cleanup"}, checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Cleanup enabled");')
        self.execute(cells={"cleanup"}, cleanup=True, mode="missing", checks="""
if (handler.Requests.Count != 3 || handler.Requests.Any(r => r.Method != "DELETE")) throw new Exception("Cleanup scope");
if (!handler.Requests[0].Uri.Contains("/knowledgebases/alice01-nb10-kb?") ||
    !handler.Requests[1].Uri.Contains("/knowledgesources/alice01-nb10-webiq?") ||
    !handler.Requests[2].Uri.Contains("/knowledgesources/alice01-nb10-blob?")) throw new Exception("Cleanup names/order");
""")

    def test_actual_configuration_and_query_validation_before_calls(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        setup = next(c for c in self.notebook.cells if c.id == "setup")
        setup.source = setup.source.replace('WebIqSettings settings = WebIqSettings.Load(Path.GetFullPath(".."));', """
string configurationFailure = "";
WebIqSettings settings = new WebIqSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "workshop-ai", "workshop-chat", "gpt-5-mini",
    "alice01", "synthetic-test-key");
try { settings = WebIqSettings.Load(Path.GetFullPath("..")); }
catch (Exception error) { configurationFailure = error.Message; }
""")
        (self.path / ".env.azure").write_text(self.settings_text.replace("WORKSHOP_PREFIX=alice01", "WORKSHOP_PREFIX=invalid!"), encoding="utf-8")
        try:
            self.execute(cells=set(), checks="""
if (!configurationFailure.Contains("prefix") || tokenCalls != 0 || handler.Requests.Count != 0) throw new Exception("Configuration failure made calls");
""")
        finally:
            self.notebook = original
            (self.path / ".env.azure").write_text(self.settings_text, encoding="utf-8")
        self.notebook = copy.deepcopy(original)
        cell = next(c for c in self.notebook.cells if c.id == "retrieve-blob")
        cell.source = cell.source.replace('string blobQuestion = $"Using {settings.BlobSourceName}, what receipts do I need for travel reimbursement?";',
                                          'string blobQuestion = " ";')
        cell.source = 'string failureMessage = ""; try {\n' + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
"""
        try:
            self.execute(cells={"retrieve-blob"}, checks="""
if (!failureMessage.Contains("non-empty") || tokenCalls != 0 || handler.Requests.Count != 0) throw new Exception("Invalid query made calls");
""")
        finally:
            self.notebook = original


if __name__ == "__main__":
    unittest.main()
