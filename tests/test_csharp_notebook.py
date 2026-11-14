import ast
import copy
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

import nbformat
from jupyter_client import KernelManager
from jupyter_client.kernelspec import KernelSpecManager
from nbclient import NotebookClient


ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which("dotnet-interactive"), "Optional deprecated .NET Interactive tool is not installed.")
class CSharpNotebookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory(prefix="webiq-csharp-notebook-")
        cls.addClassCleanup(cls.folder.cleanup)
        cls.path = Path(cls.folder.name)
        cls.working_directory = cls.path / "c-sharp-notebooks"
        cls.working_directory.mkdir()
        (cls.path / ".env").write_text("WEBIQ_API_KEY=local-test-key\n", encoding="utf-8")
        (cls.path / ".env.azure").write_text("WEBIQ_API_KEY=synthetic-test-key\n", encoding="utf-8")
        cls.kernel_directory = cls.path / "kernels"
        cls.kernel_directory.mkdir()
        result = subprocess.run(
            ["dotnet", "interactive", "jupyter", "install", "--path", str(cls.kernel_directory)],
            capture_output=True, text=True, timeout=90,
            env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        cls.notebook = nbformat.read(ROOT / "c-sharp-notebooks" / "01-web-iq-apis.ipynb", as_version=4)
        nbformat.validate(cls.notebook)

    def execute(self, handler, checks):
        notebook = copy.deepcopy(self.notebook)
        self.assertFalse((self.path / "c-sharp-console-apps").exists())
        self.assertFalse(any("#!import" in cell.source for cell in notebook.cells if cell.cell_type == "code"))
        setup_index = next(
            index for index, cell in enumerate(notebook.cells)
            if cell.id in ("9c424141", "setup")
        )
        notebook.cells.insert(setup_index + 1, nbformat.v4.new_code_cell(
            handler, metadata=copy.deepcopy(notebook.cells[setup_index].metadata),
        ))
        notebook.cells.append(nbformat.v4.new_code_cell(
            checks, metadata=copy.deepcopy(notebook.cells[setup_index].metadata),
        ))
        kernel = KernelManager(
            kernel_name=notebook.metadata.kernelspec.name,
            kernel_spec_manager=KernelSpecManager(kernel_dirs=[str(self.kernel_directory)]),
        )
        client = NotebookClient(
            notebook, km=kernel, timeout=90,
            resources={"metadata": {"path": str(self.working_directory)}},
        )
        try:
            client.execute(env={**os.environ, "DOTNET_INTERACTIVE_CLI_TELEMETRY_OPTOUT": "1"})
        finally:
            if client.kc is not None:
                client.kc.stop_channels()
            if kernel.has_kernel:
                kernel.shutdown_kernel(now=True)
        return notebook

    def handler(self, fail=False):
        return r'''
using System.Collections.Generic;
using System.Threading;

public class OfflineHandler : HttpMessageHandler
{
    public List<(string Path, string Key, JsonObject Body)> Requests = new List<(string, string, JsonObject)>();
    public bool Fail;
    int browseCalls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add((request.RequestUri.AbsolutePath,
            System.Linq.Enumerable.First(request.Headers.GetValues("x-apikey")),
            JsonNode.Parse(await request.Content.ReadAsStringAsync()).AsObject()));
        string body = "{\"webResults\":[{\"url\":\"https://example.test/ofac\"}],"
            + "\"imageResults\":[{\"title\":\"Sample\",\"hostPageUrl\":\"https://example.test\","
            + "\"thumbnailUrl\":\"https://example.test/preview.png\"}]}";
        int status = 200;
        if (Fail)
        {
            status = 403;
            body = "synthetic HTTP failure";
        }
        else if (request.RequestUri.AbsolutePath == "/v3/browse")
        {
            browseCalls++;
            body = browseCalls == 1 ? "{\"retryAfter\":\"0s\"}" : "{\"url\":\"https://example.test/ofac\"}";
            status = browseCalls == 1 ? 202 : 200;
        }
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
    }
}

client.Dispose();
OfflineHandler handler = new OfflineHandler();
client = new HttpClient(handler) { BaseAddress = new Uri("https://api.microsoft.ai/v3/") };
client.DefaultRequestHeaders.Add("x-apikey", apiKey);
''' + ("handler.Fail = true;" if fail else "")

    def test_actual_csharp_cells_match_python_requests_and_complete_pending_browse(self):
        python = nbformat.read(ROOT / "python-notebooks" / "01-web-iq-apis.ipynb", as_version=4)
        sources = {cell.id: cell.source for cell in python.cells}
        question = ast.literal_eval(ast.parse(sources["web-search"]).body[0].value)
        expected = []
        for cell_id in ["web-search", "localized-search", "browse", "news", "images", "videos"]:
            tree = ast.parse(sources[cell_id])
            call = next(node for node in ast.walk(tree) if isinstance(node, ast.Call)
                        and isinstance(node.func, ast.Attribute) and node.func.attr == "post")
            payload = next(keyword.value for keyword in call.keywords if keyword.arg == "json")
            values = {}
            for key, value in zip(payload.keys, payload.values):
                values[ast.literal_eval(key)] = (
                    {"question": question, "browse_url": "https://example.test/ofac"}[value.id]
                    if isinstance(value, ast.Name) else ast.literal_eval(value)
                )
            path = ast.literal_eval(call.args[0]).replace("https://api.microsoft.ai", "")
            expected.append((path, values))
            if cell_id == "browse":
                expected.append((path, values))
        checks = ['if (apiKey != "synthetic-test-key") throw new Exception("Settings precedence failed");',
                  f'if (handler.Requests.Count != {len(expected)}) throw new Exception("Unexpected request count");']
        for index, (path, values) in enumerate(expected):
            literal = json.dumps(values).replace('"', '""')
            checks.append(
                f'if (handler.Requests[{index}].Path != "{path}" || '
                f'handler.Requests[{index}].Key != "synthetic-test-key" || '
                f'!JsonNode.DeepEquals(handler.Requests[{index}].Body, JsonNode.Parse(@"{literal}"))) '
                f'throw new Exception("Request {index} differs from Python");'
            )
        checks.append('if (page["url"] == null) throw new Exception("Pending Browse did not complete");')
        self.execute(self.handler(), "\n".join(checks))

    def test_http_failures_clear_results_and_browse_skips_without_a_source(self):
        self.execute(self.handler(fail=True), r'''
if (handler.Requests.Count != 5) throw new Exception("Browse should skip when Web Search fails");
if (web.Count != 0 || localized.Count != 0 || page.Count != 0 ||
    news.Count != 0 || images.Count != 0 || videos.Count != 0)
    throw new Exception("An HTTP error retained stale results");
''')


if __name__ == "__main__":
    unittest.main()
