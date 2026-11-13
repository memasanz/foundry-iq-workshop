import argparse
import sys
from pathlib import Path

import nbformat
from nbclient import NotebookClient


ROOT = Path(__file__).resolve().parents[1]


def run_notebook(path, output_dir, timeout):
    notebook = nbformat.read(path, as_version=4)
    for cell in notebook.cells:
        if cell.cell_type == "code":
            cell.outputs = []
            cell.execution_count = None

    def report_cell(cell, cell_index):
        if cell.cell_type == "code" and cell.source.strip():
            print(f"  Cell {cell_index + 1}: {cell.id}", flush=True)

    client = NotebookClient(
        notebook,
        timeout=timeout,
        kernel_name="python3",
        resources={"metadata": {"path": str(ROOT)}},
        on_cell_start=report_cell,
    )
    # Use the runner's Python environment, not the notebook's saved kernel selection.
    manager = client.create_kernel_manager()
    manager.kernel_spec.argv = [
        sys.executable, "-m", "ipykernel_launcher", "-f", "{connection_file}",
    ]
    output_path = output_dir / f"{path.stem}.executed.ipynb"
    print(f"Running {path.name}", flush=True)
    success = True
    try:
        client.execute()
    except Exception as error:
        print(f"FAIL {path.name}: {type(error).__name__}", file=sys.stderr, flush=True)
        success = False
    finally:
        nbformat.write(notebook, output_path)
        print(f"  Results: {output_path}", flush=True)
    if success:
        print(f"PASS {path.name}", flush=True)
    return success


def main(argv=None):
    parser = argparse.ArgumentParser(
        description=(
            "Execute selected notebooks in fresh kernels using this Python environment. "
            "Runs real service calls and resource changes; outputs can contain sensitive data."
        ),
    )
    parser.add_argument("notebooks", nargs="+", type=Path, help="Notebook paths, in execution order.")
    parser.add_argument("--timeout", type=int, default=300, help="Maximum seconds per cell (default: 300).")
    parser.add_argument(
        "--output-dir", type=Path, default=ROOT / ".deployment" / "notebook-runs",
        help="Folder for executed copies, including failed runs (default: .deployment\\notebook-runs).",
    )
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be positive.")
    for path in args.notebooks:
        if path.suffix != ".ipynb" or not path.is_file():
            parser.error(f"Notebook not found or not an .ipynb file: {path}")
    output_dir = args.output_dir.resolve()
    for path in args.notebooks:
        if path.resolve() == output_dir / f"{path.stem}.executed.ipynb":
            parser.error("The output must not overwrite an input notebook.")
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for path in args.notebooks:
        if not run_notebook(path.resolve(), output_dir, args.timeout):
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
