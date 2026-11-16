# Choose your labs

Times are estimates for the exercises, not for administrator approvals or Azure provisioning. You do not have to run every notebook in number order.

**A few terms:** an *index* stores searchable documents; *embeddings* represent content as numbers for similarity search; *MCP* (Model Context Protocol) lets agents call tools. *ACLs* are the permissions that determine who can read a document, and a *managed identity* lets an Azure service authenticate without a shared password.

### Track 1: Web IQ and agents

Start with **01 -> 02 -> 03**. Allow roughly 2-3 hours, including setup and discussion.

| Notebook | Time | What you will do |
|---|---|---|
| [01 - Web IQ APIs](../../python-notebooks/01-web-iq-apis.ipynb) | 40-55 min | Try Web Search, Browse, News, Images and Videos; inspect results and source attribution. |
| [02 - Foundry agent + Web IQ](../../python-notebooks/02-foundry-web-iq-agent.ipynb) | 35 min | Create or update a Web IQ agent, run searches without approval prompts, and inspect its answers. |
| [03 - Foundry agent + Web IQ toolbox](../../python-notebooks/03-foundry-web-iq-toolbox.ipynb) | 30-40 min | Reuse notebook 02's connection through a toolbox: a reusable collection of tools for agents. |

C# alternatives for labs 01-03 are available as [supported .NET 10 console apps](../../c-sharp-console-apps/README.md)
or [optional legacy Polyglot Notebooks](../../c-sharp-notebooks/README.md). See [C# setup](../participants/c-sharp.md).

### Track 2: Your documents and Foundry IQ

Compare **04, 05 and 06**: text splitting in an indexer, PDF extraction/chunking with Content Understanding, and Python chunking with push ingestion. **07 and 08 are alternative SharePoint approaches**; notebook 08 does not require notebook 07's app registration.

Build knowledge bases progressively: **09 (Web IQ only) -> 10 (Web IQ + Blob) -> 11 (Web IQ + Blob + indexed SharePoint)**.
Notebook 09 needs no indexes. Notebook 10 uses the Content Understanding Blob index from 05, without SharePoint.
Notebook 11 adds the existing SharePoint index from 07; remote SharePoint notebook 08 is not a prerequisite.

Lab 04 also has a [supported C# console app](../../c-sharp-console-apps/04-blob-indexer-skills/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/04-blob-indexer-skills.ipynb).
Lab 05 has a [supported C# console app](../../c-sharp-console-apps/05-blob-content-understanding/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/05-blob-content-understanding.ipynb)
for the Content Understanding pipeline.
Lab 06 has a [supported C# console app](../../c-sharp-console-apps/06-push-chunks-vectors/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/06-push-chunks-vectors.ipynb)
for application-owned chunking, embedding and push ingestion.
Lab 07 has a [supported C# console app](../../c-sharp-console-apps/07-sharepoint-indexed/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/07-sharepoint-indexed.ipynb)
for secretless SharePoint ingestion, semantic text sections and Entra ACL-preserving projections.
Lab 08 has a [supported C# console app](../../c-sharp-console-apps/08-sharepoint-remote/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/08-sharepoint-remote.ipynb)
for user-authorized remote SharePoint retrieval without indexing.
Lab 09 has a [supported C# console app](../../c-sharp-console-apps/09-foundry-iq-webiq-only/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/09-foundry-iq-webiq-only.ipynb)
for a standalone Web IQ-only knowledge base, with no indexes or APIM.
Lab 10 has a [supported C# console app](../../c-sharp-console-apps/10-foundry-iq-webiq-blob/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/10-foundry-iq-webiq-blob.ipynb)
for combining Web IQ with lab 05's existing Content Understanding Blob index.
Lab 11 has a [supported C# console app](../../c-sharp-console-apps/11-foundry-iq-knowledge-base/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/11-foundry-iq-knowledge-base.ipynb)
for combining the Content Understanding Blob index, indexed SharePoint and Web IQ with readable source references.
Lab 12 has a [supported C# console app](../../c-sharp-console-apps/12-foundry-iq-apim/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/12-foundry-iq-apim.ipynb)
for broker-token authentication and retrieval through the existing APIM gateway.
Lab 13 has a [supported C# console app](../../c-sharp-console-apps/13-hosted-agent/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/13-hosted-agent.ipynb)
for a real C# function-tool agent deployed through remote build, without Docker or `azd`.
Lab 14 has a [supported C# console app](../../c-sharp-console-apps/14-foundry-iq-toolbox-agent/README.md)
and an [optional legacy C# notebook](../../c-sharp-notebooks/14-foundry-iq-toolbox-agent.ipynb)
for a C# hosted agent with a SharePoint Foundry IQ Toolbox, project service identity and user-delegated document ACL filtering.

| Notebook | Time | What you will do / what comes first |
|---|---|---|
| [04 - Blob indexer and skills](../../python-notebooks/04-blob-indexer-skills.ipynb) | 45-60 min | Let Search read Blob files, split text and generate embeddings. Compare keyword, vector, hybrid and semantic-ranked search. |
| [05 - Blob + Content Understanding](../../python-notebooks/05-blob-content-understanding.ipynb) | 45-60 min | Extract an original two-page PDF as Markdown, chunk and embed it, then retrieve evidence with page ranges. Uses a separate index and billable Content Understanding. |
| [06 - Chunk, embed and push](../../python-notebooks/06-push-chunks-vectors.ipynb) | 35-45 min | Do chunking and embedding in Python or C#, then upload to a separate index. Can run without notebook 04 or Blob access. |
| [07 - SharePoint indexed](../../python-notebooks/07-sharepoint-indexed.ipynb) | 60-90 min | Use Content Understanding semantic text chunks, embeddings and Entra ACL projections, following the text-only setup-index sample. Requires app/consent/site setup; no Vision components. |
| [08 - SharePoint remote](../../python-notebooks/08-sharepoint-remote.ipynb) | 20-30 min | Retrieve from SharePoint without copying content into an index. Requires Microsoft 365 Copilot licensing or enabled Retrieval API billing. |
| [09 - Foundry IQ with Web IQ only](../../python-notebooks/09-foundry-iq-webiq-only.ipynb) | 15-25 min | Create a standalone Web IQ-only knowledge base, without APIM or indexes. Inspect MCP activity, URLs and passages; compare answer synthesis with extractive retrieval. |
| [10 - Foundry IQ with Web IQ + Blob](../../python-notebooks/10-foundry-iq-webiq-blob.ipynb) | 30-40 min | Combine live Web IQ with the Content Understanding Blob index from 05 (`<prefix>-cu-chunks`). Compare web, document and mixed evidence without SharePoint or reindexing. |
| [11 - Foundry IQ knowledge base](../../python-notebooks/11-foundry-iq-knowledge-base.ipynb) | 45-60 min | Combine lab 05's Content Understanding Blob index and lab 07's indexed SharePoint with live Web IQ; ask internal, web and mixed questions without creating indexes or reindexing. |
| [12 - Foundry IQ through APIM](../../python-notebooks/12-foundry-iq-apim.ipynb) | 30-40 min | Build on notebook 11 using an existing API Management gateway that validates callers and protects the Web IQ key. |
| [13 - Hosted agent](../../python-notebooks/13-hosted-agent.ipynb) | 20-30 min | Deploy a small code-based agent to Foundry, without Docker. Needs Foundry and the chat model, not notebooks 11-12. |
| [14 - Hosted agent + Foundry IQ toolbox](../../python-notebooks/14-foundry-iq-toolbox-agent.ipynb) | 40-60 min | Reuse 07/11's indexed SharePoint ACLs through a Toolbox. Separate project service access from user source authorization and verify real Alice/Bob evidence. |

**Optional extension:** [15 - Web IQ authentication](../../python-notebooks/15-web-iq-authentication.ipynb) (25-35 min) explores Entra tokens and managed identities instead of API keys. It is independent of the Search labs and needs a separate caller app/profile binding. Managed-identity exercises require Azure compute exposing that identity; the local exercise uses a hidden secret prompt.

[Workshop home](../../README.md) | [Participant setup](../participants/README.md) | [Facilitator preparation](../facilitators/README.md)

For class-wide approvals and resource readiness, see [what to arrange before class](../facilitators/README.md#arrange-these-before-class).

### Use notebook 11's knowledge base in the Foundry IQ portal

1. Put the workshop `WEBIQ_API_KEY` in local `.env`; never paste the key into chat or commit it.
2. Run notebook 11's **Configuration** and **Web IQ knowledge source** (`create-webiq-source`) cells. The source PUT stores the key under `mcpServerParameters.authentication`, using `kind: storedHeaders` and `headers: { "x-apikey": ... }` inside `storedHeadersParameters`. On a first run, also complete the notebook through **Create the knowledge base**.
3. Open the existing `{prefix}-kb` in the Foundry IQ portal, refresh it, and start a new chat. No index rebuild or knowledge-base recreation is needed when updating source authentication.
4. Ask **What is the latest stable release of Python, and when was it released?** Confirm Web IQ activity and references, then try the notebook's Blob/web comparison.

The key is stored on the **Azure AI Search knowledge source**, not in the portal chat. Search masks stored headers on readback and supplies `x-apikey` during MCP discovery and tool calls. The portal does not send the notebook's former custom authentication headers: without stored source authentication, discovery fails with **401 `auth_missing_token`**. Inspect the retrieval activity/error rather than relying on a model's claim that it cannot browse.

Use a workshop-only key. Authorized callers share its quota/billing; restrict source-authoring permissions and rotate/revoke it afterward. The shared Web IQ key does not bypass the signed-in user's SharePoint ACLs. To rotate it, update `.env` and rerun Configuration plus `create-webiq-source`. See [MCP knowledge-source authentication](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-mcp-server#authentication-options).

See [implementation details and validation limits](../reference/README.md#lab-notes-and-validation-limits)
for the deeper differences between the labs, and [cleanup](../reference/README.md#cleanup) when finished.
