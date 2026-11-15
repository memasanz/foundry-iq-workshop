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
APP = ROOT / "c-sharp-console-apps" / "04-blob-indexer-skills"
STORAGE_ID = (
    "/subscriptions/00000000-0000-0000-0000-000000000000/"
    "resourceGroups/test/providers/Microsoft.Storage/storageAccounts/workshop"
)


def python_payloads():
    """Evaluate only the current notebook's visible payload assignments, not its requests."""
    notebook = nbformat.read(ROOT / "python-notebooks" / "04-blob-indexer-skills.ipynb", as_version=4)
    values = {
        "data_source_name": "alice01-blob-source", "index_name": "alice01-blob-chunks",
        "skillset_name": "alice01-blob-skills", "indexer_name": "alice01-blob-indexer",
        "storage_id": STORAGE_ID, "container_name": "samples", "blob_prefix": "alice01/blob/",
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
            if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("data_source", "index", "skillset", "indexer", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<visible-payload>", "eval"), {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                payload = next((k.value for k in call.keywords if k.arg == "json"), None)
                if payload is not None:
                    bodies.append(eval(compile(ast.Expression(payload), "<visible-payload>", "eval"), {"__builtins__": {}}, values))
    return bodies


class SearchConsoleParityTests(unittest.TestCase):
    def test_console_full_runtime_protocol_and_current_python_payload_parity(self):
        path = ROOT / (".csharp-search-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(
            ["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-search"), "--", "--wire", str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8"))
        actual = [r["Body"] for r in wire if r["Body"] is not None]
        self.assertEqual(actual, python_payloads())


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional deprecated .NET Interactive tool is not installed.")
class CSharpSearchNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-search-notebook-fixture-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        data = cls.path / "data" / "search"
        data.mkdir(parents=True)
        for name in ("onboarding-guide.txt", "travel-policy.txt"):
            shutil.copy2(ROOT / "data" / "search" / name, data / name)
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
            env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook_path = ROOT / "c-sharp-notebooks" / "04-blob-indexer-skills.ipynb"
        cls.original_bytes = cls.notebook_path.read_bytes()
        cls.notebook = nbformat.read(cls.notebook_path, as_version=4)
        nbformat.validate(cls.notebook)

    def execute(self, mode="complete", checks="", fail_status=None, cells=None):
        source = (ROOT / "tests" / "c-sharp-search" / "SearchTests.cs").read_text(encoding="utf-8")
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
        notebook = self.notebook
        if cells is not None:
            self.notebook = copy.deepcopy(notebook)
            self.notebook.cells = [c for c in self.notebook.cells if c.cell_type != "code" or c.id in cells or c.id == "setup"]
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = notebook
            self.assertEqual(self.notebook_path.read_bytes(), self.original_bytes)

    def test_actual_standalone_full_sequence_matches_current_python(self):
        checks = [
            'if (settings.Prefix != "alice01" || !completed || requestedRunAccepted) throw new Exception("Override/run state failed");',
            'if (handler.Requests.Count != 16 || audiences.Count != 16) throw new Exception("Unexpected request count/token refresh");',
            'if (audiences.Take(2).Any(a => a != storageAudience) || audiences.Skip(2).Any(a => a != searchAudience)) throw new Exception("Token audience differs");',
            'if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-token" || r.Headers.ContainsKey("api-key"))) throw new Exception("Wrong wire authentication");',
            'if (handler.Requests.Skip(2).Any(r => !r.Uri.EndsWith("?api-version=2025-09-01"))) throw new Exception("Search version differs");',
            'if (handler.Requests.Take(2).Any(r => !r.Uri.StartsWith(settings.StorageEndpoint + "/samples/alice01/blob/"))) throw new Exception("Upload prefix differs");',
            'if (handler.Requests.Skip(2).Any(r => !r.Uri.StartsWith(settings.SearchEndpoint + "/"))) throw new Exception("Search endpoint differs");',
            'if (!handler.Requests.Select(r => r.Method).SequenceEqual(new[] { "PUT", "PUT", "PUT", "PUT", "PUT", "GET", "PUT", "GET", "GET", "POST", "POST", "POST", "POST", "POST", "POST", "POST" })) throw new Exception("REST verb/order differs");',
        ]
        indices = [2, 3, 4, 6, 9, 10, 11, 12, 13, 14, 15]
        for index, expected in zip(indices, python_payloads()):
            literal = json.dumps(json.dumps(expected))
            checks.append(f'if (!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse({literal}))) throw new Exception("Python parity request {index}");')
        checks += [
            'if (handler.Requests.Take(2).Any(r => r.Headers["x-ms-version"] != "2023-11-03" || r.Headers["x-ms-blob-type"] != "BlockBlob" || r.ContentType != "text/plain; charset=utf-8")) throw new Exception("Blob headers differ");',
            'if (handler.Requests.Take(2).Any(r => !DateTimeOffset.TryParseExact(r.Headers["x-ms-date"], "R", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) throw new Exception("Blob date differs");',
            'if (handler.Requests[0].Text != File.ReadAllText(Path.Combine(root, "data", "search", "onboarding-guide.txt")) || handler.Requests[1].Text != File.ReadAllText(Path.Combine(root, "data", "search", "travel-policy.txt"))) throw new Exception("Original sample bytes differ");',
        ]
        self.execute(checks="\n".join(checks))

    def test_actual_existing_indexer_waits_for_queued_previous_run(self):
        self.execute("existing", cells={"indexer", "monitor"}, checks="""
if (handler.StatusCalls != 4 || !completed ||
    !handler.Requests.Select(r => r.Method).SequenceEqual(new[] { "GET", "PUT", "POST", "GET", "GET", "GET" }))
    throw new Exception("Old success accepted / existing run protocol differs");
""")

    def test_actual_busy_indexer_stops_before_put(self):
        with self.assertRaises(CellExecutionError) as caught:
            self.execute("busy", cells={"indexer", "monitor"})
        self.assertIn("already running", caught.exception.evalue)

    def test_actual_indexer_failure_and_timeout_paths(self):
        for mode, message in [("failed-items", "itemsFailed"), ("errors", "synthetic error"),
                              ("transientFailure", "transientFailure"), ("persistentFailure", "persistentFailure"),
                              ("error", "error"), ("top-error", "error"), ("timeout", "15 minutes")]:
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(mode, cells={"indexer", "monitor"})
            self.assertIn(message, caught.exception.evalue)

    def test_actual_empty_or_invalid_vector_samples_stop(self):
        for mode in ("empty", "missing-vector", "short-vector"):
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(mode, cells={"verify-vectors"})
            self.assertIn("Missing or wrong-sized", caught.exception.evalue)
        with self.assertRaises(CellExecutionError) as caught:
            self.execute("empty", cells={"inspect"})
        self.assertIn("No chunks", caught.exception.evalue)

    def test_actual_semantic_partial_http200_and_206_stop(self):
        for mode, message in (("partial200", "Semantic ranking did not complete"), ("partial206", "HTTP 206")):
            for cell in ("semantic", "semantic-hybrid"):
                with self.subTest(mode=mode, cell=cell), self.assertRaises(CellExecutionError) as caught:
                    self.execute(mode, cells={"keyword", cell})
                self.assertIn(message, caught.exception.evalue)

    def test_actual_http_status_and_monitor_guard_stop(self):
        for cell, status in (("upload", 200), ("data-source", 202), ("indexer", 403), ("keyword", 204)):
            with self.subTest(cell=cell), self.assertRaises(CellExecutionError) as caught:
                self.execute(fail_status=status, cells={cell})
            self.assertIn("HTTP " + str(status), caught.exception.evalue)
        with self.assertRaises(CellExecutionError) as caught:
            self.execute(cells={"monitor"})
        self.assertIn("Run the indexer cell first", caught.exception.evalue)


class CSharpSearchNotebookPolicyTests(unittest.TestCase):
    def test_self_contained_visible_payloads_and_pdf_explanations(self):
        notebook = nbformat.read(ROOT / "c-sharp-notebooks" / "04-blob-indexer-skills.ipynb", as_version=4)
        nbformat.validate(notebook)
        code = "\n".join(c.source for c in notebook.cells if c.cell_type == "code")
        explanations = "\n".join(c.source for c in notebook.cells if c.cell_type == "markdown")
        for forbidden in ("#!import", "#load", "File.ReadAllText(\"..\\\\c-sharp-console-apps", "class BlobWorkshop", "OcrSkill"):
            self.assertNotIn(forbidden, code)
        for expected in ("ai-search-index-fields.png", "ai-search-index-json-editor.png", "@odata.etag",
                         "Scanned or image-only PDFs need OCR", "azureOpenAITokens", "not physical PDF pages",
                         "query time", "not regenerated", "sample check"):
            self.assertIn(expected, explanations)
        self.assertIn("JsonObject index =", next(c.source for c in notebook.cells if c.id == "index"))
        self.assertIn("JsonObject skillset =", next(c.source for c in notebook.cells if c.id == "skillset"))
        self.assertEqual(len(python_payloads()), 11)


if __name__ == "__main__":
    unittest.main()
