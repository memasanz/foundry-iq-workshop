# Deploy the workshop resources

This guide is for the facilitator, including someone deploying Azure resources
for the first time. Run commands in **PowerShell 7 from the repository root**,
not from this `infra` directory. Deployment creates billable Azure resources.

An Azure **subscription** is the billing and access boundary. A **resource
group** is a folder-like collection of Azure resources. **Bicep** describes the
resources; `scripts\deploy.ps1` submits that description to Azure and saves the
resulting configuration for the notebooks.

## What gets deployed

| Resource | Purpose | Default |
|---|---|---|
| Web IQ account | Billing resource for the Web IQ APIs | Global, G3 |
| Microsoft Foundry account and project | Models, agents and connections | East US, AIServices S0 |
| Chat model deployment | Agent and knowledge-base responses | `workshop-model`: `gpt-5-mini`, version `2025-08-07`, GlobalStandard, capacity 30 |
| Embedding model deployment | Document and query vectors | `workshop-embedding`: `text-embedding-3-small`, version `1`, GlobalStandard, capacity 50 |
| Azure AI Search | Indexes, indexers and knowledge sources | East US, Standard S1, one replica and partition, free semantic tier |
| Storage account and Blob container | Sample source documents | East US, Standard LRS, Hot, `workshop-documents` |
| Managed identities and role assignments | Let services access each other without account keys | Scoped permissions defined in Bicep |

**APIM is not deployed.** Notebook 09 uses an existing APIM service configured
in `.env`; see [Existing APIM](#existing-apim-for-notebook-09).
The templates also do not create SharePoint sites, Entra app registrations,
Microsoft 365 licenses, Web IQ profile keys, notebook indexes or agents.

The files are:

| File | Responsibility |
|---|---|
| `main.bicep` | Parameters, resource names and module orchestration |
| `foundry.bicep` | Foundry account, project, two model deployments and facilitator project access |
| `data.bicep` | Search, Storage, Blob container and their scoped access grants |
| `search-foundry-access.bicep` | Search identity's Cognitive Services User role on Foundry |
| `..\scripts\deploy.ps1` | Sign-in checks, provider registration, deployment and generated notebook settings |

## 1. Check prerequisites

You need:

- A paid Azure subscription enabled for Web IQ. Azure credits and provider
  registration alone do not enable Web IQ; arrange access before deployment.
- Permission to create resources **and role assignments**. Typically this is
  subscription Owner, or Contributor plus Role Based Access Control
  Administrator at the required scopes. Tenant app consent is a separate
  permission and is not included.
- Available model quota and Search capacity in your chosen region.
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-windows)
  and [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows).
  Python and Jupyter are needed for the notebooks, not for Bicep deployment.

Check your tools:

```powershell
$PSVersionTable.PSVersion
az version
az bicep version
```

Use PowerShell version 7 or later. If Azure CLI reports that Bicep is missing,
run `az bicep install`, then repeat the version check.

## 2. Open the repository and sign in

Open the repository folder in VS Code and open a PowerShell terminal. Running
`Get-ChildItem` should show `infra`, `scripts`, `notebooks` and `README.md`.
If you are currently in `infra`, run `Set-Location ..` first.

```powershell
az login
az account list --query '[].{Name:name,SubscriptionId:id,TenantId:tenantId}' --output table
```

Choose the intended subscription from the table. Copy its **SubscriptionId**,
not its tenant ID. If you have access to multiple tenants, sign in with
`az login --tenant '<workshop-tenant-id>'`.

The deployment script explicitly targets the subscription you pass; it does
not change your CLI default subscription.

## 3. Choose the region and resource group

We recommend **East US (`eastus`)**: Central US does not currently support
Content Understanding, and East US 2 is currently capacity-constrained for new
AI Search deployments. East US supports both services, although available
capacity and model quota must still be checked for your subscription.
Choosing this region does not itself add Content Understanding to a notebook.

Use a **dedicated workshop resource group**, normally `rg-iq-workshop`. Do not
put unrelated resources in it: the repository's optional whole-group cleanup
would delete them too.

An existing resource group's metadata location can differ from its resources'
locations. For example, a group located in `eastus2` can contain resources in
`eastus`; the deployment script preserves the existing group's location.

## 4. Deploy

Replace the placeholder below with the subscription ID you selected:

```powershell
$subscriptionId = '<your-subscription-id>'
.\scripts\deploy.ps1 `
  -SubscriptionId $subscriptionId `
  -ResourceGroup 'rg-iq-workshop' `
  -Location 'eastus' `
  -SearchSku 'standard'
```

The backtick at the end of each continued line must be the final character,
with no spaces after it. You can also enter the command on one line.

The script registers the required Azure providers, creates the resource group
if needed, compiles Bicep and deploys the resources. It uses your signed-in
user as the facilitator. After provisioning, it reads the live Foundry project
identity and ensures that identity has Search Index Data Reader on the new
Search service. This also handles a project identity that differs from the
one reported during deployment. Keep the terminal open and allow provisioning
to finish; initial provider registration can also take time.

For automation using a service principal, pass `-PrincipalId` with the
facilitator **user's Entra object ID**; do not pass an application client ID.
Model overrides are available through `-ModelName`, `-ModelVersion` and
`-ModelCapacity`; check availability and quota before changing them.

If the dedicated resource group **already exists**, you can preview first by
adding `-WhatIf` to the same command. This does not register providers, deploy
resources or rewrite configuration. A first-time preview cannot target a
resource group that does not exist yet.

