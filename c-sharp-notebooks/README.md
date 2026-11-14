# C# Polyglot Notebooks

Run C# code in notebook cells alongside the workshop's explanations and API response descriptions.
Currently, [lab 01 - Web IQ APIs](01-web-iq-apis.dib) is available.

[Workshop home](../README.md) | [Participant guide](../docs/participants/README.md) | [Supported C# console apps](../c-sharp-console-apps/README.md)

**Legacy tooling warning:** Polyglot Notebooks was deprecated March 27, 2026, and .NET Interactive April 24, 2026.
They no longer receive bug or security fixes, and future VS Code/.NET updates can break them.
Continue only if your organization permits this tooling; otherwise use the console apps.
See [dotnet/interactive#4163](https://github.com/dotnet/interactive/issues/4163).

## 1. Install the notebook tools

Follow Microsoft Learn's [Exercise - Setting up your environment](https://learn.microsoft.com/en-us/training/modules/polyglot-notebooks/4-setting-up).
It describes three requirements: VS Code, a .NET runtime and the Polyglot Notebooks extension.

1. Install [Visual Studio Code](https://code.visualstudio.com/).
2. Check your installed .NET runtimes:

   ```powershell
   dotnet --list-runtimes
   ```

   Use a runtime compatible with the installed legacy .NET Interactive kernel.
   **The Learn guide's runtime link points to .NET 8, which is now out of support.**
   Do not treat that link as a current version recommendation or install unsupported runtimes without your organization's approval.
   The console apps target .NET 10; having that SDK does not guarantee compatibility with a legacy notebook kernel.
3. In VS Code, open **Extensions** (`Ctrl+Shift+X`), search for **Polyglot Notebooks**, and install Microsoft's extension:
   [`ms-dotnettools.dotnet-interactive-vscode`](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.dotnet-interactive-vscode).
   Reload VS Code if prompted. The ordinary Jupyter extension alone does not run these `.dib` files.

## 2. Check that C# cells run

Use the small, offline check from the Learn guide before making Web IQ calls:

1. Open the Command Palette (`Ctrl+Shift+P`), search for **Polyglot**, and select **Polyglot Notebook: Create new blank notebook**.
2. Select **`.ipynb`**, then **C#**, as in the Learn exercise. The workshop's existing notebook uses `.dib`; you do not need to convert it.
3. Allow the first-time **Installing .NET Interactive** step to finish if it appears.
4. Enter this code and select the cell's **Run** button:

   ```csharp
   Console.WriteLine("Hello from Polyglot");
   ```

You should see `Hello from Polyglot` below the cell. This check needs no API key and makes no Web IQ requests.
If startup reports a missing runtime, review the required framework/version before installing anything.
If organizational policy or compatibility blocks the legacy kernel, use the supported console app instead.

## 3. Configure the workshop

Open the repository folder in VS Code. In a PowerShell terminal at the **repository root**, create `.env` without overwriting an existing file:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
```

Edit that root `.env` and set `WEBIQ_API_KEY` to your Web IQ **profile-issued API key**.
Do not put it in a notebook cell or output. Lab 01 needs no Azure sign-in or `.env.azure`.
If `.env.azure` exists, the notebook loads it after `.env`, so its duplicate settings win.

## 4. Open and run lab 01

Open [01-web-iq-apis.dib](01-web-iq-apis.dib) in VS Code and use its **C#** kernel.
Run the kernel from the `c-sharp-notebooks` directory so the parent settings files and shared-helper import resolve.
To check its working directory, run this in a C# scratch cell:

```csharp
Console.WriteLine(System.IO.Directory.GetCurrentDirectory());
```

If necessary, set it before running the Configuration cell, using your actual repository path:

```csharp
System.IO.Directory.SetCurrentDirectory(@"C:\path\to\foundry-iq-workshop\c-sharp-notebooks");
```

Run the notebook cells from top to bottom. Browse uses the first URL returned by Web Search.
HTTP failures print status/body without a traceback; fix the issue and rerun the relevant search before Browse.

**Calls and Browse retries incur Web IQ usage charges.** Use public, non-sensitive queries only.
See [costs and responsible use](../docs/reference/README.md#costs-and-responsible-use).
