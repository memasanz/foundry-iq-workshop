import ast
import json
import os
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import httpx
import nbformat
import requests
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client


ROOT = Path(__file__).resolve().parents[1]


def code_cell(notebook_name, cell_id):
    notebook = nbformat.read(ROOT / "notebooks" / notebook_name, as_version=4)
    return next(cell.source for cell in notebook.cells if cell.id == cell_id)


def rest_namespace():
    namespace = {
        "requests": Mock(),
        "time": SimpleNamespace(sleep=Mock()),
        "json": json,
        "api_key": "test-key",
        "question": "public topic",
        "web": {"webResults": [{"url": "https://example.com"}]},
    }
    return namespace


def http_response(status, data, headers=None):
    response = Mock(status_code=status, headers=headers or {})
    response.json.return_value = data
    if status >= 400:
        response.raise_for_status.side_effect = requests.HTTPError(f"HTTP {status}")
    return response


class NotebookStructureTests(unittest.TestCase):
    def test_each_notebook_starts_with_permissions_and_attendee_alias_guidance(self):
        for path in sorted((ROOT / "notebooks").glob("*.ipynb")):
            with self.subTest(notebook=path.name):
                introduction = nbformat.read(path, as_version=4).cells[0]
                self.assertEqual(introduction.cell_type, "markdown")
                self.assertIn("## Required permissions", introduction.source)
                if path.name != "01-web-iq-apis.ipynb":
                    self.assertIn("WORKSHOP_PREFIX", introduction.source)
                    self.assertIn("not enforce access isolation", introduction.source)

    def test_notebook_schema_syntax_and_clean_outputs(self):
        paths = list((ROOT / "notebooks").glob("*.ipynb"))
        required = {
            "01-web-iq-apis.ipynb", "02-foundry-web-iq-agent.ipynb",
            "03-foundry-web-iq-toolbox.ipynb",
            "04-blob-indexer-skills.ipynb", "05-blob-content-understanding.ipynb",
            "06-push-chunks-vectors.ipynb",
            "07-sharepoint-indexed.ipynb", "08-sharepoint-remote.ipynb",
            "09-foundry-iq-knowledge-base.ipynb", "10-foundry-iq-apim.ipynb",
            "11-hosted-agent.ipynb", "12-foundry-iq-toolbox-agent.ipynb",
            "13-web-iq-authentication.ipynb",
            "14-foundry-iq-webiq-only.ipynb",
        }
        self.assertEqual(required, {path.name for path in paths})
        for path in sorted(ROOT / "notebooks" / name for name in required):
            with self.subTest(notebook=path.name):
                notebook = nbformat.read(path, as_version=4)
                nbformat.validate(notebook)
                for cell in notebook.cells:
                    if cell.cell_type == "code":
                        self.assertIsNone(cell.execution_count)
                        self.assertEqual(cell.outputs, [])
                        compile(
                            cell.source, f"{path.name}:{cell.id}", "exec",
                            flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT,
                        )


