"""Offline execution of actual C#09 inline cells and current Python09 wire parity."""
import ast
import copy
import json
import os
import shutil
import subprocess
import unittest
import uuid

import nbformat

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
NOTEBOOK = ROOT / "c-sharp-notebooks" / "09-foundry-iq-webiq-only.ipynb"
PROJECT = ROOT / "c-sharp-console-apps" / "09-foundry-iq-webiq-only"
QUESTION = "What is the latest stable Python release and its release date, according to Python.org?"


def python_payloads(mode="answerSynthesis"):
    notebook = nbformat.read(ROOT / "python-notebooks" / "09-foundry-iq-webiq-only.ipynb", as_version=4)
    values = {
        "webiq_source_name": "alice01-nb14-webiq", "knowledge_base_name": "alice01-nb14-kb",
        "webiq_api_key": "synthetic-test-key", "aoai_endpoint": "https://workshop-ai.openai.azure.com",
        "chat_deployment": "workshop-chat", "chat_model": "gpt-5-mini",
        "question": QUESTION, "output_mode": mode,
    }
    bodies = []
    for cell in notebook.cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("webiq_source", "knowledge_base"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<python09-payload>", "eval"),
                                        {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                body = next((item.value for item in call.keywords if item.arg == "json"), None)
                if body is not None:
                    bodies.append(eval(compile(ast.Expression(body), "<python09-payload>", "eval"),
                                       {"__builtins__": {}}, values))
    return bodies


class WebIqConsoleTests(unittest.TestCase):
    def test_console_runtime_and_current_python09_payload_parity(self):
        path = ROOT / (".csharp-webiq09-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-webiq-only"),
                                 "--", "--wire", str(path)], cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual([request["Body"] for request in wire], python_payloads())

    def test_real_entrypoint_offline_help_aliases_and_confirmation_guards(self):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        fixture = ROOT / (".csharp-webiq09-help-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        (fixture / ".env").write_text("AZURE_TENANT_ID=invalid\nWORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        environment = {**os.environ, "PATH": "", "WEBIQ_API_KEY": "", "WORKSHOP_PREFIX": "",
                       "AZURE_TENANT_ID": "", "AZURE_SEARCH_ENDPOINT": ""}
        dotnet = shutil.which("dotnet")
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "WebIqOnly.dll"
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["cleanup"], False), (["cleanup", "--help"], False),
                              (["cleanup", "--confirm", "other"], False), (["unknown"], False),
                              (["run", "--output-mode", "news"], False)):
            with self.subTest(args=args):
                result = subprocess.run([dotnet, str(dll), *args], cwd=fixture, env=environment,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertEqual(result.stderr, "")
                    self.assertIn("cleanup --confirm", result.stdout)
                    outputs.append(result.stdout)
                else:
                    self.assertNotIn("Missing setting", result.stderr)
                self.assertEqual([path.name for path in fixture.iterdir()], [".env"])
        self.assertEqual(len(set(outputs)), 1)

    def test_notebook_schema_standalone_teaching_and_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        source = "\n".join(c.source for c in notebook.cells)
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps", "Azure.Identity"):
            self.assertNotIn(forbidden, code)
        for expected in ('Path.GetFullPath("..")', "bool CLEAN_UP = false;", "bool CHECK_CONNECTIVITY = false;",
                         '"https://search.azure.com/.default"', '"storedHeaders"', '"x-apikey"',
                         '"maxOutputTokens"] = 4000', '"name"] = "web"'):
            self.assertIn(expected, code)
        self.assertNotIn("facilitator", source.lower())
        self.assertNotIn("attendee", source.lower())
        self.assertTrue(all(c.execution_count is None and not c.outputs for c in notebook.cells if c.cell_type == "code"))
        ids = [c.id for c in notebook.cells]
        self.assertLess(ids.index("retrieve"), ids.index("portal-guide"))
        self.assertLess(ids.index("portal-guide"), ids.index("portal-screenshot"))
        python = nbformat.read(ROOT / "python-notebooks" / "09-foundry-iq-webiq-only.ipynb", as_version=4)
        self.assertEqual(next(c.source for c in notebook.cells if c.id == "portal-screenshot"),
                         python.cells[13].source)
        self.assertTrue((ROOT / "docs" / "webiq-only-knowledge-base-portal.png").is_file())
        import re
        readme = PROJECT / "README.md"
        for path, text in ((NOTEBOOK, source), (readme, readme.read_text(encoding="utf-8"))):
            for link in re.findall(r"\]\(([^)]+)\)", text):
                if "://" not in link:
                    self.assertTrue((path.parent / link).is_file(), f"Missing local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class WebIqNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-webiq09-notebook-" + uuid.uuid4().hex)
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

    def execute(self, cells=None, checks="", mode="", cleanup=False, probe=False, output_mode="answerSynthesis"):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if cells is not None:
            self.notebook.cells = [c for c in self.notebook.cells if c.cell_type != "code" or
                                   c.id in {"setup", "question", "dispose"} or c.id in cells]
        for cell in self.notebook.cells:
            if cell.id == "question":
                cell.source = cell.source.replace(
                    'string question = $"What is the weather forecast for Seattle, Washington, on {today}? Cite the forecast source and date.";',
                    "string question = " + json.dumps(QUESTION) + ";")
                cell.source = cell.source.replace('string outputMode = "answerSynthesis";',
                                                  f"string outputMode = {json.dumps(output_mode)};")
            if cleanup:
                cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
            if probe:
                cell.source = cell.source.replace("bool CHECK_CONNECTIVITY = false;", "bool CHECK_CONNECTIVITY = true;")
        tests = (ROOT / "tests" / "c-sharp-webiq-only" / "WebIqOnlyTests.cs").read_text(encoding="utf-8")
        handler = "using System.Net;\n" + tests[tests.index("public sealed record CapturedWebIqRequest"):] + """
client.Dispose();
WebIqOfflineHandler handler = new WebIqOfflineHandler();
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

    def test_actual_full_notebook_python09_parity_and_no_optional_actions(self):
        checks = [
            'if (settings.Prefix != "alice01" || settings.WebIqKey != "synthetic-test-key") throw new Exception("dotenv precedence");',
            'if (handler.Requests.Count != 3 || tokenCalls != 3) throw new Exception("Hidden calls");',
            'if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-search-token" || r.HasApiKey || r.QueryAuthorization != null)) throw new Exception("Auth headers");',
        ]
        for index, payload in enumerate(python_payloads()):
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse({json.dumps(json.dumps(payload))}))) throw new Exception("Python09 payload {index}");')
        self.execute(checks="\n".join(checks))

    def test_actual_isolated_steps_and_extractive_mode(self):
        for cell, method in (("create-webiq-source", "PUT"), ("create-knowledge-base", "PUT"), ("retrieve", "POST")):
            self.execute(cells={cell}, checks=f'if (handler.Requests.Count != 1 || handler.Requests[0].Method != "{method}") throw new Exception("Hidden actions");')
        self.execute(cells={"retrieve"}, output_mode="extractiveData", checks=
                     f'if (!JsonNode.DeepEquals(handler.Requests[0].Body, JsonNode.Parse({json.dumps(json.dumps(python_payloads("extractiveData")[2]))}))) throw new Exception("extractive parity");')

    def test_actual_errors_redacted_before_failure_and_no_reference_interpretation(self):
        for mode, expected in (("denied", "HTTP 403"), ("partial", "partial or Web IQ failed"),
                               ("activity-error", "partial or Web IQ failed"), ("malformed", "invalid JSON"),
                               ("echo", ""), ("no-references", "")):
            original = self.notebook
            self.notebook = copy.deepcopy(original)
            cell = next(c for c in self.notebook.cells if c.id == "retrieve")
            cell.source = """
string failureMessage = "";
StringWriter capturedOutput = new StringWriter();
StringWriter capturedError = new StringWriter();
TextWriter savedOutput = Console.Out, savedError = Console.Error;
try {
Console.SetOut(capturedOutput); Console.SetError(capturedError);
""" + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
finally { Console.SetOut(savedOutput); Console.SetError(savedError); }
"""
            try:
                self.execute(cells={"retrieve"}, mode=mode, checks=f"""
if (!failureMessage.Contains({json.dumps(expected)}) || handler.Requests.Count != 1)
    throw new Exception("Failure swallowed/retried");
string logs = capturedOutput.ToString() + capturedError.ToString();
if (logs.Contains("synthetic-test-key") || logs.Contains("synthetic-search-token")) throw new Exception("Secret leak");
""" + ('if (!logs.Contains("synthetic-request") || !logs.Contains("UTC (Date):") || !logs.Contains("<redacted>")) throw new Exception("Missing diagnostics");' if mode == "denied" else "") +
                    ('if (!logs.Contains("alone does not establish an authentication failure")) throw new Exception("Missing-reference auth confusion");' if mode == "no-references" else ""))
            finally:
                self.notebook = original

    def test_actual_cleanup_opt_in_exact_names_and_optional_unauthenticated_probe(self):
        self.execute(cells={"cleanup", "network-diagnostics"}, checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Optional steps enabled");')
        self.execute(cells={"cleanup"}, cleanup=True, mode="missing", checks="""
if (handler.Requests.Count != 2 || handler.Requests.Any(r => r.Method != "DELETE")) throw new Exception("Cleanup scope");
if (!handler.Requests[0].Uri.Contains("/knowledgebases/alice01-nb14-kb?") ||
    !handler.Requests[1].Uri.Contains("/knowledgesources/alice01-nb14-webiq?")) throw new Exception("Legacy names/order");
""")
        self.execute(cells={"network-diagnostics"}, probe=True, mode="probe", checks="""
if (tokenCalls != 0 || handler.Requests.Count != 1 || handler.Requests[0].Authorization != null ||
    handler.Requests[0].HasApiKey || handler.Requests[0].Method != "GET") throw new Exception("Probe sends credentials");
""")

    def test_actual_configuration_and_query_validation_before_calls(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        setup = next(c for c in self.notebook.cells if c.id == "setup")
        setup.source = setup.source.replace(
            'WebIqSettings settings = WebIqSettings.Load(Path.GetFullPath(".."));',
            """
string configurationFailure = "";
WebIqSettings settings = new WebIqSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "workshop-ai", "workshop-chat", "gpt-5-mini",
    "alice01", "synthetic-test-key");
try { settings = WebIqSettings.Load(Path.GetFullPath("..")); }
catch (Exception error) { configurationFailure = error.Message; }
""")
        (self.path / ".env.azure").write_text(self.settings_text.replace("WORKSHOP_PREFIX=alice01",
                                                                       "WORKSHOP_PREFIX=invalid!"), encoding="utf-8")
        try:
            self.execute(cells=set(), checks="""
if (!configurationFailure.Contains("prefix") || tokenCalls != 0 || handler.Requests.Count != 0)
    throw new Exception("Bad configuration made calls");
""")
        finally:
            self.notebook = original
            (self.path / ".env.azure").write_text(self.settings_text, encoding="utf-8")
        self.notebook = copy.deepcopy(original)
        cell = next(c for c in self.notebook.cells if c.id == "retrieve")
        cell.source = 'question = " "; string failureMessage = "";\ntry {\n' + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
"""
        try:
            self.execute(cells={"retrieve"}, checks="""
if (!failureMessage.Contains("non-empty") || tokenCalls != 0 || handler.Requests.Count != 0)
    throw new Exception("Invalid query made calls");
""")
        finally:
            self.notebook = original


if __name__ == "__main__":
    unittest.main()
