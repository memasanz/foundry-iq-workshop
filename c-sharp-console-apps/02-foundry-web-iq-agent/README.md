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
