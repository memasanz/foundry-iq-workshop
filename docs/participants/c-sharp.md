# C# setup and code options

[Participant guide](README.md) | [Choose your labs](../labs/README.md)

C# equivalents currently cover lab 01. Choose the supported console app or the optional legacy Polyglot Notebook.
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

`Program.cs` shows the six request payloads. The small `Workshop.cs` helper, also used by the C# notebook, loads simple `KEY=VALUE` settings and handles response printing/retry delays. It supports surrounding quotes and comments, but not dotenv interpolation or multiline values. `.env.azure` overrides `.env`, and both override existing environment variables. No additional NuGet packages are needed.

### Optional legacy C# notebook (lab 01)

**Deprecated tooling warning:** Polyglot Notebooks was deprecated March 27, 2026, and .NET Interactive April 24, 2026. They receive no new features, bug fixes or security fixes; future VS Code or SDK versions can break them. See [dotnet/interactive#4163](https://github.com/dotnet/interactive/issues/4163). The console app above is the recommended supported path.

For a walkthrough of the VS Code extension and notebook interface, see Microsoft Learn's [Polyglot Notebooks: setting up your environment](https://learn.microsoft.com/en-us/training/modules/polyglot-notebooks/4-setting-up). This is a legacy guide and does not supersede the deprecation warning above.

If your organization allows the legacy tooling and you already have the **Polyglot Notebooks** VS Code extension, open [01-web-iq-apis.dib](../../c-sharp-notebooks/01-web-iq-apis.dib), select C#, and run the cells top to bottom. Use `c-sharp-notebooks` as the kernel's working directory so parent configuration paths and the shared-helper import resolve. The ordinary Python/Jupyter extension alone does not run `.dib` files. Keep public queries only and inspect source attribution; image previews can contact external hosts.

The notebook follows Python lab 01's public research examples, request descriptions, response explanations, clean HTTP errors and bounded Browse retries. It is included for exploration, not as a maintained tooling recommendation.

Review [costs and responsible use](../reference/README.md#costs-and-responsible-use)
before live requests, or use the [offline lab 01 checks](../reference/README.md#offline-checks-for-lab-01).
