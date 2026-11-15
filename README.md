# Web & Foundry IQ Workshop

## Overview

Build agents that search the web and answer questions over your own documents. These hands-on labs start with direct API calls, then introduce agents, search indexes, SharePoint permissions and Foundry IQ.

You can take one track or work through the whole workshop. The notebooks show the requests and responses directly, so you can see how the pieces fit together.

![Workshop architecture showing attendees, Foundry, Web IQ, Search, Blob Storage, SharePoint and APIM](docs/workshop-architecture.png)

*Illustrative diagram from an earlier eight-lab layout. Use the [lab guide](docs/labs/README.md) for current 01-14 numbering and the [deployment guide](infra/README.md) for the current region and existing-APIM setup.*

## Let's get started

| Your role | Where to begin |
|---|---|
| **[Participant](docs/participants/README.md)** | Ask your facilitator for the workshop configuration and access, then [open the notebooks](docs/participants/README.md). You do not need to deploy your own Azure resources. |
| **[Facilitator](docs/facilitators/README.md)** | Review [Prepare for the workshop](PREPARE-FOR-WORKSHOP.md), then follow the [beginner deployment guide](infra/README.md). Arrange approvals and licenses before class. |
| **[Learning on your own](docs/self-guided/README.md)** | Follow the facilitator setup once, then use the participant instructions. You need your own eligible Azure subscription and access to the services you choose. |
| **[Tenant / SharePoint administrator](docs/facilitators/sharepoint.md#administrator-handoff-approve-permissions-and-site-read-once)** | Review the SharePoint permissions and one-time approval handoff for lab 07; attendees do not need tenant administrator roles. |

**New to Azure?** The [infrastructure guide](infra/README.md) explains the tools, subscription, resource group, deployment command and costs from the beginning.

## Choose your code

**Choose Python or C#:** Python developers can use the [Jupyter notebooks](python-notebooks) for all labs. C# developers can explore labs 01-06 and 08 in optional [Polyglot Notebooks](c-sharp-notebooks) or [.NET 10 console apps](c-sharp-console-apps). Polyglot Notebooks uses deprecated tooling; the console apps are the supported C# option. See the [language setup guide](docs/participants/README.md#choose-your-language).

## Prerequisites

| Code option | What to install | Setup instructions |
|---|---|---|
| **Python notebooks** | Python 3.11+, VS Code with its Jupyter extension or JupyterLab, and the packages in `requirements.txt`. | [Python setup](docs/participants/python.md) |
| **C# console apps (recommended)** | The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and a terminal. No Python or notebook extensions are needed. | [C# console setup](c-sharp-console-apps/README.md) |
| **C# notebooks (legacy)** | VS Code, the deprecated Polyglot Notebooks extension, and a .NET runtime compatible with its installed kernel. Python is not required for this VS Code path. | [C# notebook setup and compatibility warnings](c-sharp-notebooks/README.md) |

For Azure-backed labs, also install [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and obtain the workshop configuration and permissions from your facilitator. Lab 01's direct Web IQ calls require a **profile-issued API key**, not Azure sign-in. Follow the [shared settings guide](docs/participants/README.md#workshop-settings-and-resource-names); keep credentials out of source code and notebook outputs.

## MCP Inspector (optional)

[MCP Inspector](https://github.com/modelcontextprotocol/inspector) provides a browser interface for discovering MCP tools, inspecting their schemas and testing calls. Install a supported [Node.js](https://nodejs.org/en/download) version first; see the Inspector's current [requirements](https://github.com/modelcontextprotocol/inspector#quick-start-development).

```powershell
npx @modelcontextprotocol/inspector
```

`npx` downloads and runs the npm package; a global installation is not required.

For Web IQ, use **Streamable HTTP**, enter `https://api.microsoft.ai/v3/mcp`, and configure the `x-apikey` request header with the `WEBIQ_API_KEY` value from your `.env` file locally. See [notebook 02](python-notebooks/02-foundry-web-iq-agent.ipynb) for an illustrated walkthrough. Keep credentials out of commands, exported configurations and Git. Tool calls incur usage charges; use public, non-sensitive queries.

## Workshop guides

Jump to: [Lab guide](docs/labs/README.md#choose-your-labs) | [Deployment](docs/facilitators/README.md#1-deploy) | [Local setup](docs/participants/README.md) | [Troubleshooting](docs/reference/README.md#troubleshooting) | [Cleanup](docs/reference/README.md#cleanup)

The detailed setup, permissions, commands, costs and validation notes are in the linked guides.
