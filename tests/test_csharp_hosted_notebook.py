"""Offline checks for lab13's real C# launcher, generated runtime and inline cells."""
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
PROJECT = ROOT / "c-sharp-console-apps" / "13-hosted-agent"
NOTEBOOK = ROOT / "c-sharp-notebooks" / "13-hosted-agent.ipynb"
SETTINGS = (
    "WORKSHOP_PREFIX=alice01\n"
    "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
    "AZURE_AI_PROJECT_ENDPOINT=https://workshop.services.ai.azure.com/api/projects/project\n"
    "AZURE_AI_MODEL_DEPLOYMENT_NAME=workshop-chat\n"
)


def python_definition():
    from azure.ai.projects.models import CodeConfiguration, HostedAgentDefinition, ProtocolVersionRecord
    notebook = nbformat.read(ROOT / "python-notebooks" / "13-hosted-agent.ipynb", as_version=4)
    tree = ast.parse(next(c.source for c in notebook.cells if c.id == "deploy"))
    assignment = next(n for n in tree.body if isinstance(n, ast.Assign) and
                      any(isinstance(t, ast.Name) and t.id == "definition" for t in n.targets))
    definition = eval(compile(ast.Expression(assignment.value), "<python13-definition>", "eval"), {
        "HostedAgentDefinition": HostedAgentDefinition, "CodeConfiguration": CodeConfiguration,
        "ProtocolVersionRecord": ProtocolVersionRecord, "model_name": "workshop-chat",
    })
    result = definition.as_dict()
    result["code_configuration"]["runtime"] = "dotnet_10"
    result["code_configuration"]["entry_point"] = ["dotnet", "WorkshopAgent.dll"]
    return json.loads(json.dumps(result))


class HostedConsoleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        result = subprocess.run(["dotnet", "build", str(PROJECT), "--nologo", "--verbosity", "quiet"],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)

    def fixture(self):
        path = ROOT / ".workshop" / ("hosted13-python-" + uuid.uuid4().hex)
        path.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, path)
        return path

    def test_offline_console_protocol_runtime_harness(self):
        result = subprocess.run(["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-hosted")],
                                cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline hosted-agent console cases", result.stdout)

    def test_real_offline_help_aliases_and_invalid_arguments(self):
        path = self.fixture()
        (path / ".env").write_text("WORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        dotnet = shutil.which("dotnet")
        dll = PROJECT / "bin" / "Debug" / "net10.0" / "HostedAgent.dll"
        env = {**os.environ, "PATH": "", "WORKSHOP_PREFIX": "", "AZURE_TENANT_ID": "",
               "AZURE_AI_PROJECT_ENDPOINT": "", "AZURE_AI_MODEL_DEPLOYMENT_NAME": ""}
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["cleanup"], False), (["cleanup", "--confirm", "other"], False),
                              (["status", "../1"], False), (["invoke", ""], False),
                              (["invoke", "one", "two"], False), (["unknown"], False)):
            with self.subTest(args=args):
                result = subprocess.run([dotnet, str(dll), *args], cwd=path, env=env,
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertIn("cleanup --confirm", result.stdout)
                    self.assertEqual(result.stderr, "")
                    outputs.append(result.stdout)
                else:
                    self.assertNotIn("Missing setting", result.stderr)
                self.assertEqual([p.name for p in path.iterdir()], [".env"])
        self.assertEqual(len(set(outputs)), 1)

    def test_actual_generated_csharp_agent_builds_without_azure_signin(self):
        path = self.fixture()
        (path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (path / ".env.azure").write_text(SETTINGS, encoding="utf-8")
        result = subprocess.run(["dotnet", str(PROJECT / "bin" / "Debug" / "net10.0" / "HostedAgent.dll"),
                                 "write-agent"], cwd=path, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        generated = path / "alice01-csharp-hosted-agent"
        self.assertEqual(sorted(p.name for p in generated.iterdir()),
                         ["Program.cs", "WorkshopAgent.csproj", "WorkshopTips.cs"])
        result = subprocess.run(["dotnet", "build", str(generated / "WorkshopAgent.csproj"),
                                 "--nologo", "--verbosity", "minimal", "--disable-build-servers"], cwd=path,
                                capture_output=True, text=True, timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((generated / "bin" / "Debug" / "net10.0" / "WorkshopAgent.dll").is_file())

    def test_notebook_schema_self_containment_and_links(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        for forbidden in ("#!import", "#load", "#r ", "c-sharp-console-apps"):
            self.assertNotIn(forbidden, code)
        self.assertIn('Path.GetFullPath("..")', code)
        self.assertIn("bool CLEAN_UP = false;", code)
        self.assertTrue(all(c.execution_count is None and not c.outputs for c in notebook.cells if c.cell_type == "code"))
        for source in ("HostedSettings.cs", "HostedSources.cs", "HostedWorkshop.cs"):
            body = "public " + (PROJECT / source).read_text(encoding="utf-8").split("public ", 1)[1].strip()
            self.assertIn(body, code)
        for path, text in ((NOTEBOOK, "\n".join(c.source for c in notebook.cells)),
                           (PROJECT / "README.md", (PROJECT / "README.md").read_text(encoding="utf-8"))):
            for link in re.findall(r"\]\(([^)]+)\)", text):
                if "://" not in link:
                    self.assertTrue((path.parent / link).is_file(), f"Missing local link: {link}")


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class HostedNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / ".workshop" / ("hosted13-notebook-" + uuid.uuid4().hex)
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
        tests = (ROOT / "tests" / "c-sharp-hosted" / "HostedTests.cs").read_text(encoding="utf-8")
        handler = "using System.Collections.Generic;\n" + tests[tests.index("public sealed record CapturedHostedRequest"):] + """
client.Dispose();
HostedOfflineHandler handler = new HostedOfflineHandler();
client = new HttpClient(handler);
int tokenCalls = 0;
DateTimeOffset testClock = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
tokenProvider = () => { tokenCalls++; return Task.FromResult("synthetic-hosted-token"); };
clockProvider = () => testClock;
delayProvider = duration => { testClock += duration; return Task.CompletedTask; };
lab = new HostedWorkshop(settings, client, () => tokenProvider(), () => clockProvider(), duration => delayProvider(duration));
handler.Mode = """ + json.dumps(mode) + ";"
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)

    def test_actual_full_notebook_definition_parity_archive_and_calls(self):
        expected = json.dumps(json.dumps(python_definition()))
        self.execute(checks=f"""
if (settings.Prefix != "alice01" || selectedVersion != "1") throw new Exception("Settings/version");
if (handler.Requests.Count != 5 || tokenCalls != 5 || handler.Requests.Any(r => r.Method == "DELETE"))
    throw new Exception("Hidden call/cleanup");
if (!JsonNode.DeepEquals(handler.Requests[0].Body["definition"], JsonNode.Parse({expected})))
    throw new Exception("Python13 hosted definition parity");
if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-hosted-token" || r.HasApiKey))
    throw new Exception("Wrong authorization");
if (handler.Requests[3].Body.ContainsKey("model") || handler.Requests[3].Body.ContainsKey("agent_session_id"))
    throw new Exception("Not simple hosted first-call invocation");
using (MemoryStream bytes = new MemoryStream(handler.Requests[0].Code))
using (ZipArchive bundle = new ZipArchive(bytes, ZipArchiveMode.Read))
    if (bundle.Entries.Count != 3 || bundle.Entries.Any(e => e.FullName.EndsWith(".py") || e.FullName == ".env"))
        throw new Exception("Not CSharp source-only archive");
""")

    def test_actual_independent_wait_and_invocation_cells(self):
        self.execute(ids={"wait"}, checks="""
if (handler.Requests.Count != 3 || handler.Requests.Any(r => r.Method != "GET"))
    throw new Exception("Wait implicitly publishes/invokes");
""")
        for cell in ("invoke", "invoke-tool"):
            self.execute(ids={cell}, checks="""
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "POST")
    throw new Exception("Implicit prerequisites/retries");
""")
        self.execute(ids={"cleanup"}, checks='if (handler.Requests.Count != 0) throw new Exception("Cleanup not opt-in");')
        self.execute(ids={"cleanup"}, cleanup=True, checks="""
if (handler.Requests.Count != 2 || handler.Requests[1].Method != "DELETE" || handler.Requests[1].Url.Contains("force"))
    throw new Exception("Cleanup scope/forcing");
""")

    def test_actual_notebook_http_errors_redacted_and_not_retried(self):
        original = self.notebook
        for mode, expected in (("denied", "HTTP 403"), ("partial", "HTTP 206"),
                               ("incomplete", "did not complete"), ("malformed", "invalid JSON")):
            self.notebook = copy.deepcopy(original)
            cell = next(c for c in self.notebook.cells if c.id == "invoke")
            cell.source = """
string failure = "";
StringWriter logs = new StringWriter();
TextWriter saved = Console.Error;
Console.SetError(logs);
try {
""" + cell.source + """
} catch (Exception error) { failure = error.Message; }
finally { Console.SetError(saved); }
"""
            try:
                self.execute(ids={"invoke"}, mode=mode, checks=f"""
if (!failure.Contains({json.dumps(expected)}) || handler.Requests.Count != 1)
    throw new Exception("Swallowed/retried failure");
if (logs.ToString().Contains("synthetic-hosted-token")) throw new Exception("Credential leak");
""")
            finally:
                self.notebook = original


if __name__ == "__main__":
    unittest.main()
