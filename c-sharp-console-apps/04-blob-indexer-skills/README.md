# 04 — Blob indexer and enrichment skills (.NET 10)

Run from the repository root. This console lab uses `HttpClient`, visible JSON
payloads, BCL helpers and Azure CLI Entra authentication; no NuGet SDK or API key.
The [standalone C# notebook](../../c-sharp-notebooks/04-blob-indexer-skills.ipynb)
teaches the same pipeline with each REST payload in its own cell.

## Configuration and permissions

Run `az login --tenant <AZURE_TENANT_ID>`. Settings load from `.env`, then
`.env.azure`, overriding existing environment variables. Required settings:
`AZURE_TENANT_ID`, `WORKSHOP_PREFIX`, `AZURE_SEARCH_ENDPOINT`,
`AZURE_STORAGE_RESOURCE_ID`, `AZURE_STORAGE_BLOB_ENDPOINT`,
`AZURE_STORAGE_CONTAINER_NAME`, `AZURE_AI_ACCOUNT_NAME`,
`AZURE_AI_EMBEDDING_DEPLOYMENT_NAME`. Optional `AZURE_AI_EMBEDDING_MODEL_NAME`
defaults to `text-embedding-3-small`; this lab fixes dimensions at 1,536.
Use a 3–20 lowercase letter/digit prefix starting with a letter.
Public Azure HTTPS Search/Blob endpoints and a tenant GUID are validated before writes.
Existing resources are supported; no infrastructure-file check is performed.

Your user needs **Search Service Contributor**, **Search Index Data Contributor**
and **Storage Blob Data Contributor** on the sample container. Search's
system-assigned identity needs **Storage Blob Data Reader** and embedding
inference access (**Cognitive Services OpenAI User**, or the broader
**Cognitive Services User** granted by this repository). Enable semantic ranker.

## Run one step at a time

| Command | What happens | Result |
|---|---|---|
| `upload` | Upload the two synthetic TXT files from `data\search` to `<prefix>/blob/` in the existing Blob container. Overwrite only those sample blobs. | Source documents ready for the indexer; no chunks or embeddings are generated yet. |
| `data-source` | Create or update the Search data source, pointing to the existing container and prefixed folder using Storage's resource ID. Search uses its managed identity to read the blobs. | A registered source for ingestion; does not upload files or start indexing. |
| `index` | Create or update the chunk index's fields, 1,536-dimensional vector field, vectorizer and semantic configuration. | The destination schema for searchable chunks; defining it does not populate or backfill documents. |
| `skillset` | Create or update the Text Split, embedding and Shaper skills, plus index projections. Configure 1,000-character chunks with 200-character overlap and preserve filename/source URL. | The enrichment recipe; saving it does not execute skills or generate embeddings. |
| `indexer` | Create or update the indexer that connects the data source, skillset and index. New creation starts indexing automatically; an existing idle indexer receives an explicit run request. | Search reads the files, splits text, generates embeddings and projects chunk documents. Embedding calls incur charges; completion is checked separately. |
| `monitor` | Poll the run requested by `indexer`, showing its status and processed/failed counts. Reject old successes and failed runs. | Confirms completion and prints warnings; does not request another run or perform a search. |
| `inspect` | Fetch up to 100 indexed chunks using a wildcard query, including parent IDs, chunk text and structured source metadata. | Shows the total chunk count and what was actually indexed; this is inspection, not a relevance-ranked search. |
| `verify-vectors` | Fetch up to three stored vectors and check that each has 1,536 dimensions. Print only chunk IDs and dimensions. | A small embedding-presence/shape check, not a whole-index or embedding-quality check; generates no new embeddings. |
| `keyword [question]` | Search chunk text using the question's words, without a vector query or semantic reranking. | Up to three BM25-ranked results; a baseline for comparing the other search modes. |
| `vector [question]` | Use the configured vectorizer to embed the question, then search stored chunk vectors for similar content. | Up to three similarity-ranked results, without keyword matching. Query-time embeddings incur charges; stored vectors are not regenerated. |
| `hybrid [question]` | Run keyword and vector searches together, merging their rankings with reciprocal rank fusion (RRF). | Up to three results combining exact-word and similarity signals; query-time embeddings incur charges. |
| `semantic [question]` | Run keyword search, then semantically rerank the candidates and request extractive captions. | Up to three semantically reranked results with captions; semantic ranking incurs charges, but no question embedding is requested. |
| `semantic-hybrid [question]` | Combine keyword and vector retrieval, then semantically rerank the merged candidates and request extractive captions. | Up to three results using all retrieval signals; query-time embeddings and semantic ranking incur charges. |
| `run [question]` | Execute upload, Search-object setup, indexing, monitoring, inspection, vector verification and all five search modes in the order below. | The complete lab; separate step commands are not prerequisites. Indexing and the applicable query operations incur charges. |
| `help` | Print the command list without loading settings or authenticating. | An offline reminder of the available steps and question arguments. |

In short: **upload files -> register the source -> define the index and skills -> start indexing -> wait -> inspect -> compare searches**.
Setup commands define the pipeline; `indexer` starts the work that generates stored document embeddings.
The five search commands retrieve evidence, not an LLM-generated answer, and can be run independently after indexing completes.

```powershell
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- help
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- upload
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- data-source
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- index
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- skillset
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- indexer
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- monitor
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- inspect
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- verify-vectors
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- keyword
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- vector
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- hybrid
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- semantic
dotnet run --project c-sharp-console-apps\04-blob-indexer-skills -- semantic-hybrid
```

Each command performs **only its named action**, plus necessary status reads.
`run` executes all steps in that order. Query commands and `run` accept an
optional public question, for example `-- hybrid "When are travel receipts due?"`.
`help` needs no configuration or authentication.

Upload overwrites only `onboarding-guide.txt` and `travel-policy.txt` from
`data\search`, under `<prefix>/blob/`; there is no Search push ingestion.
Search objects use `<prefix>-blob-chunks/source/skills/indexer`.
The source's `container.query` is a selection rule, not access isolation.
No deletion policy, schedule, reset or cleanup command is configured.
Adding vector fields does not backfill unchanged blobs: reupload samples and run
the indexer. Existing incompatible dimensions may require manual index recreation.

`indexer` checks existence, rejects an in-progress run before updating, and
explicitly requests a run only for an existing idle indexer (new creation runs
automatically). `monitor` requires the accepted request marker in ignored
`.workshop\lab04-<prefix>.json`, scoped to this Search endpoint/indexer. Atomic
writes store **only run metadata**, not credentials, questions or content.
Failure leaves the marker intact; successful monitoring marks it completed.
Queued previous successes are not accepted. Polling makes up to 90 status reads,
10 seconds apart, within a 15-minute deadline. HTTP errors are not retried;
unexpected statuses print bounded response diagnostics and stop.

Text Split uses `pages` (size-based chunks, **not PDF pages**), 1,000 characters
and 200 overlap; `sentences` is the other split mode. `unit` defaults to
`characters`; `azureOpenAITokens` is a separate length unit.
Text-based PDFs are supported; scanned PDFs need OCR, not provided here.
Shaper preserves filename and URL, not page numbers. See Python lab 05 for page
metadata. No `cognitiveServices` attachment, OCR or Vision skill is configured.

`verify-vectors` checks only three stored vectors' lengths, not every document or
embedding quality, and prints IDs/dimensions, never vectors. Vector queries embed
the question using Search's identity/vectorizer; they do not regenerate indexed
chunk vectors. Embeddings and semantic reranking incur usage/quota; Text Split
and Shaper are utility skills. Compare BM25, vector, RRF hybrid and semantic
reranking/captions; semantic ranking is not an LLM answer. Partial semantic
responses (HTTP 206 or a partial-response reason at HTTP 200) fail explicitly.
