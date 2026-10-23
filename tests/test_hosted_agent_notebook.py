import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import MagicMock, Mock, patch

from test_notebooks import code_cell

HOSTED = "08-hosted-agent.ipynb"


class HostedAgentNotebookTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.folder = Path(self.tmp.name) / "hosted-agent"
        self.ns = {
            "Path": Path,
            "zipfile": zipfile,
            "agent_folder": self.folder,
            "agent_name": "alice01-hosted-agent",
            "prefix": "alice01",
            "model_name": "workshop-model",
            "project_client": Mock(),
            "time": Mock(time=Mock(return_value=0), sleep=Mock()),
        }

    def tearDown(self):
        self.tmp.cleanup()

    def test_agent_files_are_valid_python_and_requirements_pinned(self):
        exec(code_cell(HOSTED, "write-agent-files"), self.ns)
        main = (self.folder / "main.py").read_text(encoding="utf-8")
        compile(main, "main.py", "exec")
        self.assertIn("ResponsesHostServer(agent).run()", main)
        self.assertIn("get_workshop_tip", main)
        self.assertNotIn("api_key", main.lower())
        requirements = (self.folder / "requirements.txt").read_text(encoding="utf-8")
        self.assertIn("agent-framework-foundry-hosting", requirements)

    def test_deploy_zips_code_and_uses_remote_build(self):
        exec(code_cell(HOSTED, "write-agent-files"), self.ns)
        create = self.ns["project_client"].agents.create_version_from_code
        create.return_value = Mock(version="1", status="creating")
        exec(code_cell(HOSTED, "deploy"), self.ns)
        with zipfile.ZipFile(self.folder / "agent.zip") as bundle:
            self.assertEqual(sorted(bundle.namelist()), ["main.py", "requirements.txt"])
        args, kwargs = create.call_args
        self.assertEqual(args[0], "alice01-hosted-agent")
        definition = kwargs["definition"]
        self.assertEqual(definition.code_configuration.dependency_resolution, "remote_build")
        self.assertEqual(definition.code_configuration.entry_point, ["main.py"])
        self.assertEqual(definition.environment_variables["AZURE_AI_MODEL_DEPLOYMENT_NAME"], "workshop-model")

    def test_wait_stops_when_active_and_raises_on_failure(self):
        client = self.ns["project_client"]
        self.ns["version"] = Mock(version="1")
        client.agents.get_version.side_effect = [Mock(version="1", status="creating"), Mock(version="1", status="active")]
        exec(code_cell(HOSTED, "wait"), self.ns)
        self.assertEqual(client.agents.get_version.call_count, 2)
        client.agents.get_version.side_effect = [Mock(version="1", status="failed")]
        with self.assertRaises(RuntimeError):
            exec(code_cell(HOSTED, "wait"), self.ns)

    def test_invoke_has_no_model_argument_and_cleanup_is_off_by_default(self):
        openai_client = MagicMock()
        openai_client.responses.create.return_value = Mock(output_text="tip", model_extra={"agent_session_id": "s1"})
        self.ns["project_client"].get_openai_client.return_value = openai_client
        exec(code_cell(HOSTED, "invoke"), self.ns)
        self.assertNotIn("model", openai_client.responses.create.call_args.kwargs)
        exec(code_cell(HOSTED, "cleanup"), self.ns)
        self.ns["project_client"].agents.delete.assert_not_called()
        self.ns["CLEAN_UP"] = True
        with patch.dict(self.ns):
            exec(code_cell(HOSTED, "cleanup").replace("CLEAN_UP = False", "CLEAN_UP = True"), self.ns)
        self.ns["project_client"].agents.delete.assert_called_once_with("alice01-hosted-agent")


if __name__ == "__main__":
    unittest.main()
