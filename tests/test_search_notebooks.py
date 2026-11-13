import json
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch
from urllib.parse import quote
from uuid import UUID

import nbformat
import requests

from test_notebooks import ROOT, code_cell, http_response


BLOB = "04-blob-indexer-skills.ipynb"
CU = "05-blob-content-understanding.ipynb"
PUSH = "06-push-chunks-vectors.ipynb"
SP_INDEXED = "07-sharepoint-indexed.ipynb"
SP_REMOTE = "08-sharepoint-remote.ipynb"


def search_namespace():
    namespace = {
        "requests": Mock(),
        "json": json,
        "time": SimpleNamespace(sleep=Mock()),
        "search_token": lambda: "search-user-token",
        "storage_token": lambda: "storage-user-token",
        "embedding_token": lambda: "embedding-user-token",
        "graph_token": lambda: "graph-admin-token",
        "arm_token": lambda: "arm-admin-token",
        "search_endpoint": "https://workshop.search.windows.net",
        "storage_endpoint": "https://workshop.blob.core.windows.net",
        "storage_id": "/subscriptions/test/resourceGroups/workshop/providers/Microsoft.Storage/storageAccounts/workshop",
        "container_name": "workshop-documents",
        "blob_prefix": "alice01/blob/",
        "index_name": "alice01-index",
        "data_source_name": "alice01-source",
        "skillset_name": "alice01-skills",
        "indexer_name": "alice01-indexer",
        "remote_source_name": "alice01-remote-source",
        "remote_base_name": "alice01-remote-kb",
        "api_version": "2026-08-01-preview",
        "embedding_endpoint": "https://workshop.openai.azure.com",
        "foundry_endpoint": "https://workshop.services.ai.azure.com",
        "embedding_deployment": "workshop-embedding",
        "embedding_model": "text-embedding-3-small",
        "embedding_dimensions": 1536,
        "semantic_configuration": "chunk-semantic",
        "site_url": "https://contoso.sharepoint.com/sites/workshop",
        "tenant_id": "11111111-1111-1111-1111-111111111111",
        "sp_app_client_id": "22222222-2222-2222-2222-222222222222",
        "sp_mi_client_id": "33333333-3333-3333-3333-333333333333",
        "previous_run_start": None,
        "UUID": UUID,
        "root": ROOT,
        "quote": quote,
        "sample_files": list((ROOT / "data" / "search").glob("*.txt")),
    }
    http = namespace["requests"]
    http.request.side_effect = lambda method, url, **kwargs: getattr(http, method.lower())(url, **kwargs)
    exec(code_cell(SP_INDEXED, "search-client"), namespace)
    return namespace


def run_cell(notebook, cell_id, ns, replacements=None):
    source = code_cell(notebook, cell_id)
    for old, new in (replacements or {}).items():
        source = source.replace(old, new)
    with patch("builtins.print") as output:
        exec(source, ns)
    return output


