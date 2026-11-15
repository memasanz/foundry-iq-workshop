# 11 — Foundry IQ knowledge base: existing indexes + Web IQ (.NET 10)

Standalone equivalent of [current Python11](../../python-notebooks/11-foundry-iq-knowledge-base.ipynb).
The [C# notebook](../../c-sharp-notebooks/11-foundry-iq-knowledge-base.ipynb)
is self-contained, with inline helpers and actual numbered REST payload cells.
Use Polyglot Notebooks / C# .NET Interactive with kernel directory `c-sharp-notebooks`.
Console commands use **.NET 10** and Azure CLI, without NuGet dependencies, from the
**repository root**. Payloads are visible in `KnowledgeBaseWorkshop.cs`; HTTP/auth,
configuration and the shared mixed-source reference formatter are in `KnowledgeBaseTransport.cs`.

Progression: [09 — web only](../09-foundry-iq-webiq-only/README.md) →
[10 — web + Blob](../10-foundry-iq-webiq-blob/README.md) →
this **web + Blob + indexed SharePoint** lab. Lab11 keeps stable names for labs12/14:
**`<prefix>-kb-blob`, `<prefix>-kb-sharepoint`, `<prefix>-kb-webiq`, `<prefix>-kb`**.
PUT reruns update only those definitions. Prefixes avoid collisions, not enforce isolation.
This lab never modifies the independent 09/10 definitions.

## Existing resources, permissions and private access

Complete [05 — Content Understanding](../05-blob-content-understanding/README.md)
and [07 — indexed SharePoint (Python)](../../python-notebooks/07-sharepoint-indexed.ipynb)
using the same Search service and prefix first:

| Existing index | Required schema |
|---|---|
| `<prefix>-cu-chunks` | Retrievable `chunk_id`, `chunk`, `title`, `source/file_name`, `source/url`, `page_from`, `page_to`; searchable `chunk`; semantic configuration `chunk-semantic`. |
| `<prefix>-nb7-index` | Retrievable `id`, `content`, `sourceFile`, `webUrl`, `page`, `pageTo`; searchable `content`; semantic configuration `semantic-config`; `permissionFilterOption: enabled`, `UserIds.permissionFilter: userIds` and `GroupIds.permissionFilter: groupIds`. |

Both must have **nonzero chunks visible to your signed-in user**. Missing/empty/wrong
schemas are prerequisites to resolve in 05/07, never by rebuilding ingestion here.
`verify-indexes` reads schemas and issues count-only searches; it does not retrieve
document text. This lab creates no indexes, schemas, vectorizers, data sources, skillsets,
indexers, uploads or embeddings. Existing vectorizers may run during retrieval.
SharePoint is a `searchIndex` reference, **not** `indexedSharePoint` ingestion or
`remoteSharePoint`. Lab08 and a Microsoft 365 Copilot license are not required.

| Identity / function | Permission |
|---|---|
| Signed-in user: source/KB writes and cleanup | **Search Service Contributor** on Search. |
| Signed-in user: index reads and retrieval | **Search Index Data Reader** (or **Search Index Data Contributor**) on Search, plus access to indexed SharePoint documents. |
| Search managed identity: existing chat model/vectorizers | **Cognitive Services OpenAI User**, or broader **Cognitive Services User**, on the **parent Foundry resource**. |
| Source author: Web IQ | Authorized workshop-only Web IQ profile key, quota and billing. |

Search queries use Azure CLI sign-in and scope **`https://search.azure.com/.default`**,
API **`2026-08-01-preview`**. One token is acquired per HTTP request.
`Authorization: Bearer <token>` authenticates Search. For the SharePoint count and
**every KB retrieve**, `x-ms-query-source-authorization: <same token>` preserves the
signed-in user's ACL context. It is not an elevated service/ingestion identity.
No user-context header is sent on schema reads, Blob counts, authoring or cleanup.
ACL fields are not returned in source data. Ask as two differently authorized users
to assess real ACL behavior; offline mocks do not prove tenant permissions.

Blob content has no per-user document ACLs in these workshop indexes: Search data access
governs querying it. Returned private Blob links are **metadata, not access grants**:
open them using authorized access; the app creates no public URL or SAS token.
No direct Storage access or SharePoint ingestion-app credentials are needed to run this lab.
Ensure your process can reach Search and Search can reach existing models and Web IQ.

## Settings and setup

Repository-root `.env` loads first, then `.env.azure`; **both override existing process
variables and `.env.azure` wins**. No prompts, infrastructure detection or cache.
The notebook loads these same files from its parent directory.
See [settings example](../../.env.example). Keep secret settings untracked.

| Variable | Value |
|---|---|
| `WORKSHOP_PREFIX` | Required: 3–20 lowercase letters/digits starting with a letter; reuse 05/07. |
| `AZURE_TENANT_ID` | Required tenant GUID. |
| `AZURE_SEARCH_ENDPOINT` | Required public HTTPS Search root, e.g. `https://my-search.search.windows.net`, without credentials/path/query/custom port. |
| `AZURE_AI_ACCOUNT_NAME` | Required existing Foundry account; model URI `https://<account>.openai.azure.com`. |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Required existing **chat** deployment (not embeddings). |
| `AZURE_AI_MODEL_NAME` | Optional, defaults to `gpt-5-mini`; match deployment. |
| `WEBIQ_API_KEY` | Required authorized Web IQ profile key; stored on the Search knowledge source. |

```powershell
dotnet --version # 10.x
az login --tenant '<AZURE_TENANT_ID>'
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- help
```

`help`, `--help`, `-h` and no arguments work **offline before settings/auth**.
Every other command needs the settings above. Unknown commands/options, duplicate options,
extra arguments, blank questions and invalid output modes fail before settings/auth.

## Run one step at a time

Each command does **only the named step**, without hidden prerequisite creation or retrieval.
All query commands offer **all three sources**, exactly like Python11; the question steers
the planner, not a hard source filter. Source selection remains model-driven.

| Command | Action / expected result | Prerequisites | Read-only / writes / billing |
|---|---|---|---|
| `verify-indexes` | GET both schemas; POST count-only `{"search":"*","count":true,"top":0}` on each. Reports visible chunk counts and unchanged schemas. Rejects missing fields, semantic config, ACLs or visible chunks. | Populated 05/07 indexes; settings, sign-in, Search read roles/document access. | Read-only Search operations; no model, vectorizer, Web IQ or indexing calls. |
| `blob-source` | PUT only `kb-blob`, referencing CU chunks including page/source metadata. Reports mapping. | Existing verified CU index; Search author role. | One source definition write; no retrieve/ingestion. |
| `sharepoint-source` | PUT only `kb-sharepoint`, referencing ACL-protected existing index, returning links/pages but not ACL fields. | Existing verified 07 index; author role. | One definition write; no ACL/schema changes or ingestion. |
| `webiq-source` | PUT only `kb-webiq`, stored `x-apikey`, basic `web` tool. Reports ready without echoing key/payload. | Author role and authorized profile key. | One definition write; no extra tool call/probe. |
| `knowledge-base` | PUT only `kb` with three sources, existing model, **medium** reasoning, `answerSynthesis`, passage-only guidance and internal-text protection. | All three existing sources, author role, Search MI grants. | One KB write; no implicit retrieval/model question. |
| `retrieve-internal [question]` | One retrieve; default asks Blob receipts needed for travel reimbursement. Shows answer/evidence, rich references and activity summary. | Existing indexes, sources, KB, query roles/document ACLs, MI inference grants. | Read-only resource access; billable Search/model/vectorizer/Web IQ operations if chosen by planner. |
| `retrieve-web [question]` | One retrieve; default asks today's weather in St. Paul MN. No weather facts are hardcoded. | Same retrieval prerequisites plus Web IQ profile quota. | Billable Search/model/Web IQ operations, no resource writes. |
| `retrieve-sharepoint [question]` | One retrieve; default asks indexed SharePoint recommendations about sustainability and AI. Change topic to match your library. | Same retrieval prerequisites and access to indexed SharePoint documents. | Billable Search/model/vectorizer/Web IQ if selected; no permission changes. |
| `retrieve-mixed [question]` | One retrieve; default compares internal reimbursement policy with public corporate travel best practices. | All retrieval prerequisites. | Billable Search/model/vectorizer/Web IQ for selected sources; no config write. |
| `run [mixed question]` | Verify both indexes, PUT three sources + KB, then internal, web, SharePoint and mixed retrievals. Optional question replaces **only mixed**. Retains resources. | All prerequisites above; no earlier console command required. | Four definition PUTs and exactly four documented questions after read-only checks; **never cleanup**. |
| `cleanup --confirm` | DELETE KB first, then Web IQ, SharePoint and Blob definitions; tolerate 404, stop on other failures. | Settings, author role, exact explicit confirmation. | Only four own definitions deleted; never indexes, ingestion resources, files, models, keys or neighboring 09/10. No retrieval. |
| `help` / `--help` / `-h` / no arguments | Prints offline usage. | None. | No settings reads/auth/HTTP/writes. |

Every `retrieve-*` and `run` accepts
`[--output-mode answerSynthesis|extractiveData] [--diagnostics]`.
Use one quoted nonempty question. Default wire payloads match current Python11;
`answerSynthesis` uses the KB default, while `extractiveData` adds a per-request override.

```powershell
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- verify-indexes
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- blob-source
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- sharepoint-source
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- webiq-source
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- knowledge-base
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- retrieve-internal
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- retrieve-web --diagnostics
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- retrieve-sharepoint "Using my SharePoint source, summarize our sustainability report." --output-mode extractiveData
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- retrieve-mixed --diagnostics
# Or the independent full sequence (no cleanup):
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- run
```

All queries include activity, references and **`includeReferenceSourceData: true` for
all sources**. Runtime is 60 seconds, or 90 for mixed; HTTP timeout is 120/150 seconds.
Schema/authoring calls use 90 seconds (Web IQ authoring: 120). CLI authentication has
a 60-second timeout. There are no automatic paid retries, hidden model questions or
TLS bypasses. `extractiveData` still uses model planning and can incur charges.

## References, raw diagnostics and privacy

The **same formatter regardless of question/source** handles mixed Blob, SharePoint and
Web IQ references. It reads `sourceData` as an object or JSON string, nested/flattened
Blob `source/file_name` + `source/url`, CU `page_from`/`page_to`, SharePoint
`sourceFile`/`webUrl`/`page`/`pageTo`, and Web IQ title/URL encoded in `content`.
Readable labels are `[ref_id:N] filename/title`, `p. N` or `pp. N-M`, and returned URLs.
Only valid integer page ranges add `#page=<first>`; otherwise existing URL fragments
are preserved. A range describes chunk coverage, not where every claim appears.
Absent data falls back to `docKey`/`Source` and an honest “No source URL returned”;
no URLs/pages are invented and missing references alone do not prove an auth failure.

The notebook's diagnostic cell inspects its **existing last response** without another
live call. Console commands are separate processes, so there is deliberately **no
standalone diagnostics command or persistent response cache**. Instead, add
**`--diagnostics` to that retrieve command** (or `run`) before executing it: full activity,
MCP-only activity and raw references are printed from that same in-memory response.
It adds no request or model charge. Running the command again does make a new billable
request. HTTP206 and source-activity errors are rejected even when answer text exists;
diagnostics print before the failure. No response files, internal passages or credentials
are cached. The notebook keeps only the last redacted response in memory.

Raw details can expose query text, internal passages and private URLs. Inspect locally,
clear notebook outputs and avoid sharing terminal logs. Known keys/tokens (including
escaped echoes) are redacted throughout activity, errors, answers, readable/raw references.
Source PUT response bodies and key-containing payloads/settings are never printed.

## Web IQ stored-key security and portal

Only the basic **`web`** tool is allowlisted: `maxOutputTokens: 2000`, rerank and
`outputParsing: auto` (Search parsing, **not** Web IQ Auto). Source and KB instructions
request **`contentFormat: passage`** on every call, never full-document text/html/markdown,
and prohibit sending internal document text to Web IQ. This is planner guidance, not
an enforced tool-argument rewrite. Preview MCP sources do not support `minimal`
reasoning or `alwaysQuerySource`; current Python11 uses **medium**, not low.

`webiq-source` stores `storedHeaders` → `x-apikey` on `<prefix>-kb-webiq`.
Search then supplies it during discovery/calls, including portal retrieval; no Web IQ
header is sent on retrieve. Use a workshop-only key: authorized callers share its
quota/billing. Restrict source-author permissions, rotate/revoke after use, update `.env`
and rerun `webiq-source`. Never paste the key into notebook source or portal chat.
Search masks stored header values on readback; do not rely on that to share raw outputs.

In Foundry IQ portal open `<prefix>-kb`, refresh and start a new chat after changing
authentication. Public question example: “What is the latest stable release of Python,
and when was it released?” Every portal question is another billable request. Check
activity/reference URLs rather than assuming a fluent answer or HTTP200 proves success.
Compare internal/web/SharePoint/mixed activity; the planner may skip a source. Confirm
SharePoint returns chunks and references, not just Blob-only success.

| Observation | Check |
|---|---|
| Missing index, page fields, semantic config, ACL schema or zero visible chunks | Resolve in 05/07 with same prefix and document access. Never reindex here. |
| Search 401/403 | CLI tenant, Search RBAC/networking and indexed document ACLs. Not automatically Web IQ auth failure. |
| Model/vectorizer authorization error | Search managed identity's inference grants on parent Foundry resource. |
| Web IQ `auth_missing_token` | Stored profile key/authorization; rerun `webiq-source`, refresh portal/new chat. |
| 429 | Request Retry-After, service/profile capacity/quota; no automatic retry. |
| 206 or source error | Rejected partial retrieval; inspect raw existing activity with `--diagnostics`. |
| Empty MCP activity or missing reference source data | Source routing/metadata configuration; not proof of auth failure. |

HTTP errors include request/correlation/trace IDs, UTC Date, Retry-After when returned
and bounded redacted response bodies. Malformed JSON/response structures fail clearly.
Preview API/model availability can change; offline tests do not validate live service behavior.

## Optional cleanup and offline validation

Keep the stable knowledge resources for labs12/14. Cleanup deletes **only this KB and
its three definitions**, never original content/ingestion or 09/10 resources:

```powershell
dotnet run --project c-sharp-console-apps\11-foundry-iq-knowledge-base -- cleanup --confirm
```

If another KB references a source, remove that dependency separately first. Notebook
cleanup defaults to `CLEAN_UP = false`; its final disposal cell only releases local HTTP.

Run sequentially (avoid concurrent builds sharing project references):

```powershell
dotnet build c-sharp-console-apps\11-foundry-iq-knowledge-base
dotnet run --project tests\c-sharp-knowledge-base
$env:PYTHONPATH = 'tests'
python -m unittest test_csharp_knowledge_base_notebook.KnowledgeBaseConsoleTests test_csharp_knowledge_base_notebook.KnowledgeBaseNotebookTests -v
dotnet format c-sharp-console-apps\11-foundry-iq-knowledge-base --verify-no-changes
dotnet format tests\c-sharp-knowledge-base --verify-no-changes
```

Tests mock HTTP/auth, compare exact current Python11 payloads and rich reference
formatting, check CU/ACL schema/count failures, user-context headers, errors/redaction,
independent steps, real offline help and exact cleanup scope. Actual isolated notebook
cells execute when existing optional .NET Interactive is available; **do not install
deprecated tools**. No tests make live Azure/Web IQ calls.

## References

- [Foundry IQ](https://learn.microsoft.com/azure/foundry/agents/concepts/what-is-foundry-iq)
- [Existing Search index source](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-search-index)
- [Permission filtering](https://learn.microsoft.com/azure/search/search-query-access-control-rbac-enforcement)
- [MCP sources and authentication](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-mcp-server#authentication-options)
- [Create a KB](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-create-knowledge-base)
- [Retrieve](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve)
