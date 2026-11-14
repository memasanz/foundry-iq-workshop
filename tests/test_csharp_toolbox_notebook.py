import ast
import json
import os
import re
import shutil
import subprocess
import unittest
import uuid

import nbformat
from nbclient.exceptions import CellExecutionError

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
PROJECT_ID = (
    "/subscriptions/00000000-0000-0000-0000-000000000000/"
    "resourceGroups/test/providers/Microsoft.CognitiveServices/accounts/test/projects/workshop"
)


def method(source, signature):
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def python_policy():
    python = nbformat.read(ROOT / "python-notebooks" / "03-foundry-web-iq-toolbox.ipynb", as_version=4)
    sources = {cell.id: cell.source for cell in python.cells}
    definition = next(
        n for n in ast.walk(ast.parse(sources["create-agent"]))
        if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id == "PromptAgentDefinition"
    )
    instructions = ast.literal_eval(next(k.value for k in definition.keywords if k.arg == "instructions"))
    question = ast.literal_eval(ast.parse(sources["ask"]).body[0].value.args[0])
    return instructions, question


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional deprecated .NET Interactive tool is not installed.")
class CSharpToolboxNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Keep isolated execution fixtures under the repository, never in system temporary directories.
        cls.path = ROOT / (".csharp-toolbox-notebook-fixture-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(
            "WORKSHOP_PREFIX=alice01\n"
            "AZURE_AI_PROJECT_ENDPOINT=https://workshop.services.ai.azure.com/api/projects/workshop\n"
            f"AZURE_AI_PROJECT_RESOURCE_ID={PROJECT_ID}\n"
            "AZURE_AI_MODEL_DEPLOYMENT_NAME=test-model\n"
            "AZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n",
            encoding="utf-8",
        )
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(
            ["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
            capture_output=True, text=True, timeout=90,
            env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook_path = ROOT / "c-sharp-notebooks" / "03-foundry-web-iq-toolbox.ipynb"
        cls.original_bytes = cls.notebook_path.read_bytes()
        cls.notebook = nbformat.read(cls.notebook_path, as_version=4)
        nbformat.validate(cls.notebook)
        cls.instructions, cls.question = python_policy()

    def execute(self, mode="complete", checks="", protocol="2025-06-18"):
        source = (ROOT / "tests" / "c-sharp-toolbox" / "ToolboxTests.cs").read_text(encoding="utf-8")
        handler = "using System.Net;\n" + source[source.index("public sealed class ToolboxOfflineHandler"):]
        handler = handler.replace("ToolboxTests.Settings.ConnectionId", json.dumps(PROJECT_ID + "/connections/alice01-webiq-mcp"))
        handler = handler.replace("ToolboxWorkshop.Instructions", json.dumps(self.instructions))
        wire_checks = method(source, "public static void AssertWire")
        wire_checks = wire_checks.replace("ToolboxWorkshop.Question", json.dumps(self.question))
        wire_checks = wire_checks.replace("ToolboxWorkshop.Instructions", json.dumps(self.instructions))
        assertions = """
public static class NotebookWireTests
{
    public static FoundrySettings Settings = new FoundrySettings("", "alice01",
        "https://workshop.services.ai.azure.com/api/projects/workshop",
        """ + json.dumps(PROJECT_ID) + """,
        "test-model", "00000000-0000-0000-0000-000000000000");
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
""" + wire_checks + "\n}\n"
        handler += assertions + """
client.Dispose();
ToolboxOfflineHandler handler = new ToolboxOfflineHandler();
client = new HttpClient(handler);
lab = new FoundryWorkshop(client, settings, resource => Task.FromResult("synthetic-token"));
tokenProvider = resource => Task.FromResult("synthetic-token");
handler.Mode = """ + json.dumps(mode) + ";\nhandler.Protocol = " + json.dumps(protocol) + ";"
        return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)

    def test_actual_standalone_cells_match_python_and_both_wire_auth_hops(self):
        original_key = os.environ.pop("WEBIQ_API_KEY", None)
        try:
            self.execute(checks=r'''
if (settings.ApiKey != "" || settings.Prefix != "alice01" || !grounded || availableTools.Count != 2)
    throw new Exception("No-key/override/retrieval check failed");
NotebookWireTests.AssertWire(handler);
''')
        finally:
            if original_key is not None:
                os.environ["WEBIQ_API_KEY"] = original_key
        self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)

    def test_actual_foundry_protocol_downgrade(self):
        self.execute(protocol="2024-11-05", checks=r'''
if (availableTools.Count != 2 || !grounded)
    throw new Exception("Foundry downgrade discovery did not complete");
NotebookWireTests.AssertWire(handler);
''')
        self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)

    def test_actual_missing_news_stops_notebook(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute("missing-news")
        self.assertIn("MCP does not expose webiq___news", caught.exception.evalue)
        self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)

    def test_actual_unexpected_approval_raises_without_prompt(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute("approval")
        self.assertIn("Unexpected tool approval request", caught.exception.evalue)
        self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)


class CSharpToolboxPolicyTests(unittest.TestCase):
    def test_current_python_instructions_question_and_sdk_toolbox_wire_shape(self):
        # Optional Azure SDK is already present in the workshop environment; never install it in tests.
        from azure.ai.projects.models import MCPToolboxTool
        from mcp.shared.version import SUPPORTED_PROTOCOL_VERSIONS
        from azure.ai.projects.operations._operations import (
            build_toolboxes_create_version_request,
            build_toolboxes_get_request,
            build_toolboxes_update_request,
        )
        instructions, question = python_policy()
        self.assertEqual(SUPPORTED_PROTOCOL_VERSIONS, ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"])
        notebook = nbformat.read(ROOT / "c-sharp-notebooks" / "03-foundry-web-iq-toolbox.ipynb", as_version=4)
        nbformat.validate(notebook)
        sources = {cell.id: cell.source for cell in notebook.cells}
        self.assertIn(json.dumps(instructions), sources["create-agent"])
        self.assertIn(json.dumps(question), sources["ask"])
        helper = (ROOT / "c-sharp-console-apps" / "03-foundry-web-iq-toolbox" / "ToolboxWorkshop.cs").read_text(encoding="utf-8")
        def constant(name):
            # Instruction text contains semicolons; locate the terminating string statement.
            region = helper.split(f"public const string {name} =", 1)[1].split('";', 1)[0] + '"'
            return "".join(json.loads(literal) for literal in re.findall(r'"(?:[^"\\]|\\.)*"', region))
        self.assertEqual(constant("Instructions"), instructions)
        self.assertEqual(constant("Question"), question)
        tool = MCPToolboxTool(
            server_label="webiq", server_url="https://api.microsoft.ai/v3/mcp",
            project_connection_id=PROJECT_ID + "/connections/alice01-webiq-mcp",
            allowed_tools=["web", "news"], require_approval="never",
        ).as_dict()
        self.assertEqual(tool["type"], "mcp")
        self.assertEqual(set(tool), {"type", "server_label", "server_url", "project_connection_id", "allowed_tools", "require_approval"})
        for builder, verb, suffix in [
            (build_toolboxes_create_version_request, "POST", "/versions"),
            (build_toolboxes_get_request, "GET", ""),
            (build_toolboxes_update_request, "PATCH", ""),
        ]:
            request = builder(name="alice01-webiq-toolbox")
            self.assertEqual(request.method, verb)
            self.assertEqual(request.url, "/toolboxes/alice01-webiq-toolbox" + suffix + "?api-version=v1")


if __name__ == "__main__":
    unittest.main()
