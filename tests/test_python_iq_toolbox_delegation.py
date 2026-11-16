"""Execute Python lab 14 cells offline; these checks do not prove live ACL enforcement."""

import ast
import asyncio
import base64
import io
import json
from pathlib import Path
import shutil
import time
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, Mock
import uuid
from contextlib import redirect_stdout


ROOT = Path(__file__).resolve().parents[1]
NOTEBOOK = ROOT / "python-notebooks" / "14-foundry-iq-toolbox-agent.ipynb"
TENANT = "11111111-1111-1111-1111-111111111111"
ALICE = "22222222-2222-2222-2222-222222222222"
BOB = "33333333-3333-3333-3333-333333333333"
SEARCH_ID = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Search/searchServices/search"
PROJECT_ID = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry/projects/project"


def token(oid=ALICE, **overrides):
    claims = {
        "oid": oid, "tid": TENANT, "aud": "https://ai.azure.com",
        "scp": "user_impersonation", "exp": int(time.time()) + 3600,
    }
    claims.update(overrides)
    encoded = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=")
    return f"header.{encoded}.synthetic-signature"


def http_response(status, body):
    response = Mock(status_code=status)
    response.json.return_value = body
    if status >= 400:
        response.raise_for_status.side_effect = RuntimeError(f"HTTP {status}")
    return response


def completed_response(body=None):
    response = Mock(status="completed", output_text="Synthetic permitted evidence.", output=[])
    response.model_dump.return_value = body or {"status": "completed", "output": []}
    return response


class PythonIqDelegationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = NOTEBOOK.read_bytes()
        cls.notebook = json.loads(cls.original)
        cls.sources = {
            cell["id"]: "".join(cell["source"])
            for cell in cls.notebook["cells"] if cell["cell_type"] == "code"
        }

    @classmethod
    def tearDownClass(cls):
        if NOTEBOOK.read_bytes() != cls.original:
            raise AssertionError("Offline execution changed the notebook.")

    def setUp(self):
        self.output = io.StringIO()
        self.ns = {
            "requests": Mock(), "UUID": uuid.UUID, "uuid": uuid,
            "search_token": lambda: "synthetic-search-token",
            "arm_token": lambda: "synthetic-arm-token",
            "prefix": "alice01", "search_endpoint": "https://search.search.windows.net",
            "search_resource_id": SEARCH_ID, "project_resource_id": PROJECT_ID,
            "project_endpoint": "https://foundry.services.ai.azure.com/api/projects/project",
            "api_version": "2026-08-01-preview",
            "sharepoint_source_name": "alice01-kb-sharepoint",
            "sharepoint_index_name": "alice01-nb7-index",
            "knowledge_base_name": "alice01-toolbox-kb",
            "knowledge_base_mcp_url": "https://search.search.windows.net/knowledgebases/alice01-toolbox-kb/mcp",
            "connection_name": "alice01-iq-search-mcp",
            "connection_id": PROJECT_ID + "/connections/alice01-iq-search-mcp",
            "toolbox_name": "alice01-iq-toolbox", "agent_name": "alice01-iq-agent",
            "project_client": Mock(), "tenant_id": TENANT,
            "base64": base64, "json": json, "time": time,
        }

    def execute(self, cell):
        with redirect_stdout(self.output):
            exec(compile(self.sources[cell], f"<lab14:{cell}>", "exec"), self.ns)

    def source_and_index(self):
        source = {
            "kind": "searchIndex",
            "searchIndexParameters": {
                "searchIndexName": "alice01-nb7-index",
                "semanticConfigurationName": "semantic-config",
                "searchFields": [{"name": "content"}],
                "sourceDataFields": [{"name": name} for name in
                                     ("id", "content", "sourceFile", "webUrl", "page", "pageTo")],
            },
        }
        index = {
            "permissionFilterOption": "enabled",
            "fields": [
                {"name": name, "type": "Edm.String", "retrievable": True}
                for name in ("id", "content", "sourceFile", "webUrl", "page", "pageTo")
            ] + [
                {"name": name, "type": "Collection(Edm.String)", "permissionFilter": permission,
                 "filterable": True, "retrievable": False}
                for name, permission in (("UserIds", "userIds"), ("GroupIds", "groupIds"))
            ],
            "semantic": {"configurations": [{
                "name": "semantic-config",
                "prioritizedFields": {"prioritizedContentFields": [{"fieldName": "content"}]},
            }]},
        }
        return source, index

    def prepare_kb(self, source, index):
        base = {"models": [{"kind": "azureOpenAI", "azureOpenAIParameters": {"modelName": "model"}}],
                "answerInstructions": "Cite evidence."}
        self.ns["requests"].get.side_effect = [
            http_response(200, source), http_response(200, index), http_response(200, base),
        ]
        self.ns["requests"].put.return_value = http_response(200, {})
        return base

    def test_exact_sharepoint_source_and_acl_schema_checked_before_only_kb_write(self):
        base = self.prepare_kb(*self.source_and_index())
        self.execute("create-knowledge-base")
        requests = self.ns["requests"]
        self.assertEqual(len(requests.get.call_args_list), 3)
        self.assertIn("/indexes/alice01-nb7-index?", requests.get.call_args_list[1].args[0])
        requests.put.assert_called_once()
        body = requests.put.call_args.kwargs["json"]
        self.assertEqual(body["knowledgeSources"], [{"name": "alice01-kb-sharepoint"}])
        self.assertEqual(body["models"], base["models"])
        self.assertEqual(body["answerInstructions"], base["answerInstructions"])
        self.assertEqual(body["retrievalReasoningEffort"], {"kind": "medium"})
        self.assertEqual(body["outputMode"], "answerSynthesis")
        self.assertIn("/knowledgebases/alice01-toolbox-kb?", requests.put.call_args.args[0])

    def test_invalid_source_or_missing_acl_semantic_or_content_schema_prevents_kb_write(self):
        for defect in ("wrong-index", "wrong-kind", "source-fields", "permissions-disabled",
                       "missing-user", "wrong-group-kind", "group-retrievable",
                       "group-not-filterable", "group-type", "semantic", "content"):
            with self.subTest(defect=defect):
                source, index = self.source_and_index()
                if defect == "wrong-index":
                    source["searchIndexParameters"]["searchIndexName"] = "alice01-cu-chunks"
                elif defect == "wrong-kind":
                    source["kind"] = "remoteSharePoint"
                elif defect == "source-fields":
                    source["searchIndexParameters"]["sourceDataFields"] = []
                elif defect == "permissions-disabled":
                    index["permissionFilterOption"] = "disabled"
                elif defect == "missing-user":
                    index["fields"] = [f for f in index["fields"] if f["name"] != "UserIds"]
                elif defect.startswith("group") or defect == "wrong-group-kind":
                    field = next(f for f in index["fields"] if f["name"] == "GroupIds")
                    key, value = {
                        "wrong-group-kind": ("permissionFilter", "userIds"),
                        "group-retrievable": ("retrievable", True),
                        "group-not-filterable": ("filterable", False),
                        "group-type": ("type", "Edm.String"),
                    }[defect]
                    field[key] = value
                elif defect == "semantic":
                    index["semantic"] = {}
                else:
                    next(f for f in index["fields"] if f["name"] == "content")["retrievable"] = False
                self.prepare_kb(source, index)
                self.ns["requests"].put.reset_mock()
                with self.assertRaises(RuntimeError):
                    self.execute("create-knowledge-base")
                self.ns["requests"].put.assert_not_called()

    def test_project_managed_identity_connection_and_toolbox_promotion(self):
        client = self.ns["project_client"]
        client.toolboxes.create_version.return_value = SimpleNamespace(version="2")
        client.toolboxes.get.return_value = SimpleNamespace(default_version="1")
        client.toolboxes.update.return_value = SimpleNamespace(
            name="alice01-iq-toolbox", default_version="2")
        self.ns["requests"].put.return_value = http_response(200, {})
        self.execute("create-toolbox")
        call = self.ns["requests"].put.call_args
        self.assertEqual(call.kwargs["json"], {"properties": {
            "category": "RemoteTool", "authType": "ProjectManagedIdentity",
            "target": self.ns["knowledge_base_mcp_url"], "audience": "https://search.azure.com",
            "isSharedToAll": True, "metadata": {"ApiType": "Azure"},
        }})
        tool = client.toolboxes.create_version.call_args.kwargs["tools"][0]
        self.assertEqual(tool.project_connection_id, self.ns["connection_id"])
        self.assertEqual(tool.server_url, self.ns["knowledge_base_mcp_url"])
        client.toolboxes.update.assert_called_once_with("alice01-iq-toolbox", default_version="2")

    def test_notebook_uses_deployment_prerequisite_instead_of_assigning_search_roles(self):
        self.assertNotIn("grant-search-access", self.sources)
        code = "\n".join(self.sources.values())
        self.assertNotIn("/roleAssignments", code)
        intro = "".join(next(cell for cell in self.notebook["cells"]
                             if cell["id"] == "introduction")["source"])
        for required in ("Required permissions", "Search Index Data Reader",
                         "Foundry project's managed identity", "deployment script",
                         "../scripts/deploy.ps1", "does not assign roles"):
            self.assertIn(required, intro)

    def prepare_invocations(self, tokens, result=None):
        credentials = []
        for value in tokens:
            credential = MagicMock()
            credential.__enter__.return_value = credential
            credential.get_token.return_value = SimpleNamespace(token=value)
            credentials.append(credential)
        self.ns["AzureCliCredential"] = Mock(side_effect=credentials)
        project_factory = MagicMock()
        project = project_factory.return_value.__enter__.return_value
        openai = project.get_openai_client.return_value.__enter__.return_value
        openai.responses.create.return_value = result or completed_response()
        self.ns["AIProjectClient"] = project_factory
        return credentials, project, openai

    def test_app_only_missing_or_invalid_user_tokens_block_before_question_network(self):
        invalid = [
            token(idtyp="app"), token(scp=None), token(oid=None), token(oid="not-a-guid"),
            token(tid=BOB), token(aud="https://search.azure.com"),
            token(exp=int(time.time()) - 1), token(exp=None), token(exp=True),
            "not-a-jwt", "header.!!!!.signature",
        ]
        for value in invalid:
            with self.subTest(value=invalid.index(value)):
                self.prepare_invocations([value])
                with self.assertRaisesRegex(PermissionError, "delegated USER token"):
                    self.execute("invoke")
                self.ns["AIProjectClient"].assert_not_called()
                self.assertNotIn(value, self.output.getvalue())

    def test_alice_bob_calls_get_fresh_credentials_and_no_conversation_or_token_leak(self):
        alice_token, bob_token = token(ALICE), token(BOB)
        credentials, project, openai = self.prepare_invocations([alice_token, bob_token])
        self.execute("invoke")
        question = "What is our policy for booking international travel?"
        with redirect_stdout(self.output):
            self.ns["ask_as_current_user"](question)
        self.assertEqual(self.ns["AzureCliCredential"].call_count, 2)
        for credential in credentials:
            credential.get_token.assert_called_once_with("https://ai.azure.com/.default")
        self.assertEqual(openai.responses.create.call_count, 2)
        for call in openai.responses.create.call_args_list:
            self.assertEqual(call.kwargs, {"input": question, "store": False})
        auth_calls = project.get_openai_client.call_args_list
        self.assertEqual([c.kwargs["api_key"] for c in auth_calls], [alice_token, bob_token])
        self.assertTrue(all(c.kwargs["max_retries"] == 0 and c.kwargs["timeout"] == 120
                            for c in auth_calls))
        for identity in (ALICE, BOB):
            self.assertIn(identity, self.output.getvalue())
        for secret in (alice_token, bob_token):
            self.assertNotIn(secret, self.output.getvalue())

    def test_consent_nested_tool_failure_and_network_error_stop_without_retry(self):
        for body in (
            {"output": [{"type": "oauth_consent_request"}]},
            {"output": [{"type": "mcp_call", "output": '{"type":"oauth_consent_request"}'}]},
            {"output": [{"type": "mcp_call", "error": {"code": "Forbidden"}}]},
            {"output": [{"type": "mcp_call", "output": '{"isError":true,"content":[]}'}]},
        ):
            with self.subTest(body=body):
                self.output = io.StringIO()
                _, _, openai = self.prepare_invocations([token()], completed_response(body))
                with self.assertRaises((PermissionError, RuntimeError)):
                    self.execute("invoke")
                openai.responses.create.assert_called_once()
                self.assertNotIn("Synthetic permitted evidence.", self.output.getvalue())
        _, _, openai = self.prepare_invocations([token()])
        openai.responses.create.side_effect = RuntimeError("sensitive raw transport details")
        with self.assertRaisesRegex(RuntimeError, "Stop; inspect user"):
            self.execute("invoke")
        openai.responses.create.assert_called_once()
        self.assertNotIn("sensitive raw transport details", self.output.getvalue())

    def test_generated_host_guard_blocks_missing_user_or_call_before_agent_execution(self):
        folder = ROOT / ".workshop" / ("python-iq14-" + uuid.uuid4().hex)
        self.addCleanup(shutil.rmtree, folder)
        self.ns["agent_folder"] = folder
        self.execute("write-agent-files")
        source = (folder / "main.py").read_text(encoding="utf-8")
        tree = ast.parse(source)
        guard = next(node for node in tree.body
                     if isinstance(node, ast.ClassDef) and node.name == "RequireDelegatedContext")
        request = SimpleNamespace(user_id=ALICE, call_id="request-alice")
        context = {"AgentMiddleware": object, "get_request_context": lambda: request}
        exec(compile(ast.Module(body=[guard], type_ignores=[]), "<actual-generated-guard>", "exec"), context)
        middleware = context["RequireDelegatedContext"]()
        calls = []

        async def next_call():
            calls.append("model/tool execution")

        for user, call in ((None, "call"), (ALICE, None), ("", ""), (None, None)):
            request.user_id, request.call_id = user, call
            with self.assertRaises(PermissionError):
                asyncio.run(middleware.process(None, next_call))
            self.assertEqual(calls, [])
        request.user_id, request.call_id = BOB, "request-bob"
        asyncio.run(middleware.process(None, next_call))
        self.assertEqual(calls, ["model/tool execution"])
        agent_call = next(node for node in ast.walk(tree) if isinstance(node, ast.Call)
                          and isinstance(node.func, ast.Name) and node.func.id == "Agent")
        middleware_arg = next(k.value for k in agent_call.keywords if k.arg == "middleware")
        self.assertEqual(ast.unparse(middleware_arg), "[RequireDelegatedContext()]")
        self.assertIn("FoundryToolbox(credential)", source)
        self.assertIn("ResponsesHostServer(agent).run()", source)
        self.assertNotIn("x-agent-user-id", source)
        self.assertNotIn("x-ms-query-source-authorization", source)

    def test_cleanup_remains_opt_in_and_only_four_owned_objects(self):
        self.execute("cleanup")
        requests, client = self.ns["requests"], self.ns["project_client"]
        requests.delete.assert_not_called()
        client.agents.delete.assert_not_called()
        requests.delete.return_value = http_response(204, {})
        with redirect_stdout(self.output):
            exec(self.sources["cleanup"].replace("CLEAN_UP = False", "CLEAN_UP = True"), self.ns)
        client.agents.delete.assert_called_once_with("alice01-iq-agent")
        client.toolboxes.delete.assert_called_once_with("alice01-iq-toolbox")
        self.assertEqual(requests.delete.call_count, 2)
        urls = [call.args[0] for call in requests.delete.call_args_list]
        self.assertIn("/connections/alice01-iq-search-mcp?", urls[0])
        self.assertIn("/knowledgebases/alice01-toolbox-kb?", urls[1])


if __name__ == "__main__":
    unittest.main()
