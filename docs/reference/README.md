# Architecture and workshop reference

[Workshop home](../../README.md) | [Participant guide](../participants/README.md) | [Facilitator guide](../facilitators/README.md)

Jump to: [Architecture](#architecture) | [Costs](#costs-and-responsible-use) |
[Troubleshooting](#troubleshooting) | [Cleanup](#cleanup) |
[Validation limits](#lab-notes-and-validation-limits) | [Running and checking notebooks](#running-and-checking-notebooks)


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


## Costs and responsible use

**Search Standard S1 bills continuously, even when nobody is running a notebook.** Closing VS Code does not stop Azure charges. Old resources retained after a regional deployment can also keep billing.

| Service | What to budget for |
|---|---|
| Search | One S1 replica/partition, retrieval usage and semantic-ranking allowances. This is a workshop setup, not production high availability. |
| Foundry models | Chat and embedding calls; agents can make several tool/model calls for one prompt. Model capacity is not a spending cap. |
| Web IQ | Search/Browse calls and retries. Check [current pricing](https://learn.microsoft.com/web-iq/resources/pricing). |
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
| Browse returns 202 | Lab 01 waits for `retryAfter` and retries with a bounded limit. An unfinished crawl prints an error; the console app also exits nonzero. |
| 429 | Respect `Retry-After`; reduce concurrency, especially with shared workshop AppIDs. |
| No Python kernel / imports fail | Select **Web & Foundry IQ Workshop**, install `requirements.txt` with that environment's Python, then restart the kernel. |
| Missing `.env.azure` | Ask the facilitator for the shared configuration, or finish your own deployment. Do not invent endpoints. |
| Notebook uses an old endpoint | Remove stale generated-setting overrides from `.env` and restart the kernel. |
| Foundry 403 | Check the sign-in tenant and parent-resource/project roles for the specific operation; allow time for propagation. |
| Blob upload/indexer cannot connect | Check public networking and inherited policy, then the required Blob data role. RBAC alone does not bypass a network restriction. |
| SharePoint 07 authorization fails | Check admin consent, the explicit site grant, required API permissions and federation to the current Search identity. Subscription Owner is not tenant admin consent. |
| SharePoint 08 license/billing error | Check each querying user's Microsoft 365 Copilot license or enabled Retrieval API pay-as-you-go; GitHub Copilot does not qualify. |
| Model deployment fails | Check supported versions/SKUs and remaining regional quota, then re-run with valid `-ModelName`, `-ModelVersion`, and `-ModelCapacity`. |
| MCP approval keeps appearing | Notebook 03 intentionally requires approval for every call. Notebook 02 runs `web` without prompts; rerun its agent-creation cell to apply the updated configuration and start a fresh conversation. |


## Cleanup

**Participants:** use your notebook's opt-in cleanup cells when available. They remove that lab's named objects, not shared Azure services. Notebooks 02-03 retain agents, versions, toolboxes, conversations and connections; remove them manually in Foundry when no longer needed. Notebook 06 removes only its push index; it does not delete notebook 04's index. Keep shared connections until everyone is done.

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
- **09:** requires notebook 04's Blob index (or 05's, selected in the configuration cell), notebook 07's SharePoint index, and the Search-to-Foundry role for the chat model and existing vectorizers. Both indexes are referenced through `searchIndex` knowledge sources; no uploads, indexes or ingestion pipelines are created. Notebook 08 and Copilot licensing are not required. Web IQ is an `mcpServer` knowledge source using only basic `web` search with passage-format guidance, not the built-in Bing-based `web` source or Web IQ Auto. Its workshop key is stored in the knowledge source's `storedHeaders` authentication so notebook and Foundry IQ portal retrieval both work. Authorized callers share that key's quota/billing; restrict source-authoring access and rotate/revoke the key afterward. Search masks it on readback. Notebook 10 uses a separate APIM-held key pattern. MCP sources do not support `minimal` reasoning or `alwaysQuerySource`; the notebook bounds retrieval time.
- **11-12:** hosted agents are preview features. Notebook 11's agent packages are installed remotely by Foundry. Notebook 12 uses **Blob only**: the agent's identity does not provide per-user SharePoint authorization or forward a caller's Web IQ key. Notebook 10 covers that gateway pattern.
- **Validation:** local tests mock service responses and check notebook structure/request handling. Successful resource deployment and authenticated connectivity are not full live notebook validation. Before class, test ingestion, embeddings and queries; toolbox availability and both authentication hops in 03; retrieval response/ACL behavior in 09; hosted-agent region availability in 11; and the connection target, MCP path and toolbox promotion in 12. Notebook 13's live authorization also depends on the bound caller/profile.

</details>


## References

- [Web IQ quickstart](https://learn.microsoft.com/web-iq/get-started/quickstart)
- [Web IQ Azure deployment](https://learn.microsoft.com/web-iq/get-started/deploy-azure-cli)
- [Web IQ authentication](https://learn.microsoft.com/web-iq/get-started/authentication)
- [Web IQ MCP tools](https://learn.microsoft.com/web-iq/integrations/mcp)
- [Foundry MCP tools](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/model-context-protocol)
- [Foundry MCP authentication](https://learn.microsoft.com/azure/foundry/agents/how-to/mcp-authentication)
- [Blob indexer](https://learn.microsoft.com/azure/search/search-how-to-index-azure-blob-storage)
- [Text Split skill](https://learn.microsoft.com/azure/search/cognitive-search-skill-textsplit)
- [Shaper skill](https://learn.microsoft.com/azure/search/cognitive-search-skill-shaper)
- [SharePoint indexer and app registration](https://learn.microsoft.com/azure/search/search-how-to-index-sharepoint-online)
- [SharePoint ACL permissions and native groups](https://learn.microsoft.com/azure/search/search-indexer-sharepoint-access-control-lists)
- [Remote SharePoint knowledge source](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-sharepoint-remote)

Infrastructure/API versions and service entitlements can change. The notebooks intentionally show the actual response shape and trace IDs rather than relying on fixed search results.


## Running and checking notebooks

Run the commands below from the repository root.

### Verify notebooks from a Python script

Run the actual notebook cells without opening Jupyter:

```powershell
.\.venv\Scripts\python.exe .\scripts\run-notebooks.py .\python-notebooks\01-web-iq-apis.ipynb
```

Pass additional notebook paths to run them in the specified order. Each notebook gets a fresh kernel using the same Python environment as the script. Repository notebooks run with `python-notebooks` as their working directory; notebooks elsewhere retain the repository-root working directory. Top-level `await` works just as it does in Jupyter. The runner stops at the first failure and returns a nonzero exit code.

**These are live runs, not mocked tests:** cells make service calls, incur charges, and create or delete resources according to the notebook's configuration. Prepare `.env`, `.env.azure`, credentials and required permissions first; interactive password prompts are not supported. Notebook opt-in flags retain their existing defaults, so a passing run does not verify disabled branches. Image previews are saved but their browser rendering is not verified.

Original notebooks are unchanged. Executed copies, including partial outputs and errors from failed runs, are saved under the git-ignored `.deployment\notebook-runs` folder. Open those copies to inspect results; treat them as potentially sensitive. Use `--timeout 600` to allow ten minutes per cell, or `--output-dir PATH` to choose another output folder.

### Offline checks for lab 01

These checks make no Web IQ service calls:

```powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_notebooks.py -k DirectApiCellTests
dotnet run --project .\tests\c-sharp\WebIqLabTests.csproj
```

If the optional legacy `dotnet-interactive` tool is already installed, `test_csharp_notebook.py` also executes the actual C# cells against a mocked HTTP handler and checks request parity with Python. It uses isolated synthetic settings and temporary kernel specs; it skips when the tool is absent. Do not install deprecated tooling just to run the supported console tests.