class AttendeeAliasTests(unittest.TestCase):
    def configure(self, number, alias, **overrides):
        settings = {
            "AZURE_TENANT_ID": "11111111-1111-1111-1111-111111111111",
            "AZURE_SEARCH_ENDPOINT": "https://workshop.search.windows.net",
            "AZURE_SEARCH_RESOURCE_ID": "/subscriptions/test/providers/Microsoft.Search/searchServices/workshop",
            "AZURE_STORAGE_RESOURCE_ID": "/subscriptions/test/providers/Microsoft.Storage/storageAccounts/workshop",
            "AZURE_STORAGE_BLOB_ENDPOINT": "https://workshop.blob.core.windows.net",
            "AZURE_STORAGE_CONTAINER_NAME": "samples",
            "AZURE_AI_ACCOUNT_NAME": "workshop",
            "AZURE_AI_PROJECT_ENDPOINT": "https://workshop.services.ai.azure.com/api/projects/workshop",
            "AZURE_AI_PROJECT_RESOURCE_ID": "/subscriptions/test/providers/Microsoft.CognitiveServices/accounts/workshop/projects/workshop",
            "AZURE_AI_MODEL_DEPLOYMENT_NAME": "workshop-chat",
            "AZURE_AI_EMBEDDING_DEPLOYMENT_NAME": "workshop-embedding",
            "SHAREPOINT_SITE_URL": "https://contoso.sharepoint.com/sites/workshop",
            "BROKER_APP_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
            "APIM_GATEWAY_URL": "https://workshop.azure-api.net",
            "WEBIQ_API_KEY": "synthetic-test-key",
            "WORKSHOP_PREFIX": alias,
            **overrides,
        }
        notebook = next((ROOT / "notebooks").glob(f"{number:02}-*.ipynb")).name
        cell_id = "setup" if number in (2, 3, 5) else "configuration"
        namespace = {}
        with (
            patch.dict(os.environ, settings, clear=True),
            patch("dotenv.load_dotenv"),
            patch("pathlib.Path.cwd", return_value=ROOT),
            patch("azure.identity.AzureCliCredential"),
            patch("azure.identity.get_bearer_token_provider"),
            patch("azure.ai.projects.AIProjectClient"),
            patch("builtins.print"),
            patch("builtins.input", side_effect=AssertionError("Configured aliases must not prompt")),
        ):
            exec(code_cell(notebook, cell_id), namespace)
        return namespace

    def test_two_attendees_have_disjoint_created_names_and_local_folders(self):
        objects = {
            2: ("connection_name",),
            3: ("toolbox_name", "toolbox_connection_name", "agent_name"),
            4: ("index_name", "data_source_name", "skillset_name", "indexer_name", "blob_prefix"),
            5: ("index_name", "data_source_name", "skillset_name", "indexer_name", "blob_prefix"),
            6: ("index_name",),
            7: ("index_name", "data_source_name", "skillset_name", "indexer_name"),
            8: ("remote_source_name", "remote_base_name"),
            9: ("blob_source_name", "sharepoint_source_name", "webiq_source_name", "knowledge_base_name"),
            10: ("knowledge_base_name",),
            11: ("agent_name", "agent_folder"),
            12: ("knowledge_base_name", "toolbox_name", "agent_name", "agent_folder"),
            14: ("webiq_source_name", "knowledge_base_name"),
        }
        for number, keys in objects.items():
            with self.subTest(notebook=number):
                names = []
                for alias in ("alice01", "bob02"):
                    namespace = self.configure(number, alias)
                    values = {namespace[key] for key in keys}
                    for value in values:
                        name = value.name if isinstance(value, Path) else value
                        self.assertTrue(name.startswith((f"{alias}-", f"{alias}/")), name)
                    names.append(values)
                self.assertTrue(names[0].isdisjoint(names[1]))

    def test_cross_notebook_references_follow_the_same_alias(self):
        for alias in ("alice01", "bob02"):
            with self.subTest(alias=alias):
                webiq = self.configure(2, alias)
                toolbox = self.configure(3, alias)
                self.assertEqual(webiq["connection_name"], toolbox["webiq_connection_name"])
                kb = self.configure(9, alias)
                apim = self.configure(10, alias)
                hosted = self.configure(12, alias)
                self.assertEqual(kb["blob_source_name"], apim["blob_source_name"])
                self.assertEqual(kb["sharepoint_source_name"], apim["sharepoint_source_name"])
                self.assertEqual(kb["blob_source_name"], hosted["blob_source_name"])
                self.assertEqual(kb["blob_index_name"], self.configure(4, alias)["index_name"])
                self.assertEqual(kb["sharepoint_index_name"], self.configure(7, alias)["index_name"])
                self.assertEqual(apim["webiq_source_name"], "iq-workshop-webiq")
                self.assertEqual(hosted["connection_name"], "iq-workshop-search-mcp")

    def test_toolbox_name_is_stable_and_prefixed_across_sessions(self):
        first = self.configure(3, "alice01")
        second = self.configure(3, "alice01")
        self.assertEqual(first["toolbox_name"], "alice01-webiq-toolbox")
        self.assertEqual(second["toolbox_name"], first["toolbox_name"])
        self.assertNotEqual(first["toolbox_connection_name"], second["toolbox_connection_name"])
        self.assertNotEqual(first["agent_name"], second["agent_name"])
        self.assertTrue(first["toolbox_connection_name"].startswith("alice01-webiq-toolbox-"))

    def test_webiq_connection_name_is_derived_only_from_prefix(self):
        for number in (2, 3):
            for name in ("webiq-mcp", "bob02-webiq-mcp", "alice01-custom"):
                with self.subTest(notebook=number, connection=name):
                    namespace = self.configure(number, "alice01", WEBIQ_CONNECTION_NAME=name)
                    key = "connection_name" if number == 2 else "webiq_connection_name"
                    self.assertEqual(namespace[key], "alice01-webiq-mcp")

    def test_new_webiq_alias_validation_rejects_invalid_names(self):
        for number in (2, 3):
            for alias in ("ALICE", "a", "1alice", "alice/bob", "a" * 21):
                with self.subTest(notebook=number, alias=alias):
                    with self.assertRaisesRegex(ValueError, "3-20 lowercase"):
                        self.configure(number, alias)

    def test_prompt_agents_and_conversations_carry_the_attendee_alias(self):
        for number in (2, 3):
            for alias in ("alice01", "bob02"):
                with self.subTest(notebook=number, alias=alias):
                    namespace = self.configure(number, alias)
                    namespace.update(
                        toolbox_mcp_url="https://workshop.services.ai.azure.com/toolboxes/test/mcp",
                        web_tool_name="webiq___web",
                    )
                    notebook = next((ROOT / "notebooks").glob(f"{number:02}-*.ipynb")).name
                    with patch("builtins.print"), patch.dict(
                        os.environ, {"AZURE_AI_MODEL_DEPLOYMENT_NAME": "workshop-chat"},
                    ):
                        exec(code_cell(notebook, "create-agent"), namespace)
                    arguments = namespace["project"].agents.create_version.call_args.kwargs
                    self.assertTrue(arguments["agent_name"].startswith(f"{alias}-"))
                    namespace["openai"].conversations.create.assert_called_once_with(
                        metadata={"attendee": alias},
                    )


