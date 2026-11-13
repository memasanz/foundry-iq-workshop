import json
import unittest
from unittest.mock import Mock, patch

import nbformat

import test_notebooks
from test_notebooks import ROOT, code_cell, http_response

KB = "09-foundry-iq-knowledge-base.ipynb"
QUERIES = ("ask-internal", "ask-web", "ask-sharepoint", "ask-mixed")


def kb_namespace():
    return {
        "requests": Mock(),
        "json": json,
        "search_token": lambda: "search-user-token",
        "search_endpoint": "https://workshop.search.windows.net",
        "api_version": "2026-08-01-preview",
        "blob_index_name": "alice01-blob-chunks",
        "sharepoint_index_name": "alice01-nb7-index",
        "aoai_endpoint": "https://account.openai.azure.com",
        "chat_deployment": "workshop-model",
        "chat_model": "gpt-5-mini",
        "webiq_api_key": "webiq-secret",
        "webiq_tools": ["web"],
        "prefix": "alice01",
        "blob_source_name": "alice01-kb-blob",
        "sharepoint_source_name": "alice01-kb-sharepoint",
        "webiq_source_name": "alice01-kb-webiq",
        "knowledge_base_name": "alice01-kb",
    }


def run(cell_id, ns, replacements=None):
    source = code_cell(KB, cell_id)
    for old, new in (replacements or {}).items():
        assert old in source, old
        source = source.replace(old, new)
    with patch("builtins.print"):
        exec(source, ns)


