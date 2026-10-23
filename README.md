# Azure IQ workshop

Hands-on modules for exploring Azure intelligence services. **Module 01: Microsoft Web IQ** covers the standalone APIs and a Foundry agent connected to the **same Web IQ service** through MCP.

**Module 02: AI Search indexers and skills** adds Blob ingestion, separate indexed and remote SharePoint labs, and a Foundry IQ knowledge base that combines Blob, SharePoint and Web IQ. The notebooks use direct REST requests, Entra authentication and attendee-prefixed objects. They do **not** use the Image Analysis skill or Azure Vision multimodal embeddings.

## Module 01 agenda (90-120 minutes)

| Lab | Time | What participants learn |
|---|---|---|
| Setup and architecture | 15 min | Azure resources, Web IQ onboarding, authentication and costs |
| [01 - Web IQ APIs](notebooks/01-web-iq-apis.ipynb) | 40-55 min | Search passages, source attribution, localization, news, images, videos, Browse, optional Places beta and Auto beta |
| [02 - Foundry agent + Web IQ](notebooks/02-foundry-web-iq-agent.ipynb) | 35 min | Discover MCP tools, secure project connection, agent creation, explicit tool approvals, grounded answers and tool-call inspection |
| Challenge and cleanup | 15 min | Build an evidence-backed briefing; compare direct retrieval with agent-orchestrated retrieval |

## Module 02 agenda

| Lab | Time | What participants learn |
|---|---|---|
| [03 - Blob indexer and skills](notebooks/03-blob-indexer-skills.ipynb) | 45-60 min | Upload synthetic source files; create a managed-identity Blob data source, chunk index, Text Split + Shaper skillset and indexer; inspect provenance, run queries, explore incremental indexing and reset |
| [04 - SharePoint indexed](notebooks/04-sharepoint-indexed.ipynb) | 60-90 min, plus administrator setup | Create an ingestion app registration, configure consent/federation and selected-site grants; project ACLs onto chunks; test Entra and native SharePoint groups |
| [05 - SharePoint remote](notebooks/05-sharepoint-remote.ipynb) | 20-30 min | Remote SharePoint knowledge source and knowledge base with no indexer or app registration; compare with the indexed path |
| [06 - Foundry IQ knowledge base](notebooks/06-foundry-iq-knowledge-base.ipynb) | 45-60 min | Create Blob, SharePoint (indexed or remote) and Web IQ knowledge sources, combine them in one knowledge base with answer synthesis, and ask internal, web and mixed questions |
| [07 - Foundry IQ through APIM](notebooks/07-foundry-iq-apim.ipynb) | 30-40 min | Reach the notebook 06 knowledge base through an APIM gateway using a separate broker app registration: token validation, on-behalf-of exchange for per-user SharePoint access, gateway-held Web IQ key; confirm bad callers get 401 |
| [08 - Hosted agent](notebooks/08-hosted-agent.ipynb) | 20-30 min | Write a minimal Agent Framework agent (one function tool, `ResponsesHostServer`), deploy it to Foundry as a code-based hosted agent with no Docker, and call it with the Responses API |

Notebook 08 is independent of 06-07. It needs only the Foundry project and `workshop-model`, and the `azure-ai-projects` SDK in the notebook environment; the agent's own packages are installed by Foundry (`remote_build`). Hosted agents are a preview feature, so confirm they are available in the project's region (Central US is unverified) and that attendees can create agents in the project. It was verified with mocked tests only.
Notebook 06 needs the **embedding model and the Search-to-Foundry role** that Bicep now deploys, so re-run `scripts\deploy.ps1` on an existing deployment (this updates `.env.azure` with `AZURE_AI_EMBEDDING_DEPLOYMENT_NAME`). Web IQ joins the knowledge base as an `mcpServer` knowledge source (`https://api.microsoft.ai/v3/mcp`); this is Web IQ, not the built-in Bing-based `web` source. The Web IQ key is not stored in Search: the notebook sends it per request in control headers. MCP sources do not support `minimal` reasoning effort or `alwaysQuerySource`, and tool calls are slower, so retrieval sets a runtime limit. Notebook 06 needs `WEBIQ_API_KEY` and `SHAREPOINT_SITE_URL`; indexed mode also needs the notebook 04 app IDs. It was verified with mocked tests only; the retrieve response shape and ACL options are unconfirmed against a live service.

Notebook 03 uses only the existing Search/Storage resources and non-billable utility skills. The original synthetic documents are under `data/search`; no content from the reference repositories is required to run it. Logical Text Split "pages" are chunks, **not physical page-number citations**. No embedding model, Document Intelligence, Content Understanding or Vision resource is needed by notebooks 03-05 (notebook 06 uses the embedding deployment).

