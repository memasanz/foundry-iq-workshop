import ast
import json
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import nbformat
import requests


ROOT = Path(__file__).resolve().parents[1]


def code_cell(notebook_name, cell_id):
    notebook = nbformat.read(ROOT / "notebooks" / notebook_name, as_version=4)
    return next(cell.source for cell in notebook.cells if cell.id == cell_id)


def rest_namespace():
    namespace = {
        "requests": Mock(),
        "time": SimpleNamespace(perf_counter=lambda: 1, sleep=Mock()),
        "os": Mock(),
        "json": json,
        "api_key": "test-key",
        "question": "public topic",
        "web": {"webResults": [{"url": "https://example.com"}]},
    }
    exec(code_cell("01-web-iq-apis.ipynb", "optional-exercises"), namespace)
    namespace["os"].getenv.return_value = "true"
    return namespace


def http_response(status, data, headers=None):
    response = Mock(status_code=status, headers=headers or {})
    response.json.return_value = data
    if status >= 400:
        response.raise_for_status.side_effect = requests.HTTPError(f"HTTP {status}")
    return response


class NotebookStructureTests(unittest.TestCase):
    def test_notebook_schema_syntax_and_clean_outputs(self):
        paths = list((ROOT / "notebooks").glob("*.ipynb"))
        self.assertEqual({path.name for path in paths}, {
            "01-web-iq-apis.ipynb", "02-foundry-web-iq-agent.ipynb",
            "03-blob-indexer-skills.ipynb", "04-sharepoint-indexed.ipynb", "05-sharepoint-remote.ipynb",
            "06-foundry-iq-knowledge-base.ipynb", "07-foundry-iq-apim.ipynb",
                        "08-hosted-agent.ipynb",        })
        for path in paths:
            with self.subTest(notebook=path.name):
                notebook = nbformat.read(path, as_version=4)
                nbformat.validate(notebook)
                for cell in notebook.cells:
                    if cell.cell_type == "code":
                        self.assertIsNone(cell.execution_count)
                        self.assertEqual(cell.outputs, [])
                        compile(
                            cell.source, f"{path.name}:{cell.id}", "exec",
                            flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT,
                        )


