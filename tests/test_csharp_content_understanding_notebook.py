import ast
import copy
import json
import os
import shutil
import subprocess
import unittest
import uuid

import nbformat
from nbclient.exceptions import CellExecutionError

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
APP = ROOT / "c-sharp-console-apps" / "05-blob-content-understanding"
STORAGE_ID = (
    "/subscriptions/00000000-0000-0000-0000-000000000000/"
    "resourceGroups/test/providers/Microsoft.Storage/storageAccounts/workshop"
)


def python_payloads():
    """Evaluate visible Python05 payloads only; never execute its network calls."""
    notebook = nbformat.read(ROOT / "python-notebooks" / "05-blob-content-understanding.ipynb", as_version=4)
    values = {
        "data_source_name": "alice01-cu-source", "index_name": "alice01-cu-chunks",
        "skillset_name": "alice01-cu-skills", "indexer_name": "alice01-cu-indexer",
        "storage_id": STORAGE_ID, "container_name": "samples",
        "blob_prefix": "alice01/content-understanding/",
        "foundry_endpoint": "https://workshop.services.ai.azure.com",
        "embedding_endpoint": "https://workshop.openai.azure.com",
        "embedding_deployment": "workshop-embedding", "embedding_model": "text-embedding-3-small",
        "embedding_dimensions": 1536, "semantic_configuration": "chunk-semantic",
    }
    bodies = []
    for cell in notebook.cells:
        if cell.cell_type != "code":
            continue
        tree = ast.parse(cell.source)
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("data_source", "index", "skillset", "indexer", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<visible-payload>", "eval"),
                                        {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                payload = next((k.value for k in call.keywords if k.arg == "json"), None)
                if payload is not None:
                    bodies.append(eval(compile(ast.Expression(payload), "<visible-payload>", "eval"),
                                       {"__builtins__": {}}, values))
    return bodies


class ContentUnderstandingConsoleTests(unittest.TestCase):
    def test_console_runtime_protocol_and_current_python_payload_parity(self):
        path = ROOT / (".csharp-cu-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(
            ["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-content-understanding"),
             "--", "--wire", str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual([r["Body"] for r in wire if r["Body"] is not None], python_payloads())


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class CSharpContentUnderstandingNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-cu-notebook-fixture-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        data = cls.path / "data" / "search"
        data.mkdir(parents=True)
        shutil.copy2(ROOT / "data" / "search" / "travel-handbook.pdf", data)
        (data / "not-uploaded.txt").write_text("Must not upload arbitrary files.", encoding="utf-8")
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text(
            "WORKSHOP_PREFIX=alice01\nAZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
            "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
            "AZURE_STORAGE_BLOB_ENDPOINT=https://workshop.blob.core.windows.net\n"
            f"AZURE_STORAGE_RESOURCE_ID={STORAGE_ID}\nAZURE_STORAGE_CONTAINER_NAME=samples\n"
            "AZURE_AI_ACCOUNT_NAME=workshop\nAZURE_AI_EMBEDDING_DEPLOYMENT_NAME=workshop-embedding\n"
            "AZURE_AI_EMBEDDING_MODEL_NAME=text-embedding-3-small\n",
            encoding="utf-8",
        )
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
        cls.notebook_path = ROOT / "c-sharp-notebooks" / "05-blob-content-understanding.ipynb"
        cls.original_bytes = cls.notebook_path.read_bytes()
        cls.notebook = nbformat.read(cls.notebook_path, as_version=4)
        nbformat.validate(cls.notebook)

    def execute(self, mode="complete", checks="", fail_status=None, cells=None, invalid_pdf=False, question=None):
        source = (ROOT / "tests" / "c-sharp-content-understanding" / "ContentUnderstandingTests.cs").read_text(encoding="utf-8")
        handler = source[source.index("public sealed record CapturedSearchRequest"):]
        handler += """
client.Dispose();
SearchOfflineHandler handler = new SearchOfflineHandler();
client = new HttpClient(handler);
List<string> audiences = new List<string>();
tokenProvider = resource => { audiences.Add(resource); return Task.FromResult("synthetic-token"); };
pause = duration => Task.CompletedTask;
handler.Mode = """ + json.dumps(mode) + ";\n"
        if fail_status is not None:
            handler += f"handler.ForceStatus = {fail_status};\n"
        path = self.path / "data" / "search" / "travel-handbook.pdf"
        if invalid_pdf:
            path.write_bytes(b"not a PDF")
        notebook = self.notebook
        if cells is not None:
            self.notebook = copy.deepcopy(notebook)
            self.notebook.cells = [c for c in self.notebook.cells
                                   if c.cell_type != "code" or c.id in cells or c.id == "setup"]
        if question is not None:
            if self.notebook is notebook:
                self.notebook = copy.deepcopy(notebook)
            cell = next(c for c in self.notebook.cells if c.id == "query-semantic-hybrid")
            first_line = cell.source.splitlines()[0]
            cell.source = cell.source.replace(first_line, "string question = " + json.dumps(question) + ";", 1)
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = notebook
            if invalid_pdf:
                shutil.copy2(ROOT / "data" / "search" / "travel-handbook.pdf", path)
            self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)

    def test_actual_self_contained_full_sequence_matches_python(self):
        checks = [
            'if (settings.Prefix != "alice01" || !completed || requestedRunAccepted) throw new Exception("Override/run state failed");',
            'if (handler.Requests.Count != 10 || audiences.Count != 10) throw new Exception("Unexpected requests/token refresh");',
            'if (audiences[0] != storageAudience || audiences.Skip(1).Any(a => a != searchAudience)) throw new Exception("Token audience differs");',
            'if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-token" || r.Headers.ContainsKey("api-key"))) throw new Exception("Wrong authentication");',
            'if (handler.Requests.Skip(1).Any(r => !r.Uri.EndsWith("?api-version=2026-04-01"))) throw new Exception("Search version differs");',
            'if (!handler.Requests.Select(r => r.Method).SequenceEqual(new[] { "PUT", "PUT", "PUT", "PUT", "GET", "PUT", "GET", "GET", "POST", "POST" })) throw new Exception("REST order differs");',
            'if (handler.Requests[0].Uri != settings.StorageEndpoint + "/samples/alice01/content-understanding/travel-handbook.pdf" || handler.Requests[0].ContentType != "application/pdf") throw new Exception("PDF upload differs");',
            'if (!handler.Requests[0].Bytes!.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "data", "search", "travel-handbook.pdf")))) throw new Exception("Original PDF bytes differ");',
            'if (handler.Requests[0].Headers["x-ms-version"] != "2023-11-03" || handler.Requests[0].Headers["x-ms-blob-type"] != "BlockBlob") throw new Exception("Blob headers differ");',
            'if (!DateTimeOffset.TryParseExact(handler.Requests[0].Headers["x-ms-date"], "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new Exception("Blob date differs");',
        ]
        for index, expected in zip([1, 2, 3, 5, 8, 9], python_payloads()):
            literal = json.dumps(json.dumps(expected))
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse({literal}))) throw new Exception("Python parity request {index}");')
        self.execute(checks="\n".join(checks))

    def test_actual_existing_indexer_waits_for_new_run(self):
        self.execute("existing", cells={"create-indexer", "monitor-indexer"}, checks="""
if (handler.StatusCalls != 4 || !completed ||
    !handler.Requests.Select(r => r.Method).SequenceEqual(new[] { "GET", "PUT", "POST", "GET", "GET", "GET" }))
    throw new Exception("Old success accepted / update protocol differs");
""")

    def test_actual_busy_indexer_stops_before_write(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute("busy", cells={"create-indexer"})
        self.assertIn("already running", caught.exception.evalue)

    def test_actual_monitor_failure_timeout_and_guard(self):
        for mode, message in [("failed-items", "itemsFailed"), ("errors", "synthetic error"),
                              ("transientFailure", "transientFailure"), ("persistentFailure", "persistentFailure"),
                              ("error", "error"), ("top-error", "error"), ("timeout", "15 minutes")]:
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(mode, cells={"create-indexer", "monitor-indexer"})
            self.assertIn(message, caught.exception.evalue)
        with self.assertRaises(CellExecutionError) as caught:
            self.execute(cells={"monitor-indexer"})
        self.assertIn("Run the indexer cell first", caught.exception.evalue)

    def test_actual_page_metadata_validation(self):
        for mode in ("empty", "missing-page", "string-page", "zero-page", "reversed-pages", "too-many-pages"):
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(mode, cells={"inspect-chunks"})
            self.assertIn("No chunks" if mode == "empty" else "locationMetadata", caught.exception.evalue)

    def test_actual_query_rejects_empty_evidence_and_partial_responses(self):
        for mode, message in (("empty", "No evidence"), ("partial200", "Semantic ranking did not complete"),
                              ("partial206", "HTTP 206")):
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(mode, cells={"query-semantic-hybrid"})
            self.assertIn(message, caught.exception.evalue)

    def test_actual_query_question_guard_and_safe_serialization(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute(cells={"query-semantic-hybrid"}, question=" ")
        self.assertIn("non-empty", caught.exception.evalue)
        question = 'What about "quoted" receipts?\nC:\\receipts'
        self.execute(cells={"query-semantic-hybrid"}, question=question, checks=f"""
if (handler.Requests.Single().Body!["search"]!.GetValue<string>() != {json.dumps(question)} ||
    handler.Requests.Single().Body!["vectorQueries"]![0]!["text"]!.GetValue<string>() != {json.dumps(question)})
    throw new Exception("Question was interpolated unsafely");
""")

    def test_actual_upload_checks_pdf_and_http_statuses(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute(cells={"upload-blobs"}, invalid_pdf=True)
        self.assertIn("Expected a PDF", caught.exception.evalue)
        for cell, status in (("upload-blobs", 200), ("create-data-source", 202),
                             ("create-indexer", 403), ("query-semantic-hybrid", 204)):
            with self.subTest(cell=cell), self.assertRaises(CellExecutionError) as caught:
                self.execute(fail_status=status, cells={cell})
            self.assertIn("HTTP " + str(status), caught.exception.evalue)

    def test_actual_standalone_cells_have_no_hidden_actions(self):
        for cell, method in (("upload-blobs", "PUT"), ("create-data-source", "PUT"),
                             ("create-index", "PUT"), ("create-skillset", "PUT"),
                             ("inspect-chunks", "POST"), ("query-semantic-hybrid", "POST")):
            with self.subTest(cell=cell):
                self.execute(cells={cell}, checks=f"""
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "{method}")
    throw new Exception("Hidden action in {cell}");
""")


class ContentUnderstandingNotebookPolicyTests(unittest.TestCase):
    def test_self_contained_payloads_and_current_teaching_material(self):
        notebook = nbformat.read(ROOT / "c-sharp-notebooks" / "05-blob-content-understanding.ipynb", as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        explanations = "\n".join(c.source for c in notebook.cells if c.cell_type == "markdown")
        for forbidden in ("#!import", "#load", "c-sharp-console-apps", "class ContentUnderstandingWorkshop",
                          "Text.SplitSkill", "verify-vectors", "WEBIQ_API_KEY", "/reset", "HttpMethod.Delete"):
            self.assertNotIn(forbidden, code)
        for expected in ("content-understanding-search-pipeline.png", "content-understanding-search-explorer.png",
                         "#skill-parameters", "Markdown", "range", "embedding-only", "identity=null",
                         "1,000 characters", "200-character overlap"):
            self.assertIn(expected, explanations)
        for cell, variable in (("create-index", "index"), ("create-skillset", "skillset")):
            self.assertIn("JsonObject " + variable + " =", next(c.source for c in notebook.cells if c.id == cell))
        self.assertEqual(len(python_payloads()), 6)


if __name__ == "__main__":
    unittest.main()
