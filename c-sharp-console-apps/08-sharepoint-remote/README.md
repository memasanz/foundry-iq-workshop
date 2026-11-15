# 08 — SharePoint remote retrieval (.NET 10)

**SharePoint -> remote knowledge source -> extractive knowledge base -> live
Microsoft 365 Copilot Retrieval API as your signed-in USER.** This independent
lab needs an existing Azure AI Search service and a dedicated non-sensitive
SharePoint test site. It does not require running notebook 07 or any deployment
script. Search stores the source/base configuration, **not a copy of documents**.

The app uses direct BCL `HttpClient`/`JsonNode` REST calls and Azure CLI user
tokens, with no SDK/NuGet packages, API keys, automatic paid retries, local
cache, infrastructure detection, questions or resource provisioning.
The [C# notebook](../../c-sharp-notebooks/08-sharepoint-remote.ipynb) is
self-contained: all helpers and per-step JSON are inline, with no imports or
console-source dependency. Select a C#/.NET Interactive kernel (Polyglot
Notebooks), installed alongside the .NET 10 SDK, and run it from
`c-sharp-notebooks`; the console instead runs from the **repository root**.
Both follow the [current Python08 baseline](../../python-notebooks/08-sharepoint-remote.ipynb).

## Prerequisites, settings and roles

1. Install .NET 10 and Azure CLI. Choose a dedicated test SharePoint site,
   not a production library. Upload the synthetic onboarding/travel samples
   from `data\search` to its default Documents library as **Word documents**.
   Set one document to known allowed access and the travel document to known
   restricted access. Do not use real employee/customer content.
2. Set the following in the **untracked repository-root** `.env` and/or
   `.env.azure`. `.env` loads first, then `.env.azure`, overriding existing
   environment values; `.env.azure` wins for duplicate settings.
3. Sign in: `az login --tenant <AZURE_TENANT_ID>`. Select the appropriate
   tenant/subscription in Azure CLI. The app rejects service-principal/managed
   identity CLI sessions and mismatched active tenants. Do not paste tokens,
   secrets or private retrieval output into notebooks, commits or chat.
4. Confirm each querying user's Microsoft 365 eligibility **before retrieval**.

| Required setting | Meaning and validation |
|---|---|
| `AZURE_TENANT_ID` | Tenant GUID in standard hyphenated form. |
| `WORKSHOP_PREFIX` | 3–20 lowercase letters/digits, starting with a letter, e.g. `alice01`. No default or prompt. |
| `AZURE_SEARCH_ENDPOINT` | Public Azure HTTPS Search service root, e.g. `https://my-search.search.windows.net`; no credentials, path, query, fragment or nondefault port. |
| `SHAREPOINT_SITE_URL` | Public HTTPS tenant site root, `/sites/name` or `/teams/name`, e.g. `https://contoso.sharepoint.com/sites/workshop`; no file/sharing link, query, fragment, credentials or KQL-injection characters. |

Settings are validated **before authentication and writes**. Site-root checking
is intentionally stricter than Python08's general URL check. Prefixes derive
exactly `<prefix>-sp-remote-source` and `<prefix>-sp-remote-kb`; they prevent
naming collisions, **not enforce access isolation**. PUT updates those stable
names; never reuse another person's prefix.

Your user needs **Search Service Contributor** for source/base management and
**Search Index Data Reader** (or **Search Index Data Contributor**) for retrieval
on the existing Search service, plus access to the relevant SharePoint
site/documents. No tenant-administrator role is needed to run the configured
remote path.

Each querying user needs either a **Microsoft 365 Copilot add-on license**, or
explicitly enabled **Copilot Retrieval API pay-as-you-go** eligibility for that
user. **GitHub Copilot is insufficient.** PAYG enablement requires at least one
Copilot license in the tenant and is a **separate administrator prerequisite**,
not something this app configures. Search/Foundry billing alone does not enable
Microsoft 365 Retrieval API billing. Do not change licensing/billing merely
because a gateway error occurred; check eligibility and service health first.

No ingestion app, custom registration, app admin consent, federation, Blob
data source, index/indexer/skillset, Foundry model, embedding deployment,
Content Understanding or LLM answer synthesis is involved.

## Run one step at a time

All commands below run from the **repository root**. Search REST API:
**2026-08-01-preview**. The visible source/base/retrieve payloads in
`RemoteWorkshop.cs` match Python08.

| Command | What happens | Result |
|---|---|---|
| `source` | PUT only `remoteSharePoint` source, `Path:"<site>"` KQL and `Author`/`Title` metadata. | Named source ready; no content copied, base creation or retrieval call. |
| `knowledge-base` | PUT only the base referencing the already-existing source, `models: []`, reasoning `minimal`, output `extractiveData`. | Named extractive base ready; no source write, model call or retrieval. |
| `retrieve [query]` | POST once to the existing base with a `semantic` intent, references/source data and the signed-in user's authorization header. **Invokes the license/billing-eligible, potentially billable Copilot Retrieval API.** | JSON passages/references/activity, verified site URLs; no prerequisite writes or generated answer. |
| `run [query]` | Independently execute source PUT, base PUT and one retrieve POST in order. **Same Retrieval API eligibility/billing as `retrieve`.** | Complete normal workflow irrespective of earlier commands; keeps source/base. Never cleans up. |
| `cleanup --confirm` | Explicitly DELETE exactly the derived base, then remote source; 404 is allowed. | Deletes only those Search configuration objects, not SharePoint content, sites, indexes, services, apps or other resources. |
| `help` / `--help` / `-h` / no arguments | Print actual commands without reading settings, authenticating or creating cache. | Offline help. |

