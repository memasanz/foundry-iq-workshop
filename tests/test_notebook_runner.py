import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import nbformat


ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / "scripts" / "run-notebooks.py"


class NotebookRunnerTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name)
        self.output_dir = self.path / "results"

    def notebook(self, name, sources):
        path = self.path / name
        notebook = nbformat.v4.new_notebook(
            cells=[nbformat.v4.new_code_cell(source) for source in sources],
            metadata={"kernelspec": {
                "name": "nonexistent-saved-kernel", "display_name": "Unavailable kernel",
            }},
        )
        nbformat.write(notebook, path)
        return path

    def run_notebooks(self, *paths, timeout=30):
        return subprocess.run(
            [sys.executable, str(RUNNER), *(str(path) for path in paths),
             "--output-dir", str(self.output_dir), "--timeout", str(timeout)],
            capture_output=True, text=True, timeout=90, cwd=self.path,
        )

    def test_runs_actual_cells_with_shared_state_await_and_current_python(self):
        notebook = self.notebook("success.ipynb", [
            "import asyncio, sys\nfrom pathlib import Path\nvalue = 41",
            f"await asyncio.sleep(0)\nassert value + 1 == 42\n"
            f"assert Path.cwd() == Path({str(ROOT)!r})\n"
            f"assert sys.executable == {sys.executable!r}\nprint('verified')",
        ])
        original = notebook.read_bytes()
        result = self.run_notebooks(notebook)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("PASS success.ipynb", result.stdout)
        self.assertEqual(notebook.read_bytes(), original)
        executed = nbformat.read(self.output_dir / "success.executed.ipynb", as_version=4)
        self.assertEqual(executed.cells[1].outputs[0].text, "verified\n")

    def test_stops_on_failure_and_saves_error_without_stale_outputs(self):
        notebook = self.notebook("failure.ipynb", [
            "raise ValueError('test failure')", "print('must not run')",
        ])
        stale = nbformat.read(notebook, as_version=4)
        stale.cells[1].execution_count = 99
        stale.cells[1].outputs = [nbformat.v4.new_output("stream", name="stdout", text="old")]
        nbformat.write(stale, notebook)
        later = self.notebook("later.ipynb", ["print('must not run either')"])
        result = self.run_notebooks(notebook, later)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("FAIL failure.ipynb", result.stderr)
        executed = nbformat.read(self.output_dir / "failure.executed.ipynb", as_version=4)
        self.assertEqual(executed.cells[0].outputs[0].ename, "ValueError")
        self.assertIsNone(executed.cells[1].execution_count)
        self.assertEqual(executed.cells[1].outputs, [])
        self.assertFalse((self.output_dir / "later.executed.ipynb").exists())

    def test_each_notebook_gets_a_fresh_kernel(self):
        first = self.notebook("first.ipynb", ["previous_notebook_value = 42"])
        second = self.notebook("second.ipynb", ["assert 'previous_notebook_value' not in globals()"])
        result = self.run_notebooks(first, second)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("PASS second.ipynb", result.stdout)


if __name__ == "__main__":
    unittest.main()
