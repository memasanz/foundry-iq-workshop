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

## Separate commands

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
