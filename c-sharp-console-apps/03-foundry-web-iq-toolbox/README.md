# Lab 03 — Web IQ through a Foundry toolbox

Supported path: .NET 10 console app, `HttpClient` + `JsonNode`, no NuGet dependencies. Azure CLI supplies tokens; `--resource https://ai.azure.com/` corresponds to the SDK's `https://ai.azure.com/.default` scope. The self-contained [optional C# notebook](../../c-sharp-notebooks/03-foundry-web-iq-toolbox.ipynb) uses deprecated Polyglot/.NET Interactive tooling.

From the repository root:

```powershell
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- --help
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- run
# Optionally append a quoted public research question after run.
```

Complete lab 02's connection setup first. Load `.env`, then `.env.azure` (file values override environment values; `.env.azure` wins). Required: `AZURE_AI_PROJECT_ENDPOINT`, `AZURE_AI_PROJECT_RESOURCE_ID`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`, `AZURE_TENANT_ID`, `WORKSHOP_PREFIX`. **No Web IQ API key is read or required.** Sign in with `az login --tenant <AZURE_TENANT_ID>`. You need **Foundry Owner on the parent Foundry resource**, access to lab 02's connection, an existing chat model and toolbox preview availability; no new API key or tenant administrator role is needed. Prefixes avoid collisions, not enforce isolation.

## Flow and authentication

1. Metadata-only GET of `<prefix>-webiq-mcp`: validate its exact Web IQ target; never fetch credentials or update this connection.
2. Publish `<prefix>-webiq-toolbox` with MCP source `webiq`, the original full connection ID, `web`/`news` allowlist and no approvals; promote the new default.
3. Discover both `webiq___web` and `webiq___news` at the **exact versioned** Foundry MCP URL with an Entra bearer token, not `x-apikey`.
4. Create/update `<prefix>-webiq-toolbox-connection` with `UserEntraToken`, audience `https://ai.azure.com`, and that version's URL (ARM API `2025-10-01-preview`, only 200/201 accepted).
5. Publish `<prefix>-webiq-toolbox-agent`, referencing the **new connection name**, the versioned toolbox URL, namespaced allowlist and no approvals. Create a conversation and pin the returned agent name/version.
6. Ask once, require completion, reject unexpected approvals, verify a successful non-empty web **or** news call from `webiq_toolbox` and a final answer containing a source URL. Manually review claims/citations and inspect calls to confirm **both** capabilities were used.

Two hops: **prompt agent → Entra connection → Foundry toolbox → existing API-key connection → Web IQ**. No key/token is copied into the new definitions. A hosted agent using `FoundryToolbox(credential)` authenticates directly through its SDK, so does not need the extra agent-to-toolbox connection; it still needs the downstream Web IQ connection.

Reruns publish versions under stable names and retarget the same Entra connection. Do **not** reuse an older pinned agent whose MCP URL differs from the updated connection target. After changing tools/approval policy, rerun sections 2–5 (or the console run) to publish/discover/connect a matching version and start a fresh conversation.

Model and Web IQ calls incur charges: use only public, non-sensitive questions. Passage content avoids full-document formats requiring separate access (downstream 403 can surface as Foundry 400). Errors stop the flow; there are no hidden retries, interactive approvals, automatic follow-up questions or resource deletions. Resources are retained; clear notebook outputs before sharing.

Wire shapes were checked against the installed `azure-ai-projects` serializer (`v1` toolbox POST/GET/PATCH, `default_version`, MCP `type=mcp`).
