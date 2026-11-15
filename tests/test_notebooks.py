import ast
import json
import os
import unittest
from datetime import datetime, timezone
from io import StringIO
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

import httpx
import nbformat
import requests
from dotenv import load_dotenv
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from mcp.types import PaginatedRequestParams


ROOT = Path(__file__).resolve().parents[1]


def code_cell(notebook_name, cell_id):
    notebook = nbformat.read(ROOT / "python-notebooks" / notebook_name, as_version=4)
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
    response.text = json.dumps(data)
    response.json.return_value = data
    if status >= 400:
        response.raise_for_status.side_effect = requests.HTTPError(f"HTTP {status}")
    return response


class NotebookStructureTests(unittest.TestCase):
    def test_each_notebook_starts_with_permissions_and_attendee_alias_guidance(self):
        for path in sorted((ROOT / "python-notebooks").glob("*.ipynb")):
            with self.subTest(notebook=path.name):
                introduction = nbformat.read(path, as_version=4).cells[0]
                self.assertEqual(introduction.cell_type, "markdown")
                self.assertIn("## Required permissions", introduction.source)
                if path.name != "01-web-iq-apis.ipynb":
                    self.assertIn("WORKSHOP_PREFIX", introduction.source)
                    self.assertIn("not enforce access isolation", introduction.source)

    def test_notebook_schema_syntax_and_clean_outputs(self):
        paths = list((ROOT / "python-notebooks").glob("*.ipynb"))
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
        for path in sorted(ROOT / "python-notebooks" / name for name in required):
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
    def configure(self, number, alias, working_directory=ROOT, dotenv_loader=None, **overrides):
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
        notebook = next((ROOT / "python-notebooks").glob(f"{number:02}-*.ipynb")).name
        cell_id = "setup" if number in (2, 3, 5, 13) else "configuration"
        namespace = {}
        with (
            patch.dict(os.environ, settings, clear=True),
            patch("dotenv.load_dotenv", side_effect=dotenv_loader),
            patch("pathlib.Path.cwd", return_value=working_directory),
            patch("azure.identity.AzureCliCredential"),
            patch("azure.identity.get_bearer_token_provider"),
            patch("azure.ai.projects.AIProjectClient"),
            patch("builtins.print"),
            patch("builtins.input", side_effect=AssertionError("Configured aliases must not prompt")),
        ):
            exec(code_cell(notebook, cell_id), namespace)
        return namespace

    def test_configuration_supports_the_renamed_python_notebooks_directory(self):
        for number in range(2, 15):
            with self.subTest(notebook=number):
                self.configure(number, "alice01", working_directory=ROOT / "python-notebooks")

    def test_foundry_setup_loads_parent_env_files_and_overrides_stale_settings(self):
        files = {
            r"..\.env": "WEBIQ_API_KEY=env-test-key\nWORKSHOP_PREFIX=alice01\n",
            r"..\.env.azure": (
                "WORKSHOP_PREFIX=bob02\n"
                "AZURE_AI_PROJECT_ENDPOINT=https://fresh.services.ai.azure.com/api/projects/workshop\n"
            ),
        }

        def load_settings(path, override=False):
            return load_dotenv(stream=StringIO(files[path]), override=override)

        for number in (2, 3, 4):
            with self.subTest(notebook=number):
                loader = Mock(side_effect=load_settings)
                namespace = self.configure(number, "processalias", dotenv_loader=loader)
                self.assertEqual([call.args[0] for call in loader.call_args_list],
                                 [r"..\.env", r"..\.env.azure"])
                self.assertTrue(all(call.kwargs["override"] for call in loader.call_args_list))
                self.assertEqual(namespace["prefix"], "bob02")
                expected_client = {
                    "endpoint": "https://fresh.services.ai.azure.com/api/projects/workshop",
                    "credential": namespace["credential"],
                }
                if number == 2:
                    self.assertEqual(namespace["api_key"], "env-test-key")
                    self.assertEqual(namespace["connection_name"], "bob02-webiq-mcp")
                elif number == 3:
                    self.assertEqual(namespace["webiq_connection_name"], "bob02-webiq-mcp")
                    expected_client["allow_preview"] = True
                else:
                    self.assertEqual(namespace["index_name"], "bob02-blob-chunks")
                if number in (2, 3):
                    namespace["AIProjectClient"].assert_called_once_with(**expected_client)

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
            12: ("knowledge_base_name", "connection_name", "toolbox_name", "agent_name", "agent_folder"),
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
                self.assertEqual(hosted["connection_name"], f"{alias}-iq-search-mcp")
                self.assertEqual(hosted["connection_id"], f"{hosted['project_resource_id']}/connections/{alias}-iq-search-mcp")

    def test_toolbox_connection_and_agent_names_are_stable_and_prefixed_across_runs(self):
        first = self.configure(3, "alice01")
        second = self.configure(3, "alice01")
        self.assertEqual(first["toolbox_name"], "alice01-webiq-toolbox")
        self.assertEqual(second["toolbox_name"], first["toolbox_name"])
        self.assertEqual(first["toolbox_connection_name"], "alice01-webiq-toolbox-connection")
        self.assertEqual(second["toolbox_connection_name"], first["toolbox_connection_name"])
        self.assertEqual(first["toolbox_connection_id"], second["toolbox_connection_id"])
        self.assertEqual(first["agent_name"], "alice01-webiq-toolbox-agent")
        self.assertEqual(second["agent_name"], first["agent_name"])

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
                        tool_names=["webiq___web", "webiq___news"],
                    )
                    notebook = next((ROOT / "python-notebooks").glob(f"{number:02}-*.ipynb")).name
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

    def test_setup_loads_parent_env_files_without_prompting(self):
        def load_settings(path, override=False):
            if path == r"..\.env":
                os.environ["WEBIQ_API_KEY"] = "test-key"

        with (
            patch.dict(os.environ, {}, clear=True),
            patch("dotenv.load_dotenv", side_effect=load_settings) as load,
            patch("getpass.getpass") as prompt,
        ):
            output = self.run_cell("setup")
        self.assertEqual([call.args[0] for call in load.call_args_list],
                         [r"..\.env", r"..\.env.azure"])
        self.assertTrue(all(call.kwargs["override"] for call in load.call_args_list))
        self.assertEqual(self.ns["api_key"], "test-key")
        prompt.assert_not_called()
        self.assertNotIn("test-key", str(output.call_args_list))

    def test_setup_requires_configured_api_key(self):
        with patch.dict(os.environ, {}, clear=True), patch("dotenv.load_dotenv"):
            with self.assertRaisesRegex(KeyError, "WEBIQ_API_KEY"):
                self.run_cell("setup")

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
                output = self.run_cell(cell_id)
                self.assertEqual(self.post.call_count, 1)
                self.assertEqual(self.post.call_args.args[0], f"https://api.microsoft.ai/v3/{endpoint}")
                self.assertEqual(self.post.call_args.kwargs["headers"]["x-apikey"], "test-key")
                self.assertEqual(self.post.call_args.kwargs["timeout"], (10, 90))
                self.post.return_value.raise_for_status.assert_not_called()
                self.assertFalse(any(call.args and call.args[0] == "HTTP:"
                                     for call in output.call_args_list))

    def test_browse_pending_honors_duration(self):
        self.post.side_effect = [
            http_response(202, {"retryAfter": "60s", "traceId": "pending"}),
            http_response(200, {"content": "page"}),
        ]
        output = self.run_cell("browse")
        self.assertEqual(self.ns["page"]["content"], "page")
        self.ns["time"].sleep.assert_called_once_with(60)
        self.assertEqual(self.post.call_count, 2)
        self.assertEqual(self.post.call_args.kwargs["json"]["url"], "https://example.com")
        self.assertFalse(any(call.args and call.args[0] == "HTTP:"
                             for call in output.call_args_list))

    def test_browse_pending_honors_utc_timestamp(self):
        for timestamp, seconds in (
            ("2026-11-14T16:00:30Z", 30),
            ("2026-11-14T15:59:30Z", 0),
            ("2026-11-14T16:03:00Z", None),
        ):
            with self.subTest(timestamp=timestamp):
                self.ns["time"].sleep.reset_mock()
                self.post.side_effect = [
                    http_response(202, {"retryAfter": timestamp}),
                    http_response(200, {"content": "page"}),
                ]
                with patch("datetime.datetime") as clock:
                    clock.now.return_value = datetime(2026, 11, 14, 16, tzinfo=timezone.utc)
                    clock.fromisoformat.side_effect = datetime.fromisoformat
                    if seconds is None:
                        output = self.run_cell("browse")
                        self.assertIn("retry later manually", str(output.call_args_list))
                        self.ns["time"].sleep.assert_not_called()
                        self.assertEqual(self.ns["page"], {})
                    else:
                        self.run_cell("browse")
                        self.ns["time"].sleep.assert_called_once_with(seconds)
                        self.assertEqual(self.ns["page"]["content"], "page")

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

    def test_http_failure_is_displayed_without_raising_or_reusing_results(self):
        result_names = {
            "web-search": "web", "localized-search": "localized", "browse": "page",
            "news": "news", "images": "images", "videos": "videos",
        }
        for status in (401, 403, 429, 503):
            for cell_id, result_name in result_names.items():
                with self.subTest(status=status, cell=cell_id):
                    self.post.reset_mock()
                    self.ns["web"] = {"webResults": [{"url": "https://example.com"}]}
                    self.ns[result_name] = {"stale": True} if result_name != "web" else self.ns["web"]
                    response = http_response(status, {"traceId": "failure"})
                    response.reason = "Request failed"
                    response.text = "error detail " + "x" * 8000
                    self.post.return_value = response
                    with patch("builtins.print") as output:
                        exec(code_cell("01-web-iq-apis.ipynb", cell_id), self.ns)
                    self.assertEqual(output.call_args_list[-2:], [
                        unittest.mock.call("HTTP:", status, response.reason),
                        unittest.mock.call(response.text[:8000]),
                    ])
                    response.json.assert_not_called()
                    response.raise_for_status.assert_not_called()
                    self.assertEqual(self.ns[result_name], {})
                    self.assertEqual(self.post.call_count, 1)

    def test_exhausted_crawl_is_not_success(self):
        self.post.return_value = http_response(202, {"retryAfter": "1s"})
        output = self.run_cell("browse")
        output.assert_called_with('Browse did not complete after four requests. Retry later.')
        self.assertEqual(self.ns["page"], {})
        self.assertEqual(self.post.call_count, 4)
        self.assertEqual(self.ns["time"].sleep.call_count, 3)

    def test_missing_crawl_delay_is_explicit(self):
        self.post.return_value = http_response(202, {"traceId": "bad-contract"})
        output = self.run_cell("browse")
        output.assert_called_with('Pending crawl omitted retryAfter. Retry later manually.')
        self.ns["time"].sleep.assert_not_called()
        self.assertEqual(self.ns["page"], {})

    def test_excessive_crawl_delay_does_not_sleep(self):
        self.post.return_value = http_response(202, {"retryAfter": "121s"})
        output = self.run_cell("browse")
        self.assertIn("retry later manually", str(output.call_args_list))
        self.ns["time"].sleep.assert_not_called()
        self.assertEqual(self.ns["page"], {})

    def test_missing_browse_url_prints_message_without_request(self):
        for web in ({}, {"webResults": []}, {"webResults": [{}]}):
            with self.subTest(web=web):
                self.ns["web"] = web
                self.ns["page"] = {"stale": True}
                output = self.run_cell("browse")
                output.assert_called_once_with('No source URL to browse. Change the search query and rerun search.')
                self.post.assert_not_called()
                self.assertEqual(self.ns["page"], {})

    def test_invalid_crawl_delay_prints_message_without_sleeping(self):
        self.post.return_value = http_response(202, {"retryAfter": "not-a-timestamp"})
        output = self.run_cell("browse")
        output.assert_called_with('Could not interpret retryAfter. Retry later manually.')
        self.ns["time"].sleep.assert_not_called()
        self.assertEqual(self.ns["page"], {})

    def test_unexpected_browse_status_does_not_parse_response(self):
        response = http_response(204, {})
        response.reason = "No Content"
        response.text = ""
        self.post.return_value = response
        output = self.run_cell("browse")
        self.assertIn(unittest.mock.call("HTTP:", 204, "No Content"), output.call_args_list)
        response.json.assert_not_called()
        self.ns["time"].sleep.assert_not_called()
        self.assertEqual(self.ns["page"], {})

    def test_empty_search_response_is_displayed_without_fabricated_fields(self):
        self.post.return_value = http_response(200, {})
        self.run_cell("web-search")
        self.assertEqual(self.ns["web"], {})

