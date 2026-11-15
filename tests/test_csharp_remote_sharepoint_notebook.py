"""Offline runtime checks of the actual inline C#08 cells and console."""
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
NOTEBOOK = ROOT / "c-sharp-notebooks" / "08-sharepoint-remote.ipynb"
PROJECT = ROOT / "c-sharp-console-apps" / "08-sharepoint-remote"


def python_payloads():
    """Evaluate only current Python08's visible assignments/JSON, never requests."""
    notebook = nbformat.read(ROOT / "python-notebooks" / "08-sharepoint-remote.ipynb", as_version=4)
    values = {
        "remote_source_name": "alice01-sp-remote-source",
        "remote_base_name": "alice01-sp-remote-kb",
        "site_url": "https://contoso.sharepoint.com/sites/workshop",
    }
    bodies = []
    for cell in notebook.cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("remote_source", "remote_base", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<python08-payload>", "eval"),
                                        {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                payload = next((item.value for item in call.keywords if item.arg == "json"), None)
                if payload is not None:
                    bodies.append(eval(compile(ast.Expression(payload), "<python08-payload>", "eval"),
                                       {"__builtins__": {}}, values))
    return bodies


class RemoteConsoleTests(unittest.TestCase):
    def test_console_runtime_and_current_python08_payload_parity(self):
        path = ROOT / (".csharp-remote-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(
            ["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-remote-sharepoint"),
             "--", "--wire", str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual([request["Body"] for request in wire], python_payloads())

    def test_actual_entrypoint_help_aliases_and_guards_need_no_configuration_or_auth(self):
        # Build once so this test also works alone; every invocation uses the real entrypoint.
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        fixture = ROOT / (".csharp-remote-help-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        environment = {**os.environ, "AZURE_TENANT_ID": "invalid", "WORKSHOP_PREFIX": "invalid!",
                       "AZURE_SEARCH_ENDPOINT": "", "SHAREPOINT_SITE_URL": ""}
        # Invoke compiled app directly with empty PATH: no az/dotnet subprocess can hide here.
        environment["PATH"] = ""
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "SharePointRemote.dll"
        dotnet = shutil.which("dotnet")
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["cleanup"], False), (["cleanup", "--help"], False),
                              (["cleanup", "--confirm", "other-source"], False), (["unknown"], False)):
            with self.subTest(args=args):
                result = subprocess.run([dotnet, str(dll), *args], cwd=fixture, env=environment,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertEqual(result.stderr, "")
                    self.assertIn("cleanup --confirm", result.stdout)
                    outputs.append(result.stdout)
                else:
                    self.assertIn("Cleanup requires exactly: cleanup --confirm", result.stderr)
                self.assertEqual(list(fixture.iterdir()), [], "Help/guards must not create artifacts")
        self.assertEqual(len(set(outputs)), 1)

    def test_notebook_schema_and_inline_teaching(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(cell.source for cell in notebook.cells if cell.cell_type == "code")
        source = "\n".join(cell.source for cell in notebook.cells)
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps", "Azure.Identity"):
            self.assertNotIn(forbidden, code)
        self.assertIn('Path.GetFullPath("..")', code)
        self.assertIn("bool CLEAN_UP = false;", code)
        self.assertIn("models", code)
        self.assertIn("Microsoft 365 Copilot", source)
        self.assertIn("not an authorization boundary", source)
        self.assertIn("notebook 07", source)
        self.assertNotIn("facilitator", source.lower())
        self.assertNotIn("attendee", source.lower())
        self.assertTrue(all(cell.execution_count is None and not cell.outputs
                            for cell in notebook.cells if cell.cell_type == "code"))
        python = nbformat.read(ROOT / "python-notebooks" / "08-sharepoint-remote.ipynb", as_version=4)
        portal = next(cell for cell in notebook.cells if cell.id == "portal-guide")
        self.assertEqual(portal.source, next(cell for cell in python.cells if cell.id == "portal-guide").source)
        ids = [cell.id for cell in notebook.cells]
        self.assertLess(ids.index("retrieve"), ids.index("portal-guide"))
        self.assertLess(ids.index("portal-guide"), ids.index("comparison"))
        self.assertTrue((ROOT / "docs" / "sharepoint-remote-knowledge-base-portal.png").is_file())


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class RemoteNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-remote-notebook-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        cls.settings_text = (
            "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
            "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
            "SHAREPOINT_SITE_URL=https://contoso.sharepoint.com/sites/workshop\n"
            "WORKSHOP_PREFIX=alice01\n"
        )
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(cls.settings_text, encoding="utf-8")
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(
            ["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
            capture_output=True, text=True, timeout=90,
            env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1",
                 "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook = nbformat.read(NOTEBOOK, as_version=4)
        cls.original_bytes = NOTEBOOK.read_bytes()

    def execute(self, cells=None, checks="", mode="", cleanup=False, question=None):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if cells is not None:
            self.notebook.cells = [cell for cell in self.notebook.cells
                                   if cell.cell_type != "code" or cell.id == "setup" or cell.id in cells]
        if cleanup:
            next(cell for cell in self.notebook.cells if cell.id == "cleanup").source = (
                next(cell for cell in self.notebook.cells if cell.id == "cleanup").source.replace(
                    "bool CLEAN_UP = false;", "bool CLEAN_UP = true;"))
        if question is not None:
            cell = next(cell for cell in self.notebook.cells if cell.id == "retrieve")
            cell.source = cell.source.replace('"travel reimbursement receipts"', json.dumps(question))
        test_source = (ROOT / "tests" / "c-sharp-remote-sharepoint" / "RemoteSharePointTests.cs").read_text(encoding="utf-8")
        handler = test_source[test_source.index("public sealed record CapturedRemoteRequest"):]
        handler += """
client.Dispose();
RemoteOfflineHandler handler = new RemoteOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
tokenProvider = () => { tokenCalls++; return Task.FromResult("synthetic-user-token"); };
handler.Mode = """ + json.dumps(mode) + ";\n"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)
            self.assertFalse((self.path / ".workshop").exists())

    def test_actual_full_notebook_matches_python08_and_never_cleans_up(self):
        checks = [
            'if (settings.Prefix != "alice01") throw new Exception("dotenv override failed");',
            'if (handler.Requests.Count != 3 || tokenCalls != 4) throw new Exception("Hidden calls");',
            'if (handler.Requests.Any(r => r.Method == "DELETE" || r.HasApiKey || r.Authorization != "Bearer synthetic-user-token")) throw new Exception("Wrong authentication or cleanup");',
            'if (handler.Requests.Take(2).Any(r => r.QueryAuthorization != null) || handler.Requests[2].QueryAuthorization != "synthetic-user-token") throw new Exception("Raw query-user header differs");',
            'if (handler.Requests.Any(r => !r.Uri.EndsWith("?api-version=2026-08-01-preview"))) throw new Exception("API version");',
        ]
        for index, payload in enumerate(python_payloads()):
            checks.append(
                f'if (!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse({json.dumps(json.dumps(payload))}))) '
                f'throw new Exception("Python08 payload mismatch {index}");'
            )
        self.execute(checks="\n".join(checks))

    def test_actual_each_step_has_no_hidden_writes_or_paid_prerequisite_calls(self):
        for cell, method in (("source", "PUT"), ("knowledge-base", "PUT"), ("retrieve", "POST")):
            with self.subTest(cell=cell):
                self.execute(cells={cell}, checks=f'''
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "{method}")
    throw new Exception("Hidden actions in isolated step");
''')

    def test_actual_gateway_errors_print_status_body_and_correlation_before_failing(self):
        # Catch in an isolated copy of the real retrieve cell to inspect both diagnostics and fail-fast behavior.
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        cell = next(cell for cell in self.notebook.cells if cell.id == "retrieve")
        cell.source = """
bool failed = false;
StringWriter diagnostics = new StringWriter();
TextWriter savedError = Console.Error;
Console.SetError(diagnostics);
try {
""" + cell.source + """
} catch (HttpRequestException error) { failed = error.StatusCode == HttpStatusCode.BadGateway; }
finally { Console.SetError(savedError); }
"""
        try:
            self.execute(cells={"retrieve"}, mode="gateway", checks="""
if (!failed || handler.Requests.Count != 1) throw new Exception("Gateway was swallowed/retried");
foreach (string value in new[] { "HTTP: 502", "UTC (Date):", "2030", "synthetic-request",
    "synthetic-ms-request", "synthetic-correlation", "BadGateway", "/beta/copilot/retrieval" })
    if (!diagnostics.ToString().Contains(value)) throw new Exception("Missing correlation/body diagnostic: " + value);
if (diagnostics.ToString().Contains("synthetic-user-token")) throw new Exception("Token leak");
""")
        finally:
            self.notebook = original

    def test_actual_partial_source_errors_and_outside_urls_fail_not_empty_success(self):
        for mode, message in (("partial", "partial or a source failed"),
                              ("activity-error", "partial or a source failed"),
                              ("outside-site", "outside the selected site"),
                              ("sibling-site", "outside the selected site")):
            with self.subTest(mode=mode):
                self.assert_cell_failure("retrieve", mode, message)

    def assert_cell_failure(self, cell_id, mode, message, cleanup=False, question=None):
        # .NET Interactive omits exception text in some Jupyter error messages;
        # catch the actual runtime exception inside a copy of the original cell.
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        cell = next(cell for cell in self.notebook.cells if cell.id == cell_id)
        cell.source = 'string failureMessage = "";\ntry {\n' + cell.source + """
} catch (Exception error) { failureMessage = error.Message; }
"""
        try:
            self.execute(cells={cell_id}, mode=mode, cleanup=cleanup, question=question, checks=f"""
if (!failureMessage.Contains({json.dumps(message)})) throw new Exception("Actual cell failed incorrectly: " + failureMessage);
if (handler.Requests.Count != {0 if question is not None else 1}) throw new Exception("Failed cell made hidden calls/retries");
""")
        finally:
            self.notebook = original

    def test_actual_cleanup_is_explicit_scoped_404_tolerant_and_not_auth_tolerant(self):
        self.execute(cells={"cleanup"}, checks='if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Cleanup enabled by default");')
        for mode in ("", "missing"):
            self.execute(cells={"cleanup"}, cleanup=True, mode=mode, checks="""
if (handler.Requests.Count != 2 || handler.Requests.Any(r => r.Method != "DELETE" || r.Body != null))
    throw new Exception("Cleanup scope");
if (handler.Requests[0].Uri != "https://workshop.search.windows.net/knowledgebases/alice01-sp-remote-kb?api-version=2026-08-01-preview"
    || handler.Requests[1].Uri != "https://workshop.search.windows.net/knowledgesources/alice01-sp-remote-source?api-version=2026-08-01-preview")
    throw new Exception("Cleanup order/names");
""")
        self.assert_cell_failure("cleanup", "denied", "HTTP 403", cleanup=True)

    def test_actual_query_validation_and_settings_fail_before_network(self):
        self.assert_cell_failure("retrieve", "", "non-empty question", question=" ")
        for old, new in (("WORKSHOP_PREFIX=alice01", "WORKSHOP_PREFIX=alice01/other"),
                         ("https://workshop.search.windows.net", "https://workshop.search.windows.net.evil.test"),
                         ("https://contoso.sharepoint.com/sites/workshop", "https://contoso.sharepoint.com/sites/workshop?sharing=1"),
                         ("00000000-0000-0000-0000-000000000000", "not-a-guid")):
            with self.subTest(setting=old):
                (self.path / ".env.azure").write_text(self.settings_text.replace(old, new), encoding="utf-8")
                original = self.notebook
                self.notebook = copy.deepcopy(original)
                cell = next(cell for cell in self.notebook.cells if cell.id == "setup")
                cell.source = cell.source.replace(
                    'RemoteSettings settings = RemoteSettings.Load(Path.GetFullPath(".."));',
                    '''
string configurationFailure = "";
RemoteSettings settings = new RemoteSettings("00000000-0000-0000-0000-000000000000",
    "https://workshop.search.windows.net", "https://contoso.sharepoint.com/sites/workshop", "alice01");
try { settings = RemoteSettings.Load(Path.GetFullPath("..")); }
catch (Exception error) { configurationFailure = error.Message; }
''')
                try:
                    self.execute(cells=set(), checks='''
if (!configurationFailure.Contains("Use a")) throw new Exception("Invalid settings did not fail");
if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Invalid settings authenticated/called REST");
''')
                finally:
                    self.notebook = original
                    (self.path / ".env.azure").write_text(self.settings_text, encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
