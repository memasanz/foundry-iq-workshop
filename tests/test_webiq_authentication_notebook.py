import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import requests

from test_notebooks import code_cell, http_response


NOTEBOOK = "12-web-iq-authentication.ipynb"


class WebIqAuthenticationTests(unittest.TestCase):
    def setUp(self):
        self.ns = {
            "auth_mode": "client_secret",
            "client_id": "caller-client-id",
            "tenant_id": "tenant-id",
            "webiq_app_id": "",
            "scope": "https://api.microsoft.ai/.default",
            "getpass": Mock(getpass=Mock(return_value="test-secret")),
            "ClientSecretCredential": Mock(),
            "ManagedIdentityCredential": Mock(),
            "requests": Mock(),
            "json": __import__("json"),
        }

    def run_cell(self, cell_id):
        with patch("builtins.print") as output:
            exec(code_cell(NOTEBOOK, cell_id), self.ns)
        return output

    def test_local_app_uses_explicit_credentials_without_printing_secret(self):
        output = self.run_cell("credential")
        self.ns["ClientSecretCredential"].assert_called_once_with(
            tenant_id="tenant-id", client_id="caller-client-id", client_secret="test-secret",
        )
        self.ns["ManagedIdentityCredential"].assert_not_called()
        self.assertNotIn("client_secret", self.ns)
        self.assertNotIn("test-secret", str(output.call_args_list))

    def test_managed_identity_modes_do_not_prompt_for_a_secret(self):
        for mode, expected in (
            ("system_assigned", {}),
            ("user_assigned", {"client_id": "caller-client-id"}),
        ):
            with self.subTest(mode=mode):
                self.ns["auth_mode"] = mode
                self.ns["ManagedIdentityCredential"].reset_mock()
                self.run_cell("credential")
                self.ns["ManagedIdentityCredential"].assert_called_once_with(**expected)
                self.ns["getpass"].getpass.assert_not_called()
                self.ns["ClientSecretCredential"].assert_not_called()

    def test_missing_configuration_fails_before_requesting_a_secret(self):
        for name, value in (("client_id", ""), ("tenant_id", ""), ("auth_mode", "interactive")):
            with self.subTest(setting=name):
                old = self.ns[name]
                self.ns[name] = value
                with self.assertRaises(ValueError):
                    self.run_cell("credential")
                self.ns["getpass"].getpass.assert_not_called()
                self.ns[name] = old

    def test_empty_secret_is_rejected_and_removed(self):
        self.ns["getpass"].getpass.return_value = " "
        with self.assertRaisesRegex(ValueError, "client secret value"):
            self.run_cell("credential")
        self.ns["ClientSecretCredential"].assert_not_called()
        self.assertNotIn("client_secret", self.ns)

    def prepare_search(self, status=200):
        self.ns["credential"] = Mock(
            get_token=Mock(return_value=SimpleNamespace(token="test-bearer-token")),
        )
        self.ns["requests"].post.return_value = http_response(status, {"webResults": []})

    def test_search_uses_webiq_scope_bearer_only_and_optional_profile_selection(self):
        for app_id in ("", "webiq-profile-id"):
            with self.subTest(app_id=app_id):
                self.prepare_search()
                self.ns["requests"].post.reset_mock()
                self.ns["webiq_app_id"] = app_id
                output = self.run_cell("bearer-search")
                self.ns["credential"].get_token.assert_called_once_with(
                    "https://api.microsoft.ai/.default",
                )
                self.ns["requests"].post.assert_called_once_with(
                    "https://api.microsoft.ai/v3/search/web",
                    headers={
                        "Authorization": "Bearer test-bearer-token",
                        "Content-Type": "application/json",
                    },
                    params={"APPID": app_id} if app_id else {},
                    json={
                        "query": "recent advances in battery recycling",
                        "maxResults": 3, "language": "en", "region": "US",
                        "contentFormat": "passage", "maxLength": 3000,
                    },
                    timeout=(10, 90),
                )
                self.assertEqual(self.ns["web"], {"webResults": []})
                self.assertNotIn("test-bearer-token", str(output.call_args_list))
                self.assertNotIn("access_token", self.ns)
                self.assertNotIn("response", self.ns)

    def test_authorization_errors_are_not_retried_or_replaced_with_an_api_key(self):
        for status in (400, 401, 403, 429):
            with self.subTest(status=status):
                self.prepare_search(status)
                self.ns["requests"].post.reset_mock()
                with self.assertRaises(requests.HTTPError):
                    self.run_cell("bearer-search")
                self.ns["requests"].post.assert_called_once()
                headers = self.ns["requests"].post.call_args.kwargs["headers"]
                self.assertNotIn("x-apikey", headers)
                self.assertNotIn("access_token", self.ns)

    def test_token_acquisition_failure_makes_no_http_request(self):
        self.prepare_search()
        self.ns["credential"].get_token.side_effect = RuntimeError("identity unavailable")
        with self.assertRaisesRegex(RuntimeError, "identity unavailable"):
            self.run_cell("bearer-search")
        self.ns["requests"].post.assert_not_called()


if __name__ == "__main__":
    unittest.main()