Notebook 04 includes an explicit **facilitator app-registration lab**, not just placeholders for a connection string. It explains application/client IDs versus object IDs, dynamically looks up the application permission IDs in Microsoft Graph, offers opt-in app/service-principal creation, and walks through admin consent, a federated trust for Search's system-assigned identity, selected-site authorization and read-only trust verification. App creation is disabled by default. The administrator completes consent and the selected-site grant separately; neither is silently granted by a notebook or the attendee-role script.

The documented indexed ACL configuration uses Graph `Files.Read.All` plus `Sites.Selected`, and SharePoint `Sites.Selected` plus `User.Read.All` for native site-group resolution. **`Files.Read.All` is tenant-wide file-read access**, so this is not strict site-only app isolation despite the explicit selected-site grant. The ACL lab uses a site-scoped FullControl grant on a dedicated test site to inspect permissions; review these privileges with a tenant administrator. It does not request tenant-wide `Sites.FullControl.All`. Do not use production content or weaken Conditional Access to accommodate preview limitations.

Notebook 05 is fully independent of notebook 04. No ingestion app registration or client secret is needed. Remote retrieval uses `2026-08-01-preview`, minimal reasoning effort, an extractive response and semantic query intents, without Foundry model calls or additional model role assignments. Each querying user needs a **Microsoft 365 Copilot add-on license or enabled Retrieval API pay-as-you-go**; this is separate from Azure Search billing. Remote sources do not run Search skillsets or replicate the content into a Search index.

Before class, the facilitator must configure a same-tenant SharePoint test site, satisfy indexer preview eligibility, complete the app/consent/site-grant steps for indexed ingestion, and verify remote licensing or pay-as-you-go eligibility. Demonstrate both an allowed and a denied document using two non-admin member users, including native SharePoint-group membership. Inherited ACL changes require an explicit permissions resync plus an indexer run; one successful resource PUT or empty query does not prove correct permission enforcement. Indexed ACL preview limitations include guest users, mixed Entra-inside-SharePoint group nesting and incompatible enrichment features; review notebook 04's links. Notebook 05 covers the remote path.

## Architecture

```text
Local Jupyter notebook
  |-- Web IQ REST API ------------------> api.microsoft.ai/v3
  |                                        |
  |                                        +-- Web IQ account (global, G3 billing)
  |
  +-- Entra ID --> Foundry project (centralus)
                    |-- GPT model (GlobalStandard, pay per use)
                    +-- MCP tool --> Web IQ MCP --> web/news/images/videos/browse
                         |
                         +-- API key in a secure project connection

Central US Search labs
  |-- AI Search (Standard S1, 1 replica, 1 partition)
  |     |-- Notebook 06 knowledge base: Blob + SharePoint + Web IQ (MCP) --> Foundry models (MI)
  |     +-- Notebook 07: caller --> APIM (validate-jwt, OBO, Web IQ key) --> same knowledge base
  |     |-- Managed identity --> Blob container (read) --> indexer + skills --> chunks
  |     |-- Federated ingestion app --> SharePoint --> indexer + ACL projections --> chunks
  |     +-- Remote knowledge source --> Copilot Retrieval API --> SharePoint (no local index)
  +-- Blob Storage (Standard LRS, Hot, no anonymous access)
```

The Azure Web IQ resource is the billing resource, not a per-resource API endpoint. API traffic uses `https://api.microsoft.ai/v3`. Web IQ always uses `global`; Foundry, AI Search and Blob Storage use `centralus` by default. Bicep creates Web IQ first; Foundry and the data resources are not provisioned if Web IQ creation fails.

Foundry, Search and Storage use public network endpoints protected by Entra authentication. Search API-key authentication and Storage shared-key authentication are disabled; the blob container is private (no anonymous access), **not** a private-network endpoint. This is an introductory workshop, not a private-network production architecture. No VM, Azure-hosted notebook or container runtime is required. The Web IQ notebooks do not create indexes or indexers; the Search labs create them when their cells are run.

## Prerequisites

