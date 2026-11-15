# 06 — Chunk, embed and push vectors (.NET 10)

**Local TXT -> C# character slicing -> Azure OpenAI embeddings REST -> Search
documents REST -> `<prefix>-push-chunks`**. Run from the repository root with
.NET 10 and Azure CLI installed. This is independent of labs 04/05: it reuses an
existing Search service and Foundry embedding deployment, not their pipelines.
`HttpClient`, visible JSON, BCL and Azure CLI tokens are used; no SDK packages,
API keys, infrastructure detection or interactive prompts.
The [self-contained notebook](../../c-sharp-notebooks/06-push-chunks-vectors.ipynb)
has inline helpers and no console-source dependency. Run its C# kernel from
`c-sharp-notebooks`, not the repository root.

## Authentication, settings and permissions

Run `az login --tenant <AZURE_TENANT_ID>`. Repository `.env` loads first,
then `.env.azure`; file values override environment variables and `.env.azure`
wins. Required:

- `AZURE_TENANT_ID`: tenant GUID.
- `WORKSHOP_PREFIX`: 3–20 lowercase letters/digits, starting with a letter,
  e.g. `alice01`. Required before writes; prefixes avoid collisions, **not**
  enforce access isolation.
- `AZURE_SEARCH_ENDPOINT`: public Azure HTTPS service root.
- `AZURE_AI_ACCOUNT_NAME`: parent Foundry account name.
- `AZURE_AI_EMBEDDING_DEPLOYMENT_NAME`: existing embedding deployment.
- Optional `AZURE_AI_EMBEDDING_MODEL_NAME`: defaults to `text-embedding-3-small`.

Search API **2025-09-01**, embedding API **2024-10-21**, dimensions **1,536**.
Keep the ingestion deployment/model/dimensions consistent with the query
vectorizer. Changing dimensions requires recreating only your own index and
regenerating vectors, not just updating its schema.

Your signed-in identity needs **Search Service Contributor** and **Search Index
Data Contributor** on Search, plus **Cognitive Services OpenAI User** (or
**Foundry Owner**) at the **parent Foundry account** scope. Project-scoped
Foundry User alone does not grant account-level OpenAI inference.
Search's system-assigned managed identity needs embedding inference access at
the same account (e.g. **Cognitive Services OpenAI User**, or broader **Cognitive
Services User**) for query-time text vectorization. Enable Search semantic ranker.

No Blob, data source, skillset, indexer, Web IQ key, prompt agent, SharePoint
permissions or tenant-administrator role is required. The user embeds documents;
Search embeds questions. Semantic ranking returns reranked evidence and
extractive captions, **not a generated answer**.

## Run one step at a time

| Command | What happens | Result |
|---|---|---|
| `index` | PUT only the separate push-index schema, keyless vectorizer, HNSW cosine profile and semantic configuration. | An empty/updated destination; no ingestion or model call. |
| `chunk` | Read the two `data\search\*.txt` samples, slice 1,000 characters with 200 overlap; invalidate later local artifacts. | `chunks.json` with text, stable keys, filenames and provenance; offline, no vectors. |
| `embed` | Validate/read `chunks.json`; remove old vector/upload artifacts **before** validation/calls. POST batches of at most 16 chunks as the signed-in user. **Billable embeddings.** | `vectors.json` only after all responses contain exactly one correctly sized vector per input. |
| `push` | Validate/read `vectors.json`, remove old upload marker, POST upload batches of at most 100 documents; inspect every item, including HTTP 207. | `uploaded.json` only after every returned key/status succeeds. No model calls or re-embedding. |
| `verify` | Validate/read `uploaded.json`, GET every key until stored text and vector match (tolerance 1e-6). | IDs/dimensions only; up to 30 passes, 2 seconds apart. No writes/model calls. |
| `keyword [question]` | BM25 query over chunk text. | Up to three keyword results; no embedding or semantic call. |
| `vector [question]` | Text vector query only, `k=50`, using Search identity/vectorizer. **Billable query embedding.** | Up to three vector-similarity results, no keyword candidates. |
| `hybrid [question]` | Combine keyword and text-vector candidates using RRF. **Billable query embedding.** | Up to three fused results; stored vectors unchanged. |
| `semantic [question]` | Keyword query with semantic reranking and extractive captions. **Semantic quota/billing.** | Up to three reranked results; no question embedding. |
| `semantic-hybrid [question]` | Hybrid retrieval plus semantic reranking/captions. **Billable query embedding and semantic ranking.** | Up to three results; rejects partial semantic responses. |
| `run [question]` | Execute index, chunk, embed, push, verify and all five query modes in order, rebuilding artifacts irrespective of previous cache. **Same billable operations as those steps.** | Independent full normal workflow; retains Azure resources. Earlier separate commands are not prerequisites. |
| `cleanup --confirm` | Explicitly delete only the validated `<prefix>-push-chunks` index; 404 is okay. | Deletes no blobs, other indexes, services or deployments; invalidates local upload marker, not chunk/vector cache. |
| `help` | Print commands without configuration/authentication. | Offline usage reminder. |

