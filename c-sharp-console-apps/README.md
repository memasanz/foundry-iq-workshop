# C# console apps

Explore the **Web & Foundry IQ Workshop** using ordinary .NET console projects, without Python or notebook tooling.
Each lab has its own project folder and README with configuration, run commands and code explanations.

[Workshop home](../README.md) | [Participant guide](../docs/participants/README.md) | [C# setup guide](../docs/participants/c-sharp.md)

## Getting started

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Open a PowerShell terminal in the **repository root**, not this directory or an individual project folder.
   If you are currently in `c-sharp-console-apps`, run `Set-Location ..` first.
3. Choose a lab below and follow its README for required settings, permissions and commands.
   Authentication and Azure prerequisites depend on the lab.

## Available labs

| Lab | What you will explore | Instructions |
|---|---|---|
| 01 - Web IQ APIs | Web Search, localized search, Browse, News, Images and Videos | [Set up and run lab 01](01-web-iq-apis/README.md) |
| 02 - Foundry agent + Web IQ | MCP discovery, authenticated project connection, prompt agent, Responses API and follow-up | [Set up and run lab 02](02-foundry-web-iq-agent/README.md) |
| 03 - Foundry agent + Web IQ toolbox | Reuse the Web IQ connection, publish a web/news toolbox, authenticate through Entra and verify grounded answers | [Set up and run lab 03](03-foundry-web-iq-toolbox/README.md) |
| 04 - Blob indexer and skills | Upload source files, configure chunking/embeddings, and compare keyword, vector, hybrid and semantic-ranked search | [Set up and run lab 04](04-blob-indexer-skills/README.md) |
| 05 - Blob + Content Understanding | Extract a PDF as Markdown, chunk and embed it, then retrieve evidence with page ranges | [Set up and run lab 05](05-blob-content-understanding/README.md) |
| 06 - Chunk, embed and push | Chunk local TXT files, generate document embeddings in the app, push to Search and compare five search modes | [Set up and run lab 06](06-push-chunks-vectors/README.md) |
| 08 - SharePoint remote | Retrieve SharePoint passages as the signed-in user without an indexer or content copy | [Set up and run lab 08](08-sharepoint-remote/README.md) |
| 09 - Foundry IQ with Web IQ only | Create a Web IQ-only knowledge base and compare synthesized answers with extractive web evidence | [Set up and run lab 09](09-foundry-iq-webiq-only/README.md) |
| 10 - Foundry IQ with Web IQ + Blob | Combine Web IQ with the existing Content Understanding Blob index; compare document, web and mixed evidence | [Set up and run lab 10](10-foundry-iq-webiq-blob/README.md) |

Labs 01-06 and 08-10 currently have C# console apps. The [full lab guide](../docs/labs/README.md) lists the Python labs as well.

## How the projects run

Use `dotnet run --project` to select a lab's project folder from the repository root.
Arguments after `--` are passed to the application. Each lab README provides complete commands you can copy.
`dotnet run` builds the project automatically.

Labs 02-06 and 08-10 also provide **separate commands for each lab step**.
Use `--help` to see their command lists, then follow the selected lab README in order.
Each step runs separately so you can inspect discovery, connections, toolbox or agent creation, ingestion and queries.
The `run` command still performs the complete workflow; you do not have to execute the separate steps first.
Labs 02 and 03 save version and conversation references in ignored `.workshop\lab02-<prefix>.json`
or `.workshop\lab03-<prefix>.json`, without credentials, prompts or answers.
Follow the lab README when restarting the sequence; stale references stop a question before a model call.
The lab 04 and 05 READMEs explain indexer-run monitoring and any local run references.
Lab 06's README explains its local chunk/vector artifacts so embedding and pushing can run as separate commands.
These ignored artifacts contain synthetic sample text and embeddings, not just run metadata; do not share them.

Keep local settings in the repository-root `.env` and, when required, `.env.azure`.
Follow the selected lab's instructions for which values to set; never put credentials in source code or commit them.
Build outputs in `bin` and `obj` are ignored by Git.

**Live runs can incur charges and create or change resources.**
Read the lab's prerequisites and [costs and responsible use](../docs/reference/README.md#costs-and-responsible-use) before running it.