class DirectApiCellTests(unittest.TestCase):
    def setUp(self):
        self.ns = rest_namespace()
        self.post = self.ns["requests"].post

    def run_cell(self, cell_id):
        with patch("builtins.print") as output:
            exec(code_cell("01-web-iq-apis.ipynb", cell_id), self.ns)
        return output

    def test_all_apis_run_even_with_legacy_service_flags_disabled(self):
        settings = {
            f"WEBIQ_ENABLE_{service}": "false"
            for service in ("BROWSE", "NEWS", "IMAGES", "VIDEOS")
        }
        with patch.dict(os.environ, settings):
            self.post.return_value = http_response(
                200, {"webResults": [{"url": "https://example.com"}]},
            )
            for cell_id in (
                "web-search", "browse", "news", "images", "videos",
            ):
                with self.subTest(cell=cell_id):
                    self.post.reset_mock()
                    self.run_cell(cell_id)
                    self.post.assert_called_once()

    def test_search_endpoint_headers_and_payload(self):
        self.post.return_value = http_response(200, {"webResults": [{"url": "https://example.com"}]})
        self.run_cell("web-search")
        self.assertEqual(len(self.ns["web"]["webResults"]), 1)
        self.post.assert_called_once_with(
            "https://api.microsoft.ai/v3/search/web",
            headers={"x-apikey": "test-key", "Content-Type": "application/json"},
            json={
                "query": self.ns["question"], "maxResults": 10,
                "contentFormat": "passage", "maxLength": 3000,
                "language": "en", "region": "US", "safeSearch": "strict",
            },
            timeout=(10, 90),
        )

    def test_each_exercise_posts_directly_and_displays_response(self):
        endpoints = {
            "localized-search": "search/web", "news": "search/news",
            "images": "search/images", "videos": "search/videos",
        }
        for cell_id, endpoint in endpoints.items():
            with self.subTest(cell=cell_id):
                self.post.reset_mock()
                self.post.return_value = http_response(200, {"traceId": "test-trace"})
                self.run_cell(cell_id)
                self.assertEqual(self.post.call_count, 1)
                self.assertEqual(self.post.call_args.args[0], f"https://api.microsoft.ai/v3/{endpoint}")
                self.assertEqual(self.post.call_args.kwargs["headers"]["x-apikey"], "test-key")
                self.assertEqual(self.post.call_args.kwargs["timeout"], (10, 90))
                self.post.return_value.raise_for_status.assert_called_once()

    def test_browse_pending_honors_duration(self):
        self.post.side_effect = [
            http_response(202, {"retryAfter": "60s", "traceId": "pending"}),
            http_response(200, {"content": "page"}),
        ]
        self.run_cell("browse")
        self.assertEqual(self.ns["page"]["content"], "page")
        self.ns["time"].sleep.assert_called_once_with(60)
        self.assertEqual(self.post.call_count, 2)
        self.assertEqual(self.post.call_args.kwargs["json"]["url"], "https://example.com")

    def test_browse_requests_passages_without_full_document_permission(self):
        self.post.return_value = http_response(200, {"content": "Relevant excerpt"})
        self.run_cell("browse")
        self.assertEqual(self.post.call_args.kwargs["json"], {
            "url": "https://example.com",
            "query": self.ns["question"],
            "contentFormat": "passage",
            "maxLength": 3000,
            "liveCrawl": "fallback",
        })

    def test_image_previews_show_thumbnails_and_source_pages(self):
        self.ns["images"] = {"imageResults": [
            {"title": "Datacenter", "hostPageUrl": "https://example.com/article",
             "thumbnailUrl": "https://example.com/thumb.jpg",
             "url": "https://example.com/full.jpg"},
            {"url": "https://example.com/second.jpg"},
            {"title": "No preview"},
        ]}
        with patch("IPython.display.Image") as image, patch("IPython.display.display") as display:
            output = self.run_cell("image-previews")
        self.assertEqual(image.call_args_list, [
            unittest.mock.call(url="https://example.com/thumb.jpg", width=400, embed=False),
            unittest.mock.call(url="https://example.com/second.jpg", width=400, embed=False),
        ])
        self.assertEqual(display.call_count, 2)
        printed = [call.args for call in output.call_args_list]
        self.assertIn(("Title:", "Datacenter"), printed)
        self.assertIn(("Source page:", "https://example.com/article"), printed)
        self.assertIn(("No preview URL available.",), printed)
        self.post.assert_not_called()

    def test_image_previews_handle_empty_results(self):
        self.ns["images"] = {}
        with patch("IPython.display.display") as display:
            output = self.run_cell("image-previews")
        display.assert_not_called()
        output.assert_called_once_with("No images returned. Try another image search query.")

    def test_http_failure_is_not_hidden_or_retried(self):
        for status in (401, 403, 429, 503):
            with self.subTest(status=status):
                self.post.reset_mock()
                self.post.return_value = http_response(status, {"traceId": "failure"})
                with self.assertRaisesRegex(requests.HTTPError, f"HTTP {status}"):
                    self.run_cell("web-search")
                self.assertEqual(self.post.call_count, 1)

    def test_exhausted_crawl_is_not_success(self):
        self.post.return_value = http_response(202, {"retryAfter": "1s"})
        with self.assertRaises(TimeoutError):
            self.run_cell("browse")
        self.assertEqual(self.post.call_count, 4)
        self.assertEqual(self.ns["time"].sleep.call_count, 3)

    def test_missing_crawl_delay_is_explicit(self):
        self.post.return_value = http_response(202, {"traceId": "bad-contract"})
        with self.assertRaisesRegex(RuntimeError, "omitted retryAfter"):
            self.run_cell("browse")

    def test_excessive_crawl_delay_does_not_sleep(self):
        self.post.return_value = http_response(202, {"retryAfter": "121s"})
        with self.assertRaises(TimeoutError):
            self.run_cell("browse")
        self.ns["time"].sleep.assert_not_called()

    def test_empty_search_response_is_displayed_without_fabricated_fields(self):
        self.post.return_value = http_response(200, {})
        self.run_cell("web-search")
        self.assertEqual(self.ns["web"], {})