Copyable ordered commands (repository-root PowerShell):

```powershell
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- help
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- index
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- chunk
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- embed
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- push
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- verify
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- keyword
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- vector
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- hybrid
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- semantic
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- semantic-hybrid
```

Or execute the independent complete workflow:

```powershell
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- run
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- hybrid "When are travel receipts due?"
```

Each step performs **only its own action**, plus necessary validation/local
reads. `embed` requires `chunk`; `push` requires successful `embed` and the
Azure index created by `index`; `verify` requires successful `push`.
Queries require populated Azure data, but no local step cache; they do not
create the index, upload, generate document embeddings or verify ingestion.
`index` can be rerun independently, but does not backfill changed schemas.

## Local artifacts, errors and deletion limitations

Ignored, project/prefix-scoped cache directory:
`.workshop\lab06-<prefix>\`, with **`chunks.json`, `vectors.json`,
`uploaded.json`**. These atomic JSON artifacts persist **synthetic source text,
provenance and (for the last two) full vectors**, not credentials or questions.
Content and vectors can be sensitive; do not substitute private documents.
Compatibility includes repository root, source/chunk fingerprint, algorithm
version, chunk size/overlap, Search endpoint/index and embedding
endpoint/deployment/model/dimensions; edited/incompatible artifacts fail closed.
`chunk` invalidates later artifacts even when inputs are unchanged; failed
`embed` cannot reuse old vectors, and failed `push` cannot leave an accepted
upload marker. Recreating the index outside this app does not automatically
invalidate local artifacts; verification still requires actual matching data.
Notebook documents remain in memory only; restart/rerun chunk and embed after
changing inputs, and never push a partially embedded list.

To clear **only local cache**, from the repository root (replace `alice01`):

```powershell
Remove-Item -Recurse -Force .workshop\lab06-alice01
```

This does **not** delete Azure data. To opt into Azure index deletion:

```powershell
dotnet run --project c-sharp-console-apps\06-push-chunks-vectors -- cleanup --confirm
```

Then rerun `index`, `chunk`, `embed`, `push`, `verify` (or `run`).
`cleanup` without `--confirm` refuses; `run` never cleans up. The notebook's
`CLEAN_UP = false` is also off by default.

Keys are `<filename-stem>-<zero-based-chunk-ordinal>`, parent IDs are stems,
titles/source filenames preserve the originals, and `source.url` is a
repository-relative forward-slash path, **not** a hosted URL. Overlap preserves
context; early stop avoids an extra tail chunk. These are Unicode characters,
not tokens/pages; newline normalization matches Python `read_text`. This
simple slicer need not match Text Split's sentence-aware boundaries. TXT only:
no PDF/DOCX/image extraction.

Identical uploads replace the same keys. **Shortening/removing sources or changing
chunking can leave obsolete Azure keys.** Local invalidation never deletes
documents. Use guarded index cleanup/rebuild before such experiments; production
apps must track and explicitly delete obsolete chunks. This lab has no change
tracking or deletion detection.

HTTP failures propagate with bounded body/request-ID/retry-header diagnostics;
there are **no automatic paid retries**. A 403 may mean missing account-scope
inference permissions or Search roles; 429 may mean quota. Fix the cause before
rerunning. Embedding results are mapped by returned `index`, not response order;
missing/duplicate/out-of-range indices and wrong/non-finite vectors fail.
Push 207 is inspected, not assumed successful; a failed batch may already have
written documents. Correct the error and rerun `push` using the same cached keys.
Verification treats 404/old content as pending, but propagates other HTTP errors.
HTTP 206 or a semantic partial-response reason is rejected, not presented as
successful reranking. Scores are not comparable across BM25, vector, hybrid and
semantic modes; inspect sources, ordering and captions instead.

## Offline validation

No Azure/model calls are made by these tests. Run dotnet operations sequentially
to avoid locking the shared app DLL:

```powershell
dotnet build c-sharp-console-apps\06-push-chunks-vectors
dotnet run --project tests\c-sharp-push
python -m unittest discover -s tests -p test_csharp_push_notebook.py
dotnet format c-sharp-console-apps\06-push-chunks-vectors --verify-no-changes
dotnet format tests\c-sharp-push --verify-no-changes
```

The notebook suite requires the optional .NET Interactive tool and executes
isolated cells/helpers against mocked HTTP, comparing visible payloads with
the approved Python06 baseline.