class SearchCellTests(unittest.TestCase):
    def setUp(self):
        self.ns = search_namespace()
        self.http = self.ns["requests"]
        for method in ("put", "post", "get", "delete"):
            getattr(self.http, method).return_value = http_response(200, {})

    def test_blob_source_uses_identity_and_attendee_folder(self):
        run_cell(BLOB, "create-data-source", self.ns)
        payload = self.http.put.call_args.kwargs["json"]
        self.assertEqual(payload["type"], "azureblob")
        self.assertEqual(payload["container"], {
            "name": "workshop-documents", "query": "alice01/blob/",
        })
        self.assertEqual(payload["credentials"]["connectionString"],
                         f"ResourceId={self.ns['storage_id']};")

    def test_blob_upload_is_to_storage_not_search(self):
        from datetime import datetime, timezone
        from email.utils import format_datetime

        self.ns.update(datetime=datetime, timezone=timezone, format_datetime=format_datetime)
        run_cell(BLOB, "upload-blobs", self.ns)
        self.assertEqual(self.http.put.call_count, 2)
        for call in self.http.put.call_args_list:
            self.assertTrue(call.args[0].startswith(
                "https://workshop.blob.core.windows.net/workshop-documents/alice01/blob/"))
            self.assertEqual(call.kwargs["headers"]["x-ms-blob-type"], "BlockBlob")
            self.assertEqual(call.kwargs["headers"]["Authorization"], "Bearer storage-user-token")
            self.assertGreater(len(call.kwargs["data"]), 1000)
        self.http.post.assert_not_called()

    def test_embedding_skills_and_chunk_projection_contract(self):
        run_cell(BLOB, "create-index", self.ns)
        fields = {field["name"]: field for field in self.ns["index"]["fields"]}
        self.assertEqual(fields["source"]["type"], "Edm.ComplexType")
        self.assertTrue(fields["parent_id"]["filterable"])
        run_cell(BLOB, "create-skillset", self.ns)
        skills = self.ns["skillset"]["skills"]
        self.assertEqual([skill["@odata.type"] for skill in skills], [
            "#Microsoft.Skills.Text.SplitSkill",
            "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
            "#Microsoft.Skills.Util.ShaperSkill",
        ])
        split = skills[0]
        self.assertEqual((split["maximumPageLength"], split["pageOverlapLength"]), (1000, 200))
        projections = self.ns["skillset"]["indexProjections"]
        self.assertEqual(projections["parameters"]["projectionMode"], "skipIndexingParentDocuments")
        selector = projections["selectors"][0]
        self.assertEqual(selector["parentKeyFieldName"], "parent_id")
        self.assertEqual(selector["mappings"], [
            {"name": "chunk", "source": "/document/chunks/*"},
            {"name": "text_vector", "source": "/document/chunks/*/text_vector"},
            {"name": "title", "source": "/document/metadata_storage_name"},
            {"name": "source", "source": "/document/provenance"},
        ])
        embedding = skills[1]
        self.assertEqual(embedding["context"], selector["sourceContext"])
        self.assertEqual(embedding["inputs"], [{"name": "text", "source": selector["sourceContext"]}])
        self.assertEqual(embedding["outputs"], [{"name": "embedding", "targetName": "text_vector"}])
        self.assertEqual(embedding["dimensions"], fields["text_vector"]["dimensions"])
        run_cell(BLOB, "create-index", self.ns)
        vectorizer = self.ns["index"]["vectorSearch"]["vectorizers"][0]["azureOpenAIParameters"]
        for name in ("resourceUri", "deploymentId", "modelName"):
            self.assertEqual(embedding[name], vectorizer[name])
        self.assertNotIn("apiKey", embedding)
        self.assertNotIn("authIdentity", embedding)
        self.assertNotIn("cognitiveServices", self.ns["skillset"])

    def test_content_understanding_extracts_file_and_embeds_section_content_with_page_projections(self):
        run_cell(CU, "create-index", self.ns)
        fields = {field["name"]: field for field in self.ns["index"]["fields"]}
        run_cell(CU, "create-skillset", self.ns)
        payload = self.http.put.call_args.kwargs["json"]
        extraction, embedding, shaper = payload["skills"]
        self.assertEqual(extraction["@odata.type"], "#Microsoft.Skills.Util.ContentUnderstandingSkill")
        self.assertEqual(extraction["inputs"], [{"name": "file_data", "source": "/document/file_data"}])
        self.assertEqual(extraction["outputs"], [{"name": "text_sections", "targetName": "text_sections"}])
        self.assertIn("locationMetadata", extraction["extractionOptions"])
        self.assertNotIn("#Microsoft.Skills.Text.SplitSkill",
                         [skill["@odata.type"] for skill in payload["skills"]])
        selector = payload["indexProjections"]["selectors"][0]
        self.assertEqual(embedding["context"], selector["sourceContext"])
        mappings = {item["name"]: item["source"] for item in selector["mappings"]}
        self.assertEqual(embedding["inputs"], [{"name": "text", "source": mappings["chunk"]}])
        self.assertEqual(mappings["chunk"], selector["sourceContext"] + "/content")
        self.assertEqual(embedding["dimensions"], fields["text_vector"]["dimensions"])
        for field, name in (("page_from", "pageNumberFrom"), ("page_to", "pageNumberTo"),
                            ("ordinal_position", "ordinalPosition"), ("location_source", "source")):
            self.assertIn(field, fields)
            self.assertEqual(mappings[field], selector["sourceContext"] + "/locationMetadata/" + name)
        self.assertNotIn("chunk_id", mappings)
        self.assertNotIn("parent_id", mappings)
        self.assertEqual(payload["cognitiveServices"], {
            "@odata.type": "#Microsoft.Azure.Search.AIServicesByIdentity",
            "subdomainUrl": self.ns["foundry_endpoint"], "identity": None,
        })
        self.assertNotIn("apiKey", embedding)
        self.assertNotIn("authIdentity", embedding)
        self.assertEqual(shaper["@odata.type"], "#Microsoft.Skills.Util.ShaperSkill")

    def test_content_understanding_uploads_pdf_and_enables_original_file_input(self):
        from datetime import datetime, timezone
        from email.utils import format_datetime

        self.ns.update(datetime=datetime, timezone=timezone, format_datetime=format_datetime,
                       blob_prefix="alice01/content-understanding/")
        run_cell(CU, "upload-blobs", self.ns)
        upload = self.http.put.call_args
        self.assertTrue(upload.args[0].endswith(
            "/workshop-documents/alice01/content-understanding/travel-handbook.pdf"))
        self.assertEqual(upload.kwargs["headers"]["Content-Type"], "application/pdf")
        self.assertTrue(upload.kwargs["data"].startswith(b"%PDF-"))
        self.http.get.return_value = http_response(404, {})
        run_cell(CU, "create-indexer", self.ns)
        configuration = self.http.put.call_args.kwargs["json"]["parameters"]["configuration"]
        self.assertTrue(configuration["allowSkillsetToReadFileData"])
        self.assertEqual(configuration["dataToExtract"], "allMetadata")
        self.assertEqual(configuration["indexedFileNameExtensions"], ".pdf")

    def test_content_understanding_keeps_valid_page_ranges_and_rejects_missing_or_invalid_locations(self):
        valid = {"chunk_id": "sample", "page_from": 1, "page_to": 2}
        self.http.post.return_value = http_response(200, {"value": [valid], "@odata.count": 1})
        run_cell(CU, "inspect-chunks", self.ns)
        self.assertEqual(self.ns["chunks"], [valid])
        for pages in ({}, {"page_from": 2, "page_to": 1}, {"page_from": 1, "page_to": 3}):
            with self.subTest(pages=pages):
                self.http.post.return_value = http_response(200, {"value": [pages]})
                with self.assertRaisesRegex(RuntimeError, "valid page ranges"):
                    run_cell(CU, "inspect-chunks", self.ns)

    def test_content_understanding_retrieval_rejects_empty_and_partial_results(self):
        for body in ({"value": []}, {"@search.semanticPartialResponseReason": "capacityOverloaded"}):
            self.http.post.return_value = http_response(200, body)
            with self.assertRaises(RuntimeError):
                run_cell(CU, "query-semantic-hybrid", self.ns)
        self.http.post.return_value = http_response(200, {"value": [{"chunk": "evidence"}]})
        run_cell(CU, "query-semantic-hybrid", self.ns)
        request = self.http.post.call_args.kwargs["json"]
        self.assertEqual(request["queryType"], "semantic")
        self.assertIn("page_from", request["select"])
        self.assertIn("page_to", request["select"])
        self.assertEqual(request["vectorQueries"][0]["kind"], "text")

    def test_content_understanding_cleanup_deletes_only_its_objects_and_sample(self):
        self.ns.update(
            blob_prefix="alice01/content-understanding/",
            sample_files=[ROOT / "data" / "search" / "travel-handbook.pdf"],
            index_name="alice01-cu-chunks", data_source_name="alice01-cu-source",
            skillset_name="alice01-cu-skills", indexer_name="alice01-cu-indexer",
        )
        run_cell(CU, "cleanup", self.ns)
        self.http.delete.assert_not_called()
        run_cell(CU, "cleanup", self.ns, {"CLEAN_UP = False": "CLEAN_UP = True"})
        self.assertEqual(self.http.delete.call_count, 5)
        for call in self.http.delete.call_args_list:
            self.assertTrue("-cu-" in call.args[0] or "/alice01/content-understanding/travel-handbook.pdf" in call.args[0])

    def test_both_vector_indexes_connect_profile_vectorizer_and_semantic_fields(self):
        self.ns["embedding_dimensions"] = 768
        schemas = []
        for notebook in (BLOB, PUSH):
            with self.subTest(notebook=notebook):
                run_cell(notebook, "create-index", self.ns)
                index = self.http.put.call_args.kwargs["json"]
                fields = {field["name"]: field for field in index["fields"]}
                vector_field = fields["text_vector"]
                self.assertEqual(vector_field["dimensions"], 768)
                vector_search = index["vectorSearch"]
                profile = vector_search["profiles"][0]
                self.assertEqual(vector_field["vectorSearchProfile"], profile["name"])
                self.assertEqual(profile["algorithm"], vector_search["algorithms"][0]["name"])
                self.assertEqual(profile["vectorizer"], vector_search["vectorizers"][0]["name"])
                parameters = vector_search["vectorizers"][0]["azureOpenAIParameters"]
                self.assertEqual(parameters, {
                    "resourceUri": self.ns["embedding_endpoint"],
                    "deploymentId": self.ns["embedding_deployment"],
                    "modelName": self.ns["embedding_model"],
                })
                semantic = index["semantic"]["configurations"][0]
                self.assertEqual(semantic["name"], index["semantic"]["defaultConfiguration"])
                for field in [semantic["prioritizedFields"]["titleField"],
                              *semantic["prioritizedFields"]["prioritizedContentFields"]]:
                    self.assertTrue(fields[field["fieldName"]]["searchable"])
                schemas.append(index)
        self.assertEqual(schemas[0], schemas[1])

    def test_same_prefix_gives_separate_indexes_and_correct_inference_identity(self):
        settings = {
            "AZURE_TENANT_ID": "11111111-1111-1111-1111-111111111111",
            "AZURE_SEARCH_ENDPOINT": self.ns["search_endpoint"],
            "AZURE_STORAGE_RESOURCE_ID": self.ns["storage_id"],
            "AZURE_STORAGE_BLOB_ENDPOINT": self.ns["storage_endpoint"],
            "AZURE_STORAGE_CONTAINER_NAME": self.ns["container_name"],
            "AZURE_AI_ACCOUNT_NAME": "workshop",
            "AZURE_AI_EMBEDDING_DEPLOYMENT_NAME": "workshop-embedding",
            "WORKSHOP_PREFIX": "alice01",
        }
        names = []
        with patch.dict(os.environ, settings, clear=True), patch("dotenv.load_dotenv"), \
                patch("azure.identity.AzureCliCredential") as credential, \
                patch("azure.identity.get_bearer_token_provider") as provider:
            for notebook in (BLOB, PUSH, CU):
                namespace = {}
                run_cell(notebook, "setup" if notebook == CU else "configuration", namespace)
                names.append(namespace["index_name"])
                self.assertEqual(namespace["embedding_endpoint"], self.ns["embedding_endpoint"])
            self.assertEqual(len(set(names)), 3)
            provider.assert_any_call(
                credential.return_value, "https://cognitiveservices.azure.com/.default",
            )

    def test_search_modes_are_distinct_and_keep_the_same_query(self):
        modes = {
            "query-index": (True, False, False),
            "query-vector": (False, True, False),
            "query-hybrid": (True, True, False),
            "query-semantic": (True, False, True),
            "query-semantic-hybrid": (True, True, True),
        }
        for notebook in (BLOB, PUSH):
            for cell_id, (keyword, vector, semantic) in modes.items():
                with self.subTest(notebook=notebook, cell=cell_id):
                    self.http.post.reset_mock()
                    run_cell(notebook, cell_id, self.ns)
                    self.http.post.assert_called_once()
                    call = self.http.post.call_args
                    self.assertIn(f"/indexes/{self.ns['index_name']}/docs/search?", call.args[0])
                    self.assertEqual(call.kwargs["headers"]["Authorization"], "Bearer search-user-token")
                    body = call.kwargs["json"]
                    self.assertEqual("search" in body, keyword)
                    self.assertEqual("vectorQueries" in body, vector)
                    self.assertEqual(body.get("queryType") == "semantic", semantic)
                    self.assertEqual(body["top"], 3)
                    if keyword:
                        self.assertEqual(body["search"], self.ns["question"])
                    if vector:
                        self.assertEqual(body["vectorQueries"], [{
                            "kind": "text", "text": self.ns["question"],
                            "fields": "text_vector", "k": 50,
                        }])
                    if semantic:
                        self.assertEqual(body["semanticConfiguration"], self.ns["semantic_configuration"])
                        self.assertEqual(body["captions"], "extractive")

    def test_semantic_partial_results_and_query_authorization_errors_surface(self):
        for notebook in (BLOB, PUSH):
            self.ns["question"] = "travel expenses"
            for cell in ("query-semantic", "query-semantic-hybrid"):
                for status, body in [
                    (206, {"value": []}),
                    (200, {"@search.semanticPartialResponseReason": "capacityOverloaded"}),
                ]:
                    self.http.post.return_value = http_response(status, body)
                    with self.assertRaisesRegex(RuntimeError, "Semantic ranking did not complete"):
                        run_cell(notebook, cell, self.ns)
            for status in (403, 429):
                self.http.post.return_value = http_response(status, {})
                with self.assertRaises(requests.HTTPError):
                    run_cell(notebook, "query-vector", self.ns)

    def test_blob_vector_verification_rejects_missing_or_wrong_size(self):
        for values in ([], [{"chunk_id": "one"}], [{"chunk_id": "one", "text_vector": [0.1]}]):
            self.http.post.return_value = http_response(200, {"value": values})
            with self.assertRaisesRegex(RuntimeError, "Missing or wrong-sized"):
                run_cell(BLOB, "verify-vectors", self.ns)
        self.http.post.return_value = http_response(200, {"value": [{
            "chunk_id": "one", "text_vector": [0.1] * self.ns["embedding_dimensions"],
        }]})
        output = run_cell(BLOB, "verify-vectors", self.ns)
        output.assert_called_once_with("one", "| vector dimensions:", self.ns["embedding_dimensions"])

    def test_push_chunking_preserves_all_text_overlap_and_stable_keys(self):
        run_cell(PUSH, "chunk-documents", self.ns)
        documents = self.ns["documents"]
        self.assertGreater(len(documents), len(self.ns["sample_files"]))
        self.assertEqual(len({item["chunk_id"] for item in documents}), len(documents))
        for path in self.ns["sample_files"]:
            chunks = [item["chunk"] for item in documents if item["parent_id"] == path.stem]
            reconstructed = chunks[0] + "".join(chunk[200:] for chunk in chunks[1:])
            self.assertEqual(reconstructed, path.read_text(encoding="utf-8"))
            self.assertTrue(all(0 < len(chunk) <= 1000 for chunk in chunks))
        for item in documents:
            self.assertEqual(item["@search.action"], "upload")
            self.assertEqual(item["source"]["url"], f"data/search/{item['title']}")
        run_cell(PUSH, "chunk-documents", self.ns)
        self.assertEqual(self.ns["documents"], documents)
        self.assertEqual(self.http.mock_calls, [])

    def test_push_chunking_avoids_redundant_tail_and_rejects_empty_files(self):
        with tempfile.TemporaryDirectory() as directory:
            self.ns["root"] = Path(directory)
            folder = Path(directory) / "data" / "search"
            folder.mkdir(parents=True)
            (folder / "short.txt").write_text("a" * 10, encoding="utf-8")
            for length, expected in ((1000, 1), (1001, 2), (1800, 2)):
                with self.subTest(length=length):
                    (folder / "long.txt").write_text("b" * length, encoding="utf-8")
                    run_cell(PUSH, "chunk-documents", self.ns)
                    chunks = [item for item in self.ns["documents"] if item["parent_id"] == "long"]
                    self.assertEqual(len(chunks), expected)
            (folder / "long.txt").write_text(" \n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Empty source"):
                run_cell(PUSH, "chunk-documents", self.ns)

    def test_push_embeddings_batch_and_map_response_indexes_not_response_order(self):
        self.ns["embedding_dimensions"] = 3
        self.ns["documents"] = [{"chunk": f"chunk-{number}"} for number in range(17)]
        self.http.post.side_effect = [
            http_response(200, {"data": [
                {"index": number, "embedding": [float(number)] * 3}
                for number in reversed(range(16))
            ]}),
            http_response(200, {"data": [{"index": 0, "embedding": [99.0] * 3}]}),
        ]
        run_cell(PUSH, "embed-documents", self.ns)
        self.assertEqual(self.http.post.call_count, 2)
        for call, count in zip(self.http.post.call_args_list, (16, 1)):
            self.assertIn("/openai/deployments/workshop-embedding/embeddings?", call.args[0])
            self.assertEqual(call.kwargs["headers"]["Authorization"], "Bearer embedding-user-token")
            self.assertEqual(len(call.kwargs["json"]["input"]), count)
            self.assertEqual(call.kwargs["json"]["dimensions"], 3)
        self.assertEqual(self.ns["documents"][4]["text_vector"], [4.0] * 3)
        self.assertEqual(self.ns["documents"][16]["text_vector"], [99.0] * 3)

    def test_push_embedding_failures_do_not_look_like_success(self):
        self.ns["documents"] = [{"chunk": "one"}, {"chunk": "two"}]
        for data in (
            [],
            [{"index": 0, "embedding": [0.1] * 1536}],
            [{"index": 0, "embedding": [0.1]}, {"index": 1, "embedding": [0.2]}],
            [{"index": 0, "embedding": []}, {"index": 0, "embedding": []}],
        ):
            with self.subTest(response_size=len(data)):
                self.http.post.return_value = http_response(200, {"data": data})
                with self.assertRaises(RuntimeError):
                    run_cell(PUSH, "embed-documents", self.ns)
        for status in (403, 429):
            self.http.post.return_value = http_response(status, {})
            with self.assertRaises(requests.HTTPError):
                run_cell(PUSH, "embed-documents", self.ns)

    def test_push_documents_checks_each_item_and_rejects_incomplete_embeddings(self):
        self.ns["documents"] = [
            {"chunk_id": "one", "text_vector": [0.1] * 1536},
            {"chunk_id": "two", "text_vector": [0.2] * 1536},
        ]
        for status, results in [
            (207, [{"key": "one", "status": True}, {"key": "two", "status": False}]),
            (200, [{"key": "one", "status": True}]),
            (200, [{"key": "one", "status": True}, {"key": "one", "status": True}]),
        ]:
            self.http.post.return_value = http_response(status, {"value": results})
            with self.assertRaisesRegex(RuntimeError, "Push was incomplete"):
                run_cell(PUSH, "push-documents", self.ns)
        self.http.post.return_value = http_response(200, {"value": [
            {"key": "two", "status": True}, {"key": "one", "status": True},
        ]})
        run_cell(PUSH, "push-documents", self.ns)
        call = self.http.post.call_args
        self.assertIn("/docs/index?", call.args[0])
        self.assertEqual(call.kwargs["json"], {"value": self.ns["documents"]})
        self.assertEqual(call.kwargs["headers"]["Authorization"], "Bearer search-user-token")
        self.http.post.reset_mock()
        del self.ns["documents"][1]["text_vector"]
        with self.assertRaisesRegex(RuntimeError, "embed every document"):
            run_cell(PUSH, "push-documents", self.ns)
        self.http.post.assert_not_called()

    def test_push_batches_and_verifies_exact_keys(self):
        self.ns["embedding_dimensions"] = 2
        self.ns["documents"] = [
            {"chunk_id": f"chunk-{number}", "chunk": "new text", "text_vector": [0.1, 0.2]}
            for number in range(101)
        ]
        self.http.post.side_effect = lambda url, **kwargs: http_response(200, {
            "value": [{"key": item["chunk_id"], "status": True} for item in kwargs["json"]["value"]],
        })
        run_cell(PUSH, "push-documents", self.ns)
        self.assertEqual([len(call.kwargs["json"]["value"]) for call in self.http.post.call_args_list],
                         [100, 1])
        self.http.get.return_value = http_response(200, {"chunk": "new text", "text_vector": [0.1, 0.2]})
        run_cell(PUSH, "verify-documents", self.ns)
        self.assertEqual(self.http.get.call_count, 101)
        self.assertIn("/docs/chunk-100?", self.http.get.call_args.args[0])
        self.ns["time"].sleep.assert_not_called()

    def test_push_verification_waits_for_new_content_and_times_out_on_stale_vectors(self):
        self.ns["embedding_dimensions"] = 2
        self.ns["documents"] = [{"chunk_id": "one", "chunk": "new", "text_vector": [0.1, 0.2]}]
        self.http.get.side_effect = [
            http_response(404, {}),
            http_response(200, {"chunk": "old", "text_vector": [0.1, 0.2]}),
            http_response(200, {"chunk": "new", "text_vector": [0.3, 0.4]}),
            http_response(200, {"chunk": "new", "text_vector": [0.100000001, 0.200000002]}),
        ]
        run_cell(PUSH, "verify-documents", self.ns)
        self.assertEqual(self.ns["time"].sleep.call_count, 3)
        self.http.get.side_effect = None
        self.http.get.return_value = http_response(200, {"chunk": "new", "text_vector": [0.3, 0.4]})
        with self.assertRaises(TimeoutError):
            run_cell(PUSH, "verify-documents", self.ns)
        self.http.get.return_value = http_response(403, {})
        with self.assertRaises(requests.HTTPError):
            run_cell(PUSH, "verify-documents", self.ns)

    def test_sharepoint_uses_secretless_connection_and_acl_ingestion(self):
        with patch.dict("os.environ", {
            "SHAREPOINT_APP_CLIENT_ID": self.ns["sp_app_client_id"],
            "SHAREPOINT_SEARCH_MI_CLIENT_ID": self.ns["sp_mi_client_id"],
        }):
            import os
            self.ns["os"] = os
            run_cell(SP_INDEXED, "indexed-configuration", self.ns,
                     {"INDEXED_SETUP_CONFIRMED = False": "INDEXED_SETUP_CONFIRMED = True"})
        run_cell(SP_INDEXED, "create-data-source", self.ns)
        payload = self.ns["data_source"]
        connection = payload["credentials"]["connectionString"]
        self.assertEqual(payload["type"], "sharepoint")
        self.assertEqual(payload["indexerPermissionOptions"], ["userIds", "groupIds"])
        self.assertIn(f"ApplicationId={self.ns['sp_app_client_id']};", connection)
        self.assertIn(f"FederatedCredentialApplicationId={self.ns['sp_mi_client_id']};", connection)
        self.assertNotIn("ApplicationSecret", connection)

    def test_indexed_setup_requires_explicit_acknowledgement(self):
        with self.assertRaisesRegex(RuntimeError, "Complete section 2"):
            run_cell(SP_INDEXED, "indexed-configuration", self.ns)
        self.http.put.assert_not_called()

    def test_sharepoint_text_sections_preserve_entra_acls_and_embedding_contract(self):
        run_cell(SP_INDEXED, "create-index", self.ns)
        index = self.ns["index"]
        self.assertEqual(index["permissionFilterOption"], "enabled")
        self.assertNotIn("sharePointConnectorAppRegistration", index)
        fields = {field["name"]: field for field in index["fields"]}
        self.assertNotIn("SharePointSiteUrl", fields)
        for name, kind in (("UserIds", "userIds"), ("GroupIds", "groupIds")):
            self.assertFalse(fields[name]["retrievable"])
            self.assertTrue(fields[name]["filterable"])
            self.assertEqual(fields[name]["permissionFilter"], kind)
        run_cell(SP_INDEXED, "create-skillset", self.ns)
        skillset = self.ns["skillset"]
        extraction, embedding = skillset["skills"]
        self.assertEqual(extraction["@odata.type"], "#Microsoft.Skills.Util.ContentUnderstandingSkill")
        self.assertEqual(extraction["chunkingProperties"], {
            "method": "semantic", "unit": "tokens", "maximumLength": 800, "overlapLength": 0,
        })
        self.assertEqual(extraction["extractionOptions"], ["locationMetadata"])
        self.assertNotIn("modelDeployment", extraction)
        self.assertEqual(extraction["inputs"], [{"name": "file_data", "source": "/document/file_data"}])
        self.assertEqual(skillset["cognitiveServices"], {
            "@odata.type": "#Microsoft.Azure.Search.AIServicesByIdentity",
            "subdomainUrl": self.ns["foundry_endpoint"],
        })
        selector, = skillset["indexProjections"]["selectors"]
        self.assertEqual(selector["sourceContext"], "/document/text_sections/*")
        self.assertEqual(embedding["context"], selector["sourceContext"])
        mapping = {item["name"]: item["source"] for item in selector["mappings"]}
        self.assertEqual(mapping["UserIds"], "/document/metadata_user_ids")
        self.assertEqual(mapping["GroupIds"], "/document/metadata_group_ids")
        self.assertEqual(mapping["content"], selector["sourceContext"] + "/content")
        self.assertEqual(mapping["contentVector"], selector["sourceContext"] + "/content_vector")
        self.assertEqual(embedding["inputs"], [{"name": "text", "source": mapping["content"]}])
        self.assertEqual(embedding["dimensions"], fields["contentVector"]["dimensions"])
        vectorizer, = index["vectorSearch"]["vectorizers"]
        self.assertEqual(vectorizer["kind"], "azureOpenAI")
        for key in ("resourceUri", "deploymentId", "modelName"):
            self.assertEqual(embedding[key], vectorizer["azureOpenAIParameters"][key])
        for name, source in (("page", "pageNumberFrom"), ("pageTo", "pageNumberTo")):
            self.assertIn(name, fields)
            self.assertEqual(mapping[name], selector["sourceContext"] + "/locationMetadata/" + source)
        self.assertEqual(mapping["webUrl"], "/document/metadata_spo_item_weburi")
        self.assertNotIn("parent_id", mapping)
        self.assertNotIn("id", mapping)

    def test_sharepoint_rest_helper_handles_empty_success_and_explicit_missing_objects(self):
        helper = self.ns["search_request"]
        for status in (202, 204):
            response = http_response(status, {})
            response.json.side_effect = AssertionError("Empty responses must not be decoded")
            self.http.post.return_value = response
            self.assertEqual(helper("POST", "indexers/example/run"), {})
        self.http.get.return_value = http_response(404, {})
        self.assertIsNone(helper("GET", "indexers/example/status", allow_not_found=True))
        with self.assertRaises(requests.HTTPError):
            helper("GET", "indexers/example/status")
        self.http.get.return_value = http_response(403, {})
        with self.assertRaises(requests.HTTPError):
            helper("GET", "indexers/example/status", allow_not_found=True)

    def test_sharepoint_acl_source_path_is_present_on_every_chunk(self):
        run_cell(SP_INDEXED, "create-index", self.ns)
        fields = {field["name"]: field for field in self.ns["index"]["fields"]}
        self.assertEqual(fields["metadata_spo_item_path"]["type"], "Edm.String")
        run_cell(SP_INDEXED, "create-skillset", self.ns)
        selectors = self.ns["skillset"]["indexProjections"]["selectors"]
        for selector in selectors:
            mapping = {item["name"]: item["source"] for item in selector["mappings"]}
            self.assertEqual(mapping["metadata_spo_item_path"], "/document/metadata_spo_item_path")

    def test_indexers_connect_skillset_and_reject_failed_items(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            with self.subTest(notebook=notebook):
                run_cell(notebook, "create-indexer", self.ns)
                indexer = self.ns["indexer"]
                self.assertEqual(indexer["dataSourceName"], self.ns["data_source_name"])
                self.assertEqual(indexer["targetIndexName"], self.ns["index_name"])
                self.assertEqual(indexer["skillsetName"], self.ns["skillset_name"])
                self.assertEqual(indexer["parameters"]["maxFailedItems"], 0)
                self.assertEqual(indexer["parameters"]["maxFailedItemsPerBatch"], 0)
                if notebook == SP_INDEXED:
                    configuration = indexer["parameters"]["configuration"]
                    self.assertTrue(configuration["allowSkillsetToReadFileData"])
                    self.assertNotIn("imageAction", configuration)

    def test_indexer_creation_and_update_do_not_confuse_old_run_with_new_run(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            self.http.post.reset_mock()
            self.http.get.return_value = http_response(404, {})
            run_cell(notebook, "create-indexer", self.ns)
            self.assertIsNone(self.ns["previous_run_start"])
            self.http.post.assert_not_called()

            self.http.get.return_value = http_response(200, {
                "lastResult": {"status": "success", "startTime": "old-run"},
            })
            run_cell(notebook, "create-indexer", self.ns)
            self.assertEqual(self.ns["previous_run_start"], "old-run")
            self.http.post.assert_called_once()
            self.assertIn("/run?", self.http.post.call_args.args[0])

    def test_running_indexer_is_not_updated(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            self.http.put.reset_mock()
            self.http.get.return_value = http_response(200, {
                "lastResult": {"status": "inProgress", "startTime": "active-run"},
            })
            with self.assertRaisesRegex(RuntimeError, "already running"):
                run_cell(notebook, "create-indexer", self.ns)
            self.http.put.assert_not_called()

    def test_http_errors_surface_without_retry_or_fabricated_success(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            for status in (401, 403, 429, 503):
                with self.subTest(notebook=notebook, status=status):
                    self.http.put.reset_mock()
                    self.http.put.return_value = http_response(status, {})
                    with self.assertRaises(requests.HTTPError):
                        run_cell(notebook, "create-index", self.ns)
                    self.http.put.assert_called_once()

    def test_monitor_waits_for_completion_and_surfaces_warnings(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            with self.subTest(notebook=notebook):
                self.http.get.side_effect = [
                    http_response(200, {"lastResult": {"status": "inProgress"}}),
                    http_response(200, {"lastResult": {
                        "status": "success", "itemsFailed": 0, "warnings": [{"message": "sample warning"}],
                    }}),
                ]
                output = run_cell(notebook, "monitor-indexer", self.ns)
                self.assertTrue(any("sample warning" in str(call) for call in output.call_args_list))
        self.assertEqual(self.ns["time"].sleep.call_count, 3)

    def test_monitor_rejects_failures_and_partial_indexing(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            for run in [
                {"status": "transientFailure", "errorMessage": "source failed"},
                {"status": "persistentFailure", "errorMessage": "source failed"},
                {"status": "success", "itemsFailed": 1},
                {"status": "success", "errors": [{"message": "source failed"}]},
            ]:
                with self.subTest(notebook=notebook, run=run):
                    self.http.get.return_value = http_response(200, {"lastResult": run})
                    with self.assertRaises(RuntimeError):
                        run_cell(notebook, "monitor-indexer", self.ns)

    def test_monitor_timeout_is_not_success(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            self.http.get.reset_mock()
            self.ns["time"].sleep.reset_mock()
            self.http.get.return_value = http_response(200, {"lastResult": None})
            with self.assertRaises(TimeoutError):
                run_cell(notebook, "monitor-indexer", self.ns)
            self.assertEqual(self.http.get.call_count, 90)
            self.assertEqual(self.ns["time"].sleep.call_count, 89)

    def test_monitor_does_not_accept_previous_run_success_after_rerun(self):
        for notebook in (BLOB, CU, SP_INDEXED):
            self.ns["previous_run_start"] = "old-run"
            self.http.get.side_effect = [
                http_response(200, {"lastResult": {"status": "success", "startTime": "old-run"}}),
                http_response(200, {"lastResult": {"status": "success", "startTime": "new-run"}}),
            ]
            run_cell(notebook, "monitor-indexer", self.ns)
            self.assertEqual(self.ns["last_run"]["startTime"], "new-run")

    def test_blob_empty_index_is_explicit(self):
        self.http.post.return_value = http_response(200, {"value": [], "@odata.count": 0})
        with self.assertRaisesRegex(RuntimeError, "No chunks"):
            run_cell(BLOB, "inspect-chunks", self.ns)

    def test_sharepoint_queries_always_send_user_context_without_elevated_read(self):
        for notebook, cell_id in ((SP_INDEXED, "query-index"), (SP_REMOTE, "query-remote")):
            self.http.post.return_value = http_response(200, {"references": []})
            run_cell(notebook, cell_id, self.ns)
            headers = self.http.post.call_args.kwargs["headers"]
            self.assertEqual(headers["Authorization"], "Bearer search-user-token")
            self.assertEqual(headers["x-ms-query-source-authorization"], "search-user-token")
            self.assertNotIn("x-ms-enable-elevated-read", headers)
            if notebook == SP_INDEXED:
                self.assertEqual(self.http.post.call_args.kwargs["json"]["search"], "*")

    def test_remote_path_has_no_ingestion_app_or_model_dependency(self):
        for name in ("sp_app_client_id", "sp_mi_client_id"):
            del self.ns[name]
        run_cell(SP_REMOTE, "create-remote-source", self.ns)
        source = self.ns["remote_source"]
        self.assertEqual(source["kind"], "remoteSharePoint")
        self.assertEqual(source["remoteSharePointParameters"]["filterExpression"],
                         'Path:"https://contoso.sharepoint.com/sites/workshop"')
        run_cell(SP_REMOTE, "create-remote-base", self.ns)
        base = self.ns["remote_base"]
        self.assertEqual(base["models"], [])
        self.assertEqual(base["retrievalReasoningEffort"], {"kind": "minimal"})
        self.assertEqual(base["outputMode"], "extractiveData")
        self.assertEqual(base["knowledgeSources"], [{"name": self.ns["remote_source_name"]}])
        self.http.post.return_value = http_response(200, {"references": []})
        run_cell(SP_REMOTE, "query-remote", self.ns)
        body = self.http.post.call_args.kwargs["json"]
        self.assertEqual(body["intents"], [
            {"type": "semantic", "search": "travel reimbursement receipts"},
        ])
        self.assertNotIn("messages", body)
        self.assertTrue(body["knowledgeSourceParams"][0]["includeReferenceSourceData"])
        self.assertFalse(any("/indexers/" in call.args[0] for call in self.http.put.call_args_list))

    def test_partial_remote_retrieval_and_activity_errors_are_not_success(self):
        for status, body in [
            (206, {"references": []}),
            (200, {"activity": [{"error": {"message": "retrieval failed"}}]}),
        ]:
            self.http.post.return_value = http_response(status, body)
            with self.assertRaisesRegex(RuntimeError, "partial or a source failed"):
                run_cell(SP_REMOTE, "query-remote", self.ns)

    def test_remote_gateway_errors_show_upstream_details_without_masking_failure(self):
        for body in (
            json.dumps({"error": {"message": "Copilot retrieval call failed: BadGateway"}}),
            "<html>Bad Gateway</html>",
        ):
            with self.subTest(body=body):
                response = http_response(502, {}, {
                    "request-id": "retrieval-request-id", "Date": "Fri, 13 Nov 2026 06:00:00 GMT",
                })
                response.text = body
                self.http.post.reset_mock()
                self.http.post.return_value = response
                with patch("builtins.print") as output:
                    with self.assertRaises(requests.HTTPError):
                        exec(code_cell(SP_REMOTE, "query-remote"), self.ns)
                printed = " ".join(str(call) for call in output.call_args_list)
                self.assertIn(body, printed)
                self.assertIn("retrieval-request-id", printed)
                self.assertIn("Fri, 13 Nov 2026 06:00:00 GMT", printed)
                response.json.assert_not_called()
                self.http.post.assert_called_once()

    def test_destructive_and_admin_cells_are_disabled_by_default(self):
        for notebook, cell_id in [
            (BLOB, "cleanup"), (PUSH, "cleanup"), (SP_INDEXED, "cleanup"), (SP_REMOTE, "cleanup"),
            (CU, "cleanup"), (CU, "run-indexer"), (BLOB, "run-indexer"), (SP_INDEXED, "run-indexer"),
            (SP_INDEXED, "create-app-registration"), (SP_INDEXED, "verify-federation"),
        ]:
            self.http.reset_mock()
            run_cell(notebook, cell_id, self.ns)
            self.assertEqual(self.http.mock_calls, [])

    def test_cleanup_uses_only_named_objects_and_propagates_authorization_errors(self):
        for notebook, count in [(BLOB, 6), (PUSH, 1), (SP_INDEXED, 4), (SP_REMOTE, 2)]:
            self.http.delete.reset_mock()
            self.http.delete.return_value = http_response(404, {})
            run_cell(notebook, "cleanup", self.ns, {"CLEAN_UP = False": "CLEAN_UP = True"})
            self.assertEqual(self.http.delete.call_count, count)
            for call in self.http.delete.call_args_list:
                self.assertIn("alice01", call.args[0])
                self.assertNotIn("management.azure.com", call.args[0])
                self.assertNotIn("graph.microsoft.com", call.args[0])
            self.http.delete.return_value = http_response(403, {})
            with self.assertRaises(requests.HTTPError):
                run_cell(notebook, "cleanup", self.ns, {"CLEAN_UP = False": "CLEAN_UP = True"})

    def test_rerun_can_reset_content_or_resync_acl_before_running(self):
        for notebook, flag, operation in [
            (BLOB, "RESET_BEFORE_RUN", "reset"),
            (CU, "RESET_BEFORE_RUN", "reset"),
            (SP_INDEXED, "RESYNC_PERMISSIONS", "resync"),
        ]:
            self.http.post.reset_mock()
            self.http.get.return_value = http_response(200, {
                "lastResult": {"status": "success", "startTime": "previous"},
            })
            run_cell(notebook, "run-indexer", self.ns, {
                "RUN_AGAIN = False": "RUN_AGAIN = True",
                f"{flag} = False": f"{flag} = True",
            })
            calls = self.http.post.call_args_list
            self.assertEqual(len(calls), 2)
            self.assertIn(f"/{operation}?", calls[0].args[0])
            self.assertIn("/run?", calls[1].args[0])
            self.assertEqual(self.ns["previous_run_start"], "previous")
            if operation == "resync":
                self.assertEqual(calls[0].kwargs["json"], {"options": ["permissions"]})

    def test_app_creation_declares_correct_permissions_but_never_grants_consent(self):
        import os
        self.ns["os"] = os
        api_roles = [
            {"value": name, "id": name + "-id", "isEnabled": True, "allowedMemberTypes": ["Application"]}
            for name in ("Files.Read.All", "Sites.Selected", "User.Read.All")
        ]
        self.http.get.return_value = http_response(200, {"value": [{"appRoles": api_roles}]})
        self.http.post.side_effect = [
            http_response(201, {"appId": self.ns["sp_app_client_id"], "id": "app-object-id"}),
            http_response(201, {"id": "service-principal-id"}),
        ]
        with patch.dict(os.environ, {}, clear=True):
            self.ns["prefix"] = "alice01"
            run_cell(SP_INDEXED, "create-app-registration", self.ns, {
                "CREATE_APP_REGISTRATION = False": "CREATE_APP_REGISTRATION = True",
            })
        calls = self.http.post.call_args_list
        self.assertEqual(len(calls), 2)
        app_body = calls[0].kwargs["json"]
        self.assertEqual(app_body["displayName"], "alice01-sharepoint-indexer")
        self.assertEqual(app_body["signInAudience"], "AzureADMyOrg")
        self.assertEqual(app_body["requiredResourceAccess"], [
            {"resourceAppId": "00000003-0000-0000-c000-000000000000", "resourceAccess": [
                {"id": "Files.Read.All-id", "type": "Role"},
                {"id": "Sites.Selected-id", "type": "Role"},
            ]},
        ])
        self.assertNotIn("passwordCredentials", app_body)
        self.assertTrue(calls[1].args[0].endswith("/servicePrincipals"))
        self.assertNotIn("appRoleAssignments", str(calls))

    def test_app_creation_refuses_a_configured_existing_app(self):
        import os
        self.ns["os"] = os
        with patch.dict(os.environ, {"SHAREPOINT_APP_CLIENT_ID": "already-configured"}):
            with self.assertRaisesRegex(RuntimeError, "duplicate"):
                run_cell(SP_INDEXED, "create-app-registration", self.ns, {
                    "CREATE_APP_REGISTRATION = False": "CREATE_APP_REGISTRATION = True",
                })
        self.http.post.assert_not_called()

    def test_federation_check_validates_identity_and_trust(self):
        import os
        self.ns["os"] = os
        environment = {
            "SHAREPOINT_APP_OBJECT_ID": "44444444-4444-4444-4444-444444444444",
            "SHAREPOINT_APP_CLIENT_ID": self.ns["sp_app_client_id"],
            "SHAREPOINT_SEARCH_MI_CLIENT_ID": self.ns["sp_mi_client_id"],
            "AZURE_SEARCH_RESOURCE_ID": "/subscriptions/test/search",
        }
        for subject, should_pass in [("mi-principal", True), ("wrong-principal", False)]:
            self.http.get.side_effect = [
                http_response(200, {"identity": {"principalId": "mi-principal"}}),
                http_response(200, {"appId": self.ns["sp_mi_client_id"]}),
                http_response(200, {"appId": self.ns["sp_app_client_id"]}),
                http_response(200, {"value": [{
                    "subject": subject,
                    "issuer": f"https://login.microsoftonline.com/{self.ns['tenant_id']}/v2.0",
                    "audiences": ["api://AzureADTokenExchange"],
                }]}),
            ]
            with patch.dict(os.environ, environment):
                if should_pass:
                    run_cell(SP_INDEXED, "verify-federation", self.ns, {
                        "VERIFY_FEDERATION = False": "VERIFY_FEDERATION = True",
                    })
                else:
                    with self.assertRaisesRegex(RuntimeError, "No matching"):
                        run_cell(SP_INDEXED, "verify-federation", self.ns, {
                            "VERIFY_FEDERATION = False": "VERIFY_FEDERATION = True",
                        })

    def test_no_vision_or_custom_acl_incompatible_skills_in_new_code(self):
        for notebook in (BLOB, SP_INDEXED):
            document = nbformat.read(ROOT / "notebooks" / notebook, as_version=4)
            code = "\n".join(cell.source for cell in document.cells if cell.cell_type == "code")
            for excluded in (
                "#Microsoft.Skills.Vision.", "aiServicesVision",
                "#Microsoft.Skills.Custom.WebApiSkill", "knowledgeStore",
            ):
                self.assertNotIn(excluded, code)
            self.assertNotIn("webiq_post", code)


if __name__ == "__main__":
    unittest.main()
