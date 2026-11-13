import ast
import json
import unittest
from unittest.mock import Mock, patch

import nbformat
import requests

from test_notebooks import ROOT, AttendeeAliasTests, code_cell, http_response


NOTEBOOK = "14-foundry-iq-webiq-only.ipynb"


class WebIqOnlyNotebookTests(unittest.TestCase):
    def setUp(self):
        self.ns = AttendeeAliasTests().configure(14, "alice01")
        self.ns.update(requests=Mock(), search_token=lambda: "search-user-token")
        self.http = self.ns["requests"]
        self.http.put.return_value = http_response(200, {})
        self.http.delete.return_value = http_response(204, {})

    def run_cell(self, cell_id):
        with patch("builtins.print") as output:
            exec(code_cell(NOTEBOOK, cell_id), self.ns)
        return output

    def test_notebook_is_valid_with_compilable_code_and_clean_outputs(self):
        notebook = nbformat.read(ROOT / "notebooks" / NOTEBOOK, as_version=4)
        nbformat.validate(notebook)
        for cell in notebook.cells:
            if cell.cell_type == "code":
                self.assertIsNone(cell.execution_count)
                self.assertEqual(cell.outputs, [])
                compile(cell.source, cell.id, "exec", flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT)

    def test_configuration_is_independent_of_apim_indexes_and_ingestion(self):
        ns = AttendeeAliasTests().configure(
            14, "alice01", APIM_GATEWAY_URL="", BROKER_APP_CLIENT_ID="",
            AZURE_STORAGE_RESOURCE_ID="", SHAREPOINT_SITE_URL="",
            AZURE_AI_EMBEDDING_DEPLOYMENT_NAME="", WEBIQ_KB_TOOLS="auto",
        )
        self.assertEqual(ns["webiq_source_name"], "alice01-nb14-webiq")
        self.assertEqual(ns["knowledge_base_name"], "alice01-nb14-kb")
        ns["AzureCliCredential"].assert_called_once_with(
            tenant_id=ns["tenant_id"], process_timeout=60,
        )

    def test_source_stores_key_and_exposes_only_basic_web_without_printing_key(self):
        output = self.run_cell("create-webiq-source")
        body = self.http.put.call_args.kwargs["json"]
        parameters = body["mcpServerParameters"]
        self.assertEqual(body["kind"], "mcpServer")
        self.assertEqual(parameters["authentication"], {
            "kind": "storedHeaders",
            "storedHeadersParameters": {"headers": {"x-apikey": "synthetic-test-key"}},
        })
        self.assertEqual([tool["name"] for tool in parameters["tools"]], ["web"])
        self.assertIn("contentFormat to passage", body["description"])
        self.assertNotIn("synthetic-test-key", str(output.call_args_list))

    def test_network_check_accepts_http_auth_error_without_sending_tokens_or_printing_proxy(self):
        self.http.get.return_value = http_response(401, {})
        self.http.utils.get_environ_proxies.return_value = {
            "https": "http://user:proxy-secret@proxy.example:8080",
        }
        output = self.run_cell("network-diagnostics")
        self.assertEqual(self.http.get.call_args.kwargs, {
            "timeout": (10, 20), "allow_redirects": False,
        })
        self.assertIn("HTTPS completed", str(output.call_args_list))
        self.assertNotIn("proxy-secret", str(output.call_args_list))
        self.assertNotIn("synthetic-test-key", str(self.http.get.call_args))

    def test_network_check_reports_connection_failure_without_claiming_success(self):
        self.http.get.side_effect = requests.ConnectionError("reset")
        self.http.utils.get_environ_proxies.return_value = {}
        output = self.run_cell("network-diagnostics")
        printed = str(output.call_args_list)
        self.assertIn("ConnectionError", printed)
        self.assertIn("not resolved in this kernel", printed)
        self.assertNotIn("HTTPS completed", printed)

    def test_knowledge_base_contains_only_this_notebooks_webiq_source(self):
        self.run_cell("create-knowledge-base")
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["knowledgeSources"], [{"name": "alice01-nb14-webiq"}])
        self.assertEqual(body["outputMode"], "answerSynthesis")
        self.assertEqual(body["models"][0]["azureOpenAIParameters"]["deploymentId"], "workshop-chat")
        self.assertIn("contentFormat to passage", body["retrievalInstructions"])
        self.http.get.assert_not_called()

    def test_both_output_modes_show_evidence_without_sending_webiq_key(self):
        self.run_cell("question")
        self.http.post.return_value = http_response(200, {
            "response": [{"content": [{"text": "Answer [ref_id:0]"}]}],
            "activity": [{"type": "mcpServer", "knowledgeSourceName": "alice01-nb14-webiq",
                          "count": 1}],
            "references": [{"id": "0", "sourceData": {"content": json.dumps({
                "title": "Forecast", "url": "https://example.com/weather",
                "content": "Seattle forecast evidence",
            })}}],
        })
        for mode in ("answerSynthesis", "extractiveData"):
            with self.subTest(mode=mode):
                self.ns["output_mode"] = mode
                output = self.run_cell("retrieve")
                call = self.http.post.call_args
                self.assertTrue(call.args[0].startswith("https://workshop.search.windows.net/"))
                self.assertEqual(call.kwargs["headers"], {"Authorization": "Bearer search-user-token"})
                body = call.kwargs["json"]
                self.assertEqual(body["outputMode"], mode)
                self.assertEqual(body["knowledgeSourceParams"], [{
                    "knowledgeSourceName": "alice01-nb14-webiq", "kind": "mcpServer",
                    "includeReferences": True, "includeReferenceSourceData": True,
                }])
                self.assertNotIn("synthetic-test-key", str(call))
                printed = str(output.call_args_list)
                self.assertIn("https://example.com/weather", printed)
                self.assertIn("Seattle forecast evidence", printed)

    def test_source_errors_and_partial_responses_are_not_reported_as_success(self):
        self.run_cell("question")
        for status, activity in (
            (200, [{"type": "mcpServer", "error": {"message": "auth_missing_token"}}]),
            (206, []),
        ):
            with self.subTest(status=status):
                self.http.post.return_value = http_response(status, {
                    "response": [], "references": [], "activity": activity,
                })
                with patch("builtins.print") as output:
                    with self.assertRaisesRegex(RuntimeError, "partial or Web IQ failed"):
                        exec(code_cell(NOTEBOOK, "retrieve"), self.ns)
                if activity:
                    self.assertIn("auth_missing_token", str(output.call_args_list))

    def test_http_failure_prints_redacted_details_before_raising(self):
        self.run_cell("question")
        response = http_response(403, {})
        response.text = "Rejected synthetic-test-key"
        self.http.post.return_value = response
        with patch("builtins.print") as output:
            with self.assertRaises(requests.HTTPError):
                exec(code_cell(NOTEBOOK, "retrieve"), self.ns)
        self.assertIn("<redacted>", str(output.call_args_list))
        self.assertNotIn("synthetic-test-key", str(output.call_args_list))

    def test_cleanup_is_opt_in_and_deletes_only_own_definitions_in_order(self):
        self.run_cell("cleanup")
        self.http.delete.assert_not_called()
        source = code_cell(NOTEBOOK, "cleanup").replace("CLEAN_UP = False", "CLEAN_UP = True")
        with patch("builtins.print"):
            exec(source, self.ns)
        self.assertEqual([call.args[0] for call in self.http.delete.call_args_list], [
            "https://workshop.search.windows.net/knowledgebases/alice01-nb14-kb?api-version=2026-08-01-preview",
            "https://workshop.search.windows.net/knowledgesources/alice01-nb14-webiq?api-version=2026-08-01-preview",
        ])


if __name__ == "__main__":
    unittest.main()