class AgentDiscoveryTests(unittest.IsolatedAsyncioTestCase):
    async def discover(
        self, pages, notebook_name="02-foundry-web-iq-agent.ipynb",
        cell_id="discover", headers=None, failure=None, failure_method="initialize",
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
            if message["method"] == failure_method and failure is not None:
                if isinstance(failure, BaseException):
                    raise failure
                return failure
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
        def create_client(**options):
            client.event_hooks = options.get("event_hooks", {})
            return client

        factory = Mock(side_effect=create_client)
        namespace = {
            "httpx": SimpleNamespace(
                AsyncClient=factory, HTTPStatusError=httpx.HTTPStatusError,
                TransportError=httpx.TransportError,
            ),
            "streamable_http_client": streamable_http_client,
            "ClientSession": ClientSession,
            "PaginatedRequestParams": PaginatedRequestParams,
            "mcp_url": "https://api.microsoft.ai/v3/mcp",
            "api_key": "test-key",
            "available_tools": [SimpleNamespace(name="stale-tool")],
            "available_names": {"stale-tool"},
            "credential": Mock(get_token=Mock(return_value=SimpleNamespace(token="foundry-token"))),
            "toolbox_mcp_url": "https://example.services.ai.azure.com/api/projects/test/toolboxes/webiq/versions/1/mcp?api-version=v1",
        }
        source = code_cell(notebook_name, cell_id)
        try:
            with patch("builtins.print") as output:
                await eval(compile(source, "discover", "exec", flags=ast.PyCF_ALLOW_TOP_LEVEL_AWAIT), namespace)
        finally:
            self.assertTrue(client.is_closed)
        expected_options = {"headers": headers, "timeout": 60}
        if notebook_name == "02-foundry-web-iq-agent.ipynb":
            expected_options["event_hooks"] = {"response": [namespace["read_mcp_error_body"]]}
        factory.assert_called_once_with(**expected_options)
        namespace["printed_output"] = output.call_args_list
        return namespace, cursors

    async def test_authenticated_discovery_uses_real_mcp_client_and_paginates(self):
        def tool(name):
            return {"name": name, "description": name, "inputSchema": {"type": "object"}}

        with patch.object(
            ClientSession, "list_tools", autospec=True, side_effect=ClientSession.list_tools,
        ) as list_tools:
            namespace, cursors = await self.discover([
                {"tools": [tool("news")], "nextCursor": "page-2"},
                {"tools": [tool("web")]},
            ])
        self.assertEqual(namespace["available_names"], {"news", "web"})
        self.assertEqual(cursors, [None, "page-2"])
        self.assertEqual(
            [call.kwargs["params"].cursor for call in list_tools.call_args_list],
            [None, "page-2"],
        )
        for call in list_tools.call_args_list:
            self.assertNotIn("cursor", call.kwargs)

    async def test_discovery_without_web_tool_fails_explicitly(self):
        with self.assertRaisesRegex(RuntimeError, "does not expose web search"):
            await self.discover([{"tools": []}])

    async def test_discovery_http_errors_print_streamed_bodies_and_clear_stale_tools(self):
        for status in (401, 429):
            for method in ("initialize", "tools/list"):
                with self.subTest(status=status, method=method):
                    body = "Authentication or rate-limit error: " + "x" * 9000

                    class ErrorBody(httpx.AsyncByteStream):
                        async def __aiter__(self):
                            yield body.encode()

                    failure = httpx.Response(
                        status, stream=ErrorBody(),
                        headers={"retry-after": "30", "retry-after-ms": "30000"},
                    )
                    ns, _ = await self.discover([], failure=failure, failure_method=method)
                    self.assertTrue(ns["discovery_failed"])
                    self.assertEqual(ns["available_tools"], [])
                    self.assertEqual(ns["available_names"], set())
                    self.assertTrue(failure.is_closed)
                    self.assertEqual(failure.text, body)
                    calls = ns["printed_output"]
                    self.assertTrue(any(call.args[:2] == ("HTTP:", status) for call in calls))
                    self.assertTrue(any(call.args == (body[:8000],) for call in calls))
                    if status == 429:
                        self.assertTrue(any(call.args == ("retry-after:", "30") for call in calls))
                    self.assertNotIn("Traceback", str(calls))
                    self.assertNotIn("test-key", str(calls))

    async def test_discovery_handles_transport_errors_and_nested_http_error_groups(self):
        request = httpx.Request("POST", "https://example.com/mcp")
        http_error = httpx.HTTPStatusError(
            "Unauthorized", request=request,
            response=httpx.Response(401, text="Nested authentication failure", request=request),
        )
        failures = (
            httpx.ReadTimeout("Request timed out", request=request),
            ExceptionGroup("outer", [ExceptionGroup("inner", [http_error])]),
        )
        for failure in failures:
            with self.subTest(failure=type(failure).__name__):
                ns, _ = await self.discover([], failure=failure)
                self.assertTrue(ns["discovery_failed"])
                self.assertEqual(ns["available_tools"], [])
                self.assertEqual(ns["available_names"], set())
                printed = str(ns["printed_output"])
                self.assertIn(
                    "Request timed out" if isinstance(failure, httpx.ReadTimeout) else "Nested authentication failure",
                    printed,
                )
                self.assertNotIn("Traceback", printed)

    async def test_discovery_does_not_hide_unexpected_programming_errors(self):
        with self.assertRaises(ExceptionGroup):
            await self.discover([], failure=RuntimeError("Unexpected failure"))

    async def test_toolbox_discovery_uses_foundry_token_and_namespaced_tools(self):
        with patch.object(
            ClientSession, "list_tools", autospec=True, side_effect=ClientSession.list_tools,
        ) as list_tools:
            namespace, cursors = await self.discover(
                [
                    {"tools": [], "nextCursor": "page-2"},
                    {"tools": [{
                        "name": "webiq___web", "description": "Web IQ web search",
                        "inputSchema": {"type": "object"},
                    }, {
                        "name": "webiq___news", "description": "Web IQ news search",
                        "inputSchema": {"type": "object"},
                    }]},
                ],
                notebook_name="03-foundry-web-iq-toolbox.ipynb",
                cell_id="discover-toolbox", headers={"Authorization": "Bearer foundry-token"},
            )
        self.assertEqual(cursors, [None, "page-2"])
        self.assertEqual(
            [call.kwargs["params"].cursor for call in list_tools.call_args_list],
            [None, "page-2"],
        )
        for call in list_tools.call_args_list:
            self.assertNotIn("cursor", call.kwargs)
        self.assertEqual(namespace["web_tool_name"], "webiq___web")
        self.assertEqual(namespace["tool_names"], ["webiq___web", "webiq___news"])
        namespace["credential"].get_token.assert_called_once_with("https://ai.azure.com/.default")

    async def test_toolbox_discovery_rejects_missing_news_tool(self):
        with self.assertRaisesRegex(RuntimeError, "does not expose webiq___news"):
            await self.discover(
                [{"tools": [{
                    "name": "webiq___web", "description": "Web IQ web search",
                    "inputSchema": {"type": "object"},
                }]}],
                notebook_name="03-foundry-web-iq-toolbox.ipynb",
                cell_id="discover-toolbox", headers={"Authorization": "Bearer foundry-token"},
            )

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
            self.assertEqual(definition.tools[0].allowed_tools, ["web", "browse"])
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

    def test_toolbox_completed_response_returns_tool_history_without_prompting(self):
        tool_call = SimpleNamespace(type="mcp_call", name="webiq___news")
        completed = SimpleNamespace(id="r1", output=[tool_call], status="completed")
        ns = self.namespace([completed])
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")):
            response, history = ns["ask_agent"]("public question")
        self.assertIs(response, completed)
        self.assertEqual(history, [tool_call])
        ns["openai"].responses.create.assert_called_once_with(
            conversation="conversation-1", input="public question",
            extra_body={"agent_reference": ns["agent_reference"]},
        )

    def test_toolbox_follow_up_uses_original_conversation_without_approvals(self):
        ns = self.namespace([
            SimpleNamespace(id="r1", output=[], status="completed"),
            SimpleNamespace(id="r2", output=[], status="completed"),
        ])
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")):
            ns["ask_agent"]("Initial research question")
            ns["ask_agent"]("Follow-up research question")
        calls = ns["openai"].responses.create.call_args_list
        self.assertEqual(len(calls), 2)
        self.assertEqual([call.kwargs["input"] for call in calls],
                         ["Initial research question", "Follow-up research question"])
        for call in calls:
            self.assertEqual(call.kwargs["conversation"], "conversation-1")
            self.assertNotIn("previous_response_id", call.kwargs)

    def test_toolbox_unexpected_approval_fails_without_prompting_or_submitting_a_decision(self):
        approval = SimpleNamespace(
            type="mcp_approval_request", id="approval-1",
            server_label="webiq", name="web", arguments="{}",
        )
        ns = self.namespace([SimpleNamespace(id="r1", output=[approval])])
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")):
            with self.assertRaisesRegex(RuntimeError, "Unexpected tool approval"):
                ns["ask_agent"]("public question")
        ns["openai"].responses.create.assert_called_once()

    def test_failed_response_is_not_success(self):
        for notebook in ("03-foundry-web-iq-toolbox.ipynb",):
            with self.subTest(notebook=notebook):
                ns = self.namespace([SimpleNamespace(id="r1", output=[], status="failed")], notebook)
                with self.assertRaisesRegex(RuntimeError, "status failed"):
                    ns["ask_agent"]("public question")

    def test_noninteractive_http_errors_clear_stale_answers_and_print_bounded_diagnostics(self):
        from openai import APIStatusError, RateLimitError

        for status, error_type in ((429, RateLimitError), (500, APIStatusError)):
            with self.subTest(status=status):
                body = "Request failed: " + "x" * 9000
                http_error = httpx.Response(
                    status, text=body, headers={"retry-after": "30", "retry-after-ms": "30000"},
                    request=httpx.Request("POST", "https://example.com/responses"),
                )
                error = error_type("Request failed", response=http_error, body=None)
                ns = self.namespace([error], "02-foundry-web-iq-agent.ipynb")
                ns.update(
                    response=SimpleNamespace(output_text="stale answer"),
                    tool_history=[SimpleNamespace(type="mcp_call")],
                    calls=["stale call"], successful=["stale success"],
                )
                with patch("builtins.print") as output:
                    exec(code_cell("02-foundry-web-iq-agent.ipynb", "ask"), ns)
                    exec(code_cell("02-foundry-web-iq-agent.ipynb", "verify"), ns)
                    follow_up = code_cell("02-foundry-web-iq-agent.ipynb", "follow-up")
                    exec(follow_up, ns)
                self.assertIsNone(ns["response"])
                self.assertEqual(ns["tool_history"], [])
                self.assertEqual(ns["calls"], [])
                self.assertEqual(ns["successful"], [])
                ns["openai"].responses.create.assert_called_once()
                self.assertTrue(any(call.args[:2] == ("HTTP:", status) for call in output.call_args_list))
                self.assertTrue(any(call.args == (body[:8000],) for call in output.call_args_list))
                if status == 429:
                    self.assertTrue(any(call.args == ("retry-after:", "30") for call in output.call_args_list))
                    self.assertTrue(any(call.args == ("retry-after-ms:", "30000") for call in output.call_args_list))
                printed = str(output.call_args_list)
                self.assertNotIn("stale answer", printed)
                self.assertNotIn("Traceback", printed)

    def test_noninteractive_follow_up_connection_error_does_not_display_old_answer(self):
        from openai import APIConnectionError

        error = APIConnectionError(request=httpx.Request("POST", "https://example.com/responses"))
        ns = self.namespace([error], "02-foundry-web-iq-agent.ipynb")
        ns.update(
            response=SimpleNamespace(output_text="initial answer"),
            follow_up=SimpleNamespace(output_text="stale follow-up"),
            follow_up_history=["stale call"],
        )
        source = code_cell("02-foundry-web-iq-agent.ipynb", "follow-up")
        with patch("builtins.print") as output:
            exec(source, ns)
        self.assertIsNone(ns["follow_up"])
        self.assertEqual(ns["follow_up_history"], [])
        ns["openai"].responses.create.assert_called_once()
        self.assertTrue(any(call.args[0] == "Request failed:" for call in output.call_args_list))
        self.assertNotIn("stale follow-up", str(output.call_args_list))

    def test_noninteractive_failed_or_incomplete_response_prints_details(self):
        for status in ("failed", "incomplete"):
            with self.subTest(status=status):
                response = SimpleNamespace(
                    id="r1", output=[], status=status,
                    error={"message": "request failed"} if status == "failed" else None,
                    incomplete_details={"reason": "max_output_tokens"} if status == "incomplete" else None,
                )
                ns = self.namespace([response], "02-foundry-web-iq-agent.ipynb")
                with patch("builtins.print") as output:
                    result, history = ns["ask_agent"]("public question")
                self.assertIsNone(result)
                self.assertEqual(history, [])
                printed = str(output.call_args_list)
                self.assertIn(f"status {status}", printed)
                self.assertIn("request failed" if status == "failed" else "max_output_tokens", printed)

    def test_noninteractive_verification_reports_failures_without_claiming_success(self):
        for error, evidence, answer, expected in (
            ("HTTP 429", None, "Retrieval failed.", "No successful Web IQ MCP retrieval"),
            (None, "Retrieved evidence", "", "Retrieval ran but no final answer"),
            (None, "Retrieved evidence", "Answer with sources.", "Observed 1 successful"),
        ):
            with self.subTest(expected=expected):
                ns = {
                    "response": SimpleNamespace(output_text=answer),
                    "tool_history": [SimpleNamespace(
                        type="mcp_call", server_label="webiq", name="web",
                        error=error, output=evidence, arguments="{}",
                    )],
                }
                with patch("builtins.print") as output:
                    exec(code_cell("02-foundry-web-iq-agent.ipynb", "verify"), ns)
                printed = str(output.call_args_list)
                self.assertIn(expected, printed)
                if not expected.startswith("Observed"):
                    self.assertNotIn("Observed", printed)

    def test_noninteractive_questions_and_follow_up_share_conversation(self):
        first = SimpleNamespace(id="r1", output=[SimpleNamespace(type="mcp_call")], status="completed")
        last = SimpleNamespace(id="r2", output=[], status="completed", output_text="Follow-up answer.")
        ns = self.namespace([first, last], "02-foundry-web-iq-agent.ipynb")
        with patch("builtins.input", side_effect=AssertionError("Must not prompt")), patch("builtins.print"):
            response, history = ns["ask_agent"]("public question")
            ns["response"] = response
            exec(code_cell("02-foundry-web-iq-agent.ipynb", "follow-up"), ns)
        self.assertIs(response, first)
        self.assertEqual(history, first.output)
        self.assertIs(ns["follow_up"], last)
        self.assertEqual(ns["openai"].responses.create.call_count, 2)
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


if __name__ == "__main__":
    unittest.main()
