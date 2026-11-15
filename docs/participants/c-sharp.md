# C# setup and code options

[Participant guide](README.md) | [Choose your labs](../labs/README.md)

C# equivalents currently cover labs 01-04. Choose the supported console apps or optional legacy Polyglot Notebooks.
Both use the root settings files and the same public research examples.

### C# console app (lab 01)

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then set the profile-issued `WEBIQ_API_KEY` in the root `.env`. Lab 01 needs only this key: no Azure sign-in or `.env.azure` is required for its direct Web IQ calls.

Run from the repository root:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- --help
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- web
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- images
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- browse 'https://ofac.treasury.gov/faqs'
```

Other commands: `localized`, `news`, `videos`. Append a quoted public query to any search command; for Browse, append a question after the URL. Browse reads the supplied URL without a hidden search request. HTTP errors print status/body and exit nonzero, without a traceback. Pending crawls retry at most four times with waits capped at 120 seconds. Calls and retries incur charges.

`Program.cs` shows the six request payloads. The small `Workshop.cs` helper loads simple `KEY=VALUE` settings and handles response printing/retry delays. The C# notebook defines its own helpers inline and needs no console-app files. It supports surrounding quotes and comments, but not dotenv interpolation or multiline values. `.env.azure` overrides `.env`, and both override existing environment variables. No additional NuGet packages are needed.

### Optional legacy C# notebook (lab 01)

**Deprecated tooling warning:** Polyglot Notebooks was deprecated March 27, 2026, and .NET Interactive April 24, 2026. They receive no new features, bug fixes or security fixes; future VS Code or SDK versions can break them. See [dotnet/interactive#4163](https://github.com/dotnet/interactive/issues/4163). The console app above is the recommended supported path.

For a walkthrough of the VS Code extension and notebook interface, see Microsoft Learn's [Polyglot Notebooks: setting up your environment](https://learn.microsoft.com/en-us/training/modules/polyglot-notebooks/4-setting-up). This is a legacy guide and does not supersede the deprecation warning above.

The [C# notebooks README](../../c-sharp-notebooks/README.md) walks through installation, an offline C# check, workshop settings and opening lab 01.

If your organization allows the legacy tooling and you have the **Polyglot Notebooks** VS Code extension, open [01-web-iq-apis.ipynb](../../c-sharp-notebooks/01-web-iq-apis.ipynb), select the **.NET Interactive C#** kernel, and run the cells top to bottom. Use `c-sharp-notebooks` as the kernel's working directory so parent configuration paths resolve. The notebook is self-contained; run its inline helper cells before Configuration. Use a C# kernel, not a Python kernel; Python is not required for this Polyglot VS Code path. Keep public queries only and inspect source attribution; image previews can contact external hosts.

The notebook follows Python lab 01's public research examples, request descriptions, response explanations, clean HTTP errors and bounded Browse retries. It is included for exploration, not as a maintained tooling recommendation.

Review [costs and responsible use](../reference/README.md#costs-and-responsible-use)
before live requests, or use the [offline lab 01 checks](../reference/README.md#offline-checks-for-lab-01).

### C# console app (lab 02)

Lab 02 follows the Python notebook's MCP discovery, project connection, prompt agent, **Responses API**,
retrieval verification and follow-up. It uses the same `.NET 10` SDK and simple settings loader as lab 01,
with `HttpClient` and Azure CLI tokens rather than SDK packages. No additional NuGet packages are needed.

For discovery only, your profile-issued `WEBIQ_API_KEY` is sufficient:

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- discover
```

For the full lab, install Azure CLI, sign in to `AZURE_TENANT_ID`, obtain the shared `.env.azure`,
and set `WORKSHOP_PREFIX` in `.env`. You need a deployed Foundry project/model and
**Foundry Project Manager on the project** or the workshop's inherited **Foundry Owner** access.

```powershell
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- --help
dotnet run --project .\c-sharp-console-apps\02-foundry-web-iq-agent -- run
```

The full run creates/updates the connection, creates an agent version and conversation, and incurs
model and Web IQ charges. A completed initial response always triggers another billable follow-up.
Resources are retained and shared across the language tracks when you reuse the same prefix.
See [lab 02 configuration, permissions and behavior](../../c-sharp-console-apps/02-foundry-web-iq-agent/README.md).

To explore one step at a time, use the separate commands listed by `--help` and follow the lab README's sequence.
Connection setup, agent creation, the initial question and the follow-up can run separately.
The full `run` command retains its automatic follow-up; a separate initial-question command does not hide another model call.

### Optional legacy C# notebook (lab 02)

