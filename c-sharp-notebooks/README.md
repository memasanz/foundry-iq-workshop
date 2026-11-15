# C# Polyglot Notebooks

Run C# code in notebook cells alongside the workshop's explanations and API response descriptions.
Currently available: [lab 01 - Web IQ APIs](01-web-iq-apis.ipynb), [lab 02 - Foundry agent + Web IQ](02-foundry-web-iq-agent.ipynb), [lab 03 - Foundry agent + Web IQ toolbox](03-foundry-web-iq-toolbox.ipynb), [lab 04 - Blob indexer and skills](04-blob-indexer-skills.ipynb), [lab 05 - Blob + Content Understanding](05-blob-content-understanding.ipynb), [lab 06 - Chunk, embed and push](06-push-chunks-vectors.ipynb), [lab 08 - SharePoint remote](08-sharepoint-remote.ipynb), [lab 09 - Foundry IQ with Web IQ only](09-foundry-iq-webiq-only.ipynb), [lab 10 - Foundry IQ with Web IQ + Blob](10-foundry-iq-webiq-blob.ipynb), [lab 11 - Foundry IQ knowledge base](11-foundry-iq-knowledge-base.ipynb), [lab 12 - Foundry IQ through APIM](12-foundry-iq-apim.ipynb), [lab 13 - Hosted agent](13-hosted-agent.ipynb), and [lab 14 - Hosted agent + Foundry IQ toolbox](14-foundry-iq-toolbox-agent.ipynb).
The notebooks use the same `.ipynb` file format as the Python track, but run with the .NET Interactive C# kernel.
Python is not required when using the Polyglot Notebooks extension in VS Code.

