import json
import unittest
from unittest.mock import Mock, patch

import requests

from test_notebooks import AttendeeAliasTests, code_cell, http_response


NOTEBOOK = "10-foundry-iq-webiq-blob.ipynb"
QUERIES = ("ask-blob", "ask-web", "ask-mixed")


class WebIqBlobNotebookTests(unittest.TestCase):
    def setUp(self):
        self.ns = AttendeeAliasTests().configure(
            10, "alice01", SHAREPOINT_SITE_URL="", SHAREPOINT_APP_CLIENT_ID="",
            AZURE_STORAGE_RESOURCE_ID="", AZURE_STORAGE_BLOB_ENDPOINT="",
            APIM_GATEWAY_URL="", BROKER_APP_CLIENT_ID="",
            AZURE_AI_EMBEDDING_DEPLOYMENT_NAME="", WEBIQ_KB_TOOLS="auto",
        )
        self.ns.update(requests=Mock(), search_token=lambda: "synthetic-search-token")
        self.http = self.ns["requests"]
        self.http.put.return_value = http_response(200, {})
        self.http.delete.return_value = http_response(204, {})
        self.run_cell("question")

    def run_cell(self, cell_id, replacements=None):
        source = code_cell(NOTEBOOK, cell_id)
        for old, new in (replacements or {}).items():
            self.assertIn(old, source)
            source = source.replace(old, new)
        with patch("builtins.print") as output:
            exec(source, self.ns)
        return output

    def schema(self, notebook="05-blob-content-understanding.ipynb"):
        namespace = {
            **self.ns, "index_name": "existing", "embedding_dimensions": 1536,
            "semantic_configuration": "chunk-semantic",
            "embedding_endpoint": self.ns["aoai_endpoint"],
            "embedding_deployment": "workshop-embedding",
            "embedding_model": "text-embedding-3-small", "search_request": Mock(),
            "requests": Mock(), "check_response": Mock(),
        }
        namespace["requests"].put.return_value = http_response(200, {})
        with patch("builtins.print"):
            exec(code_cell(notebook, "create-index"), namespace)
        return namespace["index"]

    def ready(self, schema=None, count=3):
        self.http.get.return_value = http_response(200, schema or self.schema())
        self.http.post.return_value = http_response(200, {"@odata.count": count})

    def evidence(self):
        return {
            "response": [{"content": [{"text": "Grounded answer [ref_id:0] [ref_id:1]"}]}],
            "activity": [
                {"type": "searchIndex", "knowledgeSourceName": "alice01-nb10-blob", "count": 1},
                {"type": "mcpServer", "knowledgeSourceName": "alice01-nb10-webiq", "count": 1},
            ],
            "references": [
                {"id": "0", "sourceData": {"chunk": "Internal receipt policy", "title": "Policy",
                                         "source/url": "https://storage.example/policy"}},
                {"id": "1", "sourceData": {"content": json.dumps({
                    "title": "Public guidelines", "url": "https://example.com/guidelines",
                    "content": "Public receipt guidance",
                })}},
            ],
        }

    def test_schema_validation_and_nonempty_query_are_read_only_for_both_blob_tracks(self):
        for notebook, index in (
            ("04-blob-indexer-skills.ipynb", "alice01-blob-chunks"),
            ("05-blob-content-understanding.ipynb", "alice01-cu-chunks"),
        ):
            with self.subTest(notebook=notebook):
                self.ns["blob_index_name"] = index
                self.ready(self.schema(notebook))
                self.run_cell("verify-index")
                self.assertIn(f"/indexes/{index}?", self.http.get.call_args.args[0])
                call = self.http.post.call_args
                self.assertIn(f"/indexes/{index}/docs/search?", call.args[0])
                self.assertEqual(call.kwargs["json"], {"search": "*", "count": True, "top": 0})
                self.assertEqual(call.kwargs["headers"],
                                 {"Authorization": "Bearer synthetic-search-token"})
                self.http.put.assert_not_called()
                self.http.delete.assert_not_called()

    def test_missing_index_and_invalid_fields_or_semantic_configuration_fail(self):
        self.http.get.return_value = http_response(404, {})
        with self.assertRaisesRegex(RuntimeError, "Complete notebook 05"):
            self.run_cell("verify-index")
        self.http.post.assert_not_called()
        for defect in ("missing", "not-retrievable", "not-searchable", "semantic"):
            schema = self.schema()
            if defect == "missing":
                schema["fields"] = []
            elif defect == "not-retrievable":
                next(f for f in schema["fields"] if f["name"] == "source")["fields"][0]["retrievable"] = False
            elif defect == "not-searchable":
                next(f for f in schema["fields"] if f["name"] == "chunk")["searchable"] = False
            else:
                schema["semantic"]["configurations"] = []
            self.ready(schema)
            with self.subTest(defect=defect), self.assertRaises(RuntimeError):
                self.run_cell("verify-index")
        self.http.put.assert_not_called()

    def test_empty_invalid_or_inaccessible_chunks_are_not_reported_ready(self):
        for count in (0, -1, None, "3", True):
            self.ready(count=count)
            with self.subTest(count=count), self.assertRaisesRegex(RuntimeError, "No chunks are visible"):
                self.run_cell("verify-index")
        self.ready()
        self.http.post.return_value = http_response(403, {})
        with self.assertRaises(requests.HTTPError):
            self.run_cell("verify-index")
        self.http.put.assert_not_called()

    def test_exact_two_sources_use_existing_blob_contract_and_stored_web_only_auth(self):
        self.run_cell("create-blob-source")
        blob = self.http.put.call_args.kwargs["json"]
        self.assertEqual(blob["kind"], "searchIndex")
        self.assertEqual(blob["searchIndexParameters"], {
            "searchIndexName": "alice01-cu-chunks",
            "semanticConfigurationName": "chunk-semantic",
            "searchFields": [{"name": "chunk"}],
            "sourceDataFields": [{"name": n} for n in
                                ("chunk_id", "chunk", "title", "source/file_name", "source/url")],
        })
        output = self.run_cell("create-webiq-source")
        web = self.http.put.call_args.kwargs["json"]
        self.assertEqual(web["kind"], "mcpServer")
        self.assertEqual(web["mcpServerParameters"]["serverURL"], "https://api.microsoft.ai/v3/mcp")
        self.assertEqual(web["mcpServerParameters"]["authentication"], {
            "kind": "storedHeaders",
            "storedHeadersParameters": {"headers": {"x-apikey": "synthetic-test-key"}},
        })
        self.assertEqual([t["name"] for t in web["mcpServerParameters"]["tools"]], ["web"])
        self.assertIn("contentFormat to passage", web["description"])
        self.assertNotIn("synthetic-test-key", str(output.call_args_list))
        self.run_cell("create-knowledge-base")
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["knowledgeSources"],
                         [{"name": "alice01-nb10-blob"}, {"name": "alice01-nb10-webiq"}])
        self.assertEqual(body["outputMode"], "answerSynthesis")
        self.assertEqual(body["retrievalReasoningEffort"], {"kind": "low"})
        self.assertEqual(body["models"], [{
            "kind": "azureOpenAI",
            "azureOpenAIParameters": {
                "resourceUri": "https://workshop.openai.azure.com",
                "deploymentId": "workshop-chat", "modelName": "gpt-5-mini",
            },
        }])
        self.assertIn("Never send internal document text to Web IQ", body["retrievalInstructions"])
        urls = [c.args[0] for c in self.http.put.call_args_list]
        self.assertEqual(urls, [
            "https://workshop.search.windows.net/knowledgesources/alice01-nb10-blob?api-version=2026-08-01-preview",
            "https://workshop.search.windows.net/knowledgesources/alice01-nb10-webiq?api-version=2026-08-01-preview",
            "https://workshop.search.windows.net/knowledgebases/alice01-nb10-kb?api-version=2026-08-01-preview",
        ])
        self.assertNotIn("sharepoint", json.dumps([blob, web, body]).lower())
        self.assertNotIn("ingestionParameters", json.dumps(blob))
        self.http.get.assert_not_called()

    def test_source_specific_and_mixed_requests_display_reference_urls_and_activity_in_both_modes(self):
        expected = {
            "ask-blob": [("alice01-nb10-blob", "searchIndex")],
            "ask-web": [("alice01-nb10-webiq", "mcpServer")],
            "ask-mixed": [("alice01-nb10-blob", "searchIndex"), ("alice01-nb10-webiq", "mcpServer")],
        }
        self.http.post.return_value = http_response(200, self.evidence())
        for mode in ("answerSynthesis", "extractiveData"):
            self.ns["output_mode"] = mode
            for cell in QUERIES:
                with self.subTest(mode=mode, cell=cell):
                    output = self.run_cell(cell)
                    call = self.http.post.call_args
                    self.assertIn("/knowledgebases/alice01-nb10-kb/retrieve?", call.args[0])
                    self.assertEqual(call.kwargs["headers"],
                                     {"Authorization": "Bearer synthetic-search-token"})
                    body = call.kwargs["json"]
                    self.assertEqual(body["outputMode"], mode)
                    self.assertEqual(body["knowledgeSourceParams"], [
                        {"knowledgeSourceName": name, "kind": kind,
                         "includeReferences": True, "includeReferenceSourceData": True}
                        for name, kind in expected[cell]
                    ])
                    self.assertTrue(body["includeActivity"])
                    self.assertNotIn("synthetic-test-key", str(call))
                    printed = str(output.call_args_list)
                    for evidence in ("Internal receipt policy", "Public receipt guidance",
                                     "https://storage.example/policy", "https://example.com/guidelines",
                                     "alice01-nb10-blob", "alice01-nb10-webiq"):
                        self.assertIn(evidence, printed)

    def test_http_source_and_partial_failures_do_not_report_success_or_expose_credentials(self):
        for cell in QUERIES:
            for status, activity in (
                (200, [{"error": {"message": "auth_missing_token synthetic-test-key synthetic-search-token"}}]),
                (206, []),
                (403, []),
            ):
                result = self.evidence()
                result["activity"] = activity
                response = http_response(status, result)
                response.text = "Rejected synthetic-test-key synthetic-search-token"
                self.http.post.return_value = response
                with self.subTest(cell=cell, status=status), patch("builtins.print") as output:
                    with self.assertRaises(requests.HTTPError if status == 403 else RuntimeError):
                        exec(code_cell(NOTEBOOK, cell), self.ns)
                    printed = str(output.call_args_list)
                    self.assertNotIn("synthetic-test-key", printed)
                    self.assertNotIn("synthetic-search-token", printed)
                    if activity:
                        self.assertIn("auth_missing_token", printed)

    def test_empty_or_malformed_reference_data_has_honest_diagnostics(self):
        for references in ([], [{"id": "0", "sourceData": {"content": "not JSON", "url": "https://example.com"}}]):
            result = self.evidence()
            result["references"] = references
            self.http.post.return_value = http_response(200, result)
            output = self.run_cell("ask-web")
            printed = str(output.call_args_list)
            self.assertIn("No references returned" if not references else "https://example.com", printed)

    def test_blob_complex_source_metadata_url_is_visible(self):
        result = self.evidence()
        result["references"] = [{"id": "0", "sourceData": {
            "chunk": "Internal receipt policy", "source": {"url": "https://storage.example/nested"},
        }}]
        self.http.post.return_value = http_response(200, result)
        output = self.run_cell("ask-blob")
        self.assertIn("https://storage.example/nested", str(output.call_args_list))

    def test_credentials_are_redacted_in_answers_and_reference_data_too(self):
        result = self.evidence()
        result["response"][0]["content"][0]["text"] = "synthetic-test-key synthetic-search-token"
        result["references"][0]["sourceData"] = {
            "title": "synthetic-test-key", "url": "https://example.com/synthetic-search-token",
            "chunk": "synthetic-test-key synthetic-search-token",
        }
        self.http.post.return_value = http_response(200, result)
        output = self.run_cell("ask-mixed")
        self.assertNotIn("synthetic-test-key", str(output.call_args_list))
        self.assertNotIn("synthetic-search-token", str(output.call_args_list))
        self.assertIn("<redacted>", str(output.call_args_list))

    def test_cleanup_is_opt_in_and_deletes_only_independent_definitions(self):
        self.run_cell("cleanup")
        self.http.delete.assert_not_called()
        self.run_cell("cleanup", {"CLEAN_UP = False": "CLEAN_UP = True"})
        self.assertEqual([c.args[0] for c in self.http.delete.call_args_list], [
            "https://workshop.search.windows.net/knowledgebases/alice01-nb10-kb?api-version=2026-08-01-preview",
            "https://workshop.search.windows.net/knowledgesources/alice01-nb10-webiq?api-version=2026-08-01-preview",
            "https://workshop.search.windows.net/knowledgesources/alice01-nb10-blob?api-version=2026-08-01-preview",
        ])


if __name__ == "__main__":
    unittest.main()