class AgentDiscoveryTests(unittest.IsolatedAsyncioTestCase):
    async def discover(
        self, pages, notebook_name="02-foundry-web-iq-agent.ipynb",
        cell_id="discover", headers=None,
    ):
        headers = headers or {"x-apikey": "test-key"}
        cursors = []

        def handle(request):
            for name, value in headers.items():
                self.assertEqual(request.headers[name], value)
            if "Authorization" in headers:
                self.assertNotIn("x-apikey", request.headers)
            if request.method == "GET":
                return httpx.Response(405)
            if request.method == "DELETE":
                return httpx.Response(200)
            message = json.loads(request.content)
            if message["method"] == "initialize":
                result = {
                    "protocolVersion": message["params"]["protocolVersion"],
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "test-webiq", "version": "1"},
                }
            elif message["method"] == "notifications/initialized":
                return httpx.Response(202)
            elif message["method"] == "tools/list":
                cursors.append(message.get("params", {}).get("cursor"))
                result = pages[len(cursors) - 1]
            else:
                self.fail(f"Unexpected MCP method: {message['method']}")
            return httpx.Response(
                200, json={"jsonrpc": "2.0", "id": message["id"], "result": result},
            )

        client = httpx.AsyncClient(
            headers=headers, transport=httpx.MockTransport(handle),
        )
        factory = Mock(return_value=client)
        namespace = {
            "httpx": SimpleNamespace(AsyncClient=factory),
            "streamable_http_client": streamable_http_client,
            "ClientSession": ClientSession,
            "mcp_url": "https://api.microsoft.ai/v3/mcp",
            "api_key": "test-key",
            "credential": Mock(get_token=Mock(return_value=SimpleNamespace(token="foundry-token"))),
            "toolbox_mcp_url": "https://example.services.ai.azure.com/api/projects/test/toolboxes/webiq/versions/1/mcp?api-version=v1",
        }
        source = code_cell(notebook_name, cell_id)
        try:
            with patch("builtins.print"):
                await eval(compile(source, "discover", "exec", flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT), namespace)
        finally:
            self.assertTrue(client.is_closed)
        factory.assert_called_once_with(headers=headers, timeout=60)
        return namespace, cursors

    async def test_authenticated_discovery_uses_real_mcp_client_and_paginates(self):
        def tool(name):
            return {"name": name, "description": name, "inputSchema": {"type": "object"}}

        namespace, cursors = await self.discover([
            {"tools": [tool("news")], "nextCursor": "page-2"},
            {"tools": [tool("web")]},
        ])
        self.assertEqual(namespace["available_names"], {"news", "web"})
        self.assertEqual(cursors, [None, "page-2"])

    async def test_discovery_without_web_tool_fails_explicitly(self):
        with self.assertRaisesRegex(RuntimeError, "does not expose web search"):
            await self.discover([{"tools": []}])

    async def test_toolbox_discovery_uses_foundry_token_and_namespaced_tools(self):
        namespace, cursors = await self.discover(
            [
                {"tools": [], "nextCursor": "page-2"},
                {"tools": [{
                    "name": "webiq___web", "description": "Web IQ web search",
                    "inputSchema": {"type": "object"},
                }]},
            ],
            notebook_name="03-foundry-web-iq-toolbox.ipynb",
            cell_id="discover-toolbox", headers={"Authorization": "Bearer foundry-token"},
        )
        self.assertEqual(cursors, [None, "page-2"])
        self.assertEqual(namespace["web_tool_name"], "webiq___web")
        namespace["credential"].get_token.assert_called_once_with("https://ai.azure.com/.default")

    async def test_toolbox_discovery_rejects_missing_namespaced_web_tool(self):
        with self.assertRaisesRegex(RuntimeError, "does not expose webiq___web"):
            await self.discover(
                [{"tools": []}], notebook_name="03-foundry-web-iq-toolbox.ipynb",
                cell_id="discover-toolbox", headers={"Authorization": "Bearer foundry-token"},
            )


