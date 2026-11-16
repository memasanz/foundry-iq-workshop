# C# setup and code options

[Participant guide](README.md) | [Choose your labs](../labs/README.md)

C# equivalents currently cover labs 01-14. Choose the supported console apps or optional legacy Polyglot Notebooks.
Both use the root settings files and the same public research examples.

### C# console app (lab 01)

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then set the profile-issued `WEBIQ_API_KEY` in the root `.env`. Lab 01 needs only this key: no Azure sign-in or `.env.azure` is required for its direct Web IQ calls.

Run from the repository root:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- --help
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- web
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- images
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- browse 'https://ofac.treasury.gov/faqs'
```

Other commands: `localized`, `news`, `videos`. Append a quoted public query to any search command; for Browse, append a question after the URL. Browse reads the supplied URL without a hidden search request. HTTP errors print status/body and exit nonzero, without a traceback. Pending crawls retry at most four times with waits capped at 120 seconds. Calls and retries incur charges.

`Program.cs` shows the six request payloads. The small `Workshop.cs` helper loads simple `KEY=VALUE` settings and handles response printing/retry delays. The C# notebook defines its own helpers inline and needs no console-app files. It supports surrounding quotes and comments, but not dotenv interpolation or multiline values. `.env.azure` overrides `.env`, and both override existing environment variables. No additional NuGet packages are needed.

### Optional legacy C# notebook (lab 01)

**Deprecated tooling warning:** Polyglot Notebooks was deprecated March 27, 2026, and .NET Interactive April 24, 2026. They receive no new features, bug fixes or security fixes; future VS Code or SDK versions can break them. See [dotnet/interactive#4163](https://github.com/dotnet/interactive/issues/4163). The console app above is the recommended supported path.

For a walkthrough of the VS Code extension and notebook interface, see Microsoft Learn's [Polyglot Notebooks: setting up your environment](https://learn.microsoft.com/en-us/training/modules/polyglot-notebooks/4-setting-up). This is a legacy guide and does not supersede the deprecation warning above.

The [C# notebooks README](../../c-sharp-notebooks/README.md) walks through installation, an offline C# check, workshop settings and opening lab 01.

If your organization allows the legacy tooling and you have the **Polyglot Notebooks** VS Code extension, open [01-web-iq-apis.ipynb](../../c-sharp-notebooks/01-web-iq-apis.ipynb), select the **.NET Interactive C#** kernel, and run the cells top to bottom. Use `c-sharp-notebooks` as the kernel's working directory so parent configuration paths resolve. The notebook is self-contained; run its inline helper cells before Configuration. Use a C# kernel, not a Python kernel; Python is not required for this Polyglot VS Code path. Keep public queries only and inspect source attribution; image previews can contact external hosts.

The notebook follows Python lab 01's public research examples, request descriptions, response explanations, clean HTTP errors and bounded Browse retries. It is included for exploration, not as a maintained tooling recommendation.

Review [costs and responsible use](../reference/README.md#costs-and-responsible-use)
before live requests, or use the [offline lab 01 checks](../reference/README.md#offline-checks-for-lab-01).

### C# console app (lab 02)

Lab 02 follows the Python notebook's MCP discovery, project connection, prompt agent, **Responses API**,
retrieval verification and follow-up. It uses the same `.NET 10` SDK and simple settings loader as lab 01,
with `HttpClient` and Azure CLI tokens rather than SDK packages. No additional NuGet packages are needed.

For discovery only, your profile-issued `WEBIQ_API_KEY` is sufficient:

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- discover
```

