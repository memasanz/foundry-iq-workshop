"""Offline execution of the actual self-contained C#06 notebook and console."""
import ast
import contextlib
import copy
import io
import json
import os
import shutil
import subprocess
import unittest
import uuid
from pathlib import Path

import nbformat
from nbclient.exceptions import CellExecutionError

import test_csharp_notebook as notebook_runner

ROOT = notebook_runner.ROOT
NOTEBOOK = ROOT / "c-sharp-notebooks" / "06-push-chunks-vectors.ipynb"


def python_payloads():
    """Evaluate only the approved Python06's local slicing and visible JSON."""
    notebook = nbformat.read(ROOT / "python-notebooks" / "06-push-chunks-vectors.ipynb", as_version=4)
    values = {
        "root": ROOT, "index_name": "alice01-push-chunks",
        "embedding_endpoint": "https://workshop.openai.azure.com",
        "embedding_deployment": "workshop-embedding", "embedding_model": "text-embedding-3-small",
        "embedding_dimensions": 1536, "semantic_configuration": "chunk-semantic",
    }
    bodies = []
    for index in (4, 6, 15, 17, 19, 21, 23):
        tree = ast.parse(notebook.cells[index].source)
        if index == 6:
            # This cell is exclusively local reads/slicing/printing, never a network call.
            with contextlib.redirect_stdout(io.StringIO()):
                exec(compile(tree, "<python06-local-slicing>", "exec"),
                     {"__builtins__": __builtins__, "json": json}, values)
            continue
        for statement in tree.body:
            if isinstance(statement, ast.Assign) and isinstance(statement.targets[0], ast.Name):
                name = statement.targets[0].id
                if name in ("index", "question"):
                    values[name] = eval(compile(ast.Expression(statement.value), "<visible-payload>", "eval"),
                                        {"__builtins__": {}}, values)
        for call in ast.walk(tree):
            if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr in ("put", "post"):
                payload = next((item.value for item in call.keywords if item.arg == "json"), None)
                if payload is not None:
                    bodies.append(eval(compile(ast.Expression(payload), "<visible-payload>", "eval"),
                                       {"__builtins__": {}}, values))
    documents = copy.deepcopy(values["documents"])
    embedding_bodies = []
    for start in range(0, len(documents), 16):
        batch = documents[start:start + 16]
        embedding_bodies.append({"input": [item["chunk"] for item in batch], "dimensions": 1536})
        for index, document in enumerate(batch):
            document["text_vector"] = [(index + 1) / 100.0] * 1536
    push_bodies = [{"value": documents[start:start + 100]} for start in range(0, len(documents), 100)]
    return [bodies[0], *embedding_bodies, *push_bodies, *bodies[1:]], values["documents"]


