# Lab 05 — Blob indexing with Content Understanding (.NET 10)

Standalone console equivalent of [Python lab 05](../../python-notebooks/05-blob-content-understanding.ipynb).
The [C# notebook](../../c-sharp-notebooks/05-blob-content-understanding.ipynb) is self-contained and
shows the same REST payloads directly.

## Prerequisites

- .NET 10 SDK, Azure CLI, and `az login --tenant <AZURE_TENANT_ID>`.
- Existing Azure AI Search with semantic ranker, Blob container, and a Foundry resource
  supporting Content Understanding with a 1,536-dimensional embedding deployment.
- Your identity: **Search Service Contributor**, **Search Index Data Contributor**,
  and **Storage Blob Data Contributor** on the source container.
- Search system-assigned managed identity: **Storage Blob Data Reader** on the container,
  and **Cognitive Services User** on the parent Foundry resource. An embedding-only role
  is insufficient for Content Understanding. Allow Search network access to both resources.
- Repository-root `.env`, then `.env.azure` (the latter overrides both `.env` and environment variables):
  `AZURE_TENANT_ID`, `AZURE_SEARCH_ENDPOINT`, `AZURE_STORAGE_BLOB_ENDPOINT`,
  `AZURE_STORAGE_RESOURCE_ID`, `AZURE_STORAGE_CONTAINER_NAME`, `AZURE_AI_ACCOUNT_NAME`,
  `AZURE_AI_EMBEDDING_DEPLOYMENT_NAME`, `WORKSHOP_PREFIX`.
  Optional `AZURE_AI_EMBEDDING_MODEL_NAME` defaults to `text-embedding-3-small`.
- Use a prefix such as `alice01`: 3–20 lowercase letters/digits, starting with a letter.
  Names are `<prefix>-cu-{source,chunks,skills,indexer}` and the Blob folder is
  `<prefix>/content-understanding/`. Prefixes avoid collisions, not enforce access isolation.

No Web IQ key, chat model, agent, storage key or SAS is required. Run commands from
the repository root. No command deploys resources or assigns permissions.

## Steps and costs

Each step performs only its named action and necessary prerequisite reads; it does not
silently execute other steps or queries. Inspect the original synthetic
[`travel-handbook.pdf`](../../data/search/travel-handbook.pdf) first.

| Command | Action | Model calls |
|---|---|---|
| `upload` | Validate and overwrite only the sample PDF in your Blob folder (`application/pdf`). | None |
| `data-source` | Create/update the folder-scoped Blob data source with a managed-identity ResourceId connection. | None |
| `index` | Create/update the chunk index, vectorizer and semantic configuration. | None |
| `skillset` | Create/update original-file Content Understanding, separate per-section embedding skill, Shaper provenance and projections. | None on creation |
| `indexer` | Read status, reject an active run, create/update indexer; new indexer auto-starts, existing idle indexer gets one explicit run request. Save local run marker. | Starts paid Content Understanding and indexing embeddings as needed |
| `monitor` | Read/poll status for the accepted run (up to 15 minutes); reject failures and failed items. Update local marker. | None; already-started indexing may continue |
| `inspect` | Read chunks, Markdown, provenance and validate page ranges against the two-page sample. | None |
| `semantic-hybrid` | Keyword + text-vector query, semantic reranking and extractive captions; reject partial responses or empty evidence. | One query embedding; semantic ranking is billable, no chat generation |
| `run` | Execute all eight steps above in order. | All workflow calls above |
| `help` | Print commands without reading configuration or authenticating. | None |

```powershell
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- help
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- upload
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- data-source
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- index
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- skillset
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- indexer
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- monitor
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- inspect
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- semantic-hybrid
# Complete workflow instead of individual steps:
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- run
# Optional question for semantic-hybrid or run:
dotnet run --project .\c-sharp-console-apps\05-blob-content-understanding -- semantic-hybrid "What if my hotel receipt is missing?"
```

Search REST uses stable **2026-04-01**. Content Understanding receives `/document/file_data`,
extracts Markdown and chunks by characters (maximum 1,000, overlap 200), with
`locationMetadata`. The indexer uses `allMetadata`, `allowSkillsetToReadFileData=true` and
PDF-only selection. The separate Azure OpenAI skill embeds each section's `content`.
Foundry is attached keylessly with `AIServicesByIdentity` and `identity=null`;
Search's system identity also handles embeddings. No preview semantic chunking or image
descriptions are enabled.

Compare table structure and retrieved evidence against the original PDF; extraction is not
guaranteed correct. Page fields are ranges, ordinal position is not a page number, and
the Blob URL still requires authorization. Semantic captions are not an LLM-written answer.
See [skill parameters](https://learn.microsoft.com/azure/search/cognitive-search-skill-content-understanding#skill-parameters).

Only `indexer` and `monitor` persist non-sensitive run metadata in `.workshop\lab05-<prefix>.json`,
so a queued previous success is not mistaken for the newly requested run.
HTTP failures display status, bounded response body and request/retry diagnostic headers
without displaying credentials. There are no cleanup, reset, extra-reprocessing or
vector-sampling commands; Blob deletion detection is not configured.

## Offline verification

```powershell
dotnet build .\c-sharp-console-apps\05-blob-content-understanding
dotnet run --project .\tests\c-sharp-content-understanding
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_content_understanding_notebook.py
dotnet format .\c-sharp-console-apps\05-blob-content-understanding --verify-no-changes
```

Tests mock all REST/model traffic and compare payloads with current Python05.
Notebook runtime cases use an isolated .NET Interactive kernel when the optional tool
is installed; they do not import console source or alter the committed notebook.