class WebIqAgentReuseTests(unittest.TestCase):
    def setUp(self):
        from azure.ai.projects.models import MCPTool, PromptAgentDefinition

        self.project = Mock()
        self.project.agents.list.return_value = []
        self.project.agents.create_version.side_effect = lambda **kwargs: SimpleNamespace(
            name=kwargs["agent_name"], version="2",
        )
        self.ns = {
            "prefix": "alice01", "project": self.project, "os": os,
            "mcp_url": "https://api.microsoft.ai/v3/mcp",
            "connection_id": "/projects/test/connections/alice01-webiq-mcp",
            "MCPTool": MCPTool, "PromptAgentDefinition": PromptAgentDefinition,
        }

    def existing_agent(self, name, url="https://api.microsoft.ai/v3/mcp"):
        definition = self.ns["PromptAgentDefinition"](
            model="old-model",
            tools=[self.ns["MCPTool"](server_label="webiq", server_url=url)],
        )
        return SimpleNamespace(
            name=name, versions=SimpleNamespace(latest=SimpleNamespace(definition=definition)),
        )

    def run_cell(self):
        with patch.dict(os.environ, {"AZURE_AI_MODEL_DEPLOYMENT_NAME": "workshop-chat"}), patch("builtins.print"):
            exec(code_cell("02-foundry-web-iq-agent.ipynb", "create-agent"), self.ns)

    def test_rerun_updates_same_agent_and_disables_tool_approvals(self):
        self.run_cell()
        self.project.agents.list.return_value = [self.existing_agent("alice01-webiq-agent")]
        self.run_cell()
        calls = self.project.agents.create_version.call_args_list
        self.assertEqual([call.kwargs["agent_name"] for call in calls], [
            "alice01-webiq-agent", "alice01-webiq-agent",
        ])
        for call in calls:
            definition = call.kwargs["definition"]
            self.assertEqual(definition.model, "workshop-chat")
            self.assertEqual(definition.tools[0].require_approval, "never")
            self.assertEqual(definition.tools[0].allowed_tools, ["web"])
            self.assertEqual(definition.tools[0].project_connection_id, self.ns["connection_id"])
        self.assertEqual(self.project.get_openai_client().conversations.create.call_count, 2)

    def test_reuses_newest_legacy_match_without_touching_other_agents(self):
        self.project.agents.list.return_value = [
            self.existing_agent("bob02-webiq-agent-new"),
            self.existing_agent("alice01x-webiq-agent-new"),
            self.existing_agent("alice01-webiq-toolbox-agent-new"),
            self.existing_agent("alice01-webiq-agent-other", "https://other.example/mcp"),
            self.existing_agent("alice01-webiq-agent-newest"),
            self.existing_agent("alice01-webiq-agent-older"),
        ]
        self.run_cell()
        self.project.agents.list.assert_called_once_with(order="desc")
        self.assertEqual(
            self.project.agents.create_version.call_args.kwargs["agent_name"],
            "alice01-webiq-agent-newest",
        )

    def test_stable_name_takes_precedence_over_legacy_names(self):
        self.project.agents.list.return_value = [
            self.existing_agent("alice01-webiq-agent-newest"),
            self.existing_agent("alice01-webiq-agent"),
        ]
        self.run_cell()
        self.assertEqual(
            self.project.agents.create_version.call_args.kwargs["agent_name"],
            "alice01-webiq-agent",
        )

    def test_name_collision_does_not_overwrite_unrelated_agent(self):
        self.project.agents.list.return_value = [
            self.existing_agent("alice01-webiq-agent", "https://other.example/mcp"),
        ]
        with self.assertRaisesRegex(RuntimeError, "not a Web IQ prompt agent"):
            self.run_cell()
        self.project.agents.create_version.assert_not_called()

    def test_missing_settings_fail_without_interactive_prompts(self):
        for setting in ("WEBIQ_API_KEY", "WORKSHOP_PREFIX"):
            with self.subTest(setting=setting), self.assertRaisesRegex(RuntimeError, setting):
                AttendeeAliasTests().configure(2, "alice01", **{setting: ""})


