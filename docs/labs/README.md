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

C# alternatives for labs 01 and 02 are available as [supported .NET 10 console apps](../../c-sharp-console-apps/README.md)
or [optional legacy Polyglot Notebooks](../../c-sharp-notebooks/README.md). See [C# setup](../participants/c-sharp.md).

### Track 2: Your documents and Foundry IQ

Compare **04, 05 and 06**: text splitting in an indexer, PDF extraction/chunking with Content Understanding, and Python chunking with push ingestion. **07 and 08 are alternative SharePoint approaches**; notebook 08 does not require notebook 07's app registration.

| Notebook | Time | What you will do / what comes first |
|---|---|---|
| [04 - Blob indexer and skills](../../python-notebooks/04-blob-indexer-skills.ipynb) | 45-60 min | Let Search read Blob files, split text and generate embeddings. Compare keyword, vector, hybrid and semantic-ranked search. |
| [05 - Blob + Content Understanding](../../python-notebooks/05-blob-content-understanding.ipynb) | 45-60 min | Extract an original two-page PDF as Markdown, chunk and embed it, then retrieve evidence with page ranges. Uses a separate index and billable Content Understanding. |
| [06 - Chunk, embed and push](../../python-notebooks/06-push-chunks-vectors.ipynb) | 35-45 min | Do chunking and embedding in Python, then upload to a separate index. Can run without notebook 04 or Blob access. |
| [07 - SharePoint indexed](../../python-notebooks/07-sharepoint-indexed.ipynb) | 60-90 min | Use Content Understanding semantic text chunks, embeddings and Entra ACL projections, following the text-only setup-index sample. Requires app/consent/site setup; no Vision components. |
| [08 - SharePoint remote](../../python-notebooks/08-sharepoint-remote.ipynb) | 20-30 min | Retrieve from SharePoint without copying content into an index. Requires Microsoft 365 Copilot licensing or enabled Retrieval API billing. |
| [09 - Foundry IQ knowledge base](../../python-notebooks/09-foundry-iq-knowledge-base.ipynb) | 45-60 min | Combine the existing Blob and indexed SharePoint indexes with live Web IQ; ask internal, web and mixed questions without creating indexes or reindexing. |
| [10 - Foundry IQ through APIM](../../python-notebooks/10-foundry-iq-apim.ipynb) | 30-40 min | Build on notebook 09 using an existing API Management gateway that validates callers and protects the Web IQ key. |
| [11 - Hosted agent](../../python-notebooks/11-hosted-agent.ipynb) | 20-30 min | Deploy a small code-based agent to Foundry, without Docker. Needs Foundry and the chat model, not notebooks 09-10. |
| [12 - Hosted agent + Foundry IQ toolbox](../../python-notebooks/12-foundry-iq-toolbox-agent.ipynb) | 30-40 min | Combine the patterns from 09 and 11. Uses Blob content, a shared toolbox connection and a per-agent Search role. |
| [14 - Foundry IQ with Web IQ only](../../python-notebooks/14-foundry-iq-webiq-only.ipynb) | 15-25 min | Create a standalone Web IQ-only knowledge base, without APIM or indexes. Inspect MCP activity, URLs and passages; compare answer synthesis with extractive retrieval. Does not require 09 or 10. |

**Optional extension:** [13 - Web IQ authentication](../../python-notebooks/13-web-iq-authentication.ipynb) (25-35 min) explores Entra tokens and managed identities instead of API keys. It is independent of the Search labs and needs a separate caller app/profile binding. Managed-identity exercises require Azure compute exposing that identity; the local exercise uses a hidden secret prompt.

[Workshop home](../../README.md) | [Participant setup](../participants/README.md) | [Facilitator preparation](../facilitators/README.md)

For class-wide approvals and resource readiness, see [what to arrange before class](../facilitators/README.md#arrange-these-before-class).

### Use notebook 09's knowledge base in the Foundry IQ portal

1. Put the workshop `WEBIQ_API_KEY` in local `.env`; never paste the key into chat or commit it.
2. Run notebook 09's **Configuration** and **Web IQ knowledge source** (`create-webiq-source`) cells. The source PUT stores the key under `mcpServerParameters.authentication`, using `kind: storedHeaders` and `headers: { "x-apikey": ... }` inside `storedHeadersParameters`. On a first run, also complete the notebook through **Create the knowledge base**.
3. Open the existing `{prefix}-kb` in the Foundry IQ portal, refresh it, and start a new chat. No index rebuild or knowledge-base recreation is needed when updating source authentication.
4. Ask **What is the latest stable release of Python, and when was it released?** Confirm Web IQ activity and references, then try the notebook's Blob/web comparison.

The key is stored on the **Azure AI Search knowledge source**, not in the portal chat. Search masks stored headers on readback and supplies `x-apikey` during MCP discovery and tool calls. The portal does not send the notebook's former custom authentication headers: without stored source authentication, discovery fails with **401 `auth_missing_token`**. Inspect the retrieval activity/error rather than relying on a model's claim that it cannot browse.

Use a workshop-only key. Authorized callers share its quota/billing; restrict source-authoring permissions and rotate/revoke it afterward. The shared Web IQ key does not bypass the signed-in user's SharePoint ACLs. To rotate it, update `.env` and rerun Configuration plus `create-webiq-source`. See [MCP knowledge-source authentication](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-mcp-server#authentication-options).

See [implementation details and validation limits](../reference/README.md#lab-notes-and-validation-limits)
for the deeper differences between the labs, and [cleanup](../reference/README.md#cleanup) when finished.
