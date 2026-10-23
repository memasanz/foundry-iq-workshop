import json
import os
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch
from uuid import UUID

from test_notebooks import code_cell, http_response

KB = "06-foundry-iq-knowledge-base.ipynb"


def kb_namespace():
    return {
        "requests": Mock(),
        "json": json,
        "os": os,
        "UUID": UUID,
        "time": SimpleNamespace(sleep=Mock()),
        "search_token": lambda: "search-user-token",
        "search_endpoint": "https://workshop.search.windows.net",
        "api_version": "2026-08-01-preview",
        "storage_id": "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st",
        "container_name": "workshop-documents",
        "blob_folder": "alice01/kb",
        "aoai_endpoint": "https://account.openai.azure.com",
        "chat_deployment": "workshop-model",
        "chat_model": "gpt-5-mini",
        "embedding_deployment": "workshop-embedding",
        "embedding_model": "text-embedding-3-small",
        "site_url": "https://contoso.sharepoint.com/sites/workshop",
        "tenant_id": "11111111-1111-1111-1111-111111111111",
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

    def test_blob_source_uses_identity_and_embedding(self):
        run("create-blob-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        parameters = body["azureBlobParameters"]
        self.assertEqual(body["kind"], "azureBlob")
        self.assertEqual(parameters["connectionString"], f"ResourceId={self.ns['storage_id']};")
        self.assertEqual(parameters["folderPath"], "alice01/kb")
        ingestion = parameters["ingestionParameters"]
        self.assertEqual(ingestion["embeddingModel"]["azureOpenAIParameters"]["deploymentId"], "workshop-embedding")
        self.assertIsNone(ingestion["chatCompletionModel"])
        self.assertNotIn("AccountKey", json.dumps(body))

    def test_sharepoint_indexed_has_no_secret_and_keeps_acls(self):
        env = {
            "SHAREPOINT_APP_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
            "SHAREPOINT_SEARCH_MI_CLIENT_ID": "33333333-3333-3333-3333-333333333333",
        }
        with patch.dict(os.environ, env):
            run("create-sharepoint-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["kind"], "indexedSharePoint")
        self.assertEqual(self.ns["sharepoint_kind"], "indexedSharePoint")
        parameters = body["indexedSharePointParameters"]
        self.assertNotIn("ClientSecret", parameters["connectionString"])
        self.assertIn("FederatedCredentialApplicationId=3333", parameters["connectionString"])
        self.assertEqual(parameters["ingestionParameters"]["ingestionPermissionOptions"], ["userIds", "groupIds"])

    def test_sharepoint_indexed_rejects_same_app_and_identity(self):
        same = "22222222-2222-2222-2222-222222222222"
        with patch.dict(os.environ, {"SHAREPOINT_APP_CLIENT_ID": same, "SHAREPOINT_SEARCH_MI_CLIENT_ID": same}):
            with self.assertRaises(ValueError):
                run("create-sharepoint-source", self.ns)

    def test_sharepoint_remote_mode(self):
        run("create-sharepoint-source", self.ns, {"SHAREPOINT_KB_MODE = 'indexed'": "SHAREPOINT_KB_MODE = 'remote'"})
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["kind"], "remoteSharePoint")
        self.assertEqual(self.ns["sharepoint_kind"], "remoteSharePoint")
        self.assertIn(self.ns["site_url"], body["remoteSharePointParameters"]["filterExpression"])

    def test_webiq_source_does_not_store_key(self):
        run("create-webiq-source", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual(body["kind"], "mcpServer")
        self.assertEqual(body["mcpServerParameters"]["serverURL"], "https://api.microsoft.ai/v3/mcp")
        self.assertIsNone(body["mcpServerParameters"]["authentication"])
        self.assertNotIn("webiq-secret", json.dumps(body))

    def test_knowledge_base_has_three_sources_and_low_effort(self):
        run("create-knowledge-base", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual([s["name"] for s in body["knowledgeSources"]],
                         ["alice01-kb-blob", "alice01-kb-sharepoint", "alice01-kb-webiq"])
        self.assertEqual(body["retrievalReasoningEffort"], {"kind": "low"})
        self.assertEqual(body["models"][0]["azureOpenAIParameters"]["deploymentId"], "workshop-model")

    def retrieve(self, cell_id, payload=None, status=200):
        self.ns["sharepoint_kind"] = "indexedSharePoint"
        self.http.post.return_value = http_response(status, payload or {
            "response": [{"content": [{"type": "text", "text": "answer"}]}],
            "references": [], "activity": [],
        })
        run(cell_id, self.ns)
        return self.http.post.call_args.kwargs

    def test_retrieve_headers_and_source_kinds(self):
        for cell_id in ("ask-internal", "ask-web", "ask-mixed"):
            kwargs = self.retrieve(cell_id)
            headers = kwargs["headers"]
            self.assertEqual(headers["x-ms-query-source-authorization"], "search-user-token")
            self.assertEqual(headers["alice01-kb-webiq-header-name"], "x-apikey")
            self.assertEqual(headers["alice01-kb-webiq-header-value"], "webiq-secret")
            self.assertNotIn("x-ms-enable-elevated-read", headers)
            kinds = {p["knowledgeSourceName"]: p["kind"] for p in kwargs["json"]["knowledgeSourceParams"]}
            self.assertEqual(kinds, {
                "alice01-kb-blob": "azureBlob",
                "alice01-kb-sharepoint": "indexedSharePoint",
                "alice01-kb-webiq": "mcpServer",
            })

    def test_retrieve_raises_on_source_error(self):
        payload = {"response": [], "references": [], "activity": [{"type": "mcp", "error": {"message": "x"}}]}
        with self.assertRaises(RuntimeError):
            self.retrieve("ask-web", payload)

    def monitor(self, statuses):
        def get(url, **kwargs):
            if "/knowledgesources/" in url:
                name = url.split("/knowledgesources/")[1].split("?")[0]
                key = "azureBlobParameters" if name.endswith("blob") else "indexedSharePointParameters"
                return http_response(200, {key: {"createdResources": {"indexer": f"{name}-indexer"}}})
            return http_response(200, {"lastResult": statuses.pop(0) if len(statuses) > 1 else statuses[0]})
        self.http.get.side_effect = get
        run("monitor-ingestion", self.ns)

    def test_monitor_success(self):
        self.monitor([{"status": "success", "itemsProcessed": 3}])

    def test_monitor_failure(self):
        with self.assertRaisesRegex(RuntimeError, "ingestion failed"):
            self.monitor([{"status": "success", "itemsFailed": 1}])

    def test_monitor_timeout(self):
        with self.assertRaises(TimeoutError):
            self.monitor([{"status": "inProgress"}])

    def test_cleanup_disabled_by_default(self):
        run("cleanup", self.ns)
        self.http.delete.assert_not_called()

    def test_cleanup_deletes_knowledge_base_first(self):
        self.http.get.return_value = http_response(200, {"value": []})
        run("cleanup", self.ns, {"CLEAN_UP = False": "CLEAN_UP = True"})
        urls = [call.args[0] for call in self.http.delete.call_args_list]
        self.assertIn("/knowledgebases/alice01-kb", urls[0])
        self.assertEqual(len(urls), 4)


if __name__ == "__main__":
    unittest.main()
