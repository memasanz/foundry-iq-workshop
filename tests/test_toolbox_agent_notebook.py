import base64
from contextlib import redirect_stdout
import io
import json
import shutil
import unittest
import uuid
import zipfile
from pathlib import Path
from unittest.mock import MagicMock, Mock

from azure.ai.projects.models import AgentVersionStatus

from test_notebooks import ROOT, code_cell, http_response

TOOLBOX = "14-foundry-iq-toolbox-agent.ipynb"
SEARCH = "https://workshop.search.windows.net"
SEARCH_ID = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Search/searchServices/workshop"
PROJECT_ID = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/workshop/projects/workshop"
PRINCIPAL = "55555555-5555-5555-5555-555555555555"


class ToolboxAgentNotebookTests(unittest.TestCase):
    def setUp(self):
        self.fixture = ROOT / ".workshop" / ("test-toolbox-" + uuid.uuid4().hex)
        self.fixture.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.fixture)
        self.folder = self.fixture / "hosted-agent-iq"
        self.ns = {
            "Path": Path, "zipfile": zipfile, "agent_folder": self.folder,
            "requests": Mock(), "time": Mock(time=Mock(return_value=0), sleep=Mock()),
            "uuid": uuid, "UUID": uuid.UUID, "json": json, "base64": base64,
            "search_token": lambda: "search-token", "arm_token": lambda: "arm-token",
            "search_endpoint": SEARCH, "search_resource_id": SEARCH_ID,
            "project_resource_id": PROJECT_ID,
            "project_endpoint": "https://workshop.services.ai.azure.com/api/projects/workshop",
            "tenant_id": "11111111-1111-1111-1111-111111111111",
            "api_version": "2026-08-01-preview", "prefix": "alice01",
            "sharepoint_source_name": "alice01-kb-sharepoint",
            "sharepoint_index_name": "alice01-nb7-index", "knowledge_base_name": "alice01-toolbox-kb",
            "toolbox_name": "alice01-iq-toolbox", "agent_name": "alice01-iq-agent",
            "connection_name": "alice01-iq-search-mcp",
            "connection_id": f"{PROJECT_ID}/connections/alice01-iq-search-mcp",
            "model_name": "workshop-model",
            "project_client": Mock(),
            "AzureCliCredential": MagicMock(), "AIProjectClient": MagicMock(),
            "knowledge_base_mcp_url": f"{SEARCH}/knowledgebases/alice01-toolbox-kb/mcp?api-version=2026-08-01-preview",
        }

    def test_knowledge_base_uses_only_validated_sharepoint_source_and_copies_models(self):
        requests = self.ns["requests"]
        source_fields = ("id", "content", "sourceFile", "webUrl", "page", "pageTo")
        requests.get.side_effect = [
            http_response(200, {
                "kind": "searchIndex",
                "searchIndexParameters": {
                    "searchIndexName": "alice01-nb7-index",
                    "semanticConfigurationName": "semantic-config",
                    "searchFields": [{"name": "content"}],
                    "sourceDataFields": [{"name": name} for name in source_fields],
                },
            }),
            http_response(200, {
                "permissionFilterOption": "enabled",
                "fields": [{"name": name, "retrievable": True} for name in source_fields] + [
                    {"name": name, "type": "Collection(Edm.String)", "permissionFilter": kind,
                     "filterable": True, "retrievable": False}
                    for name, kind in (("UserIds", "userIds"), ("GroupIds", "groupIds"))
                ],
                "semantic": {"configurations": [{
                    "name": "semantic-config",
                    "prioritizedFields": {"prioritizedContentFields": [{"fieldName": "content"}]},
                }]},
            }),
            http_response(200, {"models": [{"kind": "azureOpenAI"}], "answerInstructions": "Be brief."}),
        ]
        requests.put.return_value = http_response(200, {})
        exec(code_cell(TOOLBOX, "create-knowledge-base"), self.ns)
        body = requests.put.call_args.kwargs["json"]
        self.assertEqual(body["knowledgeSources"], [{"name": "alice01-kb-sharepoint"}])
        self.assertEqual(body["models"], [{"kind": "azureOpenAI"}])
        self.assertEqual(body["answerInstructions"], "Be brief.")
        self.assertEqual(body["outputMode"], "answerSynthesis")
        self.assertEqual(body["retrievalReasoningEffort"], {"kind": "medium"})
        self.assertEqual([call.args[0] for call in requests.get.call_args_list], [
            f"{SEARCH}/knowledgesources/alice01-kb-sharepoint?api-version=2026-08-01-preview",
            f"{SEARCH}/indexes/alice01-nb7-index?api-version=2026-08-01-preview",
            f"{SEARCH}/knowledgebases/alice01-kb?api-version=2026-08-01-preview",
        ])
        requests.put.assert_called_once()
        self.assertTrue(requests.put.call_args.args[0].startswith(f"{SEARCH}/knowledgebases/alice01-toolbox-kb"))
        self.assertTrue(self.ns["knowledge_base_mcp_url"].endswith("/knowledgebases/alice01-toolbox-kb/mcp?api-version=2026-08-01-preview"))

    def test_missing_prerequisite_notebook_is_reported(self):
        self.ns["requests"].get.return_value = http_response(404, {})
        with self.assertRaisesRegex(RuntimeError, "notebooks 07 and 11"):
            exec(code_cell(TOOLBOX, "create-knowledge-base"), self.ns)
        self.ns["requests"].put.assert_not_called()

    def test_toolbox_creates_project_identity_connection_and_promotes_version(self):
        client = self.ns["project_client"]
        self.ns["requests"].put.return_value = http_response(201, {})
        client.toolboxes.create_version.return_value = Mock(version="2")
        client.toolboxes.get.return_value = Mock(default_version="1")
        client.toolboxes.update.return_value = Mock(default_version="2")
        client.toolboxes.update.return_value.name = "alice01-iq-toolbox"
        exec(code_cell(TOOLBOX, "create-toolbox"), self.ns)
        call = self.ns["requests"].put.call_args
        self.assertEqual(call.args[0], f"https://management.azure.com{self.ns['connection_id']}?api-version=2025-04-01-preview")
        self.assertEqual(call.kwargs["headers"], {"Authorization": "Bearer arm-token"})
        self.assertEqual(call.kwargs["json"], {"properties": {
            "category": "RemoteTool",
            "authType": "ProjectManagedIdentity",
            "target": self.ns["knowledge_base_mcp_url"],
            "audience": "https://search.azure.com",
            "isSharedToAll": True,
            "metadata": {"ApiType": "Azure"},
        }})
        client.connections.get.assert_not_called()
        tool = client.toolboxes.create_version.call_args.kwargs["tools"][0]
        self.assertEqual(tool.server_url, self.ns["knowledge_base_mcp_url"])
        self.assertEqual(tool.project_connection_id, self.ns["connection_id"])
        self.assertEqual(tool.require_approval, "never")
        client.toolboxes.update.assert_called_once_with("alice01-iq-toolbox", default_version="2")

    def test_connection_creation_is_repeatable_and_keeps_default_toolbox_version(self):
        client = self.ns["project_client"]
        self.ns["requests"].put.return_value = http_response(200, {})
        client.toolboxes.create_version.return_value = Mock(version="2")
        client.toolboxes.get.return_value = Mock(default_version="2")
        for _ in range(2):
            exec(code_cell(TOOLBOX, "create-toolbox"), self.ns)
        urls = [call.args[0] for call in self.ns["requests"].put.call_args_list]
        self.assertEqual(len(urls), 2)
        self.assertEqual(urls[0], urls[1])
        client.toolboxes.update.assert_not_called()

    def test_connection_failure_stops_before_creating_toolbox(self):
        for status in (403, 500):
            with self.subTest(status=status):
                self.ns["requests"].put.return_value = http_response(status, {})
                with self.assertRaisesRegex(Exception, f"HTTP {status}"):
                    exec(code_cell(TOOLBOX, "create-toolbox"), self.ns)
                self.ns["project_client"].toolboxes.create_version.assert_not_called()

    def test_agent_code_has_no_search_details_and_deploy_passes_toolbox_name(self):
        exec(code_cell(TOOLBOX, "write-agent-files"), self.ns)
        main = (self.folder / "main.py").read_text(encoding="utf-8")
        compile(main, "main.py", "exec")
        self.assertIn("FoundryToolbox(credential)", main)
        for forbidden in ("search.windows.net", "api_key", "knowledgebases"):
            self.assertNotIn(forbidden, main)
        client = self.ns["project_client"]
        client.agents.create_version_from_code.return_value = Mock(version="1")
        client.agents.get_version.side_effect = [Mock(version="1", status=AgentVersionStatus.ACTIVE)]
        exec(code_cell(TOOLBOX, "deploy"), self.ns)
        definition = client.agents.create_version_from_code.call_args.kwargs["definition"]
        self.assertEqual(definition.environment_variables["TOOLBOX_NAME"], "alice01-iq-toolbox")
        self.assertEqual(definition.code_configuration.dependency_resolution, "remote_build")
        self.assertEqual(definition.code_configuration.entry_point, ["python", "main.py"])
        client.agents.get_version.assert_called_once_with("alice01-iq-agent", "1")
        self.ns["time"].sleep.assert_not_called()

    def prepare_user_invocation(self, **token_overrides):
        claims = {
            "oid": PRINCIPAL, "tid": self.ns["tenant_id"],
            "aud": "https://ai.azure.com", "scp": "user_impersonation", "exp": 3600,
        }
        claims.update(token_overrides)
        payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=")
        access_token = f"header.{payload}.signature"
        credential = self.ns["AzureCliCredential"].return_value.__enter__.return_value
        credential.get_token.return_value = Mock(token=access_token)
        project = self.ns["AIProjectClient"].return_value.__enter__.return_value
        client = project.get_openai_client.return_value.__enter__.return_value
        client.responses.create.return_value = Mock(
            status="completed", output_text="Synthetic grounded answer.", output=[],
            model_dump=Mock(return_value={"status": "completed", "output": []}),
        )
        return access_token, project, client

    def test_invocations_use_validated_cli_user_and_independent_nonstored_bounded_calls(self):
        access_token, project, client = self.prepare_user_invocation()
        output = io.StringIO()
        with redirect_stdout(output):
            exec(code_cell(TOOLBOX, "invoke"), self.ns)
            exec(code_cell(TOOLBOX, "invoke-unknown"), self.ns)
        self.assertEqual(self.ns["AzureCliCredential"].call_count, 2)
        self.assertEqual(client.responses.create.call_count, 2)
        for call in client.responses.create.call_args_list:
            self.assertEqual(set(call.kwargs), {"input", "store"})
            self.assertIs(call.kwargs["store"], False)
        for call in project.get_openai_client.call_args_list:
            self.assertEqual(call.kwargs, {
                "agent_name": "alice01-iq-agent", "api_key": access_token,
                "max_retries": 0, "timeout": 120.0,
            })
        self.assertIn(PRINCIPAL, output.getvalue())
        self.assertNotIn(access_token, output.getvalue())

    def test_app_only_invocation_stops_before_constructing_question_client(self):
        self.prepare_user_invocation(idtyp="app")
        with self.assertRaisesRegex(PermissionError, "delegated USER token"):
            exec(code_cell(TOOLBOX, "invoke"), self.ns)
        self.ns["AIProjectClient"].assert_not_called()

    def test_cleanup_off_by_default_and_deletes_only_attendee_resources(self):
        exec(code_cell(TOOLBOX, "cleanup"), self.ns)
        self.ns["project_client"].agents.delete.assert_not_called()
        self.ns["requests"].delete.assert_not_called()
        self.ns["requests"].delete.return_value = http_response(204, {})
        exec(code_cell(TOOLBOX, "cleanup").replace("CLEAN_UP = False", "CLEAN_UP = True"), self.ns)
        self.ns["project_client"].agents.delete.assert_called_once_with("alice01-iq-agent")
        self.ns["project_client"].toolboxes.delete.assert_called_once_with("alice01-iq-toolbox")
        connection_delete = self.ns["requests"].delete.call_args_list[0]
        self.assertEqual(connection_delete.args[0], f"https://management.azure.com{self.ns['connection_id']}?api-version=2025-04-01-preview")
        self.assertEqual(connection_delete.kwargs["headers"], {"Authorization": "Bearer arm-token"})
        self.assertIn("alice01-toolbox-kb", self.ns["requests"].delete.call_args.args[0])


class FacilitatorScriptTests(unittest.TestCase):
    def test_scripts_exist_with_expected_commands(self):
        from test_notebooks import ROOT
        connection = (ROOT / "scripts" / "setup-toolbox-connection.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("--auth-type agentic-identity", connection)
        self.assertIn("https://search.azure.com", connection)
        self.assertIn("SupportsShouldProcess", connection)
        grant = (ROOT / "scripts" / "grant-agent-search-access.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("1407120a-92aa-4202-b7e9-c0e197c71c8f", grant)
        self.assertIn("AZURE_SEARCH_RESOURCE_ID", grant)


if __name__ == "__main__":
    unittest.main()
