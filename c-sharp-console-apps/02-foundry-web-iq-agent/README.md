# Lab 02 - A Foundry agent grounded with Web IQ (C#)

[C# apps](../README.md) | [C# notebook](../../c-sharp-notebooks/02-foundry-web-iq-agent.ipynb) | [Python reference](../../python-notebooks/02-foundry-web-iq-agent.ipynb)

This .NET 10 console app follows Python notebook 02: discover Web IQ MCP tools, configure a Foundry connection, create or update a prompt agent, ask through the **Responses API**, verify actual MCP retrieval, and ask a follow-up in the same conversation.

## Prerequisites

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). There are **no extra NuGet packages** and no Python/notebook requirements. The app uses `HttpClient`, JSON-RPC for MCP discovery, and Azure CLI tokens instead of the Python SDKs.

**Discovery only:** set `WEBIQ_API_KEY` in the repository-root `.env` to a Web IQ **profile-issued key**. No Azure sign-in, model deployment or Foundry permissions are needed for discovery.

**Full lab:** install [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), have a deployed Foundry project/chat model, and sign in to the workshop tenant. Obtain `.env.azure` from your facilitator; it must define:

```text
AZURE_AI_PROJECT_ENDPOINT
AZURE_AI_PROJECT_RESOURCE_ID
AZURE_AI_MODEL_DEPLOYMENT_NAME
AZURE_TENANT_ID
```

Also set `WORKSHOP_PREFIX` in `.env`: 3-20 lowercase letters/digits, starting with a letter, such as `alice01`. Keep this prefix for lab 03.

Required permissions: **Foundry Project Manager on the project** to manage the MCP connection and build/run the agent. The workshop's **Foundry Owner on the parent account** also covers this; Foundry User alone lacks connection-write permission. No subscription Owner or tenant administrator role is required.

Settings load from `.env`, then `.env.azure`, overriding existing process settings. The shared lab 01 loader supports quoted `KEY=VALUE` lines and comments, but not dotenv interpolation or multiline values. Keys are never printed.

## Run from the repository root

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- --help
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- discover
```

Discovery sends MCP `initialize`, `notifications/initialized` and paginated `tools/list` requests to `https://api.microsoft.ai/v3/mcp`, using `x-apikey`. It supports JSON and Server-Sent Events responses, forwards session/protocol headers, and closes the MCP session when present. It lists schemas/tools without executing a search or invoking an agent.

For the full lab:

```powershell
$settings = Get-Content .\.env.azure -Raw | ConvertFrom-StringData
az login --tenant $settings.AZURE_TENANT_ID
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- run
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- run 'Find recent Microsoft AI agent announcements with primary-source URLs and one uncertainty.'
```

**A full run changes Azure resources and incurs model-token and Web IQ usage charges.** Only use public, non-sensitive questions. A completed initial response always triggers another billable follow-up turn, even if retrieval verification warns.

## Run one step at a time

Like lab 01's separate API commands, these commands run only the named step and necessary metadata reads:

| Command | What happens | Result |
|---|---|---|
| `discover` | Connect directly to Web IQ with your profile key and list its MCP tools, descriptions and input schemas. No search or agent call runs. | Shows the available tools; no Azure resources are created. |
| `connection` | Create or update `<prefix>-webiq-mcp` in Foundry, storing the profile key so Foundry can authenticate future Web IQ calls. | An authenticated Web IQ project connection, also reusable by lab 03. |
| `agent` | Check the existing connection and prepare a prompt-agent version configured to use Web IQ's `web` and `browse` tools without approval prompts. | Saves the exact agent name/version for later question commands; no question runs yet. |
| `ask [public question]` | Create a conversation and send one question to the saved agent version through the Responses API. Print its answer and check for successful MCP retrieval. | An answer plus saved conversation references. Model/tool calls incur charges; there is no automatic follow-up. |
| `follow-up [public question]` | Send another question using the same conversation and pinned agent version, preserving the earlier conversation context. Requires a completed previous response. | Another answer and retrieval check; model/tool calls incur charges. |
| `run [public question]` | Execute the full discovery, connection, agent, initial-question and follow-up workflow in one command. | The complete lab, including the automatic billable follow-up. Separate step commands are not prerequisites. |

