"""Offline protocol parity and actual inline execution of C# lab07."""
import ast
import copy
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
import uuid
from pathlib import Path

import nbformat
from nbclient.exceptions import CellExecutionError

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
PROJECT = ROOT / "c-sharp-console-apps" / "07-sharepoint-indexed"
HARNESS = ROOT / "tests" / "c-sharp-sharepoint-indexed"
NOTEBOOK = ROOT / "c-sharp-notebooks" / "07-sharepoint-indexed.ipynb"
SETUP_CELL_IDS = ("setup-imports", "setup-settings", "setup-transport", "setup")
SETTINGS = (
    "WORKSHOP_PREFIX=alice01\nAZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
    "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
    "SHAREPOINT_SITE_URL=https://workshop.sharepoint.com/sites/test\n"
    "AZURE_AI_ACCOUNT_NAME=workshop\nAZURE_AI_EMBEDDING_DEPLOYMENT_NAME=workshop-embedding\n"
    "AZURE_AI_EMBEDDING_MODEL_NAME=text-embedding-3-small\n"
    "SHAREPOINT_APP_CLIENT_ID=11111111-1111-1111-1111-111111111111\n"
    "SHAREPOINT_SEARCH_MI_CLIENT_ID=22222222-2222-2222-2222-222222222222\n"
)


def python_payloads():
    notebook = nbformat.read(ROOT / "python-notebooks" / "07-sharepoint-indexed.ipynb", as_version=4)
    sources = {c.id: ast.parse(c.source) for c in notebook.cells if c.cell_type == "code"}
    values = {
        "__builtins__": {}, "tenant_id": "00000000-0000-0000-0000-000000000000",
        "site_url": "https://workshop.sharepoint.com/sites/test",
        "sp_app_client_id": "11111111-1111-1111-1111-111111111111",
        "sp_mi_client_id": "22222222-2222-2222-2222-222222222222",
        "index_name": "alice01-nb7-index", "data_source_name": "alice01-nb7-ds",
        "skillset_name": "alice01-nb7-skillset", "indexer_name": "alice01-nb7-indexer",
        "foundry_endpoint": "https://workshop.services.ai.azure.com",
        "embedding_endpoint": "https://workshop.openai.azure.com",
        "embedding_deployment": "workshop-embedding", "embedding_model": "text-embedding-3-small",
        "embedding_dimensions": 1536, "semantic_configuration": "semantic-config", "question": "*",
    }

    def evaluate(expression):
        return eval(compile(ast.Expression(expression), "<python07-payload>", "eval"), values)

    def assignment(cell, name):
        expression = next(n.value for n in sources[cell].body if isinstance(n, ast.Assign) and
                          any(isinstance(t, ast.Name) and t.id == name for t in n.targets))
        return evaluate(expression)

    values["connection_string"] = assignment("indexed-configuration", "connection_string")
    query_call = next(n for n in ast.walk(sources["query-index"]) if isinstance(n, ast.Call) and
                      isinstance(n.func, ast.Name) and n.func.id == "search_request")
    resync_call = next(n for n in ast.walk(sources["run-indexer"]) if isinstance(n, ast.Call) and
                       isinstance(n.func, ast.Name) and n.func.id == "search_request" and len(n.args) == 3)
    return {
        "dataSource": assignment("create-data-source", "data_source"),
        "index": assignment("create-index", "index"),
        "skillset": assignment("create-skillset", "skillset"),
        "indexer": assignment("create-indexer", "indexer"),
        "query": evaluate(query_call.args[2]),
        "resync": evaluate(resync_call.args[2]),
    }


class SharePointIndexedConsoleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def fixture(self):
        path = ROOT / ".workshop" / ("sp07-python-" + uuid.uuid4().hex)
        path.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, path)
        return path

    def test_console_harness_matches_current_python07_payloads(self):
        wire = self.fixture() / "wire.json"
        result = subprocess.run(["dotnet", "run", "--project", str(HARNESS), "--", "--wire", str(wire)],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline SharePoint indexed console cases", result.stdout)
        self.assertEqual(json.loads(wire.read_text(encoding="utf-8")), python_payloads())

    def test_real_offline_help_configuration_and_confirmation_guards(self):
        fixture = self.fixture()
        (fixture / ".env").write_text("WORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "SharePointIndexed.dll"
        env = {**os.environ, "PATH": ""}
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["run"], False), (["indexer"], False),
                              (["data-source"], False), (["cleanup"], False), (["resync-permissions"], False),
                              (["query", ""], False), (["query", "one", "two"], False),
                              (["index", "--setup-confirmed"], False), (["unknown"], False)):
            result = subprocess.run([shutil.which("dotnet"), str(dll), *args], cwd=fixture, env=env,
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
            self.assertNotIn("Missing setting", result.stderr)
            if success:
                self.assertEqual(result.stderr, "")
                self.assertIn("cleanup --confirm", result.stdout)
                outputs.append(result.stdout)
            self.assertFalse((fixture / ".workshop").exists())
        self.assertEqual(len(set(outputs)), 1)
        (fixture / ".env.azure").write_text(SETTINGS, encoding="utf-8")
        result = subprocess.run([shutil.which("dotnet"), str(dll), "configuration"], cwd=fixture, env=env,
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("not tested", result.stdout)
        self.assertFalse((fixture / ".workshop").exists())

    def test_setup_cell_compiles_as_regular_csharp_for_editor_compatibility(self):
        temporary = tempfile.TemporaryDirectory(prefix="sp07-editor-")
        self.addCleanup(temporary.cleanup)
        fixture = Path(temporary.name)
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        cells = {c.id: c for c in notebook.cells}
        imports = cells["setup-imports"].source
        for filename, ident in (("Program.cs", "setup"), ("SharePointSettings.cs", "setup-settings"),
                                ("SharePointTransport.cs", "setup-transport")):
            (fixture / filename).write_text(imports + "\n\n" + cells[ident].source, encoding="utf-8")
        project = fixture / "NotebookSetup.csproj"
        project.write_text("""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>""", encoding="utf-8")
        result = subprocess.run(["dotnet", "build", str(project), "--nologo", "--verbosity", "quiet",
                                 "--disable-build-servers", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_notebook_schema_inline_helpers_safe_defaults_and_local_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        self.assertEqual(notebook.metadata.kernelspec.name, ".net-csharp")
        self.assertEqual(notebook.metadata.language_info.name, "polyglot-notebook")
        self.assertEqual(notebook.metadata.language_info.file_extension, ".cs")
        self.assertEqual(notebook.metadata.language_info.pygments_lexer, "csharp")
        self.assertEqual(notebook.metadata.kernelInfo.defaultKernelName, "csharp")
        self.assertEqual(notebook.metadata.polyglot_notebook.kernelInfo.defaultKernelName, "csharp")
        self.assertEqual(tuple(c.id for c in notebook.cells if c.cell_type == "code")[:4], SETUP_CELL_IDS)
        for cell in notebook.cells:
            if cell.cell_type == "code":
                self.assertEqual(cell.metadata.dotnet_interactive.language, "csharp")
                self.assertEqual(cell.metadata.polyglot_notebook.kernelName, "csharp")
                self.assertEqual(cell.metadata.vscode.languageId, "polyglot-notebook")
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps"):
            self.assertNotIn(forbidden, code)
        self.assertIn("bool INDEXED_SETUP_CONFIRMED = true;", code)
        for flag in ("RUN_AGAIN", "RESYNC_PERMISSIONS", "CLEAN_UP"):
            self.assertIn(f"bool {flag} = false;", code)
        self.assertNotIn("SharePointWorkshop", code)
        for name in ("SharePointSettings.cs", "SharePointTransport.cs"):
            body = "public " + (PROJECT / name).read_text(encoding="utf-8").split("public ", 1)[1].strip()
            self.assertIn(body, code)
        for path, text in ((NOTEBOOK, "\n".join(c.source for c in notebook.cells)),
                           (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            for markdown, image in re.findall(r"\]\(([^)]+)\)|src=\"([^\"]+)\"", text):
                link = markdown or image
                if "://" not in link:
                    self.assertTrue((path.parent / link).is_file(), f"Missing local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class SharePointIndexedNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / ".workshop" / ("sp07-notebook-" + uuid.uuid4().hex)
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

    def execute(self, ids=None, mode="new", token_mode="", checks="", confirmed=False, cleanup=False):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if ids is not None:
            self.notebook.cells = [c for c in self.notebook.cells if c.cell_type != "code" or
                                   c.id in {*SETUP_CELL_IDS, "dispose"} or c.id in ids]
        if confirmed:
            cell = next(c for c in self.notebook.cells if c.id == "indexed-configuration")
            cell.source = cell.source.replace("bool INDEXED_SETUP_CONFIRMED = false;", "bool INDEXED_SETUP_CONFIRMED = true;")
        if cleanup:
            cell = next(c for c in self.notebook.cells if c.id == "cleanup")
            cell.source = cell.source.replace("bool CLEAN_UP = false;", "bool CLEAN_UP = true;")
        tests = (HARNESS / "SharePointIndexedTests.cs").read_text(encoding="utf-8")
        handler = tests[tests.index("public sealed record CapturedSpRequest"):] + """
client.Dispose();
SpOfflineHandler handler = new SpOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
tokenProvider = resource => { tokenCalls++; return Task.FromResult(SpTestData.Token(""" + json.dumps(token_mode) + """)); };
pause = duration => Task.CompletedTask;
transport = new SharePointTransport(client, settings, resource => tokenProvider(resource));
handler.Reset(""" + json.dumps(mode) + ");"
        payloads = python_payloads()
        for collection, key in (("datasources", "dataSource"), ("indexes", "index"), ("skillsets", "skillset"), ("indexers", "indexer")):
            handler += f"\nSpTestData.Definitions[{json.dumps(collection)}] = JsonNode.Parse({json.dumps(json.dumps(payloads[key]))}).AsObject();"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)
            self.assertFalse((self.path / ".workshop").exists(), "Notebook wrote console checkpoint/cache")

    def test_actual_split_setup_defines_helpers_before_loading_settings_without_network_calls(self):
        self.execute(ids=set(), checks="""
if (settings.Prefix != "alice01" || transport == null || handler.Requests.Count != 0 || tokenCalls != 0)
    throw new Exception("Split setup failed or performed an implicit request");
""")

    def test_actual_full_notebook_has_python07_payloads_acl_headers_and_no_extra_runs(self):
        payloads = python_payloads()
        checks = """
if (settings.Prefix != "alice01" || handler.Requests.Count != 8 || tokenCalls != 8)
    throw new Exception("Settings override or hidden/missing request");
if (handler.Requests.Any(r => r.Method == "DELETE" || r.HasKey || r.HasElevatedRead))
    throw new Exception("Implicit cleanup or elevated/key access");
foreach (CapturedSpRequest request in handler.Requests)
{
    if (request.Authorization != "Bearer " + SpTestData.Token() ||
        request.SourceAuthorization != (request.Url.Contains("/docs/search?") ? SpTestData.Token() : null))
        throw new Exception("Query identity/header contract");
}
"""
        for collection, key in (("datasources", "dataSource"), ("indexes", "index"),
                                ("skillsets", "skillset"), ("indexers", "indexer")):
            expected = json.dumps(json.dumps(payloads[key]))
            checks += f"""
if (!JsonNode.DeepEquals(handler.Requests.Single(r => r.Method == "PUT" && r.Url.Contains("/{collection}/")).Body,
    JsonNode.Parse({expected}))) throw new Exception("Python07 {key} differs");
"""
        self.execute(confirmed=True, checks=checks)

    def test_default_setup_rerun_resync_and_cleanup_are_opt_in(self):
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        cell = next(c for c in self.notebook.cells if c.id == "indexed-configuration")
        cell.source = cell.source.replace("bool INDEXED_SETUP_CONFIRMED = true;", "bool INDEXED_SETUP_CONFIRMED = false;")
        try:
            with self.assertRaises(CellExecutionError) as caught:
                self.execute(ids={"indexed-configuration"})
            self.assertIn("Complete section 2", caught.exception.evalue)
        finally:
            self.notebook = original
        self.execute(ids={"run-indexer", "cleanup"}, checks="""
if (handler.Requests.Count != 0 || tokenCalls != 0) throw new Exception("Default opt-ins contact Search");
""")
        self.execute(ids={"cleanup"}, mode="existing", cleanup=True, checks="""
if (handler.Requests.Count != 4 || handler.Requests.Count(r => r.Method == "DELETE") != 4)
    throw new Exception("Cleanup scope");
""")

    def test_actual_independent_user_queries_forward_each_identity_and_need_no_app_ids(self):
        path = self.path / ".env.azure"
        path.write_text(SETTINGS + "SHAREPOINT_APP_CLIENT_ID=\nSHAREPOINT_SEARCH_MI_CLIENT_ID=\n", encoding="utf-8")
        try:
            for mode, count in (("", 2), ("restricted", 1)):
                self.execute(ids={"query-index"}, mode="existing", token_mode=mode, checks=f"""
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "POST" || tokenCalls != 1 ||
    handler.Requests[0].SourceAuthorization != SpTestData.Token({json.dumps(mode)}) ||
    handler.Requests[0].Authorization != "Bearer " + SpTestData.Token({json.dumps(mode)}))
    throw new Exception("Wrong user identity or implicit setup");
if (indexed_result["@odata.count"].GetValue<int>() != {count}) throw new Exception("Synthetic identity response");
""")
        finally:
            path.write_text(SETTINGS, encoding="utf-8")

    def test_actual_independent_setup_and_monitor_cells_have_no_hidden_queries(self):
        for ids, confirmed, mode, count in (
                ({"create-index"}, False, "existing", 1),
                ({"create-index"}, False, "put-no-content", 1),
                ({"create-skillset"}, False, "existing", 1),
                ({"indexed-configuration", "create-data-source"}, True, "existing", 1),
                ({"indexed-configuration", "create-indexer"}, True, "new", 2),
                ({"monitor-indexer"}, False, "existing", 1)):
            self.execute(ids=ids, confirmed=confirmed, mode=mode, checks=f"""
if (handler.Requests.Count != {count} || handler.Requests.Any(r => r.Url.Contains("/docs/search?") || r.Method == "DELETE"))
    throw new Exception("Hidden setup/query/cleanup action");
""")

    def test_actual_app_only_http_and_indexer_errors_stop_without_retry(self):
        original = self.notebook
        for ident, mode, token, message, count in (
                ("query-index", "existing", "app", "delegated Search token", 0),
                ("query-index", "http403", "", "HTTP 403", 1),
                ("monitor-indexer", "failure", "", "persistentFailure", 1)):
            self.notebook = copy.deepcopy(original)
            cell = next(c for c in self.notebook.cells if c.id == ident)
            cell.source = """
string failure = "";
try {
""" + cell.source + """
} catch (Exception error) { failure = error.Message; }
"""
            try:
                self.execute(ids={ident}, mode=mode, token_mode=token, checks=f"""
if (!failure.Contains({json.dumps(message)}) || handler.Requests.Count != {count})
    throw new Exception("Swallowed/retried failure");
if (failure.Contains(SpTestData.Token())) throw new Exception("Credential leak");
""")
            finally:
                self.notebook = original


if __name__ == "__main__":
    unittest.main()
