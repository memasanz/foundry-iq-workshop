import json
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch
from urllib.parse import quote
from uuid import UUID

import nbformat
import requests

from test_notebooks import ROOT, code_cell, http_response


BLOB = "03-blob-indexer-skills.ipynb"
SP_INDEXED = "04-sharepoint-indexed.ipynb"
SP_REMOTE = "05-sharepoint-remote.ipynb"


def search_namespace():
    return {
        "requests": Mock(),
        "json": json,
        "time": SimpleNamespace(sleep=Mock()),
        "search_token": lambda: "search-user-token",
        "storage_token": lambda: "storage-user-token",
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

    def test_utility_skills_and_chunk_projection_contract(self):
        run_cell(BLOB, "create-index", self.ns)
        fields = {field["name"]: field for field in self.ns["index"]["fields"]}
        self.assertEqual(fields["source"]["type"], "Edm.ComplexType")
        self.assertTrue(fields["parent_id"]["filterable"])
        run_cell(BLOB, "create-skillset", self.ns)
        skills = self.ns["skillset"]["skills"]
        self.assertEqual([skill["@odata.type"] for skill in skills], [
            "#Microsoft.Skills.Text.SplitSkill", "#Microsoft.Skills.Util.ShaperSkill",
        ])
        split = skills[0]
        self.assertEqual((split["maximumPageLength"], split["pageOverlapLength"]), (1000, 200))
        projections = self.ns["skillset"]["indexProjections"]
        self.assertEqual(projections["parameters"]["projectionMode"], "skipIndexingParentDocuments")
        selector = projections["selectors"][0]
        self.assertEqual(selector["parentKeyFieldName"], "parent_id")
        self.assertEqual(selector["mappings"], [
            {"name": "chunk", "source": "/document/chunks/*"},
            {"name": "source", "source": "/document/provenance"},
        ])
        self.assertNotIn("cognitiveServices", self.ns["skillset"])

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

    def test_native_groups_use_managed_identity_client_id_and_exact_metadata_path(self):
        run_cell(SP_INDEXED, "create-index", self.ns)
        index = self.ns["index"]
        self.assertEqual(index["permissionFilterOption"], "enabled")
        connector = index["sharePointConnectorAppRegistration"]
        self.assertEqual(connector["applicationId"], self.ns["sp_app_client_id"])
        self.assertEqual(connector["federatedCredentialId"], self.ns["sp_mi_client_id"])
        fields = {field["name"]: field for field in index["fields"]}
        self.assertTrue(fields["SharePointSiteUrl"]["sharepointSiteUrl"])
        for name in ("UserIds", "GroupIds", "SharePointSiteUrl"):
            self.assertFalse(fields[name]["retrievable"])
        run_cell(SP_INDEXED, "create-skillset", self.ns)
        selector = self.ns["skillset"]["indexProjections"]["selectors"][0]
        mapping = {item["name"]: item["source"] for item in selector["mappings"]}
        self.assertEqual(mapping["UserIds"], "/document/metadata_user_ids")
        self.assertEqual(mapping["GroupIds"], "/document/metadata_group_ids")
        self.assertEqual(mapping["SharePointSiteUrl"], "/document/metadata_spo_site_url")
        self.assertNotIn("parent_id", mapping)
        self.assertNotIn("chunk_id", mapping)

    def test_both_indexers_connect_skillset_and_reject_failed_items(self):
        for notebook in (BLOB, SP_INDEXED):
            with self.subTest(notebook=notebook):
                run_cell(notebook, "create-indexer", self.ns)
                indexer = self.ns["indexer"]
                self.assertEqual(indexer["dataSourceName"], self.ns["data_source_name"])
                self.assertEqual(indexer["targetIndexName"], self.ns["index_name"])
                self.assertEqual(indexer["skillsetName"], self.ns["skillset_name"])
                self.assertEqual(indexer["parameters"]["maxFailedItems"], 0)
                self.assertEqual(indexer["parameters"]["maxFailedItemsPerBatch"], 0)

    def test_indexer_creation_and_update_do_not_confuse_old_run_with_new_run(self):
        for notebook in (BLOB, SP_INDEXED):
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
        for notebook in (BLOB, SP_INDEXED):
            self.http.put.reset_mock()
            self.http.get.return_value = http_response(200, {
                "lastResult": {"status": "inProgress", "startTime": "active-run"},
            })
            with self.assertRaisesRegex(RuntimeError, "already running"):
                run_cell(notebook, "create-indexer", self.ns)
            self.http.put.assert_not_called()

    def test_http_errors_surface_without_retry_or_fabricated_success(self):
        for notebook in (BLOB, SP_INDEXED):
            for status in (401, 403, 429, 503):
                with self.subTest(notebook=notebook, status=status):
                    self.http.put.reset_mock()
                    self.http.put.return_value = http_response(status, {})
                    with self.assertRaises(requests.HTTPError):
                        run_cell(notebook, "create-index", self.ns)
                    self.http.put.assert_called_once()

    def test_monitor_waits_for_completion_and_surfaces_warnings(self):
        for notebook in (BLOB, SP_INDEXED):
            with self.subTest(notebook=notebook):
                self.http.get.side_effect = [
                    http_response(200, {"lastResult": {"status": "inProgress"}}),
                    http_response(200, {"lastResult": {
                        "status": "success", "itemsFailed": 0, "warnings": [{"message": "sample warning"}],
                    }}),
                ]
                output = run_cell(notebook, "monitor-indexer", self.ns)
                self.assertTrue(any("sample warning" in str(call) for call in output.call_args_list))
        self.assertEqual(self.ns["time"].sleep.call_count, 2)

    def test_monitor_rejects_failures_and_partial_indexing(self):
        for notebook in (BLOB, SP_INDEXED):
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
        for notebook in (BLOB, SP_INDEXED):
            self.http.get.reset_mock()
            self.ns["time"].sleep.reset_mock()
            self.http.get.return_value = http_response(200, {"lastResult": None})
            with self.assertRaises(TimeoutError):
                run_cell(notebook, "monitor-indexer", self.ns)
            self.assertEqual(self.http.get.call_count, 90)
            self.assertEqual(self.ns["time"].sleep.call_count, 89)

    def test_monitor_does_not_accept_previous_run_success_after_rerun(self):
        for notebook in (BLOB, SP_INDEXED):
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

    def test_destructive_and_admin_cells_are_disabled_by_default(self):
        for notebook, cell_id in [
            (BLOB, "cleanup"), (SP_INDEXED, "cleanup"), (SP_REMOTE, "cleanup"),
            (BLOB, "run-indexer"), (SP_INDEXED, "run-indexer"),
            (SP_INDEXED, "create-app-registration"), (SP_INDEXED, "verify-federation"),
        ]:
            self.http.reset_mock()
            run_cell(notebook, cell_id, self.ns)
            self.assertEqual(self.http.mock_calls, [])

    def test_cleanup_uses_only_named_objects_and_propagates_authorization_errors(self):
        for notebook, count in [(BLOB, 6), (SP_INDEXED, 4), (SP_REMOTE, 2)]:
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
            run_cell(SP_INDEXED, "create-app-registration", self.ns, {
                "CREATE_APP_REGISTRATION = False": "CREATE_APP_REGISTRATION = True",
            })
        calls = self.http.post.call_args_list
        self.assertEqual(len(calls), 2)
        app_body = calls[0].kwargs["json"]
        self.assertEqual(app_body["signInAudience"], "AzureADMyOrg")
        self.assertEqual(app_body["requiredResourceAccess"], [
            {"resourceAppId": "00000003-0000-0000-c000-000000000000", "resourceAccess": [
                {"id": "Files.Read.All-id", "type": "Role"},
                {"id": "Sites.Selected-id", "type": "Role"},
            ]},
            {"resourceAppId": "00000003-0000-0ff1-ce00-000000000000", "resourceAccess": [
                {"id": "Sites.Selected-id", "type": "Role"},
                {"id": "User.Read.All-id", "type": "Role"},
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