class DirectApiCellTests(unittest.TestCase):
    def setUp(self):
        self.ns = rest_namespace()
        self.post = self.ns["requests"].post

    def run_cell(self, cell_id):
        with patch("builtins.print") as output:
            exec(code_cell("01-web-iq-apis.ipynb", cell_id), self.ns)
        return output

    def test_search_endpoint_headers_and_payload(self):
        self.post.return_value = http_response(200, {"webResults": [{"url": "https://example.com"}]})
        self.run_cell("web-search")
        self.assertEqual(len(self.ns["web"]["webResults"]), 1)
        self.post.assert_called_once_with(
            "https://api.microsoft.ai/v3/search/web",
            headers={"x-apikey": "test-key", "Content-Type": "application/json"},
            json={
                "query": self.ns["question"], "maxResults": 10,
                "contentFormat": "passage", "maxLength": 3000,
                "language": "en", "region": "US", "safeSearch": "strict",
            },
            timeout=(10, 90),
        )

    def test_each_exercise_posts_directly_and_displays_response(self):
        endpoints = {
            "localized-search": "search/web", "news": "search/news",
            "images": "search/images", "videos": "search/videos",
            "places": "search/places", "auto": "auto",
        }
        for cell_id, endpoint in endpoints.items():
            with self.subTest(cell=cell_id):
                self.post.reset_mock()
                self.post.return_value = http_response(200, {"traceId": "test-trace"})
                self.run_cell(cell_id)
                self.assertEqual(self.post.call_count, 1)
                self.assertEqual(self.post.call_args.args[0], f"https://api.microsoft.ai/v3/{endpoint}")
                self.assertEqual(self.post.call_args.kwargs["headers"]["x-apikey"], "test-key")
                self.assertEqual(self.post.call_args.kwargs["timeout"], (10, 90))
                self.post.return_value.raise_for_status.assert_called_once()

    def test_browse_pending_honors_duration(self):
        self.post.side_effect = [
            http_response(202, {"retryAfter": "60s", "traceId": "pending"}),
            http_response(200, {"content": "page"}),
        ]
        self.run_cell("browse")
        self.assertEqual(self.ns["page"]["content"], "page")
        self.ns["time"].sleep.assert_called_once_with(60)
        self.assertEqual(self.post.call_count, 2)
        self.assertEqual(self.post.call_args.kwargs["json"]["url"], "https://example.com")

    def test_http_failure_is_not_hidden_or_retried(self):
        for status in (401, 403, 429, 503):
            with self.subTest(status=status):
                self.post.reset_mock()
                self.post.return_value = http_response(status, {"traceId": "failure"})
                with self.assertRaisesRegex(requests.HTTPError, f"HTTP {status}"):
                    self.run_cell("web-search")
                self.assertEqual(self.post.call_count, 1)

    def test_exhausted_crawl_is_not_success(self):
        self.post.return_value = http_response(202, {"retryAfter": "1s"})
        with self.assertRaises(TimeoutError):
            self.run_cell("browse")
        self.assertEqual(self.post.call_count, 4)
        self.assertEqual(self.ns["time"].sleep.call_count, 3)

    def test_missing_crawl_delay_is_explicit(self):
        self.post.return_value = http_response(202, {"traceId": "bad-contract"})
        with self.assertRaisesRegex(RuntimeError, "omitted retryAfter"):
            self.run_cell("browse")

    def test_excessive_crawl_delay_does_not_sleep(self):
        self.post.return_value = http_response(202, {"retryAfter": "121s"})
        with self.assertRaises(TimeoutError):
            self.run_cell("browse")
        self.ns["time"].sleep.assert_not_called()

    def test_disabled_exercises_do_not_make_requests(self):
        self.ns["os"].getenv.return_value = "false"
        for cell_id in (
            "browse", "news", "images", "videos", "places", "places-inspect",
            "auto", "auto-inspect", "auto-advanced",
        ):
            self.run_cell(cell_id)
        self.post.assert_not_called()

    def test_empty_search_response_is_displayed_without_fabricated_fields(self):
        self.post.return_value = http_response(200, {})
        self.run_cell("web-search")
        self.assertEqual(self.ns["web"], {})

    def test_places_request_uses_documented_parameters(self):
        self.post.return_value = http_response(200, {"placeResults": []})
        self.run_cell("places")
        self.assertEqual(self.post.call_args.kwargs["json"], {
            "query": "public libraries near downtown Seattle",
            "maxResults": 3, "language": "en", "region": "US",
            "location": "lat:47.6062;long:-122.3321",
        })
        self.assertEqual(self.ns["places"]["placeResults"], [])

    def test_places_inspection_preserves_missing_rating_and_zero_count(self):
        self.ns["places"] = {"placeResults": [{
            "name": "Example library",
            "location": {"city": "Seattle"},
            "reviews": [{"providerName": "Example provider", "ratingCount": 0}],
        }]}
        output = self.run_cell("places-inspect")
        printed = [call.args for call in output.call_args_list]
        self.assertIn(("Rating:", "(unavailable)"), printed)
        self.assertIn(("Rating count:", 0), printed)
        self.assertIn(("Rating provider:", "Example provider"), printed)
        self.post.assert_not_called()

    def test_places_inspection_handles_no_results(self):
        self.ns["places"] = {}
        output = self.run_cell("places-inspect")
        output.assert_called_once_with("Places returned:", 0)

    def test_beta_permission_failures_remain_explicit(self):
        for cell_id in ("places", "auto"):
            with self.subTest(cell=cell_id):
                self.post.reset_mock()
                self.post.return_value = http_response(403, {})
                with self.assertRaises(requests.HTTPError):
                    self.run_cell(cell_id)
                self.assertEqual(self.post.call_count, 1)

    def test_auto_inspection_keeps_citations_with_evidence(self):
        self.ns["auto"] = {"results": [
            {
                "evidence": {"type": "text", "content": "First finding"},
                "citations": [{"title": "First source", "url": "https://example.com/first"}],
            },
            {
                "evidence": {"type": "structured/timeZone", "content": {"name": "Example zone"}},
                "citations": [],
            },
        ]}
        output = self.run_cell("auto-inspect")
        printed = [call.args for call in output.call_args_list]
        first_url = printed.index(("URL:", "https://example.com/first"))
        second_evidence = printed.index(("\n[2] Type: structured/timeZone",))
        self.assertLess(first_url, second_evidence)
        self.assertIn(("Supporting citations:", 0), printed)
        self.assertIn(("Updated:", "(unavailable)"), printed)
        self.post.assert_not_called()

    def test_auto_inspection_handles_no_results(self):
        self.ns["auto"] = {}
        output = self.run_cell("auto-inspect")
        output.assert_called_once_with("Evidence items:", 0)

    def test_advanced_auto_requires_explicit_opt_in(self):
        self.run_cell("auto-advanced")
        self.post.assert_not_called()

    def test_auto_modes_compare_the_same_query_and_budget(self):
        self.post.return_value = http_response(200, {"results": []})
        self.run_cell("auto")
        fast_payload = self.post.call_args.kwargs["json"]
        source = code_cell("01-web-iq-apis.ipynb", "auto-advanced")
        source = source.replace("RUN_AUTO_ADVANCED = False", "RUN_AUTO_ADVANCED = True")
        with patch("builtins.print"):
            exec(source, self.ns)
        advanced_payload = self.post.call_args.kwargs["json"]
        self.assertEqual(advanced_payload, {**fast_payload, "mode": "advanced"})
        self.assertEqual(fast_payload["mode"], "fast")
        self.assertEqual(fast_payload["responseFilter"], ["web", "news"])
        self.assertEqual(fast_payload["maxResponseLength"], 6000)
        self.assertEqual(self.post.call_count, 2)


