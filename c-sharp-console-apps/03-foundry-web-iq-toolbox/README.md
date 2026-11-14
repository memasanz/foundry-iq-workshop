# Lab 03 — Web IQ through a Foundry toolbox

Supported path: .NET 10 console app, `HttpClient` + `JsonNode`, no NuGet dependencies. Azure CLI supplies tokens; `--resource https://ai.azure.com/` corresponds to the SDK's `https://ai.azure.com/.default` scope. The self-contained [optional C# notebook](../../c-sharp-notebooks/03-foundry-web-iq-toolbox.ipynb) uses deprecated Polyglot/.NET Interactive tooling.

From the repository root:

```powershell
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- --help
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- run
# Optionally append a quoted public research question after run.
```

Complete lab 02's connection setup first. Load `.env`, then `.env.azure` (file values override environment values; `.env.azure` wins). Required: `AZURE_AI_PROJECT_ENDPOINT`, `AZURE_AI_PROJECT_RESOURCE_ID`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`, `AZURE_TENANT_ID`, `WORKSHOP_PREFIX`. **No Web IQ API key is read or required.** Sign in with `az login --tenant <AZURE_TENANT_ID>`. You need **Foundry Owner on the parent Foundry resource**, access to lab 02's connection, an existing chat model and toolbox preview availability; no new API key or tenant administrator role is needed. Prefixes avoid collisions, not enforce isolation.

## Run one step at a time

```powershell
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- webiq-connection
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- toolbox
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- discover
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- toolbox-connection
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- agent
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- ask
```

Each command performs only its named action plus necessary metadata reads. `webiq-connection` checks the original connection without writing it. `toolbox` publishes/promotes a new version. `discover` reads the **existing default**, validates its source/policy, lists MCP tools and selects that version without publishing. To reuse an existing toolbox, start with `discover`.

`toolbox-connection` writes only the new Entra connection for the selected version. `agent` requires that existing connection's target, audience and metadata to match the selected immutable toolbox version; it does not silently retarget it. `ask [public question]` validates the toolbox, Entra connection and pinned agent version/model/policy, creates a fresh conversation, asks once and verifies retrieval. No step runs an implicit search/question, interactive approval or follow-up. `run` remains the independent original single-question full flow and does not read/populate the step cache.

Step references persist atomically in ignored `.workshop\lab03-<prefix>.json`: project identifiers, selected toolbox version, agent name/version, conversation ID and completion state only. URLs/names are derived from the validated project/prefix and immutable version. **No credentials, tokens, prompts, answers or tool outputs are stored.** Selecting a different toolbox or updating its connection clears the pinned agent and conversation; run `agent` again. Failed new questions invalidate prior completion. Failed setup steps do not replace successfully selected references.

Keep running from the repository root. Missing references give the prerequisite command; mismatched remote targets/policies stop before any billable call. To clear local references (not Azure resources), run `Remove-Item .\.workshop\lab03-alice01.json`, replacing `alice01` with your prefix, then `discover`, `toolbox-connection`, `agent`, `ask` as needed. Different prefixes use different files; changing projects under the same prefix requires clearing that file. Console references survive editor/kernel restarts. Help reads no configuration or cache.

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

Discovery accepts the negotiated versions supported by the installed Python MCP SDK: `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`. It forwards the **server-selected** version on subsequent requests; unknown versions fail with the actual version in the message. The unchanged lab 02 initialization offer is `2025-06-18`. Compatibility is for successful single-endpoint Streamable HTTP POST initialization (JSON or SSE, including empty SSE priming events), **not** the legacy separate GET/SSE transport or reconnect/resume features. See the official [version negotiation](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle#version-negotiation) and [transport backwards compatibility](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports#backwards-compatibility) rules.
