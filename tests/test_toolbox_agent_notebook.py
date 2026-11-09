import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import Mock

from test_notebooks import code_cell, http_response

TOOLBOX = "11-foundry-iq-toolbox-agent.ipynb"
SEARCH = "https://workshop.search.windows.net"
SEARCH_ID = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Search/searchServices/workshop"
PRINCIPAL = "55555555-5555-5555-5555-555555555555"


class ToolboxAgentNotebookTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.folder = Path(self.tmp.name) / "hosted-agent-iq"
        self.ns = {
            "Path": Path, "zipfile": zipfile, "agent_folder": self.folder,
            "requests": Mock(), "time": Mock(time=Mock(return_value=0), sleep=Mock()),
            "uuid": __import__("uuid"),
            "search_token": lambda: "search-token", "arm_token": lambda: "arm-token",
            "search_endpoint": SEARCH, "search_resource_id": SEARCH_ID,
            "api_version": "2026-08-01-preview", "prefix": "alice01",
            "blob_source_name": "alice01-kb-blob", "knowledge_base_name": "alice01-toolbox-kb",
            "toolbox_name": "alice01-iq-toolbox", "agent_name": "alice01-iq-agent",
            "connection_name": "iq-workshop-search-mcp", "model_name": "workshop-model",
            "project_client": Mock(),
            "knowledge_base_mcp_url": f"{SEARCH}/knowledgebases/alice01-toolbox-kb/mcp?api-version=2026-08-01-preview",
        }

    def tearDown(self):
        self.tmp.cleanup()

    def test_knowledge_base_uses_only_blob_source_and_copies_models(self):
        requests = self.ns["requests"]
        requests.get.side_effect = [
            http_response(200, {"kind": "azureBlob"}),
            http_response(200, {"models": [{"kind": "azureOpenAI"}], "answerInstructions": "Be brief."}),
        ]
        requests.put.return_value = http_response(200, {})
        exec(code_cell(TOOLBOX, "create-knowledge-base"), self.ns)
        body = requests.put.call_args.kwargs["json"]
        self.assertEqual(body["knowledgeSources"], [{"name": "alice01-kb-blob"}])
        self.assertEqual(body["models"], [{"kind": "azureOpenAI"}])
        self.assertTrue(requests.put.call_args.args[0].startswith(f"{SEARCH}/knowledgebases/alice01-toolbox-kb"))
        self.assertTrue(self.ns["knowledge_base_mcp_url"].endswith("/knowledgebases/alice01-toolbox-kb/mcp?api-version=2026-08-01-preview"))

    def test_missing_prerequisite_notebook_is_reported(self):
        self.ns["requests"].get.return_value = http_response(404, {})
        with self.assertRaisesRegex(RuntimeError, "notebook 08"):
            exec(code_cell(TOOLBOX, "create-knowledge-base"), self.ns)

    def test_toolbox_tool_points_at_kb_mcp_with_shared_connection_and_is_promoted(self):
        client = self.ns["project_client"]
        client.connections.get.return_value = Mock(id="conn-id")
        client.connections.get.return_value.name = "iq-workshop-search-mcp"
        client.toolboxes.create_version.return_value = Mock(version="2")
        client.toolboxes.get.return_value = Mock(default_version="1")
        client.toolboxes.update.return_value = Mock(default_version="2")
        client.toolboxes.update.return_value.name = "alice01-iq-toolbox"
        exec(code_cell(TOOLBOX, "create-toolbox"), self.ns)
        tool = client.toolboxes.create_version.call_args.kwargs["tools"][0]
        self.assertEqual(tool.server_url, self.ns["knowledge_base_mcp_url"])
        self.assertEqual(tool.project_connection_id, "conn-id")
        self.assertEqual(tool.require_approval, "never")
        client.toolboxes.update.assert_called_once_with("alice01-iq-toolbox", default_version="2")

    def test_agent_code_has_no_search_details_and_deploy_passes_toolbox_name(self):
        exec(code_cell(TOOLBOX, "write-agent-files"), self.ns)
        main = (self.folder / "main.py").read_text(encoding="utf-8")
        compile(main, "main.py", "exec")
        self.assertIn("FoundryToolbox(credential)", main)
        for forbidden in ("search.windows.net", "api_key", "knowledgebases"):
            self.assertNotIn(forbidden, main)
        client = self.ns["project_client"]
        client.agents.create_version_from_code.return_value = Mock(version="1")
        client.agents.get_version.return_value = Mock(version="1", status="active")
        exec(code_cell(TOOLBOX, "deploy"), self.ns)
        definition = client.agents.create_version_from_code.call_args.kwargs["definition"]
        self.assertEqual(definition.environment_variables["TOOLBOX_NAME"], "alice01-iq-toolbox")
        self.assertEqual(definition.code_configuration.dependency_resolution, "remote_build")

    def grant(self, status):
        self.ns["version"] = Mock(instance_identity=Mock(principal_id=PRINCIPAL))
        self.ns["requests"].put.return_value = http_response(status, {})
        exec(code_cell(TOOLBOX, "grant-search-access"), self.ns)
        return self.ns["requests"].put

    def test_grant_assigns_search_index_data_reader_to_agent_identity(self):
        put = self.grant(201)
        body = put.call_args.kwargs["json"]["properties"]
        self.assertEqual(body["principalId"], PRINCIPAL)
        self.assertEqual(body["principalType"], "ServicePrincipal")
        self.assertTrue(body["roleDefinitionId"].endswith("1407120a-92aa-4202-b7e9-c0e197c71c8f"))
        self.assertTrue(put.call_args.args[0].startswith(f"https://management.azure.com{SEARCH_ID}/"))

    def test_grant_tolerates_existing_assignment_and_falls_back_on_403(self):
        self.grant(409)
        self.grant(403)

    def test_grant_raises_on_other_errors(self):
        with self.assertRaises(Exception):
            self.grant(500)

    def test_cleanup_off_by_default_and_deletes_only_attendee_resources(self):
        exec(code_cell(TOOLBOX, "cleanup"), self.ns)
        self.ns["project_client"].agents.delete.assert_not_called()
        self.ns["requests"].delete.assert_not_called()
        self.ns["requests"].delete.return_value = http_response(204, {})
        exec(code_cell(TOOLBOX, "cleanup").replace("CLEAN_UP = False", "CLEAN_UP = True"), self.ns)
        self.ns["project_client"].agents.delete.assert_called_once_with("alice01-iq-agent")
        self.ns["project_client"].toolboxes.delete.assert_called_once_with("alice01-iq-toolbox")
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