class PushConsoleTests(unittest.TestCase):
    def test_offline_console_runtime_and_exact_current_python_payload_parity(self):
        path = ROOT / (".csharp-push-wire-" + uuid.uuid4().hex + ".json")
        self.addCleanup(path.unlink, missing_ok=True)
        result = subprocess.run(
            ["dotnet", "run", "--project", str(ROOT / "tests" / "c-sharp-push"),
             "--", "--wire", str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("offline console protocol/runtime cases", result.stdout)
        wire = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual([item["Body"] for item in wire if item["Body"] is not None], python_payloads()[0])

    def test_help_and_cleanup_argument_guard_are_offline(self):
        project = str(ROOT / "c-sharp-console-apps" / "06-push-chunks-vectors")
        fixture = ROOT / (".csharp-push-help-fixture-" + uuid.uuid4().hex)
        fixture.mkdir()
        self.addCleanup(shutil.rmtree, fixture)
        (fixture / ".env").write_text("AZURE_TENANT_ID=invalid\nWORKSHOP_PREFIX=invalid!\n", encoding="utf-8")
        outputs = []
        for args, success in (([], True), (["help"], True), (["--help"], True), (["-h"], True),
                              (["HELP"], True), (["cleanup"], False), (["cleanup", "--help"], False),
                              (["cleanup", "--confirm", "other-index"], False), (["unknown"], False)):
            with self.subTest(args=args):
                result = subprocess.run(
                    ["dotnet", "run", "--no-build", "--project", project, "--", *args],
                    cwd=fixture, capture_output=True, text=True, timeout=30,
                )
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertEqual(result.stderr, "")
                    self.assertIn("cleanup", result.stdout)
                    outputs.append(result.stdout)
                else:
                    self.assertIn("Cleanup requires exactly: cleanup --confirm", result.stderr)
                self.assertFalse((fixture / ".workshop").exists())
        self.assertEqual(len(set(outputs)), 1, "All help aliases should produce identical output")

    def test_notebook_structure_and_teaching_are_self_contained(self):
        notebook = nbformat.read(NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        source = "\n".join(cell.source for cell in notebook.cells)
        self.assertNotIn("#!import", "\n".join(c.source for c in notebook.cells if c.cell_type == "code"))
        self.assertNotIn("#load", source)
        self.assertNotIn("#r ", source)
        self.assertNotIn("c-sharp-console-apps", source)
        self.assertIn("06: Push API", source)
        self.assertNotIn("05: Push API", source)
        self.assertIn("bool CLEAN_UP = false;", source)
        self.assertIn('Path.GetFullPath("..")', source)
        self.assertTrue(all(not cell.outputs and cell.execution_count is None
                            for cell in notebook.cells if cell.cell_type == "code"))


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional .NET Interactive tool is not installed.")
class CSharpPushNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / (".csharp-push-notebook-fixture-" + uuid.uuid4().hex)
        cls.path.mkdir()
        cls.addClassCleanup(shutil.rmtree, cls.path)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        data = cls.path / "data" / "search"
        data.mkdir(parents=True)
        for name in ("onboarding-guide.txt", "travel-policy.txt"):
            shutil.copy2(ROOT / "data" / "search" / name, data)
        (cls.path / ".env").write_text("WORKSHOP_PREFIX=wrong01\n", encoding="utf-8")
        cls.settings_text = (
            "WORKSHOP_PREFIX=alice01\nAZURE_TENANT_ID=00000000-0000-0000-0000-000000000000\n"
            "AZURE_SEARCH_ENDPOINT=https://workshop.search.windows.net\n"
            "AZURE_AI_ACCOUNT_NAME=workshop\nAZURE_AI_EMBEDDING_DEPLOYMENT_NAME=workshop-embedding\n"
            "AZURE_AI_EMBEDDING_MODEL_NAME=text-embedding-3-small\n"
        )
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

    def execute(self, cells=None, checks="", mode="", question=None):
        test_source = (ROOT / "tests" / "c-sharp-push" / "PushTests.cs").read_text(encoding="utf-8")
        handler = test_source[test_source.index("public sealed record CapturedPushRequest"):]
        handler += """
client.Dispose();
PushOfflineHandler handler = new PushOfflineHandler();
client = new HttpClient(handler);
List<string> audiences = new List<string>();
List<TimeSpan> delays = new List<TimeSpan>();
tokenProvider = resource => { audiences.Add(resource); return Task.FromResult("synthetic-token"); };
pause = duration => { delays.Add(duration); return Task.CompletedTask; };
handler.Mode = """ + json.dumps(mode) + ";\n"
        if question is not None:
            handler += "string question = " + json.dumps(question) + ";\n"
        original = self.notebook
        self.notebook = copy.deepcopy(original)
        if cells is not None:
            self.notebook.cells = [cell for cell in self.notebook.cells
                                   if cell.cell_type != "code" or cell.id == "setup" or cell.id in cells]
        try:
            return notebook_runner.CSharpNotebookTests.execute(self, handler, checks)
        finally:
            self.notebook = original
            self.assertEqual(NOTEBOOK.read_bytes(), self.original_bytes)
            self.assertFalse((self.path / ".workshop").exists(), "Notebook must not persist console cache")

    def test_actual_full_pipeline_has_exact_python06_payloads(self):
        bodies, chunks = python_payloads()
        checks = [
            'if (settings.Prefix != "alice01" || !uploaded) throw new Exception("Settings override or push state failed");',
            'if (handler.Requests.Any(r => r.Method == "DELETE")) throw new Exception("Cleanup enabled by default");',
            'if (handler.Requests.Any(r => r.Authorization != "Bearer synthetic-token" || r.HasApiKey)) throw new Exception("Authentication differs");',
            'if (audiences.Count(a => a == PushTransport.EmbeddingAudience) != 1 || audiences.Count != handler.Requests.Count) throw new Exception("Token audiences/refresh failed");',
            'if (handler.Requests.Count(r => r.Uri.Contains("/embeddings?api-version=2024-10-21")) != 1) throw new Exception("Embedding version differs");',
            'if (handler.Requests.Where(r => !r.Uri.Contains("/embeddings?")).Any(r => !r.Uri.EndsWith("?api-version=2025-09-01"))) throw new Exception("Search version differs");',
            f'if (handler.Requests.Count != {len(chunks) + len(bodies)}) throw new Exception("Hidden calls");',
            'var actualBodies = handler.Requests.Where(r => r.Body != null).Select(r => r.Body).ToArray();',
        ]
        for index, expected in enumerate(bodies):
            checks.append(
                f'if (!JsonNode.DeepEquals(actualBodies[{index}], JsonNode.Parse({json.dumps(json.dumps(expected))}))) '
                f'throw new Exception("Python06 payload mismatch {index}");'
            )
        self.execute(checks="\n".join(checks))

    def test_actual_chunking_boundaries_all_text_overlap_unicode_and_ids(self):
        self.execute(cells=set(), checks=r'''
foreach (int length in new[] { 1, 800, 999, 1000, 1001, 1800, 1801, 2600 })
{
    string text = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
    var sliced = PushWorkshop.Slice(text, "sample.txt", "data/search/sample.txt");
    int count = length <= 1000 ? 1 : 1 + (int)Math.Ceiling((length - 1000) / 800.0);
    if (sliced.Count != count) throw new Exception("Redundant tail/missing chunk");
    string rebuilt = sliced[0]["chunk"].GetValue<string>();
    for (int i = 1; i < sliced.Count; i++)
    {
        string previous = sliced[i - 1]["chunk"].GetValue<string>();
        string current = sliced[i]["chunk"].GetValue<string>();
        if (previous.Substring(previous.Length - 200) != current.Substring(0, 200)) throw new Exception("Overlap failed");
        rebuilt += current.Substring(200);
    }
    if (rebuilt != text || !JsonNode.DeepEquals(sliced, PushWorkshop.Slice(text, "sample.txt", "data/search/sample.txt")))
        throw new Exception("Lost text/unstable keys");
    if (sliced[count - 1]["chunk_id"].GetValue<string>() != $"sample-{count - 1}" ||
        sliced[0]["source"]["url"].GetValue<string>() != "data/search/sample.txt")
        throw new Exception("IDs/provenance differs");
}
if (PushWorkshop.Slice(string.Concat(Enumerable.Repeat("😀", 1001)), "unicode.txt", "unicode.txt").Count != 2)
    throw new Exception("UTF16 slicing");
if (handler.Requests.Count != 0) throw new Exception("Hidden chunk calls");
''')

    def test_actual_embedding_batch_mapping_failures_clear_old_vectors(self):
        self.execute(cells=set(), checks=r'''
async Task ExpectFailure(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(message)) { return; }
    throw new Exception("Expected failure " + message);
}
var batch = PushWorkshop.Slice(new string('a', 27000), "sample.txt", "sample.txt");
await Lab().EmbedAsync(batch);
if (!handler.Requests.Select(r => r.Body["input"].AsArray().Count).SequenceEqual(new[] { 16, 16, 2 }))
    throw new Exception("Embedding batch limit");
if (batch[0]["text_vector"][0].GetValue<double>() != 0.01 || batch[15]["text_vector"][0].GetValue<double>() != 0.16)
    throw new Exception("Response index ignored");
foreach (string mode in new[] { "missing-embedding", "duplicate-embedding", "out-of-range", "wrong-vector", "late-embedding-failure" })
{
    handler.Reset(mode);
    await ExpectFailure(() => Lab().EmbedAsync(batch), mode == "wrong-vector" ? "dimensions" :
        mode == "late-embedding-failure" ? "HTTP 429" : "exactly one vector");
    if (batch.Any(item => item["text_vector"] != null)) throw new Exception("Retained partial/stale vectors");
    if (handler.Requests.Count > 2) throw new Exception("Paid automatic retries");
}
''')

    def test_actual_push_batches_partial_incomplete_and_mismatched_keys(self):
        self.execute(cells=set(), checks=r'''
async Task ExpectPushFailure(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error.Message.Contains(message)) { return; }
    throw new Exception("Expected failure " + message);
}
var batch = PushWorkshop.Slice(new string('a', 160201), "sample.txt", "sample.txt");
foreach (var document in batch) document["text_vector"] = PushOfflineHandler.Vector(0.01);
await Lab().PushAsync(batch);
if (!handler.Requests.Select(r => r.Body["value"].AsArray().Count).SequenceEqual(new[] { 100, 100, 1 }))
    throw new Exception("Push batch limit");
foreach (string mode in new[] { "partial-push", "incomplete-push", "wrong-key", "duplicate-key" })
{
    handler.Reset(mode);
    await ExpectPushFailure(() => Lab().PushAsync(batch), "Push was incomplete");
    if (handler.Requests.Count != 1) throw new Exception("Push automatically retried");
}
handler.Reset("success207");
await Lab().PushAsync(batch);
handler.Reset();
batch[0].AsObject().Remove("text_vector");
await ExpectPushFailure(() => Lab().PushAsync(batch), "dimensions");
if (handler.Requests.Count != 0) throw new Exception("Push made hidden embedding call");
''')

    def test_actual_verification_polls_all_keys_compares_content_and_float_precision(self):
        self.execute(cells={"chunk-documents", "embed-documents", "push-documents"}, checks=r'''
handler.Reset("pending");
await Lab().VerifyAsync(documents);
if (delays.Count != 1 || delays[0] != TimeSpan.FromSeconds(2)) throw new Exception("Polling pending/stale content failed");
handler.Reset("precision");
await Lab().VerifyAsync(documents);
foreach (string mode in new[] { "old-text", "old-vector", "missing", "missing-stored-vector" })
{
    handler.Reset(mode); delays.Clear();
    bool timedOut = false;
    try { await Lab().VerifyAsync(documents); }
    catch (TimeoutException) { timedOut = true; }
    if (!timedOut || handler.Requests.Count != documents.Count * 30 || delays.Count != 29)
        throw new Exception("Verification accepted stale content/wrong vector or exceeded bounds");
}
handler.Reset("http403");
bool failed = false;
try { await Lab().VerifyAsync(documents); }
catch (HttpRequestException) { failed = true; }
if (!failed || handler.Requests.Count != 1) throw new Exception("Verification hid HTTP error");
''')

    def test_actual_query_contracts_partials_errors_and_safe_question_serialization(self):
        self.execute(cells=set(), checks=r'''
foreach (string mode in PushWorkshop.QueryModes)
{
    handler.Reset();
    await Lab().QueryAsync(mode, "What about \"quotes\"?\nC:\\receipts");
    var body = handler.Requests.Single().Body;
    if ((body["search"] != null) != (mode != "vector") ||
        (body["vectorQueries"] != null) != (mode == "vector" || mode == "hybrid" || mode == "semantic-hybrid"))
        throw new Exception("Query contract");
    if (body["search"] != null && body["search"].GetValue<string>() != "What about \"quotes\"?\nC:\\receipts")
        throw new Exception("Question serialization");
}
foreach (string mode in new[] { "partial200", "partial206", "http403" })
{
    handler.Reset(mode);
    bool failed = false;
    try { await Lab().QueryAsync("semantic-hybrid", PushWorkshop.Question); }
    catch (Exception error) when (error.Message.Contains(mode == "partial200" ? "Semantic ranking did not complete" :
        mode == "partial206" ? "HTTP 206" : "HTTP 403")) { failed = true; }
    if (!failed || handler.Requests.Count != 1) throw new Exception("Partial/error accepted or retried");
}
handler.Reset();
try { await Lab().QueryAsync("vector", " "); } catch (ArgumentException) { }
if (handler.Requests.Count != 0) throw new Exception("Invalid question made model call");
''')

    def test_actual_independent_cells_and_opt_in_scoped_cleanup(self):
        self.execute(cells={"create-index", "chunk-documents", "query-keyword", "cleanup"}, checks=r'''
if (!handler.Requests.Select(r => r.Method).SequenceEqual(new[] { "PUT", "POST" }))
    throw new Exception("Hidden cell actions or default cleanup");
handler.Reset();
bool guarded = false;
try { await Lab().CleanupAsync(false); } catch (InvalidOperationException) { guarded = true; }
if (!guarded || handler.Requests.Count != 0) throw new Exception("Cleanup not opt-in");
handler.Reset("missing");
await Lab().CleanupAsync(true);
if (handler.Requests.Single().Method != "DELETE" ||
    handler.Requests.Single().Uri != settings.SearchEndpoint + "/indexes/alice01-push-chunks?api-version=2025-09-01")
    throw new Exception("Cleanup scope differs");
''')
        with self.assertRaises(CellExecutionError) as caught:
            self.execute(cells={"verify-documents"})
        self.assertIn("Push every document successfully", caught.exception.evalue)

    def test_actual_cell_http_errors_and_partial_semantic_rejection(self):
        for mode, message in (("partial200", "Semantic ranking did not complete"),
                              ("partial206", "HTTP 206"), ("http403", "HTTP 403")):
            with self.subTest(mode=mode), self.assertRaises(CellExecutionError) as caught:
                self.execute(cells={"query-semantic", "query-semantic-hybrid"}, mode=mode,
                             question="When are receipts due?")
            self.assertIn(message, caught.exception.evalue)

    def test_actual_independent_query_cells_have_no_hidden_actions(self):
        bodies, _ = python_payloads()
        for cell, expected in zip(("query-vector", "query-hybrid", "query-semantic", "query-semantic-hybrid"),
                                  bodies[-4:]):
            executed = self.execute(cells={cell}, question="What receipts do I need to claim travel expenses, and when must I submit them?",
                                    checks=f"""
if (handler.Requests.Count != 1 || handler.Requests[0].Method != "POST" ||
    !JsonNode.DeepEquals(handler.Requests[0].Body, JsonNode.Parse({json.dumps(json.dumps(expected))})))
    throw new Exception("Hidden actions or Python06 query payload differs");
""")
            query = next(c for c in executed.cells if c.id == cell)
            printed = "".join(output.get("text", output.get("data", {}).get("text/plain", ""))
                              for output in query.outputs)
            self.assertIn('\n  "value": [', printed, f"{cell} should print indented results")
            self.assertEqual(json.loads(printed), {"value": [{}]})


if __name__ == "__main__":
    unittest.main()
