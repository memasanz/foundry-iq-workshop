# Python notebook setup

[Participant guide](README.md) | [Choose your labs](../labs/README.md)

## What you need

Participants need Python 3.11+, Azure CLI, and either VS Code with its Jupyter extension or JupyterLab. The commands below use PowerShell 7. The facilitator supplies access and the generated `.env.azure` configuration.

### Python notebooks

Set up the Python environment once. If `.venv` already exists, reuse it and skip the first command:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m ipykernel install --user --name iq-workshop --display-name 'Web & Foundry IQ Workshop'
# Skip this command if using VS Code's notebook editor.
.\.venv\Scripts\python.exe -m jupyter lab
```

On Microsoft-managed devices that block public PyPI, add `--index-url https://packagefeedproxy.microsoft.io/pypi/simple` to the pip command.

Choose **Web & Foundry IQ Workshop** in the notebook's kernel picker. In VS Code, open the `.ipynb` file under `python-notebooks` and select the kernel at the top right; you do not need to start JupyterLab if using VS Code. Start the kernel with `python-notebooks` as its working directory, as lab 01 loads its settings from the parent folder. For the Web IQ track, open notebook 01 and run its cells from top to bottom, then continue with 02 and 03.

After setup, follow the [shared settings and resource-name guidance](README.md#workshop-settings-and-resource-names).
For headless execution, see [the notebook runner](../reference/README.md#verify-notebooks-from-a-python-script).