## 5. Understand the automatically updated configuration

**Yes: the deployment script updates the notebook configuration for you.**
After Azure reports success, it writes:

| Local file | Contents | Edit it yourself? |
|---|---|---|
| `.env.azure` | Subscription, tenant, locations, Foundry project endpoint, model deployment names, Search endpoint and resource ID, Storage endpoint/account/container and Web IQ resource ID | Normally no; generated again after each successful deployment |
| `.deployment\deployment.json` | Azure deployment result and outputs | No; local deployment record |
| `.env` | Your Web IQ key, attendee prefix, SharePoint settings, existing APIM resource ID and other manual notebook settings | Yes; deployment leaves this file unchanged |

All three are excluded from Git. Do not copy deployment-specific endpoints
into notebook code or commit credentials.

If deployment fails, the script does **not** replace `.env.azure`. Azure may
still have created some resources; inspect the deployment error and rerun the
same command after resolving it rather than deleting the group.

Create `.env` only if you do not already have one:

```powershell
if (-not (Test-Path .\.env)) { Copy-Item .\.env.example .\.env }
```

Review `.env` for old endpoint or model overrides. Notebooks load `.env` before
`.env.azure`, so a manually copied old value can hide the newly deployed one.
Restart notebook kernels after a deployment to discard old environment values.

## 6. Verify deployment and finish onboarding

Open **Azure portal -> Resource groups -> your workshop group -> Deployments**
and check that `iq-workshop` shows **Succeeded**. Open the Foundry account,
Search service and Storage account and verify their resource location is East
US. The global Web IQ account is expected to stay global.

Inspect the generated settings without showing `.env` secrets:

```powershell
$settings = Get-Content .\.env.azure -Raw | ConvertFrom-StringData
$settings.AZURE_LOCATION
$settings.AZURE_AI_PROJECT_ENDPOINT
$settings.AZURE_SEARCH_ENDPOINT
$settings.AZURE_STORAGE_ACCOUNT_NAME
```

A successful deployment does not finish these manual steps:

- Link the Web IQ billing resource in the [Web IQ portal](https://webiq.microsoft.ai/profiles),
  create a workshop key, and arrange Auto beta enablement.
- Check [Prepare for the workshop](../PREPARE-FOR-WORKSHOP.md) for participant
  roles. Bicep grants the facilitator Foundry User on the project; the
  parent-resource Foundry Owner access required by the agent labs is separate.
- Verify public-network access is allowed by your organization's policies.
  Notebook 04 needs both your computer and Search to reach Blob Storage.
  Public networking does **not** mean anonymous Blob access: anonymous access
  and shared keys remain disabled.
- If policy disables Storage public networking, obtain the approved exception
  for **this Storage account** and verify authenticated access. An exemption
  on an old account does not transfer. Do not bypass tenant policies or weaken
  authentication to make the lab work.
- Complete notebook 06's app registration, administrator consent and site
  grant, and notebook 07's Microsoft 365 Copilot licensing or explicitly
  enabled Retrieval API pay-as-you-go. Deployment does not perform those steps.

Use the [repository README](../README.md#3-open-the-notebooks) to install the
notebook environment and continue with the labs.

## Existing APIM for notebook 09

Add your gateway's full resource ID to `.env`:

```dotenv
APIM_RESOURCE_ID=/subscriptions/<subscription-id>/resourceGroups/<gateway-group>/providers/Microsoft.ApiManagement/service/<existing-gateway-name>
```

The gateway can remain in another region or resource group, and even another
subscription in the same tenant. Enable its system-assigned managed identity
and grant that identity **Search Index Data Reader** on the workshop Search
service. Then follow the [facilitator APIM setup](../README.md#6-set-up-the-apim-gateway-facilitator-before-the-workshop-notebook-09).
The scripts configure the workshop API and named values on the existing
gateway; they do not provision an APIM service. Add the printed gateway URL
and broker client ID to `.env` for notebook 09.

## Redeployment, errors and costs

Rerunning the same deployment command uses the same deterministic resource
names. Changing region creates new regional resources; it does not move or
delete old ones. Recreate region-dependent Foundry connections/agents and
update the SharePoint federated trust and APIM backend/access grants afterward.

| Symptom | What to check |
|---|---|
| `az` or Bicep not found | Install the missing prerequisite and reopen the terminal |
| Authorization or role-assignment error | Correct subscription/tenant, resource permissions, and permission to assign roles |
| Web IQ deployment rejected | Paid subscription and Web IQ service enablement; provider registration is insufficient |
| Model unavailable or quota exceeded | Exact model version, GlobalStandard availability and regional quota |
| Search capacity error | Current regional capacity; do not assume subscription quota guarantees physical capacity |
| Blob upload or indexing denied | Networking/policy as well as Entra data roles; allow time for role propagation |
| Notebook still uses the old endpoint | Remove stale `.env` overrides and restart its kernel |

**Search Standard S1 bills continuously while provisioned, even when idle.**
Models, Web IQ requests, Storage and the existing APIM tier can also incur
charges. Setting model capacity is not a spending cap. Old resources retained
after a regional redeployment can continue to cost money.

Do not delete a group to fix a deployment error. Only use the repository's
cleanup instructions when you intentionally want to delete **every resource
in the dedicated workshop group**, and keep any APIM gateway you want to retain
outside that group.
