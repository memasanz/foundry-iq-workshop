# Participant guide

[Workshop home](../../README.md) | [Choose your labs](../labs/README.md) | [Troubleshooting](../reference/README.md#troubleshooting)

**Participants start here once access is ready.** Open a terminal in the repository root (the folder containing `requirements.txt`). For Azure-connected labs, get the shared `.env.azure` from your facilitator, or generate it by completing deployment yourself. Lab 01 only needs a Web IQ profile API key.

### Choose your language

| Code option | Available labs | How to explore |
|---|---|---|
| [Python notebooks](python.md) | 01-14 | Python/Jupyter cells; follow the Python setup guide. |
| [C# console apps](c-sharp.md) | 01-02 | Supported .NET 10 console apps; direct APIs or the Foundry agent flow. |
| [C# Polyglot Notebooks](c-sharp.md) | 01-02 | C# cells in optional legacy Polyglot Notebooks (`.ipynb`); deprecated tooling. |

The tracks share the root `.env` and `.env.azure`. Labs 03-14 have not been translated to C# yet. No Python installation is required for the console apps.

Create your local settings file without replacing an existing one:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
```

For Azure-connected labs (not required for lab 01), sign in to the workshop tenant:

```powershell
$settings = Get-Content .\.env.azure -Raw | ConvertFrom-StringData
az login --tenant $settings.AZURE_TENANT_ID
```

### Workshop settings and resource names

All labs use the same local environment. Edit `.env` only for the labs you plan to run:

| Setting | When you need it |
|---|---|
| `WEBIQ_API_KEY` | Web IQ calls/connections; notebook 09 stores it in its Web IQ knowledge source for portal/notebook retrieval. Use a workshop-only key. |
| `WORKSHOP_PREFIX` | Your attendee alias throughout the workshop, such as `alice01` (3-20 lowercase letters/digits, starting with a letter). Keep the same value between notebooks. |
| `SHAREPOINT_SITE_URL` | SharePoint ingestion/remote labs (07-08); 09 references the existing index instead. |
| Three `SHAREPOINT_*` app/identity IDs | Indexed SharePoint only; supplied after facilitator setup. Notebook 08 does not need them. |
| `APIM_RESOURCE_ID` | Facilitator setup for notebook 10's existing gateway. Participants receive the printed `APIM_GATEWAY_URL`, `BROKER_APP_CLIENT_ID` and `WEBIQ_APIM_SOURCE_NAME`. |

Every notebook starts with its required participant permissions and facilitator/service-identity prerequisites. Use your alias for all attendee-created top-level objects: connections, agents, toolboxes, indexes, data sources, skillsets, indexers, knowledge sources/bases, Blob folders and generated-agent folders. Notebooks 01 and 13 only make requests and create no named resources. Fields/tool labels inside your objects and service-generated IDs stay unchanged. Shared Azure services, models, the ingestion app, notebook 10's Web IQ source and notebook 12's facilitator connection remain shared. **Aliases prevent naming collisions, not provide security isolation**; the workshop's Search roles can still access other attendees' objects.

Notebook 02 stores the workshop Web IQ key in the `{prefix}-webiq-mcp` Foundry connection; sufficiently privileged project members can access it. Notebooks 02 and 03 derive the connection name from `WORKSHOP_PREFIX`, so keep the same prefix in both. Use a workshop-only key and rotate/revoke it afterward. Keep the connection until both labs are finished.

Notebook 02 reuses a matching `{prefix}-webiq-agent` or older `{prefix}-webiq-agent-<session>` and creates a new version on each run. With no matching agent, it creates the stable `{prefix}-webiq-agent` name. It automatically permits the allowlisted `web` and `browse` tools, requires `WEBIQ_API_KEY` and `WORKSHOP_PREFIX` in configuration rather than prompting, and retains its resources. Queries must remain public and non-sensitive. Notebook 03 still demonstrates interactive approvals.

Notebook 03 publishes versions under the stable `{prefix}-webiq-toolbox` name. Its agents and version-pinned toolbox connections have prefix-based session names, and all resources are retained when local clients close.

For the Python and Polyglot notebook paths:

**You are ready when** you can select the kernel, sign in to the correct tenant and run the first configuration cell of your chosen notebook without missing-setting errors. Later service calls also require the permissions and approvals in the preparation checklist.

## Start your chosen labs

Open the [lab guide](../labs/README.md) for the exercise order, times and prerequisites.
The [lab 09 portal walkthrough](../labs/README.md#use-notebook-09s-knowledge-base-in-the-foundry-iq-portal)
explains its Web IQ source configuration and key rotation.

For scripted runs and offline checks, see [running and checking notebooks](../reference/README.md#running-and-checking-notebooks).
Review [costs and responsible use](../reference/README.md#costs-and-responsible-use) before service calls,
and use [participant cleanup](../reference/README.md#cleanup) when finished.
