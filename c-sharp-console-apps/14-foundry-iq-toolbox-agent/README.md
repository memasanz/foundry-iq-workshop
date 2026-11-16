# Lab 14 - Toolbox with user-delegated SharePoint access

[C# notebook](../../c-sharp-notebooks/14-foundry-iq-toolbox-agent.ipynb) | [Python notebook](../../python-notebooks/14-foundry-iq-toolbox-agent.ipynb) | [Lab 07](../07-sharepoint-indexed/README.md) | [Lab 11](../11-foundry-iq-knowledge-base/README.md) | [Lab 13](../13-hosted-agent/README.md)

The **.NET 10 console app** creates a SharePoint-only Foundry IQ KB, project-identity Search connection, versioned Toolbox and C# hosted agent. **Document access follows the signed-in user's indexed SharePoint ACLs**, not the agent's Search role. Keep the Toolbox; do not replace it with a custom retrieval function. The local launcher uses only .NET libraries; Foundry restores the generated host's pinned packages. No Python, Docker, ACR, `azd`, APIM broker, new app registration or ingestion is required.

**Migration:** reusing an existing lab 14 prefix intentionally repoints `<prefix>-toolbox-kb` from Blob to SharePoint and changes `<prefix>-iq-search-mcp` from `AgenticIdentityToken` to `ProjectManagedIdentity`. Existing agents using these definitions are affected. Redeploy the generated host to include caller-context guards. Old per-agent Search grants are not removed automatically.

## Prerequisites and identities

Complete **07 and 11** in either language with the same prefix; retain `<prefix>-nb7-index`, `<prefix>-kb-sharepoint` and `<prefix>-kb`. Lab 14 validates the SharePoint source/index and copies model configuration from lab 11; it does not modify any shared source, index, ingestion app or mixed KB. Confirm lab 13's C# hosted remote-build path works; Python availability does not prove .NET runtime availability.

| Identity | Permission/purpose |
|---|---|
| Workshop author | Foundry Owner on the parent resource, Search Service Contributor and existing Search read access; create resources using the deployment-tenant Azure CLI login. |
| Role-assignment administrator | Permission to assign roles at the Search service, separate from ordinary authoring roles. |
| **Project managed identity** | **Search Index Data Reader on the Search service**: service authentication for this `ProjectManagedIdentity` connection, reusable across agents. The deployment scripts provision and verify this prerequisite; the notebooks do not assign roles. |
| **Signed-in ordinary user** | Foundry invocation access; user's identity determines SharePoint ACL filtering. Use non-admin accounts for the two-user test, without Search Contributor/elevated-read privileges. |
| Hosted runtime identity | Existing model inference and Toolbox access. No per-agent Search grant for this connection. |
| Search service identity | Existing KB model planning/synthesis and ingestion permissions from earlier labs. |

The project must already have a managed identity; this sample does not enable one or alter tenant consent. Lab 07's index must contain permission metadata on every chunk, not merely correctly named fields. Permission and content changes need indexing/resync, including inherited ACL changes. Lab 07's native/nested group, guest and propagation limitations apply; this is not a claim of full live SharePoint permission parity. This **indexed** path does not use lab 08's Copilot Retrieval API; lab 07's ingestion prerequisites still apply.

Required settings: `WORKSHOP_PREFIX`, `AZURE_TENANT_ID`, `AZURE_SEARCH_ENDPOINT`, `AZURE_SEARCH_RESOURCE_ID`, `AZURE_AI_PROJECT_ENDPOINT`, `AZURE_AI_PROJECT_RESOURCE_ID`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`. Root `.env`, then `.env.azure` override environment values; `.env.azure` wins. Prefix: 3-20 lowercase letters/digits, starting with a letter. Use matching public-cloud HTTPS endpoints/full ARM IDs and a model deployment name. Settings reject endpoint credentials, query/fragment and mismatches.

## How the user identity reaches Search

```text
Alice / Bob -- delegated Foundry user token --> hosted Responses endpoint
  --> C# host -- AddFoundryToolboxes / per-request call ID --> managed Toolbox
  --> ProjectManagedIdentity Search connection --> SharePoint-only Foundry IQ KB
  --> lab 07 index -- indexed user/group ACLs --> authorized evidence + citations
```

**Service authentication and source authorization are separate.** The project identity authenticates the Search request. For Foundry IQ, Foundry dynamically sets `x-ms-query-source-authorization` with the signed-in user's Search token, resolved from the request's caller context. It is not stored in the shared connection or Toolbox. A project/agent Search role alone does **not** make retrieval user-specific.

The generated host uses the official `AddFoundryToolboxes` integration and Responses protocol **2.0.0**, preserving the platform call ID. It rejects POST requests missing user/call context before model execution. An arbitrary user ID is not a delegated access token; the sample does not manufacture `x-ms-user-identity`, forward `x-agent-user-id`, or implement its own MCP client. Do not expose the container directly or trust client-supplied platform headers outside Foundry.

**Preview limitation and release gate:** Search [documents unfiltered indexed retrieval when source authorization is missing](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve#enforce-permissions-at-query-time-preview). There is no mandatory-header setting implemented by this lab. Client/container guards catch missing context but cannot prove the managed proxy attached the final Search header. **Verify real Alice/Bob tool evidence before using sensitive content.** If your requirements demand an independently enforced downstream mandatory-header boundary, use a separately enforced gateway rather than claiming this native path is unconditionally fail-closed.

## Run individual steps

Run from the repository root:

```powershell
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- --help
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- verify-sources
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- knowledge-base
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- connection
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- toolbox
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- check-toolbox
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- write-agent
# Optional local build; replace the prefix. Needs NuGet, not Azure sign-in.
dotnet build .\alice01-csharp-hosted-agent-iq\WorkshopIqAgent.csproj
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- package
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- deploy
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- wait
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- grant-search
# Switch to an ordinary user for invocation, not the author/admin.
az login --tenant <tenant-id>
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- invoke
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- invoke "What is the restricted fact in <document>?" --diagnostics
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- invoke-unknown
```

| Command | Exact action |
|---|---|
| `verify-sources` | Three metadata reads: lab 11 SharePoint source, lab 07 ACL-enabled index and lab 11 KB model settings. Validates exact index target, user/group permission schema, source fields and semantic config before any write. |
| `knowledge-base` | Those checks, then create/update `<prefix>-toolbox-kb` containing **only `<prefix>-kb-sharepoint`**, existing models/answer instructions, `answerSynthesis`, medium reasoning. Successful HTTP 204 updates are verified with one metadata GET, not a write retry. No Blob/web/unfiltered fallback or reingestion. |
| `connection` | Check the SharePoint-only KB; PUT prefixed ARM `RemoteTool` connection, `ProjectManagedIdentity`, Search audience and exact KB MCP target. No key or user token in its definition. |
| `toolbox` | Check connection, publish one immutable MCP Toolbox version, promote its default if necessary. `require_approval=never` removes approval prompts, not authorization. |
| `check-toolbox` | Check connection/default/version metadata. No MCP execution or document query. |
| `write-agent` | **Overwrite** the two local generated source files. No authentication/Azure call. |
| `package` | Zip only existing `Program.cs` and `WorkshopIqAgent.csproj`; preserve edits and exclude `.env`, `bin`, `obj`, credentials and unrelated files. |
| `deploy` | Validate existing archive/Toolbox/connection, hash/upload code and create one immutable hosted version. No implicit packaging, repair, role grant or question. |
| `status [version]` | Read specified or latest existing version metadata. |
| `wait [version]` | Poll fixed version every 15 seconds for at most 15 minutes; stop on active/failure/deletion/timeout. Active is provisioning, not proof of runtime/ACL readiness. |
| `grant-search` | Read the **project** ARM identity; assign service-scoped Search Index Data Reader to that project principal. **No version argument**, no agent grant. Denial prints administrator handoff and returns exit code 3. |
| `invoke [question] [--diagnostics]` | Validate delegated-user token claims and send one Responses request; default asks about accessible SharePoint documents. No prior conversation/session selectors. Optional bounded diagnostics can expose internal tool evidence. |
| `invoke-unknown [--diagnostics]` | One out-of-documents question. A refusal is a behavior check, **not an authorization test**. |
| `run [--diagnostics]` | Setup/write/package/deploy/wait/project grant and two questions using the current login. **Mutating and billable**; stops on denied grant, overwrites generated source and publishes versions. Use individual steps to switch from author to ordinary user before permission tests. |
| `cleanup --confirm` | Preflight then delete only lab 14 agent, Toolbox, connection and KB. No force-delete; agent deletion 409 stops before dismantling the rest. |
| `help`, `--help`, `-h` | Offline; no settings, credentials or side effects. No arguments also shows help. |

Commands are independent and do not silently repair mismatched prerequisites. No question retries, credential/answer/session cache or implicit cleanup. Agent invocation follows the **agent name's endpoint routing**; a version selected for polling does not pin Responses routing. Each C# invocation acquires an Azure CLI token afresh so account switches do not reuse a cached user token.

### Project Search access: already handled by deployment

`scripts\deploy.ps1` and `infra\data.bicep` configure the project's Search Index Data Reader role. Both notebooks document it under **Required permissions**, without a role-assignment step. For an environment prepared another way, an administrator must configure it before participants start. The standalone console command below remains available for explicitly repairing a missing grant; it is not a notebook step.

`grant-search` authenticates ARM with **your author/admin login**, reads the configured project, verifies its resource ID and `identity.principalId`, and PUTs a deterministic role assignment at the Search service:

```text
principalId:       <project managed identity principal, NOT agent or user>
principalType:     ServicePrincipal
roleDefinitionId:  <Search scope>/providers/Microsoft.Authorization/roleDefinitions/
                   1407120a-92aa-4202-b7e9-c0e197c71c8f
```

HTTP 403 is an **administrator handoff**, not success. Give the printed project principal and exact Search scope to an administrator: Search **Access control (IAM) -> Add role assignment -> Search Index Data Reader**, assigned to that project principal. An arbitrary 409 is not accepted; `RoleAssignmentExists` requires verifying the exact existing principal/role/scope. Propagation can take time; invoke explicitly afterward instead of automatically retrying billable questions.

This is service access shared by agents using this project connection. Separately, per invocation, the signed-in user's source token drives ACL filtering. Do not grant the user/agent broader Search privileges to work around a source-authorization failure. Existing per-agent grants from the old exercise remain for administrator review.

## Required live two-user verification

Prepare a SharePoint document with a distinctive **nonpublic fact** that **Alice can read and Bob cannot**. Confirm these permissions in SharePoint, index/resync using lab 07, and use ordinary same-tenant member accounts with Foundry invocation access, not Search Contributor/admin/elevated-read privileges.

1. Log in as Alice and use a **new process** to invoke `"What is the exact restricted fact in <document>?" --diagnostics`. Inspect actual retrieved passages, identifiers and citations; confirm Alice's authorized answer is grounded in those passages.
2. Log in as Bob and use another new process with **the identical question**. Bob's retrieval evidence, answer and citations must exclude the restricted fact/document. Never reuse Alice's response/session ID.
3. Check missing/invalid/app-only credentials are rejected before the question request, and missing container user/call context stops before model/tool calls. A no-login test must not silently use the author's credential.
4. If consent/context/source authorization fails, **stop**. Do not drop ACLs, broaden roles, add elevated reads or fall back to shared retrieval. Verify native Toolbox integration, protocol 2.0.0, deployed routing and tenant consent.

**If the evidence cannot be inspected or Bob receives Alice's evidence, safe delegated access has not been demonstrated.** HTTP 200, model refusal, prompt instructions and offline fixtures do not prove document isolation. There is deliberately no simulated “Bob denied” result presented as a live ACL test.

Invocation locally decodes JWT claims to reject missing scope/user, app-only identity, wrong tenant/audience and expiry. This is a sanity check, **not signature verification**; Foundry validates real tokens. `oauth_consent_request` stops with explicit supported-client authorization guidance, never app-only fallback or automatic retry.

## Runtime, notebook and resource lifecycle

Azure names match Python: `<prefix>-toolbox-kb`, `<prefix>-iq-search-mcp`, `<prefix>-iq-toolbox`, `<prefix>-iq-agent`. C# source is `<prefix>-csharp-hosted-agent-iq`; Python's source folder is separate. The same prefix targets the **same Azure objects** in both languages; prefixes avoid collisions, not enforce tenant/data isolation.

`AIProjectClient(...).AsAIAgent(...)`, `AddFoundryToolboxes`, `AgentHost`, `AddFoundryResponses` and `MapFoundryResponses` follow the [official C# server-side Toolbox sample](https://github.com/microsoft-foundry/foundry-samples/tree/main/samples/csharp/hosted-agents/agent-framework/foundry-toolbox-server-side). There is no local retrieval function. Foundry supplies project endpoint/runtime identity; deployment sends model/Toolbox names, not user tokens or reserved `FOUNDRY_*` variables. Source upload uses multipart metadata/code, SHA-256, `dotnet_10` remote build, Responses `2.0.0`, CPU `0.5`, memory `1Gi`. Search API: `2026-08-01-preview`; ARM connection/project: `2025-04-01-preview`; Toolbox: `v1`.

Toolbox versions are immutable. Native discovery forwards per-request context; do not assume warm sessions immediately rediscover schema changes. The linked standalone C# notebook embeds the same helpers. Set working directory to `c-sharp-notebooks`; use the **Polyglot Notebook editor language with csharp execution kernel**, not a plain-C# editor cell. Restart/setup only when changing users; do not rerun authoring/grant/deploy cells as ordinary users. Cleanup defaults to false. Clear notebook outputs before sharing.

Responses use `store=false` without prior conversation selectors, not a promise of zero telemetry/session retention. Errors print bounded bearer-redacted diagnostics; optional tool diagnostics can contain internal text. Model/KB/hosted compute, sessions and Search can incur charges. Closing the terminal does not stop cloud resources.

```powershell
dotnet run --project .\c-sharp-console-apps\14-foundry-iq-toolbox-agent -- cleanup --confirm
```

Cleanup verifies owned agent markers, exact Toolbox, project-identity connection and SharePoint-only KB before deleting those four objects. Missing objects are skipped. A blocked agent delete stops the chain. Retain lab 07 index, lab 11 sources/base, model/services, independent Search roles and local files. No obsolete role is automatically removed.

## Offline validation

```powershell
dotnet run --project .\tests\c-sharp-iq-toolbox
$env:PYTHONPATH = '.\tests'
.\.venv\Scripts\python.exe -m unittest test_csharp_iq_toolbox_notebook test_python_iq_toolbox_delegation -v
dotnet format .\c-sharp-console-apps\14-foundry-iq-toolbox-agent\IqToolboxAgent.csproj --verify-no-changes --no-restore
dotnet format .\tests\c-sharp-iq-toolbox\IqToolboxTests.csproj --verify-no-changes --no-restore
```

Tests cover exact Python/C# payload/UUID parity, ACL-schema prerequisites, project-vs-agent identity, app-only/missing-user rejection, independent user requests/conversations, consent/errors/redaction, source archive integrity, scoped cleanup, actual inline C# notebook execution and generated host compilation. **No test deploys to Azure or proves the final managed Search header/live ACL filtering.**

References: [Foundry IQ connection](https://learn.microsoft.com/azure/foundry/agents/how-to/foundry-iq-connect), [source authorization](https://learn.microsoft.com/azure/foundry/agents/concepts/user-delegation#foundry-iq-source-authorization), [hosted Toolbox context forwarding](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/use-toolbox-hosted-agent).
