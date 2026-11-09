import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

from azure.ai.projects.models import MCPTool, MCPToolboxTool, PromptAgentDefinition

import test_notebooks
from test_notebooks import code_cell, http_response


NOTEBOOK = "03-foundry-web-iq-toolbox.ipynb"
PROJECT_ENDPOINT = "https://example.services.ai.azure.com/api/projects/workshop"
PROJECT_ID = "/subscriptions/s/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/ai/projects/workshop"
WEBIQ_URL = "https://api.microsoft.ai/v3/mcp"


class WebIqToolboxNotebookTests(unittest.TestCase):
    def setUp(self):
        self.project = Mock()
        self.project.connections.get.return_value = SimpleNamespace(
            name="webiq-mcp", id=f"{PROJECT_ID}/connections/webiq-mcp", target=WEBIQ_URL,
        )
        self.project.toolboxes.create_version.return_value = SimpleNamespace(version="2")
        self.project.toolboxes.get.return_value = SimpleNamespace(default_version="1")
        self.project.toolboxes.update.return_value = SimpleNamespace(default_version="2")
        self.ns = {
            "project": self.project,
            "project_endpoint": PROJECT_ENDPOINT,
            "mcp_url": WEBIQ_URL,
            "webiq_connection_name": "webiq-mcp",
            "toolbox_name": "webiq-toolbox-session",
            "toolbox_connection_name": "webiq-toolbox-session-connection",
            "toolbox_connection_id": f"{PROJECT_ID}/connections/webiq-toolbox-session-connection",
            "agent_name": "webiq-toolbox-agent-session",
            "model_name": "workshop-model",
            "MCPToolboxTool": MCPToolboxTool, "MCPTool": MCPTool,
            "PromptAgentDefinition": PromptAgentDefinition,
            "credential": Mock(get_token=Mock(return_value=SimpleNamespace(token="arm-token"))),
            "requests": Mock(),
            "web_tool_name": "webiq___web",
        }

    def run_cell(self, cell_id, source=None):
        with patch("builtins.print"):
            exec(source or code_cell(NOTEBOOK, cell_id), self.ns)

    def create_toolbox(self):
        self.run_cell("get-webiq-connection")
        self.run_cell("create-toolbox")

    def test_existing_webiq_connection_is_read_without_credentials_or_updates(self):
        self.run_cell("get-webiq-connection")
        self.project.connections.get.assert_called_once_with("webiq-mcp")
        self.ns["requests"].put.assert_not_called()
        self.project.toolboxes.create_version.assert_not_called()

    def test_wrong_connection_target_stops_before_creating_a_toolbox(self):
        self.project.connections.get.return_value.target = "https://different.example/mcp"
        with self.assertRaisesRegex(ValueError, "does not target Web IQ"):
            self.run_cell("get-webiq-connection")
        self.project.toolboxes.create_version.assert_not_called()

    def test_toolbox_references_key_connection_and_exposes_only_web(self):
        self.create_toolbox()
        args = self.project.toolboxes.create_version.call_args.kwargs
        self.assertEqual(args["name"], "webiq-toolbox-session")
        tool = args["tools"][0]
        self.assertEqual(tool.server_url, WEBIQ_URL)
        self.assertEqual(tool.server_label, "webiq")
        self.assertEqual(tool.project_connection_id, f"{PROJECT_ID}/connections/webiq-mcp")
        self.assertEqual(tool.allowed_tools, ["web"])
        self.assertEqual(tool.require_approval, "always")
        self.assertIsNone(tool.headers)
        self.assertIsNone(tool.authorization)
        self.project.toolboxes.update.assert_called_once_with(
            "webiq-toolbox-session", default_version="2",
        )
        self.assertEqual(
            self.ns["toolbox_mcp_url"],
            f"{PROJECT_ENDPOINT}/toolboxes/webiq-toolbox-session/versions/2/mcp?api-version=v1",
        )

    def test_already_default_version_is_not_promoted_again(self):
        self.project.toolboxes.get.return_value.default_version = "2"
        self.create_toolbox()
        self.project.toolboxes.update.assert_not_called()

    def test_agent_to_toolbox_connection_has_exact_target_and_foundry_audience(self):
        self.create_toolbox()
        self.ns["requests"].put.return_value = http_response(201, {})
        self.run_cell("create-toolbox-connection")
        self.ns["credential"].get_token.assert_called_once_with("https://management.azure.com/.default")
        call = self.ns["requests"].put.call_args
        self.assertIn("webiq-toolbox-session-connection?api-version=2025-10-01-preview", call.args[0])
        properties = call.kwargs["json"]["properties"]
        self.assertEqual(properties["authType"], "UserEntraToken")
        self.assertEqual(properties["audience"], "https://ai.azure.com")
        self.assertEqual(properties["target"], self.ns["toolbox_mcp_url"])
        self.assertEqual(properties["metadata"]["toolboxVersion"], "2")
        self.assertNotIn("credentials", properties)
        self.assertNotIn("arm_token", self.ns)

    def test_pending_connection_creation_does_not_claim_success(self):
        self.create_toolbox()
        self.ns["requests"].put.return_value = http_response(202, {})
        with self.assertRaisesRegex(RuntimeError, "Unexpected connection status: 202"):
            self.run_cell("create-toolbox-connection")

    def test_agent_attaches_only_toolbox_and_requires_approval(self):
        self.create_toolbox()
        self.run_cell("create-agent")
        definition = self.project.agents.create_version.call_args.kwargs["definition"]
        self.assertEqual(definition.model, "workshop-model")
        self.assertEqual(len(definition.tools), 1)
        tool = definition.tools[0]
        self.assertEqual(tool.server_url, self.ns["toolbox_mcp_url"])
        self.assertNotEqual(tool.server_url, WEBIQ_URL)
        self.assertEqual(tool.project_connection_id, "webiq-toolbox-session-connection")
        self.assertEqual(tool.allowed_tools, ["webiq___web"])
        self.assertEqual(tool.require_approval, "always")
        self.assertIsNone(tool.headers)

    def test_verification_requires_successful_webiq_call_from_toolbox(self):
        self.ns["response"] = SimpleNamespace(output_text="An answer with sources.")
        expected = {
            "type": "mcp_call", "server_label": "webiq_toolbox", "name": "webiq___web",
            "error": None, "output": "Retrieved evidence", "arguments": "{}",
        }
        for change in (
            {"server_label": "webiq"}, {"name": "different_tool"},
            {"error": "authorization failed"}, {"output": ""},
        ):
            with self.subTest(change=change):
                self.ns["tool_history"] = [SimpleNamespace(**{**expected, **change})]
                with self.assertRaisesRegex(RuntimeError, "No successful Web IQ call"):
                    self.run_cell("verify")
        self.ns["tool_history"] = [SimpleNamespace(**expected)]
        self.run_cell("verify")
        self.ns["response"].output_text = ""
        with self.assertRaisesRegex(RuntimeError, "no final answer"):
            self.run_cell("verify")

    def test_cleanup_is_opt_in_and_preserves_shared_webiq_connection(self):
        self.create_toolbox()
        self.ns["openai"] = Mock()
        self.ns["conversation"] = SimpleNamespace(id="conversation-session")
        self.ns["agent"] = SimpleNamespace(name="webiq-toolbox-agent-session", version="1")
        self.run_cell("cleanup")
        self.ns["requests"].delete.assert_not_called()
        self.project.toolboxes.delete.assert_not_called()
        self.ns["requests"].delete.return_value = http_response(200, {})
        self.run_cell(
            "cleanup",
            code_cell(NOTEBOOK, "cleanup").replace(
                "DELETE_SESSION_RESOURCES = False", "DELETE_SESSION_RESOURCES = True",
            ),
        )
        self.ns["openai"].conversations.delete.assert_called_once_with(
            conversation_id="conversation-session",
        )
        self.project.agents.delete_version.assert_called_once_with(
            agent_name="webiq-toolbox-agent-session", agent_version="1",
        )
        self.project.toolboxes.delete.assert_called_once_with("webiq-toolbox-session")
        self.ns["requests"].delete.assert_called_once()
        self.assertEqual(
            self.ns["requests"].delete.call_args.args[0],
            f"https://management.azure.com{self.ns['toolbox_connection_id']}?api-version=2025-10-01-preview",
        )


class WebIqToolboxApprovalTests(test_notebooks.AgentHelperTests):
    def namespace(self, responses, notebook_name=NOTEBOOK):
        return super().namespace(responses, notebook_name)


if __name__ == "__main__":
    unittest.main()