For the full lab, install Azure CLI, sign in to `AZURE_TENANT_ID`, obtain the shared `.env.azure`,
and set `WORKSHOP_PREFIX` in `.env`. You need a deployed Foundry project/model and
**Foundry Project Manager on the project** or the workshop's inherited **Foundry Owner** access.

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- --help
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- run
```

The full run creates/updates the connection, creates an agent version and conversation, and incurs
model and Web IQ charges. A completed initial response always triggers another billable follow-up.
Resources are retained and shared across the language tracks when you reuse the same prefix.
See [lab 02 configuration, permissions and behavior](../../c-sharp-console-apps/02-foundry-web-iq-agent/README.md).

To explore one step at a time, use the separate commands listed by `--help` and follow the lab README's sequence.
Connection setup, agent creation, the initial question and the follow-up can run separately.
The full `run` command retains its automatic follow-up; a separate initial-question command does not hide another model call.

### Optional legacy C# notebook (lab 02)

The same deprecation and kernel-compatibility warnings above apply. Open
[02-foundry-web-iq-agent.ipynb](../../c-sharp-notebooks/02-foundry-web-iq-agent.ipynb) with the C# kernel,
using `c-sharp-notebooks` as the working directory. It preserves the Python notebook's diagrams,
MCP Inspector screenshots, tool descriptions, allowlist, clean HTTP errors and guarded follow-up.
Complete the full lab 02 prerequisites above before starting.

Offline checks use synthetic settings/tokens and mock HTTP responses:

```powershell
dotnet run --project .\tests\c-sharp-foundry\FoundryLabTests.csproj
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_foundry_notebook.py
```

The second check requires the optional legacy `.NET Interactive` tool plus the repository's Python test
environment; Python is a test-runner dependency here, not a requirement to use the C# notebook in VS Code.

### C# console app (lab 03)

Lab 03 follows the Python toolbox notebook, using .NET 10, `HttpClient` and Azure CLI tokens.
Complete lab 02's connection setup in either language with the same `WORKSHOP_PREFIX`.
You need **Foundry Owner on the parent Foundry resource**, the shared Azure settings and Azure CLI sign-in.
This lab reads the existing Web IQ connection's metadata, not its API key.

```powershell
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- --help
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- run
```

The full run publishes a toolbox exposing `web` and `news`, discovers it using Entra authentication,
creates or updates a separate agent-to-toolbox connection, and creates a prompt-agent version and conversation.
Both tool layers use `require_approval='never'`; the question runs without an approval prompt or automatic follow-up.
The toolbox, connection and agent names stay stable across runs. Resources are retained and calls incur charges.
See [lab 03 configuration and authentication](../../c-sharp-console-apps/03-foundry-web-iq-toolbox/README.md).

Use `--help` and the lab README to run individual steps instead:
inspect the existing Web IQ connection, publish the toolbox, discover tools, configure its connection,
create the agent, and ask a question. The discovery command reads an existing toolbox rather than publishing a new version.
The `run` command remains the complete workflow.

### Optional legacy C# notebook (lab 03)

The same deprecation and kernel-compatibility warnings above apply. Open
[03-foundry-web-iq-toolbox.ipynb](../../c-sharp-notebooks/03-foundry-web-iq-toolbox.ipynb) with the C# kernel,
using `c-sharp-notebooks` as the working directory. Its commented helpers are inline; no console-app imports are needed.
Run the numbered sections in order to explore the two authentication hops:
agent to toolbox uses Entra tokens; toolbox to Web IQ uses lab 02's stored profile-key connection.
Rerun agent creation after updating the toolbox connection so the agent and connection target the same toolbox version.

### C# console app (lab 04)

Lab 04 uses .NET 10 and visible REST requests to upload Blob documents, configure a Search indexer and
chunking/embedding skills, then compare keyword, vector, hybrid and semantic-ranked search.
It does not require completing labs 01-03 or providing a Web IQ API key.
Configure the existing Search, Storage and embedding resources, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.

Your identity needs **Search Service Contributor**, **Search Index Data Contributor** and
**Storage Blob Data Contributor** on the relevant resources.
Search's managed identity needs **Storage Blob Data Reader** and embedding inference access on the Foundry resource.
Enable semantic ranker before using the semantic commands. See [lab 04 prerequisites and step overview](../../c-sharp-console-apps/04-blob-indexer-skills/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\04-blob-indexer-skills -- --help
dotnet run --project .\c-sharp-console-apps\04-blob-indexer-skills -- run
```

