# Azure IQ workshop

Build agents that search the web and answer questions over your own documents. These hands-on Python notebooks start with direct API calls, then introduce agents, search indexes, SharePoint permissions and Foundry IQ.

You can take one track or work through the whole workshop. The notebooks show the requests and responses directly, so you can see how the pieces fit together.

## Start here

| Your role | Where to begin |
|---|---|
| **Participant** | Ask your facilitator for the workshop configuration and access, then [open the notebooks](#3-open-the-notebooks). You do not need to deploy your own Azure resources. |
| **Facilitator** | Review [Prepare for the workshop](PREPARE-FOR-WORKSHOP.md), then follow the [beginner deployment guide](infra/README.md). Arrange approvals and licenses before class. |
| **Learning on your own** | Follow the facilitator setup once, then use the participant instructions. You need your own eligible Azure subscription and access to the services you choose. |

**New to Azure?** The [infrastructure guide](infra/README.md) explains the tools, subscription, resource group, deployment command and costs from the beginning.

Jump to: [Lab guide](#choose-your-labs) | [Deployment](#1-deploy) | [Local setup](#3-open-the-notebooks) | [Troubleshooting](#troubleshooting) | [Cleanup](#cleanup)

## Choose your labs

Times are estimates for the exercises, not for administrator approvals or Azure provisioning. You do not have to run every notebook in number order.

**A few terms:** an *index* stores searchable documents; *embeddings* represent content as numbers for similarity search; *MCP* (Model Context Protocol) lets agents call tools. *ACLs* are the permissions that determine who can read a document, and a *managed identity* lets an Azure service authenticate without a shared password.

### Track 1: Web IQ and agents

Start with **01 -> 02 -> 03**. Allow roughly 2-3 hours, including setup and discussion.

| Notebook | Time | What you will do |
|---|---|---|
| [01 - Web IQ APIs](notebooks/01-web-iq-apis.ipynb) | 40-55 min | Try Web Search, Browse, News, Images, Videos and Auto; inspect sources and citations. Places is optional. |
| [02 - Foundry agent + Web IQ](notebooks/02-foundry-web-iq-agent.ipynb) | 35 min | Give an agent access to the same Web IQ service through MCP, approve tool calls, and inspect its answers. |
| [03 - Foundry agent + Web IQ toolbox](notebooks/03-foundry-web-iq-toolbox.ipynb) | 30-40 min | Reuse notebook 02's connection through a toolbox: a reusable collection of tools for agents. |

### Track 2: Your documents and Foundry IQ

Compare **04, 05 and 06**: text splitting in an indexer, PDF extraction/chunking with Content Understanding, and Python chunking with push ingestion. **07 and 08 are alternative SharePoint approaches**; notebook 08 does not require notebook 07's app registration.

| Notebook | Time | What you will do / what comes first |
|---|---|---|
| [04 - Blob indexer and skills](notebooks/04-blob-indexer-skills.ipynb) | 45-60 min | Let Search read Blob files, split text and generate embeddings. Compare keyword, vector, hybrid and semantic-ranked search. |
| [05 - Blob + Content Understanding](notebooks/05-blob-content-understanding.ipynb) | 45-60 min | Extract an original two-page PDF as Markdown, chunk and embed it, then retrieve evidence with page ranges. Uses a separate index and billable Content Understanding. |
| [06 - Chunk, embed and push](notebooks/06-push-chunks-vectors.ipynb) | 35-45 min | Do chunking and embedding in Python, then upload to a separate index. Can run without notebook 04 or Blob access. |
| [07 - SharePoint indexed](notebooks/07-sharepoint-indexed.ipynb) | 60-90 min | Copy test-site content into Search while preserving document permissions. Requires facilitator app/consent/site setup. |
| [08 - SharePoint remote](notebooks/08-sharepoint-remote.ipynb) | 20-30 min | Retrieve from SharePoint without copying content into an index. Requires Microsoft 365 Copilot licensing or enabled Retrieval API billing. |
| [09 - Foundry IQ knowledge base](notebooks/09-foundry-iq-knowledge-base.ipynb) | 45-60 min | Bring Blob, SharePoint and Web IQ together and ask internal, web and mixed questions. Choose indexed or remote SharePoint. |
| [10 - Foundry IQ through APIM](notebooks/10-foundry-iq-apim.ipynb) | 30-40 min | Build on notebook 09 using an existing API Management gateway that validates callers and protects the Web IQ key. |
| [11 - Hosted agent](notebooks/11-hosted-agent.ipynb) | 20-30 min | Deploy a small code-based agent to Foundry, without Docker. Needs Foundry and the chat model, not notebooks 09-10. |
| [12 - Hosted agent + Foundry IQ toolbox](notebooks/12-foundry-iq-toolbox-agent.ipynb) | 30-40 min | Combine the patterns from 09 and 11. Uses Blob content, a shared toolbox connection and a per-agent Search role. |

**Optional extension:** [13 - Web IQ authentication](notebooks/13-web-iq-authentication.ipynb) (25-35 min) explores Entra tokens and managed identities instead of API keys. It is independent of the Search labs and needs a separate caller app/profile binding. Managed-identity exercises require Azure compute exposing that identity; the local exercise uses a hidden secret prompt.

### Arrange these before class

The full resource and permission matrix is in [Prepare for the workshop](PREPARE-FOR-WORKSHOP.md). These are the most common blockers:

| Labs | What must be ready |
|---|---|
| 01-03 | A paid Azure subscription, a deployed Web IQ resource, a linked profile/API key, and Auto beta approval. Participants in 02-03 need **Foundry Owner on the parent Foundry resource**. |
| 04-06 | Search, the embedding deployment and inference access. Notebooks 04-05 also need authenticated network access to Blob Storage. Notebook 06 needs account-level embedding permission; project-only Foundry User is insufficient. |
| 05 | A Content Understanding-supported Foundry region (East US recommended), **Cognitive Services User for Search's identity on Foundry**, and budget for PDF analysis and embeddings. Bicep already declares this role. |
| 07 | SharePoint indexer preview registration, one shared Entra ingestion app, **tenant admin consent**, and an explicit test-site grant. Graph `Files.Read.All` is tenant-wide file-read access; obtain approval before class. |
| 08 | **Microsoft 365 Copilot add-on license for each querying user**, or explicitly enabled Retrieval API pay-as-you-go. Pay-as-you-go requires at least one Copilot license in the tenant. GitHub Copilot is not a substitute. |
| 10 | An **existing APIM service**, its managed-identity Search access, and the separate broker app/policy setup below. Bicep does not create APIM. |
| 03, 11-12 | Verify toolbox/hosted-agent preview availability in your project and region. Successful infrastructure deployment does not prove these notebook flows work end to end. |

Use only a dedicated SharePoint test site and non-sensitive documents. Never weaken tenant security policies to make an exercise work.

## Architecture

```text
Local Jupyter notebook
  |-- Web IQ REST API ------------------> api.microsoft.ai/v3
  |                                        |
  |                                        +-- Web IQ account (global, G3 billing)
  |
  +-- Entra ID --> Foundry project (eastus)
                    |-- GPT model (GlobalStandard, pay per use)
                    +-- MCP tool --> Web IQ MCP --> web/news/images/videos/browse
                         |
                         +-- API key in a secure project connection

East US Search labs
  |-- AI Search (Standard S1, 1 replica, 1 partition)
  |     |-- Notebook 09 knowledge base: Blob + SharePoint + Web IQ (MCP) --> Foundry models (MI)
  |     +-- Notebook 10: caller --> APIM (validate-jwt, OBO, Web IQ key) --> same knowledge base
  |     |-- Managed identity --> Blob container (read) --> indexer + skills --> chunks
  |     |-- Notebook 05: PDF --> Content Understanding --> Markdown chunks + page ranges + embeddings
  |     |-- Notebook 06: local files --> Python chunking --> embeddings REST --> push API --> separate chunk/vector index
  |     |-- Federated ingestion app --> SharePoint --> indexer + ACL projections --> chunks
  |     +-- Remote knowledge source --> Copilot Retrieval API --> SharePoint (no local index)
  +-- Blob Storage (Standard LRS, Hot, no anonymous access)
```

The Azure Web IQ resource is the billing resource, not a per-resource API endpoint. API traffic uses `https://api.microsoft.ai/v3`. Web IQ always uses `global`; Foundry, AI Search and Blob Storage use `eastus` by default. East US supports both AI Search and Content Understanding, allowing same-region enrichment. Confirm available Search capacity and model quota before deployment. Bicep creates Web IQ first; Foundry and the data resources are not provisioned if Web IQ creation fails. APIM is not provisioned by Bicep; notebook 10 uses an existing gateway.

Foundry, Search and Storage use public network endpoints protected by Entra authentication. Search API-key authentication and Storage shared-key authentication are disabled; the blob container is private (no anonymous access), **not** a private-network endpoint. This is an introductory workshop, not a private-network production architecture. No VM, Azure-hosted notebook or container runtime is required. The Web IQ notebooks do not create indexes or indexers; the Search labs create them when their cells are run.

## What you need

Participants need Python 3.11+, Azure CLI, and either VS Code with its Jupyter extension or JupyterLab. The commands below use PowerShell 7. The facilitator supplies access and the generated `.env.azure` configuration.

Facilitators additionally need:

- Bicep (`az bicep version`); use the [deployment guide](infra/README.md) if a tool is missing.
- A **paid Azure subscription** where you can create resources. The current [Web IQ deployment prerequisites](https://learn.microsoft.com/web-iq/get-started/deploy-azure-portal) do not list prior access approval. Azure credits do not work for Web IQ. The deployment script registers the required resource providers.
- Permissions to create resources and role assignments. The deployer normally needs Owner, or Contributor plus Role Based Access Control Administrator. The script resolves the signed-in user's object ID; service-principal deployments must pass a participant user's `-PrincipalId`.
- Foundry model quota in the selected region. Defaults: `gpt-5-mini`, version `2025-08-07`, GlobalStandard capacity `30`. Adjust the script parameters if the model/version is unavailable. GlobalStandard can process inference outside the selected Azure region.
- Access to the [Web IQ portal](https://webiq.microsoft.ai/profiles). Review the [Web IQ terms](https://www.microsoft.com/en-us/webiq/webiq-legal) before making requests.

## 1. Deploy

**Facilitator only.** Participants using shared workshop resources can skip to [local setup](#3-open-the-notebooks). For a step-by-step explanation, see [Deploy the workshop resources](infra/README.md).

From the workshop directory:

```powershell
az login
.\scripts\deploy.ps1 `
  -SubscriptionId '<your-subscription-id>' `
  -ResourceGroup 'rg-iq-workshop' `
  -Location 'eastus' `
  -SearchSku 'standard'
```

The script targets the subscription you specify without changing your CLI default. It creates the group if needed, deploys the resources, and ensures the **live Foundry project identity** has Search Index Data Reader. Reruns skip a matching grant. Add `-WhatIf` to preview changes in an **existing** group without deploying or updating settings.

### Know your configuration files

| File | Who maintains it? | What belongs here? |
|---|---|---|
| `.env.azure` | Deployment script, automatically after success | Deployed endpoints, resource IDs, locations and model deployment names |
| `.env` | You; deployment does not overwrite it | Web IQ key, unique participant prefix, SharePoint settings and existing APIM resource ID |
| `.deployment\deployment.json` | Deployment script | Local deployment result and outputs |

All three are excluded from Git. A failed deployment leaves the previous `.env.azure` unchanged, although some Azure resources may already exist. **Do not copy generated endpoints into `.env`**: its values load first and can hide the new settings. Restart notebook kernels after redeployment.

The deployment grants the facilitator project-level Foundry User, Search administration/data access, and Blob data access. It also grants the required Search-to-Blob, Search-to-Foundry and project-to-Search access. It does **not** create notebook connections, indexes, agents, tenant app registrations or the participant parent-resource Foundry Owner grant.

**Changing region creates new regional resources; it does not move or delete the old ones.** Old resources can keep billing. A group's metadata location can differ from its resources' location. Recreate region-dependent connections/agents and update SharePoint federation and the existing APIM backend/access grants afterward. Web IQ stays global and APIM can stay in its current region.

If policy disables Storage public networking, obtain the approved exception for the **new account** and verify authenticated connectivity. An old account's exemption does not transfer. Keep anonymous Blob access and shared keys disabled. More detail is in the [deployment guide](infra/README.md).

Use a **dedicated** resource group: whole-group cleanup deletes everything inside it.

## 2. Complete Web IQ onboarding

1. Open [Profile Management](https://webiq.microsoft.ai/profiles), sign in, and link the `WEBIQ_RESOURCE_ID` from `.env.azure`.
2. Create an API key. This portal step is **not automated by the Azure resource deployment**.
3. Set `WEBIQ_API_KEY` in your untracked `.env`, or use the notebooks' hidden password prompt. Local setup below creates `.env` without overwriting an existing file. Never put credentials directly in a notebook cell.
4. Confirm your profile's allowed service list. Notebook 01 runs Web Search, Browse, News, Images, Videos and Auto by default. Auto beta requires service-side AppID enablement; request access before the workshop. Full-document formats and Places beta may also require additional access. Places remains optional (`WEBIQ_ENABLE_PLACES=true`). To skip a core exercise deliberately, set its `WEBIQ_ENABLE_*` flag to `false`; remove old `false` settings from an existing `.env` to restore the defaults, then restart the kernel. These flags only control which exercises run; they do not grant access or require additional Azure resources. API permission errors remain visible.

Web IQ API keys are not Azure Cognitive Services keys and are not retrieved with `az cognitiveservices account keys list`. Production Web IQ Entra authentication uses an **app-only** token and a client ID bound to the Web IQ profile; `az login` alone does not authenticate a Web IQ REST call. The notebooks use a Web IQ API key and Entra credentials for Foundry.

## 3. Open the notebooks

**Participants start here once access is ready.** Open a terminal in the repository root (the folder containing `requirements.txt`). Get the shared `.env.azure` from your facilitator, or generate it by completing deployment yourself.

Create your local settings file without replacing an existing one, and sign in to the workshop tenant:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
$settings = Get-Content .\.env.azure -Raw | ConvertFrom-StringData
az login --tenant $settings.AZURE_TENANT_ID
```

Set up the Python environment once. If `.venv` already exists, reuse it and skip the first command:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m ipykernel install --user --name iq-workshop --display-name 'Azure IQ Workshop'
# Skip this command if using VS Code's notebook editor.
.\.venv\Scripts\python.exe -m jupyter lab
```

On Microsoft-managed devices that block public PyPI, add `--index-url https://packagefeedproxy.microsoft.io/pypi/simple` to the pip command.

Choose **Azure IQ Workshop** in the notebook's kernel picker. In VS Code, open the `.ipynb` file and select the kernel at the top right; you do not need to start JupyterLab if using VS Code. For the Web IQ track, open notebook 01 and run its cells from top to bottom, then continue with 02 and 03.

All labs use the same local environment. Edit `.env` only for the labs you plan to run:

| Setting | When you need it |
|---|---|
| `WEBIQ_API_KEY` | Web IQ calls/connections; notebook 09 also needs it for retrieval. Use your workshop-only key. |
| `WORKSHOP_PREFIX` | Search labs: a unique value such as `alice01` (3-20 lowercase letters/digits, starting with a letter). |
| `SHAREPOINT_SITE_URL` | SharePoint labs and notebook 09; use the approved test site. |
| Three `SHAREPOINT_*` app/identity IDs | Indexed SharePoint only; supplied after facilitator setup. Notebook 08 does not need them. |
| `APIM_RESOURCE_ID` | Facilitator setup for notebook 10's existing gateway. Participants receive the printed `APIM_GATEWAY_URL`, `BROKER_APP_CLIENT_ID` and `WEBIQ_APIM_SOURCE_NAME`. |

Notebook 02 stores the workshop Web IQ key in a Foundry connection; sufficiently privileged project members can access it. Notebook 03 reuses that connection. Use a workshop-only key and rotate/revoke it afterward. Do not remove the shared connection while other labs still need it.

**You are ready when** you can select the kernel, sign in to the correct tenant and run the first configuration cell of your chosen notebook without missing-setting errors. Later service calls also require the permissions and approvals in the preparation checklist.

## 4. Grant attendee access

After deployment, run the permissions script as an administrator with role-assignment write permissions on the workshop resources, such as Owner or Role Based Access Control Administrator. Azure subscription Contributor alone cannot grant roles. The script also needs directory access to resolve the specified Microsoft Entra users.

```powershell
# Preview first; this reads identities and assignments but does not grant access.
.\scripts\grant-workshop-access.ps1 `
  -Users 'attendee1@contoso.com', 'attendee2@contoso.com' `
  -WhatIf

# Assign the workshop roles.
.\scripts\grant-workshop-access.ps1 `
  -Users 'attendee1@contoso.com', 'attendee2@contoso.com'

# A text file can supply one user principal name or user object ID per line.
.\scripts\grant-workshop-access.ps1 -Users (Get-Content '.\attendees.txt')
```

`-Users` accepts Microsoft Entra user principal names or user object IDs in the deployment tenant, not arbitrary contact email addresses. Invite external attendees as guests first, then use their **guest object IDs in the workshop tenant**. The script does not create users or send guest invitations. Do not commit attendee rosters to source control.

The script reads resource IDs from `.env.azure`, validates that the scopes belong to its workshop resource group, resolves all attendees before making changes, and skips identical existing direct assignments. It targets the configured subscription without changing your CLI default. Use `-EnvironmentFile` to select another deployment's generated settings.

| Role | Scope | Enables |
|---|---|---|
| Search Service Contributor | Workshop Search service | Create/manage indexes, indexers, data sources, skillsets and other Search objects |
| Search Index Data Contributor | Workshop Search service | Upload, update and delete documents; run queries |
| Storage Blob Data Contributor | `workshop-documents` container only | Upload, read and delete source documents |
| Foundry User | Workshop Foundry project | Create and use agents in that project |
| Reader | Web IQ resource (only if `WEBIQ_RESOURCE_ID` is in `.env.azure`) | View the resource in the portal; read-only |

**Additional grants are still required:** notebooks 02-03 use **Foundry Owner on the parent Foundry resource** for each participant. Notebook 06 needs account-level Cognitive Services OpenAI User or the workshop's Foundry Owner role; project-only Foundry User is not enough. This script does not add those parent-resource roles.

These roles do not grant subscription/resource-group Contributor, Owner, role-assignment management, SharePoint access or write access to Web IQ. Web IQ API access uses a separate profile key.

**Shared-service warning:** participants can modify one another's Search objects and access shared index/blob content. A unique prefix prevents name collisions, **not access isolation**. Use trusted attendees and non-sensitive data; separate services/containers if strict isolation is required. Check Search object limits and capacity for the class size, and allow several minutes for role propagation.

## 5. Set up the SharePoint app registration (facilitator, before the workshop)

Notebook 07's **indexed** path needs an Entra app that Azure AI Search uses to read SharePoint. Run `scripts\setup-sharepoint-app.ps1` once, after deployment, as a user who can create app registrations or update the selected existing app (add `-GrantAdminConsent` only if you also hold a role that can consent). Attendees do not need Entra permissions. The **remote** path needs no app; it runs as each attendee (see notebook 08).

Use `-AppName` for the app registration's **exact display name** in the deployment tenant. If one app matches, the script configures that app; if none matches, it creates a new single-tenant app with that name. If multiple apps match, it stops without changing them; use a unique display name. Omitting `-AppName` uses `iq-workshop-sharepoint-indexer`.

```powershell
# Existing app: use its exact display name.
.\scripts\setup-sharepoint-app.ps1 -AppName 'Existing SharePoint Indexer' -SiteUrl 'https://contoso.sharepoint.com/sites/iq-workshop' -WhatIf
.\scripts\setup-sharepoint-app.ps1 -AppName 'Existing SharePoint Indexer' -SiteUrl 'https://contoso.sharepoint.com/sites/iq-workshop' -GrantAdminConsent

# New app: choose a name that does not already exist.
.\scripts\setup-sharepoint-app.ps1 -AppName 'New Workshop SharePoint Indexer' -SiteUrl 'https://contoso.sharepoint.com/sites/iq-workshop'
```

What it does, in order (safe to re-run; it reuses the app by name and preserves existing API permissions):

1. Reads the Search service's system-assigned managed identity (from `.env.azure`).
2. Reuses the named app, or creates it as single-tenant, and ensures its service principal exists.
3. Requests these **application** permissions, looked up by name:
   - Microsoft Graph: `Files.Read.All`, `Sites.Selected`
   - SharePoint: `Sites.Selected`, `User.Read.All`
4. Adds a **federated credential** that trusts the Search managed identity. No client secret is created; Search proves its identity with its managed identity. Existing credentials on a reused app are left unchanged.
5. With `-GrantAdminConsent`, grants tenant admin consent. Otherwise approve it in Entra > App registrations > API permissions.
6. Prints the three values for `.env` (`SHAREPOINT_APP_CLIENT_ID`, `SHAREPOINT_APP_OBJECT_ID`, `SHAREPOINT_SEARCH_MI_CLIENT_ID`).

**One manual step remains:** a SharePoint admin must grant the app access to the workshop site. `Sites.Selected` gives no access until a site is granted. The script prints the command:

```powershell
Grant-PnPEntraIDAppSitePermission -AppId <app-id> -DisplayName 'iq-workshop-sharepoint-indexer' -Site '<site url>' -Permissions FullControl
```

`FullControl` on a dedicated test site is needed for the document-level permission (ACL) lab; `Read` is enough for content-only indexing. Caveat: Graph `Files.Read.All` is tenant-wide read, so this is not strict site-only isolation. Use a test tenant or non-sensitive content. The notebook's own app-creation and federation-check cells stay disabled by default; this script replaces them for facilitators.

Before class, use two non-admin member users to test an allowed and a restricted document, including native SharePoint-group membership. An empty query alone does not prove permissions are correct. Inherited ACL changes need a permissions resync and an indexer run. Review the notebook's preview limitations, including guest users, nested groups and Conditional Access compatibility.

**Using notebook 08 instead?** Skip this ingestion app setup. Queries run as each signed-in user and require their Microsoft 365 Copilot license or explicitly enabled Retrieval API pay-as-you-go. This billing is separate from Search/Foundry. Verify the documents are available in Microsoft 365 search before class.

## 6. Set up the APIM gateway (facilitator, before the workshop; notebook 10)

Notebook 10 calls the knowledge base through an **existing Azure API Management service**, using a **different app registration** from the SharePoint one. Bicep does not create or configure the gateway. It can be in another resource group or subscription in the same tenant and does not have to move to East US. Keep it outside the dedicated workshop resource group if you intend to run the whole-group cleanup and retain the gateway. Before running the scripts, enable its **system-assigned managed identity** and grant that identity **Search Index Data Reader** on the workshop Search service. The facilitator needs permission to configure APIM, create the broker app, and grant this Search role.

Set your existing gateway's resource ID in the untracked `.env` file. Both setup scripts read it; the gateway is not a Bicep deployment parameter:

```dotenv
APIM_RESOURCE_ID=/subscriptions/<apim-subscription-id>/resourceGroups/<apim-resource-group>/providers/Microsoft.ApiManagement/service/<existing-apim-name>
```

On the workshop Search service, use **Access control (IAM) -> Add role assignment -> Search Index Data Reader**, select **Managed identity**, and choose the existing APIM service's system-assigned identity. This replaces the role assignment previously made by Bicep.

Run these after `scripts\deploy.ps1` and the identity/role preparation:

```powershell
.\scripts\setup-broker-app.ps1 -GrantAdminConsent
.\scripts\setup-apim-policy.ps1  # reads WEBIQ_API_KEY from .env or prompts
```

The setup scripts target only this gateway's workshop API and named values, not the APIM service's SKU, location or identity configuration. They reuse or overwrite `iq-knowledge-base` and the named values `broker-client-secret`, `broker-client-id`, `tenant-id` and `webiq-api-key`; obtain approval and check for naming collisions before using a shared gateway. Use `-WhatIf` to preview script changes. Allow time for the Search role assignment to propagate.

<details>
<summary>What the broker and policy scripts configure</summary>

`setup-broker-app.ps1` (supports reruns and `-WhatIf`):

1. Creates the single-tenant app `iq-workshop-apim-broker` and its service principal.
2. Exposes the delegated scope `api://<appId>/user_impersonation` and **pre-authorizes Azure CLI** for it, so attendees get a token with `az login` and no consent prompt.
3. Requests the delegated **Azure Cognitive Search `user_impersonation`** permission (looked up by name). `-GrantAdminConsent` consents tenant-wide; otherwise approve it in Entra.
4. Creates a one-year client secret and stores it **only** as the secret APIM named value `broker-client-secret`. It is never printed or written to disk. Use `-RotateSecret` to replace it. It also stores `broker-client-id` and `tenant-id`.
5. Prints `BROKER_APP_CLIENT_ID` for `.env`.

`setup-apim-policy.ps1` stores the Web IQ key as the secret named value `webiq-api-key`, creates the API `iq-knowledge-base` (path `/iq`, `retrieve` and `mcp` operations in front of Search), and sets this policy on every call:

| Step | What it does |
|---|---|
| `validate-jwt` | Accepts only Entra tokens for the broker app with scope `user_impersonation`; otherwise 401 |
| On-behalf-of | Exchanges the caller's token for an Azure AI Search token for the **same user** and sends it as `x-ms-query-source-authorization`, so SharePoint ACLs and remote SharePoint run as that user |
| Web IQ key | Adds `<source>-header-name` / `-header-value` control headers from the named value; the key never reaches attendees |
| Elevated read | Deletes any caller-supplied `x-ms-enable-elevated-read` header, which would bypass ACLs |
| Managed identity | Replaces `Authorization` with the APIM identity's Search token |

</details>

APIM charges depend on the existing gateway's tier. Because header names embed the source name, the policy is written for **one shared Web IQ source** (`-WebIqSourceName`, default `iq-workshop-webiq`); notebook 10 creates it. Give attendees `BROKER_APP_CLIENT_ID`, `APIM_GATEWAY_URL` and `WEBIQ_APIM_SOURCE_NAME` (printed by the scripts). The APIM named-value secrets are visible to anyone with APIM contributor access; use Key Vault references for production.

## 7. Set up the toolbox connection (facilitator, before the workshop; notebook 12)

Create the shared toolbox connection once as the facilitator:

```powershell
azd extension install azure.ai.agents   # once
.\scripts\setup-toolbox-connection.ps1 -WhatIf   # preview
.\scripts\setup-toolbox-connection.ps1
```

This creates the shared connection `iq-workshop-search-mcp` (`remote-tool`, `agentic-identity`, audience `https://search.azure.com`). Each attendee's toolbox tool carries its own `server_url` (their knowledge base), so one connection serves everyone. No secret is stored.

**Per-agent access.** The agent calls Search as its own Entra identity, created when the agent is deployed, so it cannot be granted in advance. Notebook 12 prints the identity's principal ID and tries to assign **Search Index Data Reader** (read-only, Search service scope) itself. Attendees normally cannot assign roles, so expect to run, with the printed IDs:

```powershell
.\scripts\grant-agent-search-access.ps1 -PrincipalId <id1>,<id2>
```

To skip this per attendee, you could instead give attendees a constrained *Role Based Access Control Administrator* role (limited to Search Index Data Reader) on the Search service; this workshop does not do that by default. Attendees need `TOOLBOX_CONNECTION_NAME` in `.env` only if you change the default name.

## Costs and responsible use

**Search Standard S1 bills continuously, even when nobody is running a notebook.** Closing VS Code does not stop Azure charges. Old resources retained after a regional deployment can also keep billing.

| Service | What to budget for |
|---|---|
| Search | One S1 replica/partition, retrieval usage and semantic-ranking allowances. This is a workshop setup, not production high availability. |
| Foundry models | Chat and embedding calls; agents can make several tool/model calls for one prompt. Model capacity is not a spending cap. |
| Web IQ | Search/Browse calls, retries, optional exercises and applicable beta/premium terms. Check [current pricing](https://learn.microsoft.com/web-iq/resources/pricing), including Auto and Places terms. |
| Content Understanding (05) | PDF analysis on the attached Foundry resource, plus embeddings. Reprocessing can incur analysis charges again; timeouts can still be billable. See [skillset billing](https://learn.microsoft.com/azure/search/cognitive-search-attach-cognitive-services). |
| Storage | Stored data, transactions and data retained by seven-day Blob/container soft deletion. |
| SharePoint remote | Required Microsoft 365 licensing or enabled Retrieval API pay-as-you-go, plus applicable Search charges. Minimal reasoning does not make retrieval free. |
| Existing APIM | Your gateway's tier and usage. |

Use workshop-only credentials and non-sensitive sample data. Never print tokens or secrets in notebook outputs or commit `.env` files.

Use only public, non-sensitive queries. Retrieved pages are untrusted evidence, not instructions. Search and crawl timestamps do not establish when a fact became true. Preserve source URLs, distinguish publisher dates from crawl dates, and inspect whether citations actually support each claim. Search results do not grant copyright or media reuse rights. Follow the service's attribution and [instrumentation requirements](https://learn.microsoft.com/web-iq/guides/instrumentation) before adapting the lab into a user-facing application.

## Troubleshooting

| Symptom | Action |
|---|---|
| Web IQ resource creation fails | Read the Azure error details; check the paid subscription, resource-creation permissions, provider registration and applicable Azure policies. |
| API 401 | Confirm the profile-issued API key; do not use a Foundry/Azure key. |
| API 403 / MCP tool absent | Check your allowed services and full-document entitlement. The optional notebook flags cannot grant access. |
| Browse returns 202 | The notebook waits for `retryAfter` and retries with a bounded limit. An unfinished crawl raises an explicit error. |
| 429 | Respect `Retry-After`; reduce concurrency, especially with shared workshop AppIDs. |
| No Python kernel / imports fail | Select **Azure IQ Workshop**, install `requirements.txt` with that environment's Python, then restart the kernel. |
| Missing `.env.azure` | Ask the facilitator for the shared configuration, or finish your own deployment. Do not invent endpoints. |
| Notebook uses an old endpoint | Remove stale generated-setting overrides from `.env` and restart the kernel. |
| Foundry 403 | Check the sign-in tenant and parent-resource/project roles for the specific operation; allow time for propagation. |
| Blob upload/indexer cannot connect | Check public networking and inherited policy, then the required Blob data role. RBAC alone does not bypass a network restriction. |
| SharePoint 07 authorization fails | Check admin consent, the explicit site grant, required API permissions and federation to the current Search identity. Subscription Owner is not tenant admin consent. |
| SharePoint 08 license/billing error | Check each querying user's Microsoft 365 Copilot license or enabled Retrieval API pay-as-you-go; GitHub Copilot does not qualify. |
| Model deployment fails | Check supported versions/SKUs and remaining regional quota, then re-run with valid `-ModelName`, `-ModelVersion`, and `-ModelCapacity`. |
| MCP approval keeps appearing | The agent intentionally requires approval for every call. Review tool arguments and approve only expected public searches. |

## Cleanup

**Participants:** use your notebook's opt-in cleanup cells when finished. They remove that lab's named objects, not shared Azure services. Notebook 06 removes only its push index; it does not delete notebook 04's index. Keep shared connections until everyone is done.

**Facilitators:** to stop charges for the dedicated workshop resources, intentionally delete the resource group. This removes **everything in that group**, including models, connection secrets, Search, Storage and the Web IQ account:

```powershell
az group delete --subscription '<your-subscription-id>' --name 'rg-iq-workshop'
```

This is destructive and prompts for confirmation. Check the subscription and group first. Any existing APIM service inside that group will be deleted too; keep a gateway you want to retain outside the group. On a shared gateway, separately remove only the approved workshop API/named values and broker setup when no longer needed, not the APIM service.

Revoke Web IQ portal keys and remove/unlink the profile as applicable; group deletion alone does not prove credentials were revoked. Review/revoke temporary policy exemptions and access grants. Expiry of a networking waiver does not itself switch public networking off. Remove local configuration and executed notebook outputs before sharing your copy.

SharePoint app registrations, enterprise applications, consent, federated credentials and site grants are **tenant objects**, not resources in this group. After checking that no other attendee index uses the shared ingestion app, the facilitator must separately revoke its site grant and remove the dedicated workshop app/trust/consent through the approved administrator workflow. Remove only the synthetic SharePoint documents or test site created for the workshop. Do not delete pre-existing apps or sites.

## Lab notes and validation limits

<details>
<summary>Implementation details and what still needs live testing</summary>

- **04 and 06:** use the original synthetic TXT documents in `data/search`, `text-embedding-3-small` with 1,536 dimensions, and semantic ranking. Notebook 04 uses Text Split and Shaper; its logical "pages" are text chunks, not physical page citations. Notebook 06 generates document embeddings locally; Search generates query embeddings. Text Split/Shaper do not require billable AI enrichment, but embeddings and other service usage can incur charges.
- **05:** uses the original two-page `travel-handbook.pdf`, the stable `2026-04-01` Search API and keyless Content Understanding attachment. Content Understanding extracts Markdown and chunks it; there is no additional Text Split skill. Page ranges and embeddings are projected to a separate index. Budget for analysis from the first PDF, including possible timeout charges; preview image descriptions and semantic chunking are not enabled.
- **07-08:** do not need an embedding model. Indexed SharePoint copies content and permissions into Search; remote retrieval leaves content in SharePoint and evaluates the signed-in user's access. Notebook 08 uses minimal reasoning and an extractive response without Foundry model calls.
- **09:** requires the deployed embedding model and Search-to-Foundry role. Web IQ is an `mcpServer` knowledge source, not the built-in Bing-based `web` source. The Web IQ key travels in per-request control headers rather than being stored in Search. MCP sources do not support `minimal` reasoning or `alwaysQuerySource`; the notebook bounds retrieval time.
- **11-12:** hosted agents are preview features. Notebook 11's agent packages are installed remotely by Foundry. Notebook 12 uses **Blob only**: the agent's identity does not provide per-user SharePoint authorization or forward a caller's Web IQ key. Notebook 10 covers that gateway pattern.
- **Validation:** local tests mock service responses and check notebook structure/request handling. Successful resource deployment and authenticated connectivity are not full live notebook validation. Before class, test ingestion, embeddings and queries; toolbox availability and both authentication hops in 03; retrieval response/ACL behavior in 09; hosted-agent region availability in 11; and the connection target, MCP path and toolbox promotion in 12. Notebook 13's live authorization also depends on the bound caller/profile.

</details>

## References

- [Web IQ quickstart](https://learn.microsoft.com/web-iq/get-started/quickstart)
- [Web IQ Azure deployment](https://learn.microsoft.com/web-iq/get-started/deploy-azure-cli)
- [Web IQ authentication](https://learn.microsoft.com/web-iq/get-started/authentication)
- [Web IQ MCP tools](https://learn.microsoft.com/web-iq/integrations/mcp)
- [Web IQ Places API (Beta)](https://learn.microsoft.com/web-iq/reference/places)
- [Web IQ Auto API (Beta)](https://learn.microsoft.com/web-iq/reference/auto)
- [Foundry MCP tools](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/model-context-protocol)
- [Foundry MCP authentication](https://learn.microsoft.com/azure/foundry/agents/how-to/mcp-authentication)
- [Blob indexer](https://learn.microsoft.com/azure/search/search-how-to-index-azure-blob-storage)
- [Text Split skill](https://learn.microsoft.com/azure/search/cognitive-search-skill-textsplit)
- [Shaper skill](https://learn.microsoft.com/azure/search/cognitive-search-skill-shaper)
- [SharePoint indexer and app registration](https://learn.microsoft.com/azure/search/search-how-to-index-sharepoint-online)
- [SharePoint ACL permissions and native groups](https://learn.microsoft.com/azure/search/search-indexer-sharepoint-access-control-lists)
- [Remote SharePoint knowledge source](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-sharepoint-remote)

Infrastructure/API versions and service entitlements can change. The notebooks intentionally show the actual response shape and trace IDs rather than relying on fixed search results.