[Workshop home](../README.md) | [Participant guide](../docs/participants/README.md) | [Supported C# console apps](../c-sharp-console-apps/README.md)

**Legacy tooling warning:** Polyglot Notebooks was deprecated March 27, 2026, and .NET Interactive April 24, 2026.
They no longer receive bug or security fixes, and future VS Code/.NET updates can break them.
Continue only if your organization permits this tooling; otherwise use the console apps.
See [dotnet/interactive#4163](https://github.com/dotnet/interactive/issues/4163).

## 1. Install the notebook tools

Follow Microsoft Learn's [Exercise - Setting up your environment](https://learn.microsoft.com/en-us/training/modules/polyglot-notebooks/4-setting-up).
It describes three requirements: VS Code, a .NET runtime and the Polyglot Notebooks extension.

1. Install [Visual Studio Code](https://code.visualstudio.com/).
2. Check your installed .NET runtimes:

   ```powershell
   dotnet --list-runtimes
   ```

   Use a runtime compatible with the installed legacy .NET Interactive kernel.
   **The Learn guide's runtime link points to .NET 8, which is now out of support.**
   Do not treat that link as a current version recommendation or install unsupported runtimes without your organization's approval.
   The console apps target .NET 10; having that SDK does not guarantee compatibility with a legacy notebook kernel.
3. In VS Code, open **Extensions** (`Ctrl+Shift+X`), search for **Polyglot Notebooks**, and install Microsoft's extension:
   [`ms-dotnettools.dotnet-interactive-vscode`](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.dotnet-interactive-vscode).
   Reload VS Code if prompted. For these `.ipynb` files, use the .NET Interactive C# kernel, not a Python kernel.

## 2. Check that C# cells run

Use the small, offline check from the Learn guide before making Web IQ calls:

1. Open the Command Palette (`Ctrl+Shift+P`), search for **Polyglot**, and select **Polyglot Notebook: Create new blank notebook**.
2. Select **`.ipynb`**, then **C#**, as in the Learn exercise and the workshop's notebook format.
3. Allow the first-time **Installing .NET Interactive** step to finish if it appears.
4. Enter this code and select the cell's **Run** button:

   ```csharp
   Console.WriteLine("Hello from Polyglot");
   ```

You should see `Hello from Polyglot` below the cell. This check needs no API key and makes no Web IQ requests.
If startup reports a missing runtime, review the required framework/version before installing anything.
If organizational policy or compatibility blocks the legacy kernel, use the supported console app instead.

## 3. Configure the workshop

Open the repository folder in VS Code. In a PowerShell terminal at the **repository root**, create `.env` without overwriting an existing file:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
```

Edit that root `.env` and set `WEBIQ_API_KEY` to your Web IQ **profile-issued API key**.
Do not put it in a notebook cell or output. Lab 01 needs no Azure sign-in or `.env.azure`.
If `.env.azure` exists, the notebook loads it after `.env`, so its duplicate settings win.

## 4. Open and run lab 01

Open [01-web-iq-apis.ipynb](01-web-iq-apis.ipynb) in VS Code and select the **.NET Interactive C#** kernel.
Run the kernel from the `c-sharp-notebooks` directory so the parent settings files resolve.
All notebooks define their helpers inline; no console-app source files or `#!import` directives are needed.
Run the helper cells before Configuration.
To check its working directory, run this in a C# scratch cell:

```csharp
Console.WriteLine(System.IO.Directory.GetCurrentDirectory());
```

If necessary, set it before running the Configuration cell, using your actual repository path:

```csharp
System.IO.Directory.SetCurrentDirectory(@"C:\path\to\foundry-iq-workshop\c-sharp-notebooks");
```

Run the notebook cells from top to bottom. Browse uses the first URL returned by Web Search.
HTTP failures print status/body without a traceback; fix the issue and rerun the relevant search before Browse.

**Calls and Browse retries incur Web IQ usage charges.** Use public, non-sensitive queries only.
See [costs and responsible use](../docs/reference/README.md#costs-and-responsible-use).

Unlike `.dib`, `.ipynb` can save execution outputs. Clear outputs before committing or sharing the source notebook;
`.gitignore` ignores separate executed copies, not outputs embedded in this file.

## 5. Open and run lab 02

Lab 02 additionally requires Azure CLI, sign-in to the workshop tenant, a deployed Foundry project/model,
the facilitator's `.env.azure`, and `WORKSHOP_PREFIX` in `.env`.
Use **Foundry Project Manager on the project** or the workshop's inherited **Foundry Owner** access.
See [lab 02 prerequisites](../c-sharp-console-apps/02-foundry-web-iq-agent/README.md#prerequisites).

Open [02-foundry-web-iq-agent.ipynb](02-foundry-web-iq-agent.ipynb) with the same C# kernel and working directory.
It preserves Python lab 02's diagrams, Inspector screenshots, tool discovery, connection/agent explanations,
`web`/`browse` allowlist, Responses API verification and always-on follow-up after a completed response.
The helper uses `HttpClient` and Azure CLI tokens, with no extra NuGet packages or automatic retries.
Failed discovery prevents later resource cells from writing to Azure. Resources are retained for lab 03.

**Full runs incur model and Web IQ charges**, including the follow-up. Use public queries and clear outputs before sharing.

## 6. Open and run lab 03

Complete lab 02's Web IQ connection setup first, using either language track with the same `WORKSHOP_PREFIX`.
Lab 03 requires **Foundry Owner on the parent Foundry resource**, the Azure settings and Azure CLI sign-in.
It reuses the stored Web IQ connection; you do not need to supply or copy its API key into this lab.
See [lab 03 prerequisites](../c-sharp-console-apps/03-foundry-web-iq-toolbox/README.md).

Open [03-foundry-web-iq-toolbox.ipynb](03-foundry-web-iq-toolbox.ipynb) with the C# kernel and the same working directory.
Run its commented inline helpers and then the numbered sections in order.
The notebook follows Python lab 03: publish a toolbox exposing `web` and `news`, discover its namespaced tools
with Entra authentication, configure a separate agent-to-toolbox connection, and create a prompt agent.
Both layers use automatic approvals. The toolbox, connection and agent have stable prefix-based names.

Reruns publish new toolbox and agent versions and update the existing toolbox connection.
Use the newly created agent version and conversation after updating the connection; older agent versions can
still point at a different toolbox URL. The question runs once, with no automatic follow-up.
Resources are retained, and live model/Web IQ calls incur charges. Clear outputs before sharing.

## 7. Open and run lab 04

Lab 04 is independent of labs 01-03. Configure existing Search, Blob Storage and embedding resources
in the root `.env` and `.env.azure`, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.
No Web IQ API key or Foundry prompt agent is needed.
See [lab 04 settings and permissions](../c-sharp-console-apps/04-blob-indexer-skills/README.md).

Open [04-blob-indexer-skills.ipynb](04-blob-indexer-skills.ipynb) with the same C# kernel and working directory.
Its helpers are inline, and the REST payloads remain visible in the numbered lab sections.
It follows Python lab 04's Blob uploads, data source, index, chunking/embedding skills, indexer monitoring,
stored-vector checks and five search modes. The portal screenshots and PDF/chunking explanations are included.

Unexpected REST statuses print diagnostic bodies and stop the cell; indexer failures do not become successful results.
Text-based PDFs are supported, but OCR and physical PDF page metadata are not configured in this lab.
Indexing and query-time embeddings and semantic ranking can incur charges.
Use synthetic data, preserve the resource-prefix scope, and clear outputs before sharing.

## 8. Open and run lab 05

Lab 05 is independent of labs 01-04. Configure existing Search, Blob Storage, Foundry and embedding resources
in the root settings files, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.
No Web IQ API key or Foundry prompt agent is needed.
Search's managed identity needs **Cognitive Services User** on the attached Foundry resource for
Content Understanding, in addition to Blob read and embedding inference access.
See [lab 05 settings and permissions](../c-sharp-console-apps/05-blob-content-understanding/README.md).

Open [05-blob-content-understanding.ipynb](05-blob-content-understanding.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order; REST payloads remain visible and no console-app imports are needed.
It follows Python lab 05: upload the original two-page PDF, extract Markdown with Content Understanding,
embed each section, project page metadata into a separate index, and retrieve semantic-hybrid evidence.
The pipeline diagram, Search Explorer screenshot and skill-parameters link are included.

Fixed-size chunking uses 1,000 characters with 200-character overlap, not physical PDF page boundaries.
The page ranges identify where each chunk originated; chunks can span pages.
There are no experiments, extra reprocessing or cleanup cells. Resources are retained.
Live analysis, embeddings and semantic ranking can incur charges; use synthetic data and clear outputs before sharing.

## 9. Open and run lab 06

Lab 06 is independent of labs 01-05. Configure existing Search and Foundry embedding resources
in the root settings files, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.
Your identity needs **Search Service Contributor**, **Search Index Data Contributor** and
**Cognitive Services OpenAI User** (or **Foundry Owner**) on the parent Foundry resource.
Search's managed identity needs embedding inference access for query-time vectorization.
Enable semantic ranker; no Blob access, indexer, Web IQ key or prompt agent is needed.
See [lab 06 settings and step instructions](../c-sharp-console-apps/06-push-chunks-vectors/README.md).

Open [06-push-chunks-vectors.ipynb](06-push-chunks-vectors.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order; no console-app imports are needed.
It follows Python lab 06: read local synthetic TXT files, slice overlapping chunks, generate document
embeddings as the signed-in user, push complete chunk documents to a separate index, and verify stored content.
Then compare keyword, vector, hybrid and semantic-ranked retrieval.

Document embeddings are generated in the application; question embeddings still use Search's vectorizer.
Changing or removing chunks can leave old keys behind, so follow the notebook's explicit deletion guidance.
Optional cleanup is disabled by default and targets only the prefixed push index.
Live embeddings and semantic ranking can incur charges; use synthetic data and clear outputs before sharing.

## 10. Open and run lab 08

Lab 08 is independent of the indexed SharePoint lab 07. Configure the existing Search endpoint,
tenant ID, test SharePoint site URL and `WORKSHOP_PREFIX` in the root settings files, then sign in with Azure CLI.
Your identity needs **Search Service Contributor**, **Search Index Data Reader** (or Contributor),
and access to the source documents in Microsoft 365.
Each querying user also needs a **Microsoft 365 Copilot add-on license** or enabled Retrieval API pay-as-you-go.
A GitHub Copilot license does not satisfy this requirement.
See [lab 08 settings, eligibility and step instructions](../c-sharp-console-apps/08-sharepoint-remote/README.md).

Open [08-sharepoint-remote.ipynb](08-sharepoint-remote.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order; no console-app imports are needed.
It creates a remote SharePoint knowledge source and an extractive knowledge base, then retrieves passages
as the signed-in user. There is no custom app registration, indexer, document copy or Foundry model call.

The site filter selects content, but Microsoft 365 document permissions are the authorization boundary.
Test allowed and denied documents separately. Gateway failures print correlation details before stopping.
Live Retrieval API calls are subject to licensing, quota and billing; optional cleanup is off by default.
Clear outputs before sharing.

## 11. Open and run lab 09

Lab 09 creates a standalone Web IQ-only knowledge base using existing Search and Foundry model resources.
It needs a Web IQ profile API key, `WORKSHOP_PREFIX`, root settings files and Azure CLI sign-in.
No Blob or SharePoint index, APIM gateway, prompt agent or earlier lab is required.
See [lab 09 settings, permissions and step instructions](../c-sharp-console-apps/09-foundry-iq-webiq-only/README.md).

Open [09-foundry-iq-webiq-only.ipynb](09-foundry-iq-webiq-only.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order; no console-app imports are needed.
It configures a stored `x-apikey` on the Web IQ source, enables only the `web` tool, creates the knowledge base,
and compares answer synthesis with extractive retrieval. Inspect MCP activity, cited URLs and passages.
The portal screenshot is included; the legacy `nb14` resource suffix is retained to reuse existing resources.

The unauthenticated HTTPS probe is optional troubleshooting, not a permissions check; HTTP 401 is expected.
Keep the Web IQ key out of outputs and source control. Live retrieval and synthesis can incur charges.
Cleanup is explicitly opt-in and targets only this lab's named knowledge base and source.
Clear outputs before sharing.

## 12. Open and run lab 10

Lab 10 combines Web IQ with lab 05's existing Content Understanding Blob index, `<prefix>-cu-chunks`.
Complete lab 05 using either language track with the same `WORKSHOP_PREFIX`.
Configure existing Search and Foundry model resources, the Web IQ profile key and Azure CLI sign-in.
No SharePoint, APIM, new index or reingestion is required.
See [lab 10 settings, permissions and step instructions](../c-sharp-console-apps/10-foundry-iq-webiq-blob/README.md).

Open [10-foundry-iq-webiq-blob.ipynb](10-foundry-iq-webiq-blob.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order, without console-app imports.
It checks the populated index read-only, creates two knowledge sources and a separate knowledge base,
then compares Blob-only, web-only and mixed retrieval in synthesized or extractive output modes.
The portal screenshot illustrates separate public and internal questions; inspect references and activity
to establish which sources answered a particular request.

Keep the stored Web IQ key out of logs and source control. Live retrieval, planning and synthesis can incur charges.
Cleanup is explicitly opt-in and deletes only this lab's knowledge base and source definitions, not the CU index.
Clear outputs before sharing.

## 13. Open and run lab 11

Lab 11 combines lab 05's Content Understanding Blob index (`<prefix>-cu-chunks`),
lab 07's ACL-enabled SharePoint index (`<prefix>-nb7-index`) and live Web IQ.
Complete those ingestion labs with the same prefix; lab 07 currently uses the Python track.
Configure existing Search and Foundry models, a Web IQ profile key and Azure CLI sign-in.
Your Search roles do not replace the required SharePoint document permissions.
See [lab 11 settings, permissions and step instructions](../c-sharp-console-apps/11-foundry-iq-knowledge-base/README.md).

Open [11-foundry-iq-knowledge-base.ipynb](11-foundry-iq-knowledge-base.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order, without console-app imports.
It checks the existing indexes read-only, creates three sources and a knowledge base, then asks
internal, web, SharePoint and mixed questions. No index creation, uploads or reingestion occurs.
The knowledge base is separate from labs 09 and 10; labs 12 and 14 reuse its stable resource names.

References display returned web URLs, PDF filenames/page ranges and SharePoint source links.
Private Blob links still require authenticated access and viewer support for page fragments.
The raw diagnostics explanation describes the last query's activity and references without another request.
Treat diagnostic text as potentially sensitive. Live retrieval and model/Web IQ calls can incur charges.
Cleanup is explicitly opt-in and preserves both indexes. Clear outputs before sharing.

## 14. Open and run lab 12

Complete lab 11 in either language with the same prefix and retain its knowledge sources.
Lab 12 also requires an existing APIM gateway, configured broker app/policy, approved delegated consent
and APIM's system-assigned identity granted **Search Index Data Reader** on Search.
Those preparation steps are separate; this notebook does not configure the gateway or app registrations.
See [lab 12 prerequisites and step instructions](../c-sharp-console-apps/12-foundry-iq-apim/README.md).

Open [12-foundry-iq-apim.ipynb](12-foundry-iq-apim.ipynb) with the same C# kernel and working directory.
Run its inline helpers and numbered sections in order, without console-app imports.
It inspects a broker-audience token, reads the shared keyless Web IQ source and lab 11 definitions,
creates `<prefix>-apim-kb` directly in Search, then retrieves through APIM.
Setup needs your Search roles; gateway retrieval uses your broker token instead.

The included architecture diagram explains the broker app, on-behalf-of user token,
APIM's managed identity and separately held Web IQ key.
Azure CLI is the pre-authorized client, not a user assignment or the broker itself.
Enterprise-app assignment requirements and tenant policies can further restrict users.
SharePoint document authorization remains separate.
The knowledge base uses `medium` reasoning; live retrieval/model calls can incur charges.
Cleanup is explicitly opt-in and deletes only this lab's knowledge base. Clear outputs before sharing.

## 15. Open and run lab 13

Open [13-hosted-agent.ipynb](13-hosted-agent.ipynb) with the C# kernel and `c-sharp-notebooks` working directory.
Use an existing Foundry project/chat deployment with hosted .NET remote-build support,
Foundry Owner on the parent resource and Azure CLI sign-in. No Search or earlier knowledge-base lab is required.
See [lab 13 prerequisites and step explanations](../c-sharp-console-apps/13-hosted-agent/README.md).

Run the inline helpers and numbered cells to write a C# Agent Framework host with a local function tool,
package the three source files, upload a version, wait and call its Responses endpoint.
The generated host's pinned NuGet packages are restored by Foundry; no Python, Docker or `azd` is required.
Your user handles authoring/invocation, while the hosted identity handles model calls.
An active version is not proof of runtime readiness or a function call; inspect successful invocation and tool output.

Source goes to `<prefix>-csharp-hosted-agent`; Azure name remains `<prefix>-hosted-agent`.
Python13 and C#13 under the same prefix create versions of that one agent, not separate agents.
After editing the generated C# tip, package/deploy/wait/invoke again without rerunning source writing.
Hosted compute, model calls and sessions can incur charges. Cleanup is opt-in and does not force-delete
active sessions or remove shared resources/local files. Clear outputs before sharing.

## 16. Open and run lab 14

Open [14-foundry-iq-toolbox-agent.ipynb](14-foundry-iq-toolbox-agent.ipynb) with the same C# kernel and
`c-sharp-notebooks` working directory. Keep lab 11's Blob source/KB with the same prefix and confirm
the .NET remote-build path from lab 13 works. Lab 12 is not required.
See [lab 14 prerequisites, architecture and step explanations](../c-sharp-console-apps/14-foundry-iq-toolbox-agent/README.md).

Run inline setup, Blob-only KB, agentic connection, toolbox publication, source writing, packaging,
deployment, fixed-version wait, per-agent Search grant and the two question cells.
The shared architecture diagram explains caller, agent and Search identities; the notebook maps its
Python labels to C# `AsAIAgent`, `AddFoundryToolboxes` and the Responses hosting layer.
The hosted agent uses `TOOLBOX_NAME`, not a Search URL, KB name, key or local retrieval function.

Source goes to `<prefix>-csharp-hosted-agent-iq`; Azure names match Python14.
Your user authors/invokes, but the `AgenticIdentityToken` connection calls Search as the deployed
agent's own principal. It needs Search Index Data Reader on Search. Role assignment needs separate
administrator rights: HTTP 403 prints the handoff and stops execute-all before questions.
After the administrator confirms the grant, continue with invocation; do not redeploy or repeatedly ask questions.

Inspect successful retrieval output/citations and the unknown-answer test; active does not prove runtime
readiness or grounding. Hosted compute, Search and both model layers can incur charges.
Cleanup is opt-in, preflights/deletes only the four prefixed lab objects and never forces active sessions.
Lab 11's source/base/index and local code are preserved. Clear outputs before sharing.