class AgentHelperTests(unittest.TestCase):
    def namespace(self, responses):
        client = Mock()
        client.responses.create.side_effect = responses
        namespace = {
            "openai": client,
            "conversation": SimpleNamespace(id="conversation-1"),
            "agent_reference": {"type": "agent_reference", "name": "test", "version": "1"},
        }
        exec(code_cell("02-foundry-web-iq-agent.ipynb", "agent-helper"), namespace)
        return namespace

    def test_approval_continues_previous_response(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments='{"query":"public"}',
        )
        tool_call = SimpleNamespace(type="mcp_call")
        first = SimpleNamespace(id="r1", output=[approval])
        last = SimpleNamespace(id="r2", output=[tool_call], status="completed")
        ns = self.namespace([first, last])
        with patch("builtins.input", return_value="y"):
            response, history = ns["ask_agent"]("public question")
        self.assertIs(response, last)
        self.assertEqual(len(history), 2)
        continuation = ns["openai"].responses.create.call_args.kwargs
        self.assertEqual(continuation["previous_response_id"], "r1")
        self.assertTrue(continuation["input"][0]["approve"])
        self.assertNotIn("conversation", continuation)

    def test_approval_defaults_to_denied(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        ns = self.namespace([
            SimpleNamespace(id="r1", output=[approval]),
            SimpleNamespace(id="r2", output=[], status="completed"),
        ])
        with patch("builtins.input", return_value=""):
            ns["ask_agent"]("public question")
        decision = ns["openai"].responses.create.call_args.kwargs["input"][0]
        self.assertFalse(decision["approve"])

    def test_failed_response_is_not_success(self):
        ns = self.namespace([SimpleNamespace(id="r1", output=[], status="failed")])
        with self.assertRaisesRegex(RuntimeError, "status failed"):
            ns["ask_agent"]("public question")

    def test_final_allowed_approval_can_complete(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        final = SimpleNamespace(id="r2", output=[], status="completed")
        ns = self.namespace([SimpleNamespace(id="r1", output=[approval]), final])
        with patch("builtins.input", return_value="y"):
            response, _ = ns["ask_agent"]("public question", max_approval_rounds=1)
        self.assertIs(response, final)

    def test_approval_limit_is_bounded(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        ns = self.namespace([SimpleNamespace(id="r1", output=[approval])])
        with self.assertRaisesRegex(RuntimeError, "Approval-round limit"):
            ns["ask_agent"]("public question", max_approval_rounds=0)
        self.assertEqual(ns["openai"].responses.create.call_count, 1)


if __name__ == "__main__":
    unittest.main()