Use the separate commands listed by `--help` to explore one step or search mode at a time.
Text-based PDFs can be indexed alongside TXT files; scanned PDFs need OCR, which this lab does not configure.
Embedding and semantic-ranking calls can incur charges. Resources are retained; use synthetic, non-sensitive data.

### Optional legacy C# notebook (lab 04)

The same deprecation and kernel-compatibility warnings above apply. Open
[04-blob-indexer-skills.ipynb](../../c-sharp-notebooks/04-blob-indexer-skills.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings and sample-data paths resolve.
Run the inline helpers and numbered sections in order; no console-app imports are needed.
The notebook includes the index screenshots, Text Split/PDF explanations, stored-vector sample check,
query-time question embeddings, and explicit REST status/error reporting from the Python reference.

Offline checks use synthetic HTTP responses, credentials and sample files:

```powershell
dotnet run --project .\tests\c-sharp-search
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_search_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure calls.

### C# console app (lab 05)

Lab 05 uses .NET 10 and visible REST requests to extract an original PDF with Content Understanding,
embed its Markdown sections and retrieve evidence with source URLs and page ranges.
It is independent of labs 01-04 and needs no Web IQ API key or Foundry prompt agent.
Configure existing Search, Storage, Foundry and embedding resources, set `WORKSHOP_PREFIX`,
and sign in with Azure CLI.

Your identity needs Search management/query and Blob upload access.
Search's managed identity needs Blob read access and **Cognitive Services User** on the attached Foundry resource;
embedding-only access is insufficient for Content Understanding.
Enable semantic ranker for the retrieval step. See [lab 05 prerequisites and step overview](../../c-sharp-console-apps/05-blob-content-understanding/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- --help
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- run
```

Use the separate commands listed by `--help` to upload, create Search objects, monitor indexing,
inspect Markdown/page metadata and query independently.
The complete `run` follows the same workflow; no experiments, extra reprocessing or cleanup commands are included.
Resources are retained. Use synthetic PDFs; Content Understanding, embeddings and semantic ranking can incur charges.

### Optional legacy C# notebook (lab 05)

The same deprecation and kernel-compatibility warnings above apply. Open
[05-blob-content-understanding.ipynb](../../c-sharp-notebooks/05-blob-content-understanding.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings and sample-data paths resolve.
Its commented helpers are inline, with no console-app imports.
The notebook includes Python lab 05's pipeline diagram, Search Explorer screenshot,
skill-parameters link and Markdown/page-range explanations.
Fixed-size chunks may span physical pages; the location metadata supports tracing evidence back to the PDF.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-content-understanding
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_content_understanding_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure calls.

### C# console app (lab 06)

Lab 06 moves ingestion into the application: read local TXT files, chunk them, call the embeddings REST API,
and push text, vectors and provenance to a separate Search index.
It is independent of labs 01-05 and needs no Blob access, indexer, Web IQ key or Foundry prompt agent.
Configure existing Search and Foundry embedding resources, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.

Your identity needs **Search Service Contributor**, **Search Index Data Contributor** and
**Cognitive Services OpenAI User** (or **Foundry Owner**) on the parent Foundry resource.
Unlike the indexer labs, document embeddings run as you, not Search.
Search's managed identity still needs embedding inference access for query-time text vectorization.
Enable semantic ranker. See [lab 06 prerequisites and step overview](../../c-sharp-console-apps/06-push-chunks-vectors/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\06-push-chunks-vectors -- --help
dotnet run --project .\c-sharp-console-apps\06-push-chunks-vectors -- run
```

Follow the README's **Run one step at a time** table to create the index, chunk, embed, push, verify
and compare five search modes independently.
Ignored local artifacts preserve chunks and embeddings between commands; these contain sample content,
not just metadata. Follow the README before changing inputs or deployment settings.
The complete `run` does not delete resources. Cleanup requires an explicit opt-in and targets only the push index.
Embeddings and semantic ranking can incur charges; use synthetic, non-sensitive TXT files.

### Optional legacy C# notebook (lab 06)

The same deprecation and kernel-compatibility warnings above apply. Open
[06-push-chunks-vectors.ipynb](../../c-sharp-notebooks/06-push-chunks-vectors.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings and sample-data paths resolve.
Run its commented inline helpers and numbered sections in order, without console-app imports.
It preserves Python lab 06's visible payloads, deterministic chunk keys, embedding response mapping,
per-document push checks, asynchronous content/vector verification and retrieval comparisons.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-push
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_push_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure calls.

### C# console app (lab 07)

Lab 07 ports Python07's **REST SharePoint indexing pipeline**: secretless source, Entra ACL-aware
text/vector index, Content Understanding semantic sections, embeddings and per-chunk ACL projections.
It uses the existing ingestion app and Search identity federation; **no new app, secret, consent, Graph site
grant or source upload is performed**. Use a non-sensitive dedicated site, preview enrollment and the existing
Foundry embedding deployment. See [lab 07 README and identity/permission tables](../../c-sharp-console-apps/07-sharepoint-indexed/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\07-sharepoint-indexed -- --help
# After the administrator setup is complete and reviewed:
dotnet run --project .\c-sharp-console-apps\07-sharepoint-indexed -- run --setup-confirmed
```

Independent commands: `configuration`, `data-source --setup-confirmed`, `index`, `skillset`,
`indexer --setup-confirmed`, `status`, `wait`, `query [question]`, `run-indexer`,
`resync-permissions --confirm` and `cleanup --confirm`.
Setup confirmation acknowledges review, not a federation/site-access test. Indexer creation starts
billable extraction/embeddings; the monitor never requests another run automatically.
The console's ignored checkpoint contains only endpoint/indexer/prior-run metadata.

Setup users need Search Service Contributor. Query users need Search Index Data Reader and actual
document access. Search's system identity needs Cognitive Services User on Foundry and federation
to the ingestion app. The connection uses the app **client ID** and Search identity **client ID**,
not their object/principal IDs. App setup, consent and selected-site grants require separately authorized administrators.
Graph Files.Read.All is tenant-wide; Sites.Selected does not narrow that independent grant.

Queries use the delegated CLI user's Search token for source authorization, never elevated-read access.
Test two non-admin member accounts and directly assigned Entra groups; no native SharePoint-group
expansion or guest ACL support is claimed. Inherited ACL changes need explicit resync/run.
Cleanup removes only the four matching Search objects, preserving the app, site grants and source documents.

### Optional legacy C# notebook (lab 07)

Open [07-sharepoint-indexed.ipynb](../../c-sharp-notebooks/07-sharepoint-indexed.ipynb) with the C# kernel,
using `c-sharp-notebooks` as the working directory.
The cells contain the same visible JSON/REST call sequence as Python07, rather than invoking console workflows.
Inline BCL settings/transport require no SDK packages or imports from another lab.
`INDEXED_SETUP_CONFIRMED = true` matches Python07; review administrator setup before ingestion.
Rerun, permissions resync and cleanup remain opt-in; previous-run state is only in kernel memory.
Pretty-printing keeps responses readable. Outputs can contain private document text/URLs; clear before sharing.

```powershell
dotnet run --project .\tests\c-sharp-sharepoint-indexed
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_sharepoint_indexed_notebook.SharePointIndexedConsoleTests test_csharp_sharepoint_indexed_notebook.SharePointIndexedNotebookTests -v
```

Offline tests compare current Python07 payloads and execute actual C# cells with synthetic HTTP/user tokens.
Kernel checks additionally need the existing Python test environment and optional `.NET Interactive`.
They make no live Azure/Graph/SharePoint call and do not replace the real two-user permission experiment.

### C# console app (lab 08)

Lab 08 retrieves SharePoint passages remotely through a Search knowledge source and knowledge base.
It is independent of lab 07 and needs no custom ingestion app, federation, indexer, document copy,
Web IQ key or Foundry model. Configure `AZURE_TENANT_ID`, `AZURE_SEARCH_ENDPOINT`,
`SHAREPOINT_SITE_URL` and `WORKSHOP_PREFIX`, then sign in with Azure CLI.

Your identity needs **Search Service Contributor**, **Search Index Data Reader** (or Contributor),
and access to the source documents in Microsoft 365.
Each querying user needs a **Microsoft 365 Copilot add-on license** or enabled Retrieval API pay-as-you-go.
Pay-as-you-go enablement requires at least one Copilot license in the tenant; its setup is separate from this app.
A GitHub Copilot license is insufficient. See [lab 08 prerequisites and step overview](../../c-sharp-console-apps/08-sharepoint-remote/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\08-sharepoint-remote -- --help
dotnet run --project .\c-sharp-console-apps\08-sharepoint-remote -- run
```

Follow the README's **Run one step at a time** table to create the remote source, create the knowledge base,
and retrieve independently. Setup commands do not hide a retrieval call.
The full `run` retains resources; cleanup is explicitly opt-in and affects only the prefixed source/base.
The site filter is not an access-control boundary; retrieval evaluates the signed-in user's Microsoft 365 permissions.
Use non-sensitive test documents and inspect returned URLs and gateway diagnostics.

### Optional legacy C# notebook (lab 08)

The same deprecation and kernel-compatibility warnings above apply. Open
[08-sharepoint-remote.ipynb](../../c-sharp-notebooks/08-sharepoint-remote.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings paths resolve.
Run the commented inline helpers and numbered sections in order, without console-app imports.
It preserves Python lab 08's minimal reasoning, extractive output, user-authorized retrieval,
upstream-error diagnostics and indexed-versus-remote comparison.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-remote-sharepoint
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_remote_sharepoint_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure or Microsoft 365 calls.

### C# console app (lab 09)

Lab 09 creates a standalone Web IQ-only knowledge base using existing Search and Foundry model resources.
Configure the root `.env` and `.env.azure`, including `WORKSHOP_PREFIX` and a Web IQ profile API key,
and sign in with Azure CLI. No Blob or SharePoint index, APIM gateway or prompt agent is needed.
See [lab 09 prerequisites and step overview](../../c-sharp-console-apps/09-foundry-iq-webiq-only/README.md)
for the required settings and permissions.

```powershell
dotnet run --project .\c-sharp-console-apps\09-foundry-iq-webiq-only -- --help
dotnet run --project .\c-sharp-console-apps\09-foundry-iq-webiq-only -- run
```

Follow the README's **Run one step at a time** table to create the source, create the knowledge base,
and retrieve independently. Compare synthesized answers with extractive evidence and inspect MCP activity
and cited passages. Only the `web` tool is enabled, not a separate `news` tool.
The full `run` retains resources; cleanup requires explicit confirmation.
The unauthenticated connectivity probe is optional troubleshooting: HTTP 401 is expected, not a failed login.
Live retrieval and synthesis can incur charges; keep the stored Web IQ key out of logs and source control.

### Optional legacy C# notebook (lab 09)

The same deprecation and kernel-compatibility warnings above apply. Open
[09-foundry-iq-webiq-only.ipynb](../../c-sharp-notebooks/09-foundry-iq-webiq-only.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings paths resolve.
Run its inline helpers and numbered sections in order, without console-app imports.
It preserves Python lab 09's Web IQ source, knowledge base, output modes and diagnostic explanations,
including the portal screenshot and the retained legacy `nb14` resource names.
Clear outputs before sharing.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-webiq-only
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_webiq_only_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure or Web IQ calls.

### C# console app (lab 10)

Lab 10 combines Web IQ with the existing Content Understanding Blob index from lab 05.
Complete lab 05 in either language with the same prefix; this lab selects `<prefix>-cu-chunks`.
Configure existing Search and Foundry resources, a Web IQ profile key and `WORKSHOP_PREFIX`
in the root settings files, then sign in with Azure CLI.
See [lab 10 prerequisites and step overview](../../c-sharp-console-apps/10-foundry-iq-webiq-blob/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\10-foundry-iq-webiq-blob -- --help
dotnet run --project .\c-sharp-console-apps\10-foundry-iq-webiq-blob -- run
```

Follow the README's **Run one step at a time** table to verify the populated CU index,
create the two source definitions and knowledge base, then retrieve Blob-only, web-only or mixed evidence.
Compare synthesized and extractive output and inspect activity and reference URLs.
No SharePoint setup, APIM, index creation or reingestion is needed.
Live retrieval and model calls can incur charges; cleanup requires explicit confirmation and preserves the CU index.

### Optional legacy C# notebook (lab 10)

The same deprecation and kernel-compatibility warnings above apply. Open
[10-foundry-iq-webiq-blob.ipynb](../../c-sharp-notebooks/10-foundry-iq-webiq-blob.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings paths resolve.
Run its inline helpers and numbered sections in order, without console-app imports.
It follows Python lab 10's CU index verification, two knowledge sources, output modes and three retrieval examples,
including the portal screenshot. Keep the profile key out of outputs and clear outputs before sharing.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-webiq-blob
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_webiq_blob_notebook.WebIqBlobConsoleTests test_csharp_webiq_blob_notebook.WebIqBlobNotebookTests -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure or Web IQ calls.

### C# console app (lab 11)

Lab 11 combines lab 05's Content Understanding Blob index (`<prefix>-cu-chunks`),
lab 07's ACL-enabled SharePoint index (`<prefix>-nb7-index`) and live Web IQ.
Complete those ingestion labs first using the same prefix; lab 07 currently uses the Python track.
Configure existing Search and Foundry resources, a Web IQ profile key and `WORKSHOP_PREFIX`
in the root settings files, then sign in with Azure CLI.
Your identity also needs access to the indexed SharePoint documents.
See [lab 11 prerequisites and step overview](../../c-sharp-console-apps/11-foundry-iq-knowledge-base/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\11-foundry-iq-knowledge-base -- --help
dotnet run --project .\c-sharp-console-apps\11-foundry-iq-knowledge-base -- run
```

Follow the README's **Run one step at a time** table to verify both indexes, create each source
and the knowledge base independently, then ask internal, web, SharePoint or mixed questions.
Returned references show available filenames, page ranges and URLs regardless of which source the planner selects.
The lab creates no indexes, uploads or ingestion resources. No APIM gateway is needed.
Live Search/model/Web IQ calls can incur charges; cleanup is explicitly confirmed and preserves the indexes.
The stable knowledge-base/source names are reused by labs 12 and 14.

### Optional legacy C# notebook (lab 11)

The same deprecation and kernel-compatibility warnings above apply. Open
[11-foundry-iq-knowledge-base.ipynb](../../c-sharp-notebooks/11-foundry-iq-knowledge-base.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings paths resolve.
Run its inline helpers and numbered sections in order, without console-app imports.
It follows Python lab 11's index checks, source definitions, user-context authorization, rich citations
and raw retrieval diagnostics with an explanation above the diagnostic cell.
Private source links still require the appropriate access; diagnostic output can contain internal text.
Clear outputs before sharing.

Offline checks use synthetic HTTP responses and credentials:

```powershell
dotnet run --project .\tests\c-sharp-knowledge-base
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_knowledge_base_notebook.KnowledgeBaseConsoleTests test_csharp_knowledge_base_notebook.KnowledgeBaseNotebookTests -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure, SharePoint or Web IQ calls.

### C# console app (lab 12)

Lab 12 retrieves through an existing APIM gateway using a token for the broker API.
Complete lab 11 with the same prefix and retain its source/base definitions.
Finish the broker/policy preparation, tenant consent and APIM identity's **Search Index Data Reader** grant first.
No gateway, app registration, source or index is provisioned by this console app.
See [lab 12 prerequisites and step overview](../../c-sharp-console-apps/12-foundry-iq-apim/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\12-foundry-iq-apim -- --help
dotnet run --project .\c-sharp-console-apps\12-foundry-iq-apim -- run
```

Follow the README's **Run one step at a time** table to inspect the broker token,
check the shared Web IQ source, create the APIM-specific knowledge base and retrieve independently.
Source lookup and knowledge-base creation call Search directly and require your Search permissions.
Retrieval calls APIM with the broker token; APIM supplies its own Search identity,
the user's on-behalf-of context and Web IQ authentication.
The architecture diagram explains why these identities and app registrations are separate.

Azure CLI is pre-authorized on the broker's exposed scope; this does not make its Users and groups list
an access allowlist when **Assignment required?** is **No**. Tenant policies still apply.
SharePoint document access remains user-specific; Blob contents are not automatically per-user isolated.
The knowledge base uses `medium` reasoning; live retrieval and model calls can incur charges.
Cleanup requires explicit confirmation and preserves shared sources, indexes, broker and gateway.

### Optional legacy C# notebook (lab 12)

The same deprecation and kernel-compatibility warnings above apply. Open
[12-foundry-iq-apim.ipynb](../../c-sharp-notebooks/12-foundry-iq-apim.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings paths resolve.
Run its inline helpers and numbered sections in order, without console-app imports.
It follows Python lab 12's broker-token inspection, read-only shared source check, model/routing reuse,
gateway retrieval and explained architecture. Token inspection does not replace APIM's JWT validation.
Clear outputs before sharing.

Offline checks use synthetic broker/Search responses and isolated C# kernels:

```powershell
dotnet run --project .\tests\c-sharp-apim\ApimTests.csproj
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_apim_notebook.ApimConsoleTests test_csharp_apim_notebook.ApimNotebookTests -v
```

### C# console app (lab 13)

Lab 13 writes a **C# hosted agent** with one local workshop-tip function, zips its source,
creates an immutable version through Foundry remote build, waits and calls the hosted Responses API.
It needs only an existing Foundry project/chat model, hosted .NET code deployment availability,
Foundry Owner on the parent resource, .NET 10 and Azure CLI sign-in. No labs 11-12 prerequisite,
Search, Web IQ key, Docker, Python, `azd` or app registration is needed.
See [lab 13 prerequisites, step table and code explanation](../../c-sharp-console-apps/13-hosted-agent/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\13-hosted-agent -- --help
dotnet run --project .\c-sharp-console-apps\13-hosted-agent -- run
```

Independent commands: `write-agent`, `package`, `deploy`, `status [version]`, `wait [version]`,
`invoke [question]`, `invoke-tool`, and guarded `cleanup --confirm`.
The launcher is dependency-free; the generated C# host uses pinned NuGet packages restored by Foundry.
It writes `<prefix>-csharp-hosted-agent`, without overwriting Python13's local files.
Both languages publish under `<prefix>-hosted-agent`; use different prefixes for independent Azure agents.
After editing the generated tip, package/deploy/wait/invoke again; `write-agent` and `run` overwrite the edit.

Your user authorizes deployment/invocation; the hosted identity authorizes model calls.
An active build is not proof of runtime readiness or tool use: invoke successfully and inspect tool diagnostics.
Hosted compute, model calls and sessions can incur charges. Cleanup never force-deletes active sessions,
and preserves the project/model and local code.

### Optional legacy C# notebook (lab 13)

Open [13-hosted-agent.ipynb](../../c-sharp-notebooks/13-hosted-agent.ipynb) with the same C# kernel,
using `c-sharp-notebooks` as its working directory. All helpers are inline; no console-project import
or local SDK package installation is required by the notebook. Run the numbered source writing,
packaging, deployment, fixed-version wait and invocation cells. Cleanup is explicitly opt-in.
The README explains the generated Agent Framework host and each step's results and prerequisites.
Clear outputs before sharing.

Offline checks include synthetic HTTP/credentials, actual notebook cells and a local build of the generated C# host:

```powershell
dotnet run --project .\tests\c-sharp-hosted
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_hosted_notebook.HostedConsoleTests test_csharp_hosted_notebook.HostedNotebookTests -v
```

Notebook execution checks additionally need the existing Python test environment and optional `.NET Interactive`;
generated-agent build checks need access to the configured NuGet feed. Neither makes live Azure calls.

### C# console app (lab 14)

Lab 14 connects a **C# hosted agent to a Foundry IQ toolbox**, backed by lab 11's existing Blob source.
It creates a separate Blob-only KB, an `AgenticIdentityToken` Search connection, an immutable toolbox
version/default and a hosted version; then grants the deployed agent principal Search access before questions.
Use lab 11's prefix/resources and an existing .NET remote-build-capable Foundry project/model.
Lab 12, Python, Docker, `azd`, Web IQ keys, a broker and app registrations are not prerequisites.
See [lab 14 README, architecture, permissions and step table](../../c-sharp-console-apps/14-foundry-iq-toolbox-agent/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- --help
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- run
```

`run` writes resources/source and makes two model-backed calls; HTTP 403 on the Search grant prints
an administrator handoff and exits 3 **before questions**. After the administrator grants access, use `invoke`,
not another full deployment. Role propagation may take time; questions are never retried automatically.

Independent steps: `verify-sources`, `knowledge-base`, `connection`, `toolbox`, `check-toolbox`,
`write-agent`, `package`, `deploy`, `status [version]`, `wait [version]`, `grant-search [version]`,
`invoke [question] [--diagnostics]`, `invoke-unknown [--diagnostics]` and `cleanup --confirm`.
Metadata/setup commands do not implicitly ask questions or repair earlier missing resources.
Generated source goes to `<prefix>-csharp-hosted-agent-iq`; packaging preserves edits.
`write-agent`/`run` overwrite source; both language tracks publish under the same prefixed Azure names.

The generated host follows the sample's `AsAIAgent` + `AddFoundryToolboxes` + Responses hosting pattern.
There is no local retrieval function or Search URL in agent code. Your user authors/invokes;
the connection calls Search as **this deployed agent's own principal**, not your user/project identity.
It needs Search Index Data Reader on Search; assigning that role requires separate role-assignment permission.
Retain Search/model service-identity grants from earlier labs. The architecture explains the two model layers.

Inspect evidence, tool errors and citations; the unknown-answer question is not guaranteed to be refused.
Hosted compute/sessions, Search and both model layers can incur charges.
Guarded cleanup removes only this lab's four objects, preserves lab 11/index/local files and never forces sessions.

### Optional legacy C# notebook (lab 14)

Open [14-foundry-iq-toolbox-agent.ipynb](../../c-sharp-notebooks/14-foundry-iq-toolbox-agent.ipynb)
with the C# kernel and `c-sharp-notebooks` working directory.
All BCL helpers are inline; no console import or local SDK packages are required by the notebook.
Run the explained, separate KB/connection/toolbox/source/package/deploy/wait/grant/question cells.
A denied grant stops execute-all before model calls; after the administrator confirms access,
continue with invocation. Cleanup defaults to false. Clear outputs before sharing.

Offline tests compile the actual generated host, compare current Python14 payloads/role UUIDs,
and execute the real inline cells against synthetic HTTP and credentials:

```powershell
dotnet run --project .\tests\c-sharp-iq-toolbox
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_iq_toolbox_notebook.IqToolboxConsoleTests test_csharp_iq_toolbox_notebook.IqToolboxNotebookTests -v
```

Kernel checks additionally need the existing Python test environment and optional `.NET Interactive`;
the generated-host build needs the configured NuGet feed. These checks make no live Azure calls.