Copyable ordered PowerShell commands:

```powershell
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- help
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- source
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- knowledge-base
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- retrieve "travel reimbursement receipts"
```

Or use the independent full normal workflow (the earlier steps are **not**
prerequisites for `run`):

```powershell
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- run "travel reimbursement receipts"
```

Without an optional quoted query, `retrieve` and `run` default to
`travel reimbursement receipts`. Empty/over-1,500-character questions fail
before calls; pass the query as one quoted argument. Each step performs **only
its named action**, plus settings/identity validation. If the source/base is
missing, the corresponding server error propagates; no command silently
creates prerequisites or performs extra paid queries. No persistent local
cache is necessary because source/base names are stable.

## Identity, security, limits and evidence

Azure CLI obtains a token for `https://search.azure.com/` in your tenant.
Management uses `Authorization: Bearer <user token>`. Retrieval also sends
**`x-ms-query-source-authorization: <raw user token>`**, without a second
`Bearer` prefix, matching Python08. Microsoft 365 evaluates the **querying
user's** document permissions; Search is not using a new ingestion app or
different delegated identity.

The `Path` KQL filter selects the test site but **is not an authorization
boundary**: invalid KQL can be ignored upstream. The app prints extractive
JSON and verifies encountered `webUrl` values against the selected host/site
path, failing on out-of-site URLs rather than silently accepting them.
Inspect all references/source data yourself; URL checks are not a substitute
for permission tests. A successful PUT does not demonstrate retrieval,
licensing, document access or a working filter.

Preview and Microsoft 365 indexing affect results. A newly uploaded document
may not immediately appear. Supported formats are those of the upstream
Retrieval API; use the documented Word test content, not an assumption that
every uploaded file is supported. This is textual retrieval, **not a
multimodal image/chart extraction pipeline**. Current baseline limits are
200 requests/user/hour, 1,500 query characters and up to 25 results; verify
current documentation before running. `semantic` intent is an API retrieval
term, not a configured embedding model. Minimal reasoning plus extractive
output avoids LLM planning and synthesis.

Compare with [indexed Python07](../../python-notebooks/07-sharepoint-indexed.ipynb)
using the same question and two user identities. Compare indexed
`sourceFile`/`webUrl`/`content` and page ranges against remote references,
source data and activity. Passage ordering/counts may differ. Indexed07
copies content into Search and requires ingestion app/federation and ACL
resynchronization; remote08 does none of those, but still has M365
permission/indexing propagation latency and per-user Copilot/PAYG eligibility.
No C#07 implementation is needed for this lab.

**Acceptance exercise:** demonstrate a known allowed and a known denied
document under both users. For comparison with indexed07, use direct
user/Entra-group document access; test native SharePoint-group access separately
on the remote path. A fluent answer, empty result or successful source/base PUT
is not evidence that permission enforcement works.

## Errors, retention and explicit cleanup

On HTTP failures, the app prints **status, UTC Date, request/correlation/trace
headers and bounded response body (8,000 characters)** before failing.
`Copilot retrieval call failed ... BadGateway ... /beta/copilot/retrieval`
is an **upstream Microsoft 365 gateway failure**, not an empty success or
missing Content Understanding role. Check user licensing/PAYG eligibility and
Microsoft 365 service health. If a manual retry still fails, retain UTC and
correlation/request IDs for support, redacting private output. No automatic
paid retry, auth changes, permission grants or billing enablement occur.
HTTP 206 and activity source errors also fail after printing activity, rather
than being presented as complete retrieval. No-reference success displays a
query/permissions/indexing/eligibility reminder, not a permission-test pass.

Source/base configuration persists until you explicitly opt in to deletion:

```powershell
dotnet run --project c-sharp-console-apps\08-sharepoint-remote -- cleanup --confirm
```

`cleanup` without exactly `--confirm` refuses. Deletes are base-first,
source-second; 404 is okay, but 401/403 and other failures stop rather than
being ignored. This never deletes indexes, shared services, SharePoint sites,
documents or Entra resources. The notebook has **`CLEAN_UP = false`** by
default; set it to true only when you want the same exact-scope deletion.
Rerun `source`/`knowledge-base` (or `run`) afterward.

No document content is persisted by the console, but terminal/notebook output
can contain private text and URLs. Clear all notebook outputs before sharing
and do not retain sensitive console logs.

## Offline validation

No live Azure/Microsoft 365 calls, provisioning or licensing changes:

```powershell
dotnet build c-sharp-console-apps\08-sharepoint-remote
dotnet run --project tests\c-sharp-remote-sharepoint
python -m unittest discover -s tests -p test_csharp_remote_sharepoint_notebook.py
dotnet format c-sharp-console-apps\08-sharepoint-remote --verify-no-changes
dotnet format tests\c-sharp-remote-sharepoint --verify-no-changes
```

Run dotnet builds/formats **sequentially** to avoid shared app-reference DLL
locks. The notebook suite requires the optional .NET Interactive tool, validates
notebook schema and executes actual isolated cells/helpers against mock HTTP,
including exact current Python08 payload parity, entrypoint help without
configuration/authentication, diagnostics, step independence and cleanup guards.

## References

- [Remote SharePoint source, eligibility and limits](https://learn.microsoft.com/azure/search/agentic-knowledge-source-how-to-sharepoint-remote)
- [Create a knowledge base](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-create-knowledge-base)
- [Minimal reasoning effort](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-set-retrieval-reasoning-effort)
- [Retrieve action and semantic intents](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve)

Preview API versions and Microsoft 365 eligibility can change.