class AgentHelperTests(unittest.TestCase):
    def namespace(self, responses, notebook_name="03-foundry-web-iq-toolbox.ipynb"):
        client = Mock()
        client.responses.create.side_effect = responses
        namespace = {
            "openai": client,
            "conversation": SimpleNamespace(id="conversation-1"),
            "agent_reference": {"type": "agent_reference", "name": "test", "version": "1"},
        }
        exec(code_cell(notebook_name, "agent-helper"), namespace)
        return namespace

    def test_approval_continues_same_conversation(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments='{"query":"public"}',
        )
        tool_call = SimpleNamespace(type="mcp_call")
        first = SimpleNamespace(id="r1", output=[approval])
        last = SimpleNamespace(id="r2", output=[tool_call], status="completed")
        ns = self.namespace([first, last])
        with patch("builtins.input", return_value="y"):
            response, history = ns["ask_agent"]("public question")
        self.assertIs(response, last)
        self.assertEqual(len(history), 2)
        continuation = ns["openai"].responses.create.call_args.kwargs
        self.assertEqual(continuation["conversation"], "conversation-1")
        self.assertTrue(continuation["input"][0]["approve"])
        self.assertNotIn("previous_response_id", continuation)

    def test_follow_up_resolves_approvals_in_original_conversation(self):
        for notebook in ("03-foundry-web-iq-toolbox.ipynb",):
            with self.subTest(notebook=notebook):
                pending = set()
                turns = []

                def create_response(**kwargs):
                    self.assertEqual(kwargs.get("conversation"), "conversation-1")
                    self.assertNotIn("previous_response_id", kwargs)
                    if isinstance(kwargs["input"], str):
                        self.assertFalse(pending, "Previous approval was not resolved.")
                        turns.append(kwargs["input"])
                        approval_id = f"approval-{len(turns)}"
                        pending.add(approval_id)
                        return SimpleNamespace(
                            id=f"request-{len(turns)}",
                            output=[SimpleNamespace(
                                type="mcp_approval_request", id=approval_id,
                                server_label="webiq", name="web", arguments="{}",
                            )],
                        )
                    for decision in kwargs["input"]:
                        self.assertTrue(decision["approve"])
                        pending.remove(decision["approval_request_id"])
                    return SimpleNamespace(
                        id=f"completed-{len(turns)}", output=[], status="completed",
                    )

                ns = self.namespace(create_response, notebook_name=notebook)
                with patch("builtins.input", return_value="y"), patch("builtins.print"):
                    ns["ask_agent"]("Initial research question")
                    ns["ask_agent"]("Follow-up research question")
                self.assertEqual(len(turns), 2)
                self.assertFalse(pending)

    def test_approval_defaults_to_denied(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        ns = self.namespace([
            SimpleNamespace(id="r1", output=[approval]),
            SimpleNamespace(id="r2", output=[], status="completed"),
        ])
        with patch("builtins.input", return_value=""):
            ns["ask_agent"]("public question")
        decision = ns["openai"].responses.create.call_args.kwargs["input"][0]
        self.assertFalse(decision["approve"])

    def test_failed_response_is_not_success(self):
        for notebook in ("02-foundry-web-iq-agent.ipynb", "03-foundry-web-iq-toolbox.ipynb"):
            with self.subTest(notebook=notebook):
                ns = self.namespace([SimpleNamespace(id="r1", output=[], status="failed")], notebook)
                with self.assertRaisesRegex(RuntimeError, "status failed"):
                    ns["ask_agent"]("public question")

    def test_noninteractive_questions_and_follow_up_share_conversation(self):
        first = SimpleNamespace(id="r1", output=[SimpleNamespace(type="mcp_call")], status="completed")
        last = SimpleNamespace(id="r2", output=[], status="completed")
        ns = self.namespace([first, last], "02-foundry-web-iq-agent.ipynb")
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")):
            response, history = ns["ask_agent"]("public question")
            follow_up, _ = ns["ask_agent"]("follow-up question")
        self.assertIs(response, first)
        self.assertEqual(history, first.output)
        self.assertIs(follow_up, last)
        for call in ns["openai"].responses.create.call_args_list:
            self.assertEqual(call.kwargs["conversation"], "conversation-1")

    def test_unexpected_approval_fails_without_prompting(self):
        ns = self.namespace([
            SimpleNamespace(id="r1", output=[SimpleNamespace(type="mcp_approval_request")]),
        ], "02-foundry-web-iq-agent.ipynb")
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")):
            with self.assertRaisesRegex(RuntimeError, "Unexpected tool approval"):
                ns["ask_agent"]("public question")
        self.assertEqual(ns["openai"].responses.create.call_count, 1)

    def test_final_allowed_approval_can_complete(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        final = SimpleNamespace(id="r2", output=[], status="completed")
        ns = self.namespace([SimpleNamespace(id="r1", output=[approval]), final])
        with patch("builtins.input", return_value="y"):
            response, _ = ns["ask_agent"]("public question", max_approval_rounds=1)
        self.assertIs(response, final)

    def test_approval_limit_is_bounded(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        ns = self.namespace([SimpleNamespace(id="r1", output=[approval])])
        with self.assertRaisesRegex(RuntimeError, "Approval-round limit"):
            ns["ask_agent"]("public question", max_approval_rounds=0)
        self.assertEqual(ns["openai"].responses.create.call_count, 1)


if __name__ == "__main__":
    unittest.main()