The same deprecation and kernel-compatibility warnings above apply. Open
[02-foundry-web-iq-agent.ipynb](../../c-sharp-notebooks/02-foundry-web-iq-agent.ipynb) with the C# kernel,
using `c-sharp-notebooks` as the working directory. It preserves the Python notebook's diagrams,
MCP Inspector screenshots, tool descriptions, allowlist, clean HTTP errors and guarded follow-up.
Complete the full lab 02 prerequisites above before starting.

Offline checks use synthetic settings/tokens and mock HTTP responses:

```powershell
dotnet run --project .\tests\c-sharp-foundry\FoundryLabTests.csproj
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_foundry_notebook.py
```

The second check requires the optional legacy `.NET Interactive` tool plus the repository's Python test
environment; Python is a test-runner dependency here, not a requirement to use the C# notebook in VS Code.

### C# console app (lab 03)

Lab 03 follows the Python toolbox notebook, using .NET 10, `HttpClient` and Azure CLI tokens.
Complete lab 02's connection setup in either language with the same `WORKSHOP_PREFIX`.
You need **Foundry Owner on the parent Foundry resource**, the shared Azure settings and Azure CLI sign-in.
This lab reads the existing Web IQ connection's metadata, not its API key.

```powershell
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- --help
dotnet run --project .\c-sharp-console-apps\03-foundry-web-iq-toolbox -- run
```

The full run publishes a toolbox exposing `web` and `news`, discovers it using Entra authentication,
creates or updates a separate agent-to-toolbox connection, and creates a prompt-agent version and conversation.
Both tool layers use `require_approval='never'`; the question runs without an approval prompt or automatic follow-up.
The toolbox, connection and agent names stay stable across runs. Resources are retained and calls incur charges.
See [lab 03 configuration and authentication](../../c-sharp-console-apps/03-foundry-web-iq-toolbox/README.md).

Use `--help` and the lab README to run individual steps instead:
inspect the existing Web IQ connection, publish the toolbox, discover tools, configure its connection,
create the agent, and ask a question. The discovery command reads an existing toolbox rather than publishing a new version.
The `run` command remains the complete workflow.

### Optional legacy C# notebook (lab 03)

The same deprecation and kernel-compatibility warnings above apply. Open
[03-foundry-web-iq-toolbox.ipynb](../../c-sharp-notebooks/03-foundry-web-iq-toolbox.ipynb) with the C# kernel,
using `c-sharp-notebooks` as the working directory. Its commented helpers are inline; no console-app imports are needed.
Run the numbered sections in order to explore the two authentication hops:
agent to toolbox uses Entra tokens; toolbox to Web IQ uses lab 02's stored profile-key connection.
Rerun agent creation after updating the toolbox connection so the agent and connection target the same toolbox version.

### C# console app (lab 04)

Lab 04 uses .NET 10 and visible REST requests to upload Blob documents, configure a Search indexer and
chunking/embedding skills, then compare keyword, vector, hybrid and semantic-ranked search.
It does not require completing labs 01-03 or providing a Web IQ API key.
Configure the existing Search, Storage and embedding resources, set `WORKSHOP_PREFIX`, and sign in with Azure CLI.

Your identity needs **Search Service Contributor**, **Search Index Data Contributor** and
**Storage Blob Data Contributor** on the relevant resources.
Search's managed identity needs **Storage Blob Data Reader** and embedding inference access on the Foundry resource.
Enable semantic ranker before using the semantic commands. See [lab 04 prerequisites and step overview](../../c-sharp-console-apps/04-blob-indexer-skills/README.md).

```powershell
dotnet run --project .\c-sharp-console-apps\04-blob-indexer-skills -- --help
dotnet run --project .\c-sharp-console-apps\04-blob-indexer-skills -- run
```

Use the separate commands listed by `--help` to explore one step or search mode at a time.
Text-based PDFs can be indexed alongside TXT files; scanned PDFs need OCR, which this lab does not configure.
Embedding and semantic-ranking calls can incur charges. Resources are retained; use synthetic, non-sensitive data.

### Optional legacy C# notebook (lab 04)

The same deprecation and kernel-compatibility warnings above apply. Open
[04-blob-indexer-skills.ipynb](../../c-sharp-notebooks/04-blob-indexer-skills.ipynb) with the C# kernel,
using `c-sharp-notebooks` as its working directory so parent settings and sample-data paths resolve.
Run the inline helpers and numbered sections in order; no console-app imports are needed.
The notebook includes the index screenshots, Text Split/PDF explanations, stored-vector sample check,
query-time question embeddings, and explicit REST status/error reporting from the Python reference.

Offline checks use synthetic HTTP responses, credentials and sample files:

```powershell
dotnet run --project .\tests\c-sharp-search
.\.venv\Scripts\python.exe -m unittest discover -s tests -p test_csharp_search_notebook.py -v
```

The notebook check additionally needs the existing Python test environment and optional legacy
`.NET Interactive` tool. Neither check makes live Azure calls.
