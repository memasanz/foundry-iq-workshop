import base64
import json
import os
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import Mock, patch

from test_notebooks import ROOT, code_cell, http_response

APIM = "10-foundry-iq-apim.ipynb"
BROKER_ID = "44444444-4444-4444-4444-444444444444"


def make_token(claims):
    payload = base64.urlsafe_b64encode(json.dumps(claims).encode()).decode().rstrip("=")
    return f"header.{payload}.signature"


def namespace():
    return {
        "requests": Mock(),
        "json": json,
        "base64": base64,
        "search_token": lambda: "search-token",
        "broker_token": lambda: make_token({"aud": BROKER_ID, "scp": "user_impersonation", "upn": "a@contoso.com"}),
        "broker_client_id": BROKER_ID,
        "search_endpoint": "https://workshop.search.windows.net",
        "apim_url": "https://apim.azure-api.net",
        "api_version": "2026-08-01-preview",
        "prefix": "alice01",
        "blob_source_name": "alice01-kb-blob",
        "sharepoint_source_name": "alice01-kb-sharepoint",
        "webiq_source_name": "iq-workshop-webiq",
        "knowledge_base_name": "alice01-apim-kb",
        "source_kinds": {
            "alice01-kb-blob": "azureBlob",
            "alice01-kb-sharepoint": "indexedSharePoint",
            "iq-workshop-webiq": "mcpServer",
        },
    }


def run(cell_id, ns):
    with patch("builtins.print"):
        exec(code_cell(APIM, cell_id), ns)


class ApimNotebookTests(unittest.TestCase):
    def setUp(self):
        self.ns = namespace()
        self.http = self.ns["requests"]
        for method in ("put", "post", "get", "delete"):
            getattr(self.http, method).return_value = http_response(200, {})

    def test_token_cell_rejects_wrong_audience(self):
        run("inspect-token", self.ns)
        self.ns["broker_token"] = lambda: make_token({"aud": "other"})
        with self.assertRaises(RuntimeError):
            run("inspect-token", self.ns)

    def test_broker_token_requests_resource_uri_without_permission_suffix(self):
        settings = {
            "AZURE_TENANT_ID": "11111111-1111-1111-1111-111111111111",
            "AZURE_SEARCH_ENDPOINT": self.ns["search_endpoint"],
            "BROKER_APP_CLIENT_ID": BROKER_ID,
            "APIM_GATEWAY_URL": self.ns["apim_url"],
            "WORKSHOP_PREFIX": "alice01",
        }
        token = make_token({"aud": BROKER_ID, "scp": "user_impersonation"})
        with (
            patch.dict(os.environ, settings, clear=True),
            patch("dotenv.load_dotenv"),
            patch("pathlib.Path.cwd", return_value=ROOT),
            patch("azure.identity._credentials.azure_cli._run_command",
                  return_value=json.dumps({"accessToken": token, "expires_on": 4102444800})) as cli,
        ):
            run("configuration", self.ns)
            run("inspect-token", self.ns)
        arguments = cli.call_args.args[0]
        self.assertEqual(arguments[arguments.index("--resource") + 1], f"api://{BROKER_ID}")

    def test_token_cell_rejects_missing_delegated_scope(self):
        self.ns["broker_token"] = lambda: make_token({"aud": BROKER_ID, "scp": "User.Read"})
        with self.assertRaisesRegex(RuntimeError, "missing user_impersonation"):
            run("inspect-token", self.ns)

    def test_shared_webiq_source_is_read_without_modification(self):
        self.http.get.return_value = http_response(200, {
            "kind": "mcpServer",
            "mcpServerParameters": {"serverURL": "https://api.microsoft.ai/v3/mcp"},
        })
        run("create-webiq-source", self.ns)
        self.assertIn("/knowledgesources/iq-workshop-webiq", self.http.get.call_args.args[0])
        self.http.put.assert_not_called()
        self.http.post.assert_not_called()
        self.http.delete.assert_not_called()

    def test_missing_or_incorrect_shared_source_requires_gateway_setup(self):
        for status, body in (
            (404, {}),
            (200, {"kind": "azureBlob"}),
            (200, {"kind": "mcpServer", "mcpServerParameters": {"serverURL": "https://other.example/mcp"}}),
        ):
            with self.subTest(status=status, source=body):
                self.http.get.return_value = http_response(status, body)
                with self.assertRaisesRegex(RuntimeError, "setup"):
                    run("create-webiq-source", self.ns)
                self.http.put.assert_not_called()

    def test_knowledge_base_reuses_notebook_08_sources(self):
        def get(url, **kwargs):
            if "/knowledgebases/" in url:
                return http_response(200, {
                    "models": [{"kind": "azureOpenAI"}], "answerInstructions": "x",
                    "retrievalInstructions": "Use alice01-kb-webiq with contentFormat passage.",
                })
            kind = "azureBlob" if "kb-blob" in url else "indexedSharePoint"
            return http_response(200, {"kind": kind})
        self.http.get.side_effect = get
        self.ns.pop("source_kinds")
        run("create-knowledge-base", self.ns)
        body = self.http.put.call_args.kwargs["json"]
        self.assertEqual([s["name"] for s in body["knowledgeSources"]],
                         ["alice01-kb-blob", "alice01-kb-sharepoint", "iq-workshop-webiq"])
        self.assertEqual(self.ns["source_kinds"]["alice01-kb-sharepoint"], "indexedSharePoint")
        self.assertEqual(body["retrievalInstructions"], "Use iq-workshop-webiq with contentFormat passage.")

    def test_web_reference_urls_are_displayed_from_source_data(self):
        url = "https://www.python.org/downloads/"
        for data in (
            {"content": json.dumps({"url": url, "content": "evidence"})},
            {"url": url, "content": "plain text evidence"},
            {"url": url},
        ):
            self.http.post.return_value = http_response(200, {
                "response": [], "activity": [],
                "references": [{"type": "mcpServer", "id": "0", "sourceData": data}],
            })
            for cell_id in ("ask-internal", "ask-web", "ask-mixed"):
                with self.subTest(cell=cell_id, data=data), patch("builtins.print") as output:
                    exec(code_cell(APIM, cell_id), self.ns)
                    self.assertIn(url, str(output.call_args_list))
                    parameters = self.http.post.call_args.kwargs["json"]["knowledgeSourceParams"]
                    web = next(p for p in parameters if p["kind"] == "mcpServer")
                    self.assertTrue(web["includeReferenceSourceData"])

    def test_knowledge_base_requires_notebook_08(self):
        self.http.get.return_value = http_response(404, {})
        with self.assertRaisesRegex(RuntimeError, "notebook 09"):
            run("create-knowledge-base", self.ns)

    def test_ask_sends_only_the_broker_token_to_apim(self):
        self.http.post.return_value = http_response(200, {"response": [], "references": [], "activity": []})
        for cell_id in ("ask-internal", "ask-web", "ask-mixed"):
            run(cell_id, self.ns)
            call = self.http.post.call_args
            self.assertTrue(call.args[0].startswith("https://apim.azure-api.net/iq/knowledgebases/alice01-apim-kb/retrieve"))
            headers = call.kwargs["headers"]
            self.assertEqual(list(headers), ["Authorization"])
            self.assertEqual(headers["Authorization"], f"Bearer {self.ns['broker_token']()}")

    def test_rejected_calls_pass_only_when_both_401(self):
        self.http.post.return_value = http_response(401, {})
        run("rejected-calls", self.ns)
        self.http.post.return_value = http_response(200, {})
        with self.assertRaises(RuntimeError):
            run("rejected-calls", self.ns)

    def test_cleanup_deletes_only_own_knowledge_base(self):
        run("cleanup", self.ns)
        self.http.delete.assert_not_called()
        source = code_cell(APIM, "cleanup").replace("CLEAN_UP = False", "CLEAN_UP = True")
        with patch("builtins.print"):
            exec(source, self.ns)
        self.http.delete.assert_called_once()
        self.assertIn("/knowledgebases/alice01-apim-kb", self.http.delete.call_args.args[0])


class PolicyScriptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        text = (ROOT / "scripts" / "setup-apim-policy.ps1").read_text(encoding="utf-8")
        cls.policy = re.search(r"@'\r?\n(<policies>.*?</policies>)\r?\n'@", text, re.S).group(1)
        cls.script = text

    def test_policy_is_well_formed_xml(self):
        root = ET.fromstring(self.policy)
        self.assertEqual([child.tag for child in root], ["inbound", "backend", "outbound", "on-error"])

    def test_policy_validates_and_swaps_tokens(self):
        for expected in (
            "validate-jwt", "user_impersonation", "on_behalf_of", "https://search.azure.com/.default",
            'name="x-ms-query-source-authorization"', "authentication-managed-identity",
        ):
            self.assertIn(expected, self.policy)

    def test_policy_removes_elevated_read_and_hides_secrets(self):
        self.assertRegex(self.policy, r'name="x-ms-enable-elevated-read" exists-action="delete"')
        for named_value in ("broker-client-secret", "webiq-api-key"):
            self.assertIn("{{%s}}" % named_value, self.policy)
        self.assertNotRegex(self.policy, r"client_secret=[A-Za-z0-9~._-]{20,}")

    def test_validate_jwt_comes_before_obo(self):
        self.assertLess(self.policy.index("validate-jwt"), self.policy.index("send-request"))

    def test_scripts_do_not_print_secrets(self):
        app = (ROOT / "scripts" / "setup-broker-app.ps1").read_text(encoding="utf-8")
        self.assertIn("secret = $true", app)
        self.assertNotRegex(app, r"Write-Host[^\n]*\$secret")
        self.assertNotRegex(self.script, r"Write-Host[^\n]*\$webIqKey")


if __name__ == "__main__":
    unittest.main()
