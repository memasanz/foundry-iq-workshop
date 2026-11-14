# C# console apps

Explore the workshop APIs in ordinary .NET console projects, without Python or notebook tooling.
Currently, [lab 01 - Web IQ APIs](01-web-iq-apis) is available; C# versions of the other labs have not been added yet.

[Workshop home](../README.md) | [C# setup guide](../docs/participants/c-sharp.md) | [Lab guide](../docs/labs/README.md)

## Before you run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Open a PowerShell terminal in the **repository root**, not this folder or an individual app folder.
If your terminal is currently in `c-sharp-console-apps`, run `Set-Location ..` first.

Create the root settings file without overwriting an existing one:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
```

Edit `.env` and set `WEBIQ_API_KEY` to your Web IQ **profile-issued API key**.
Lab 01 needs no Azure sign-in or `.env.azure`; do not use a Foundry/Azure resource key.
Never put the key in source code or commit it.

Settings load from the current working directory: `.env`, then `.env.azure` if present.
File values override existing environment variables, and `.env.azure` wins for duplicate settings.

## Run lab 01

Start with help; this makes no service calls:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- --help
```

Choose **one API per run**:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- web
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- localized
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- news
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- images
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- videos
```

Append a quoted public query to override the example query:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- web 'OFAC guidance on potential sanctions name matches'
```

For Browse, copy a source URL from Web Search and pass it explicitly. You can also supply a question after the URL:

```powershell
dotnet run --project .\c-sharp-console-apps\01-web-iq-apis -- browse 'https://ofac.treasury.gov/faqs' 'How should a potential sanctions name match be reviewed?'
```

Browse makes no hidden search call. Pending crawls retry up to four times, with each wait capped at 120 seconds.
HTTP failures print the status and response body and return a nonzero exit code; check `$LASTEXITCODE` in PowerShell.

**Calls and retries incur Web IQ usage charges.** Use only public, non-sensitive queries.
See [costs and responsible use](../docs/reference/README.md#costs-and-responsible-use).

## Explore or check the code

[Program.cs](01-web-iq-apis/Program.cs) shows the six request payloads.
[Workshop.cs](01-web-iq-apis/Workshop.cs) contains the small settings, response-printing and retry-delay helpers.
No additional NuGet packages are required; `dotnet run` builds the project automatically.

From the repository root, run the offline checks without making service calls:

```powershell
dotnet run --project .\tests\c-sharp\WebIqLabTests.csproj
```