In short: **discover what Web IQ offers -> give Foundry access -> configure an agent -> ask -> follow up**.
Creating the connection or agent does not itself run a Web IQ search.

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- discover
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- connection
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- agent
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- ask
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- follow-up
```

`agent` does not modify the connection or rediscover tools. Before asking, `ask` and `follow-up` validate the existing connection and exact pinned agent policy/model. Default questions match the full run. Retrieval checks show that tools returned evidence, not that every claim is correct; review the cited sources yourself.

Only `discover`, `connection` and `run` read/require `WEBIQ_API_KEY`. The other steps need the Azure settings and prefix but use the already provisioned connection. Missing/stale prerequisites stop before any billable response; commands never replay earlier steps or silently repair resources. `run` retains its original automatic follow-up and does not read or populate the step cache.

### Local references

Step commands save atomic, prefix/lab/project-scoped references in ignored `.workshop\lab02-<prefix>.json`. This contains only project identifiers, agent name/version, conversation ID and completion state—**no API keys, access tokens, prompts, answers or tool outputs**. Agent/connection changes clear the old conversation. Before a new question or follow-up, completion is invalidated so errors/interruption cannot leave a stale successful turn eligible for follow-up. Successful completed questions restore it.

Keep running from the repository root. A kernel/editor restart does not affect this console cache. To start over locally:

```powershell
Remove-Item .\.workshop\lab02-alice01.json # Replace alice01 with WORKSHOP_PREFIX.
```

Then rerun `agent`, `ask` (and `connection` if needed). Removing the cache deletes **no Azure resources**. A different project under the same prefix requires clearing this file; a different prefix has a separate file. Do not manually edit versions or store credentials in it. Help reads neither configuration nor cache.

## What the app does

1. Discovers tools; a failed discovery stops the run before any Azure writes.
2. PUTs `{prefix}-webiq-mcp` through ARM (`2025-04-01-preview`) as a shared `RemoteTool`/`CustomKeys` connection. The key is stored under `credentials.keys.x-apikey`; connection responses are not displayed.
3. Lists all agent pages, preferring a matching `{prefix}-webiq-agent`, otherwise an older matching `{prefix}-webiq-agent-*`, otherwise the stable new name. A same-name non-Web-IQ agent is never overwritten. Creates a new version through the Foundry project API (`api-version=v1`).
4. Creates a conversation and POSTs to `{projectEndpoint}/openai/v1/responses`. `input`, `conversation` and the top-level `agent_reference` select the question, conversation and pinned agent version. See [Generate responses](https://learn.microsoft.com/azure/foundry/agents/concepts/runtime-components#generate-responses).
5. Prints actual MCP calls and checks for non-empty, error-free `webiq` retrieval plus a final answer. This is not factual validation: open and review the cited URLs yourself.
6. Asks the follow-up in the same conversation after a completed first response.

The agent allows **`web` and `browse`**, with `require_approval="never"`. `news`, `images` and `videos` are optional tools on the same endpoint, not enabled here. Change `CreateDefinition` and the instructions together if extending the exercise.

`FoundryWorkshop.cs` contains the console app's discovery, connection, agent and response logic; `Program.cs` shows the console sequence. The C# notebook defines its own helpers inline and does not import console-app source files. Unlike Python's SDK, this HTTP implementation has **no automatic retries**. HTTP errors, including 401/429, print bounded diagnostics and retry headers; transport/timeouts and failed/incomplete responses return no answer. Unexpected approvals and agent-name conflicts remain configuration errors. Failures return a nonzero exit code.

## Inspector and retained resources

For browser-based MCP exploration, run `npx @modelcontextprotocol/inspector` in a separate terminal with a supported Node.js version. The [C# notebook](../../c-sharp-notebooks/02-foundry-web-iq-agent.ipynb) includes both screenshots and the Streamable HTTP/`x-apikey` walkthrough. Never paste keys into commands, screenshots, exported configurations or Git.

The connection, agent versions and conversation are retained. Reuse the same prefix in Python lab 03; the C# and Python tracks share these Azure resources. Prefixes prevent naming collisions, **not** access isolation. Authorized project members may access the shared credential; use a workshop key and rotate/revoke it afterward.

## Offline validation

```powershell
dotnet build .\c-sharp-console-apps\02-foundry-web-iq-agent\FoundryWebIq.csproj
dotnet run --project .\tests\c-sharp-foundry\FoundryLabTests.csproj
```

The harness uses synthetic settings, tokens and HTTP responses; no Azure or paid API calls. See [participant setup](../../docs/participants/c-sharp.md) for optional notebook execution checks.