class KnowledgeBaseNotebookTests(unittest.TestCase):
    def setUp(self):
        self.ns = kb_namespace()
        self.http = self.ns["requests"]
        for method in ("put", "post", "get", "delete"):
            getattr(self.http, method).return_value = http_response(200, {})

    def test_blob_source_references_existing_index_and_metadata(self):
        run("create-blob-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        parameters = body["searchIndexParameters"]
        self.assertEqual(body["kind"], "searchIndex")
        self.assertEqual(parameters["searchIndexName"], "alice01-blob-chunks")
        self.assertEqual(parameters["semanticConfigurationName"], "chunk-semantic")
        self.assertEqual(parameters["searchFields"], [{"name": "chunk"}])
        self.assertEqual([f["name"] for f in parameters["sourceDataFields"]],
                         ["chunk_id", "chunk", "title", "source/file_name", "source/url"])
        self.assertNotIn("ingestionParameters", json.dumps(body))
        self.ns["blob_index_name"] = "alice01-cu-chunks"
        run("create-blob-source", self.ns)
        self.assertEqual(self.http.put.call_args.kwargs["json"]["searchIndexParameters"]["searchIndexName"],
                         "alice01-cu-chunks")

    def test_sharepoint_source_references_existing_acl_index(self):
        run("create-sharepoint-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["kind"], "searchIndex")
        parameters = body["searchIndexParameters"]
        self.assertEqual(parameters["searchIndexName"], "alice01-nb7-index")
        self.assertEqual(parameters["semanticConfigurationName"], "semantic-config")
        self.assertEqual(parameters["searchFields"], [{"name": "content"}])
        self.assertEqual([f["name"] for f in parameters["sourceDataFields"]],
                         ["id", "content", "sourceFile", "webUrl", "page", "pageTo"])
        self.assertNotIn("UserIds", json.dumps(body))
        self.assertNotIn("ingestionParameters", json.dumps(body))

    def test_configuration_needs_no_storage_ingestion_app_or_embedding_settings(self):
        namespace = test_notebooks.AttendeeAliasTests().configure(
            9, "alice01", AZURE_STORAGE_RESOURCE_ID="", AZURE_STORAGE_BLOB_ENDPOINT="",
            AZURE_STORAGE_CONTAINER_NAME="", SHAREPOINT_SITE_URL="",
            AZURE_AI_EMBEDDING_DEPLOYMENT_NAME="", SHAREPOINT_APP_CLIENT_ID="",
            SHAREPOINT_SEARCH_MI_CLIENT_ID="",
            WEBIQ_KB_TOOLS="auto",
        )
        self.assertEqual(namespace["blob_index_name"], "alice01-blob-chunks")
        self.assertEqual(namespace["sharepoint_index_name"], "alice01-nb7-index")
        self.assertEqual(namespace["webiq_tools"], ["web"])

    def test_webiq_source_stores_header_for_portal_discovery_without_printing_key(self):
        run("create-webiq-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["kind"], "mcpServer")
        self.assertEqual(body["mcpServerParameters"]["serverURL"], "https://api.microsoft.ai/v3/mcp")
        self.assertEqual(body["mcpServerParameters"]["authentication"], {
            "kind": "storedHeaders",
            "storedHeadersParameters": {"headers": {"x-apikey": "webiq-secret"}},
        })
        self.assertEqual([tool["name"] for tool in body["mcpServerParameters"]["tools"]], ["web"])
        with patch("builtins.print") as output:
            exec(code_cell(KB, "create-webiq-source"), self.ns)
        self.assertNotIn("webiq-secret", str(output.call_args_list))

    def test_knowledge_base_has_three_sources_and_low_effort(self):
        run("create-knowledge-base", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual([s["name"] for s in body["knowledgeSources"]],
                         ["alice01-kb-blob", "alice01-kb-sharepoint", "alice01-kb-webiq"])
        self.assertEqual(body["retrievalReasoningEffort"], {"kind": "low"})
        self.assertEqual(body["models"][0]["azureOpenAIParameters"]["deploymentId"], "workshop-model")

    def retrieve(self, cell_id, payload=None, status=200):
        self.http.post.return_value = http_response(status, payload or {
            "response": [{"content": [{"type": "text", "text": "answer"}]}],
            "references": [], "activity": [],
        })
        run(cell_id, self.ns)
        return self.http.post.call_args.kwargs

    def test_retrieve_headers_and_source_kinds(self):
        for cell_id in QUERIES:
            kwargs = self.retrieve(cell_id)
            headers = kwargs["headers"]
            self.assertEqual(headers["x-ms-query-source-authorization"], "search-user-token")
            self.assertEqual(headers["Authorization"], "Bearer search-user-token")
            self.assertEqual(set(headers), {"Authorization", "x-ms-query-source-authorization"})
            self.assertNotIn("webiq-secret", json.dumps(kwargs))
            self.assertNotIn("x-ms-enable-elevated-read", headers)
            kinds = {p["knowledgeSourceName"]: p["kind"] for p in kwargs["json"]["knowledgeSourceParams"]}
            self.assertEqual(kinds, {
                "alice01-kb-blob": "searchIndex",
                "alice01-kb-sharepoint": "searchIndex",
                "alice01-kb-webiq": "mcpServer",
            })

    def test_retrieve_raises_on_source_error_or_partial_result(self):
        for cell_id in QUERIES:
            for status, activity in (
                (200, [{"type": "mcp", "error": {"message": "x"}}]),
                (206, []),
            ):
                with self.subTest(cell=cell_id, status=status):
                    with self.assertRaises(RuntimeError):
                        self.retrieve(cell_id, {"response": [], "references": [], "activity": activity}, status)

    def index_schemas(self):
        def schema(notebook):
            namespace = {
                **self.ns, "index_name": "existing", "embedding_dimensions": 1536,
                "semantic_configuration": "chunk-semantic" if notebook.startswith("04") else "semantic-config",
                "embedding_endpoint": self.ns["aoai_endpoint"],
                "embedding_deployment": "workshop-embedding",
                "embedding_model": "text-embedding-3-small",
                "search_request": Mock(),
            }
            with patch("builtins.print"):
                exec(code_cell(notebook, "create-index"), namespace)
            return namespace["index"]
        schemas = [schema("04-blob-indexer-skills.ipynb"), schema("07-sharepoint-indexed.ipynb")]
        self.http.reset_mock()
        self.http.get.side_effect = [http_response(200, body) for body in schemas]
        self.http.post.return_value = http_response(200, {"@odata.count": 3})
        return schemas

    def test_index_checks_are_read_only_and_send_user_context_only_for_sharepoint(self):
        self.index_schemas()
        run("verify-indexes", self.ns)
        self.http.put.assert_not_called()
        self.http.delete.assert_not_called()
        posts = self.http.post.call_args_list
        self.assertEqual(len(posts), 2)
        self.assertNotIn("x-ms-query-source-authorization", posts[0].kwargs["headers"])
        self.assertEqual(posts[1].kwargs["headers"]["x-ms-query-source-authorization"], "search-user-token")
        for call in posts:
            self.assertIn("/docs/search?", call.args[0])
            self.assertEqual(call.kwargs["json"], {"search": "*", "count": True, "top": 0})

    def test_missing_index_reports_upstream_notebook_without_creating_one(self):
        self.http.get.return_value = http_response(404, {})
        with self.assertRaisesRegex(RuntimeError, "Complete notebook 04 or 05"):
            run("verify-indexes", self.ns)
        self.http.put.assert_not_called()
        self.http.post.assert_not_called()

    def test_invalid_schema_or_missing_sharepoint_acls_is_rejected(self):
        for failure in ("content", "semantic", "acl"):
            with self.subTest(failure=failure):
                schemas = self.index_schemas()
                if failure == "content":
                    schemas[0]["fields"] = []
                elif failure == "semantic":
                    schemas[0]["semantic"]["configurations"] = []
                else:
                    schemas[1]["permissionFilterOption"] = "disabled"
                with self.assertRaises(RuntimeError):
                    run("verify-indexes", self.ns)
                self.http.put.assert_not_called()

    def test_empty_or_inaccessible_index_is_not_reported_as_ready(self):
        self.index_schemas()
        self.http.post.return_value = http_response(200, {"@odata.count": 0})
        with self.assertRaisesRegex(RuntimeError, "No chunks are visible"):
            run("verify-indexes", self.ns)
        self.http.put.assert_not_called()

    def test_notebook_only_creates_sources_and_knowledge_base(self):
        self.index_schemas()
        for cell in nbformat.read(ROOT / "notebooks" / KB, as_version=4).cells:
            if cell.cell_type == "code" and cell.id != "configuration":
                if cell.id.startswith("ask-"):
                    self.http.post.return_value = http_response(200, {"response": [], "activity": []})
                run(cell.id, self.ns)
        urls = [call.args[0] for call in self.http.put.call_args_list]
        self.assertEqual(len(urls), 4)
        self.assertTrue(all("/knowledgesources/" in url or "/knowledgebases/" in url for url in urls))
        self.http.delete.assert_not_called()

    def test_cleanup_disabled_by_default(self):
        run("cleanup", self.ns)
        self.http.delete.assert_not_called()

    def test_cleanup_deletes_knowledge_base_first(self):
        run("cleanup", self.ns, {"CLEAN_UP = False": "CLEAN_UP = True"})
        urls = [call.args[0] for call in self.http.delete.call_args_list]
        self.assertIn("/knowledgebases/alice01-kb", urls[0])
        self.assertEqual(len(urls), 4)
        self.assertTrue(all("/knowledgebases/" in url or "/knowledgesources/" in url for url in urls))
        self.http.get.assert_not_called()


if __name__ == "__main__":
    unittest.main()