- Azure CLI, Bicep (`az bicep version`), PowerShell 7, Python 3.11+ and VS Code's Jupyter extension or JupyterLab.
- A **paid subscription enabled for Web IQ**. Azure credits do not work for Web IQ. Enablement is separate from resource-provider registration; request access from your Microsoft representative if necessary.
- Permissions to create resources and role assignments. The deployer normally needs Owner, or Contributor plus Role Based Access Control Administrator. The script resolves the signed-in user's object ID; service-principal deployments must pass a participant user's `-PrincipalId`.
- Foundry model quota in the selected region. Defaults: `gpt-5-mini`, version `2025-08-07`, GlobalStandard capacity `30`. Adjust the script parameters if the model/version is unavailable. GlobalStandard can process inference outside the selected Azure region.
- Access to the [Web IQ portal](https://webiq.microsoft.ai/profiles). Review the [Web IQ terms](https://www.microsoft.com/en-us/webiq/webiq-legal) before making requests.

## 1. Deploy

From the workshop directory:

```powershell
az login
.\scripts\deploy.ps1 `
  -SubscriptionId '<your-subscription-id>' `
  -ResourceGroup 'rg-iq-workshop' `
  -Location 'centralus' `
  -SearchSku 'standard'
```

The script explicitly targets the supplied subscription without changing your default subscription. It creates a dedicated resource group if needed, deploys the Bicep templates, and writes **non-secret** Foundry, Search and Blob settings to `.env.azure`. Re-run the same command to converge the same resources. `-WhatIf` previews changes in an **existing** resource group without deploying or registering providers.

Foundry and Search names include their region, so a region change creates new regional resources rather than trying to move existing ones. Deployments are incremental: old regional resources are not removed automatically, while `.env.azure` points to the newly deployed project after success. The original East US 2 Foundry account for this workshop has been explicitly removed; Central US is the active deployment. Restart notebook kernels to reload those settings and rerun the MCP connection/agent setup in the active project. The existing global Web IQ resource and its profile do not need to move.

An existing resource group's metadata location is preserved. For this workshop, `rg-iq-workshop` remains in `eastus2`, while the new resources inside it are in `centralus`; this is supported. Do not confuse `AZURE_RESOURCE_GROUP_LOCATION` with `AZURE_LOCATION` in the generated environment.

The facilitator receives Foundry User on the project, Search Service Contributor and Search Index Data Contributor on Search, and Storage Blob Data Contributor on the `workshop-documents` container. Search's managed identity receives Blob Data Reader on that container; the Foundry project's identity receives Search Index Data Reader. These grants prepare authenticated access but do not create a Foundry connection, data source or indexer. Additional participants need equivalent appropriate roles for later Search/Storage labs.

Do not use an existing shared resource group: the cleanup command deletes the entire dedicated group.

## 2. Complete Web IQ onboarding

1. Open [Profile Management](https://webiq.microsoft.ai/profiles), sign in, and link the `WEBIQ_RESOURCE_ID` from `.env.azure`.
2. Create an API key. This portal step is **not automated by the Azure resource deployment**.
3. Optionally copy `.env.example` to `.env` and set `WEBIQ_API_KEY`; otherwise use the notebooks' hidden password prompt. Never put credentials directly in a notebook cell.
4. Confirm your profile's allowed service list. Enable the matching optional flags in `.env` only when entitled: news, images, videos, Browse, Places, and Auto. Full-document formats, Places beta and Auto beta require additional access. Set `WEBIQ_ENABLE_PLACES=true` and `WEBIQ_ENABLE_AUTO=true` for the new beta exercises, then restart the kernel. These flags only control which exercises run; they do not grant access or require additional Azure resources.

Web IQ API keys are not Azure Cognitive Services keys and are not retrieved with `az cognitiveservices account keys list`. Production Web IQ Entra authentication uses an **app-only** token and a client ID bound to the Web IQ profile; `az login` alone does not authenticate a Web IQ REST call. The notebooks use a Web IQ API key and Entra credentials for Foundry.

## 3. Open the notebooks

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m ipykernel install --user --name iq-workshop --display-name 'Azure IQ Workshop'
.\.venv\Scripts\python.exe -m jupyter lab
```

On Microsoft-managed devices that block public PyPI, add `--index-url https://packagefeedproxy.microsoft.io/pypi/simple` to the pip command.

Choose the **Azure IQ Workshop** kernel. Run notebook 01, then notebook 02, from top to bottom. Both notebooks locate `.env.azure` and `.env` from either the repository root or `notebooks` directory. The second notebook creates a Foundry project connection containing the shared Web IQ key; project members with sufficient permissions can access it. Use a workshop-only key and rotate or revoke it afterwards.

The Search notebooks use the same kernel and requirements. Set `WORKSHOP_PREFIX` in `.env` (or enter it when prompted), then run notebook 03. For notebooks 04 and 05, add `SHAREPOINT_SITE_URL`; notebook 04 also needs the three registration/identity IDs described in `.env.example`. Notebook 05 needs no registration IDs. Notebook 06 also needs `WEBIQ_API_KEY`. No additional pip packages are needed; PnP PowerShell is optional administrator tooling for the site-grant walkthrough.

The Places exercise covers geographic context, structured addresses, hours and closure exceptions, optional provider ratings, and comparison with ordinary web results. The Auto exercises cover typed evidence, per-item citations, filters and total response-length budgets, plus an opt-in additional request comparing fast and advanced modes. Beta access must be enabled on the Web IQ profile; a successful Azure deployment does not enable it.

Only the facilitator gets a role assignment automatically. For each participant, grant **Foundry User** on the project to run agent operations; creating/updating the MCP connection additionally needs management-plane write permissions, so have the facilitator run that cell first and participants skip it. Give participants their own Web IQ keys where available. Shared AppIDs also share rate limits (initially 15 QPS; confirm in the portal).

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

These roles do **not** grant subscription/resource-group Contributor, Owner, role-assignment management, or write access to Web IQ. The facilitator must still configure the shared Web IQ MCP connection. Web IQ calls use a profile API key (`x-apikey`), which is not controlled by Azure RBAC.

The script grants nothing for SharePoint; that is handled by the app registration below.

## 5. Set up the SharePoint app registration (facilitator, before the workshop)

Notebook 04's **indexed** path needs an Entra app that Azure AI Search uses to read SharePoint. Run `scripts\setup-sharepoint-app.ps1` once, after deployment, as a user who can create app registrations (add `-GrantAdminConsent` only if you also hold a role that can consent). Attendees do not need Entra permissions. The **remote** path needs no app; it runs as each attendee (see notebook 05).

```powershell
.\scripts\setup-sharepoint-app.ps1 -SiteUrl 'https://contoso.sharepoint.com/sites/iq-workshop' -WhatIf  # preview
.\scripts\setup-sharepoint-app.ps1 -SiteUrl 'https://contoso.sharepoint.com/sites/iq-workshop' -GrantAdminConsent
```

What it does, in order (safe to re-run; it reuses the app by name):

1. Reads the Search service's system-assigned managed identity (from `.env.azure`).
2. Creates the app `iq-workshop-sharepoint-indexer` and its service principal, single-tenant.
3. Requests these **application** permissions, looked up by name:
   - Microsoft Graph: `Files.Read.All`, `Sites.Selected`
   - SharePoint: `Sites.Selected`, `User.Read.All`
4. Adds a **federated credential** that trusts the Search managed identity. The app has **no client secret**; Search proves its identity with its managed identity.
5. With `-GrantAdminConsent`, grants tenant admin consent. Otherwise approve it in Entra > App registrations > API permissions.
6. Prints the three values for `.env` (`SHAREPOINT_APP_CLIENT_ID`, `SHAREPOINT_APP_OBJECT_ID`, `SHAREPOINT_SEARCH_MI_CLIENT_ID`).

**One manual step remains:** a SharePoint admin must grant the app access to the workshop site. `Sites.Selected` gives no access until a site is granted. The script prints the command:

```powershell
Grant-PnPEntraIDAppSitePermission -AppId <app-id> -DisplayName 'iq-workshop-sharepoint-indexer' -Site '<site url>' -Permissions FullControl
```

`FullControl` on a dedicated test site is needed for the document-level permission (ACL) lab; `Read` is enough for content-only indexing. Caveat: Graph `Files.Read.All` is tenant-wide read, so this is not strict site-only isolation. Use a test tenant or non-sensitive content. The notebook's own app-creation and federation-check cells stay disabled by default; this script replaces them for facilitators.

**Shared-service warning:** Search Service Contributor is a broad role within the Search service and allows service administration, not just index creation. Attendees can modify/delete one another's Search objects and access shared index documents and blob contents. Use trusted attendees, non-sensitive data and a unique prefix such as `alice-documents` for each person's index, indexer and data source. Prefixes avoid name collisions but are **not** access isolation. Use separate Search services/containers if strict per-user isolation is required. S1 also has finite object and capacity limits, so account for class size before creating many indexes or running all indexers concurrently.

Allow several minutes for RBAC propagation. Re-running the script for the same attendees is safe; attendees do not need shared Search or Storage account keys.

## 6. Set up the APIM gateway (facilitator, before the workshop; notebook 07)

Notebook 07 calls the knowledge base through Azure API Management with a **different app registration** from the SharePoint one. Run these in order after `scripts\deploy.ps1` (which now also creates a Consumption-tier APIM with a system-assigned identity and grants it **Search Index Data Reader** on the Search service):

```powershell
.\scripts\setup-broker-app.ps1 -GrantAdminConsent   # app registration
.\scripts\setup-apim-policy.ps1                      # API + policy; reads WEBIQ_API_KEY from .env or prompts
```

`setup-broker-app.ps1` (idempotent, supports `-WhatIf`):

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

The APIM gateway is billed per call under Consumption. Because header names embed the source name, the policy is written for **one shared Web IQ source** (`-WebIqSourceName`, default `iq-workshop-webiq`); notebook 07 creates it. Give attendees `BROKER_APP_CLIENT_ID`, `APIM_GATEWAY_URL` and `WEBIQ_APIM_SOURCE_NAME` (printed by the scripts). The APIM named-value secrets are visible to anyone with APIM contributor access; use Key Vault references for production.

## Costs and responsible use

**AI Search Standard S1 bills continuously while provisioned**, including when idle. The deployment uses one replica and one partition (one search unit); this is a workshop configuration, not a production high-availability deployment. Semantic ranker starts on its free allowance. Blob Storage uses Standard LRS with Hot access; storage and transactions are metered, and seven-day blob/container soft deletion can retain billable data. Text Split and Shaper do not require a billable AI enrichment account. Remote SharePoint can incur Retrieval API usage charges under pay-as-you-go and Search retrieval charges; minimal reasoning avoids LLM planning/synthesis calls, not all service charges. Check current Central US and Microsoft 365 pricing before class.

As documented on October 23, 2026, Web IQ web/news/image/video search costs **$7 per 1,000 calls** and Browse **$1 per 1,000 calls**; Auto is uncharged while in beta. Five ordinary search calls plus one Browse call are approximately **$0.036**, excluding model tokens, retries, extra exercises, and any separately billed premium content. An agent may issue multiple searches for one prompt. See [current Web IQ pricing](https://learn.microsoft.com/web-iq/resources/pricing) and your Foundry model pricing before a large class.

The estimate above excludes Places. Confirm its current beta/commercial terms with your Microsoft representative before enabling it for a class; do not assume Auto's beta pricing also applies to Places.

Use only public, non-sensitive queries. Retrieved pages are untrusted evidence, not instructions. Search and crawl timestamps do not establish when a fact became true. Preserve source URLs, distinguish publisher dates from crawl dates, and inspect whether citations actually support each claim. Search results do not grant copyright or media reuse rights. Follow the service's attribution and [instrumentation requirements](https://learn.microsoft.com/web-iq/guides/instrumentation) before adapting the lab into a user-facing application.

## Troubleshooting

| Symptom | Action |
|---|---|
| Web IQ resource creation says the subscription is not enabled | Contact your Microsoft representative; provider registration alone cannot fix entitlement. Re-run deployment after enablement. |
| API 401 | Confirm the profile-issued API key; do not use a Foundry/Azure key. |
| API 403 / MCP tool absent | Check your allowed services and full-document entitlement. The optional notebook flags cannot grant access. |
| Browse returns 202 | The notebook waits for `retryAfter` and retries with a bounded limit. An unfinished crawl raises an explicit error. |
| 429 | Respect `Retry-After`; reduce concurrency, especially with shared workshop AppIDs. |
| Foundry 403 | Check project RBAC, `az login` tenant, and allow a few minutes for role propagation. |
| Model deployment fails | Check supported versions/SKUs and remaining regional quota, then re-run with valid `-ModelName`, `-ModelVersion`, and `-ModelCapacity`. |
| MCP approval keeps appearing | The agent intentionally requires approval for every call. Review tool arguments and approve only expected public searches. |

## Cleanup

The second notebook can remove the agent version and conversation it created. Notebooks 03 and 04 have opt-in cleanup for their own named Search objects; notebook 03 can also remove its two sample blobs. Neither deletes shared Azure services. To remove **all Azure workshop resources**, including Foundry, models, connection secrets, AI Search, Blob Storage and the Web IQ billing resource, explicitly delete the dedicated resource group:

```powershell
az group delete --subscription '<your-subscription-id>' --name 'rg-iq-workshop'
```

This is destructive and prompts for confirmation. Revoke Web IQ portal keys and remove/unlink the profile as applicable; Azure group deletion alone is not proof that portal credentials were revoked. Delete local `.env`, `.env.azure`, and any executed notebook outputs before sharing your copy.

SharePoint app registrations, enterprise applications, consent, federated credentials and site grants are **tenant objects**, not resources in this group. After checking that no other attendee index uses the shared ingestion app, the facilitator must separately revoke its site grant and remove the dedicated workshop app/trust/consent through the approved administrator workflow. Remove only the synthetic SharePoint documents or test site created for the workshop. Do not delete pre-existing apps or sites.

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
