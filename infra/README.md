# SquadScout Infrastructure

This directory contains Bicep infrastructure-as-code templates for deploying SquadScout's Azure resources.

## Azure Resources

The following resources are deployed:

- **Azure Web PubSub** - Real-time client connectivity and message relay
- **Azure Functions** - Mobile authentication and Web PubSub token minting
- **Application Insights** - Telemetry and observability
- **Log Analytics Workspace** - Log aggregation backend
- **Storage Account** - Functions runtime storage
- **App Service Plan** - Functions compute (Flex Consumption tier)

The Function App uses a **system-assigned managed identity** with Web PubSub Service Owner role to mint client access tokens without connection strings.

## Prerequisites

- Azure CLI (`az`) or Azure Developer CLI (`azd`)
- An Azure subscription
- Owner or Contributor + User Access Administrator permissions on the subscription or resource group

## Quick Start with AZD

Azure Developer CLI provides the simplest deployment path:

```bash
# Initialize environment (first time only)
azd auth login
azd env new dev

# Set required parameters
azd env set AZURE_LOCATION eastus

# Provision infrastructure
azd provision

# Deploy Functions code
azd deploy
```

After deployment, the Functions negotiate endpoint will be available at the `FUNCTIONS_APP_URL` output.

## Manual Deployment with Azure CLI

If not using `azd`, you can deploy directly with `az`:

```bash
# Login and set subscription
az login
az account set --subscription <your-subscription-id>

# Create deployment
az deployment sub create \
  --name squadscout-$(date +%s) \
  --location eastus \
  --template-file ./infra/main.bicep \
  --parameters environmentName=dev location=eastus
```

## Configuration

### Environment Parameters

Required parameters in `main.bicep`:

| Parameter | Description | Default | Example |
| --------- | ----------- | ------- | ------- |
| `environmentName` | Environment name (used for resource naming) | (required) | `dev`, `staging`, `prod` |
| `location` | Azure region for all resources | (required) | `eastus`, `westus2` |

### Deployment Outputs

After deployment, these outputs are available for configuring the Functions app and MAUI client:

| Output | Description | Use in |
| ------ | ----------- | ------ |
| `WEBPUBSUB_ENDPOINT` | Web PubSub service HTTPS endpoint | Functions `Functions__WebPubSubEndpoint` |
| `WEBPUBSUB_HUB_NAME` | Web PubSub hub name | Functions `Functions__WebPubSubHub` |
| `FUNCTIONS_APP_URL` | Function App HTTPS endpoint | MAUI app negotiate client |
| `FUNCTIONS_IDENTITY_PRINCIPAL_ID` | Functions managed identity principal | (informational) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights connection string | Functions observability |

### Configuring Functions App Settings

The Bicep deployment automatically configures required Function app settings. For local development, create `src/SquadScout.Functions/local.settings.json` from the sample:

```json
{
  "Values": {
    "Functions__WebPubSubEndpoint": "<from WEBPUBSUB_ENDPOINT output>",
    "Functions__WebPubSubHub": "squadscout",
    "Functions__TokenLifetimeMinutes": "60"
  }
}
```

### Configuring Web PubSub Event Handlers

After infrastructure deployment, configure the Web PubSub hub to route live session events directly to the broker:

```bash
# Resolve the public broker hostname
BROKER_HOST=<broker-hostname>

# Configure upstream handler
az webpubsub hub update \
  -g rg-<env> \
  -n wps-<token> \
  --hub-name squadscout \
  --event-handler url-template="https://${BROKER_HOST}/api/upstream" \
    user-event-pattern="session-*" \
    system-events="connect" "connected" "disconnected"
```

> **Note:** The broker validates upstream requests using the Azure Web PubSub connection string's endpoint/access key and optional `AzureWebPubSub:TrustedUpstreamPrincipalIds` allow-list. This step is not automated in Phase 1 Bicep to keep the IaC focused on resource provisioning. Future iterations may include a deployment script.

## Cost Considerations

Phase 1 uses Azure's free/consumption tiers where available:

- **Web PubSub**: Free tier (20 concurrent connections, 20K messages/day)
- **Functions**: Flex Consumption plan (pay per execution with configurable scale)
- **Application Insights**: Pay-as-you-go (first 5 GB/month free)
- **Log Analytics**: Pay-as-you-go (first 5 GB/month free)
- **Storage**: Standard LRS (minimal Functions storage only)

For production deployments, consider:

- Upgrading Web PubSub to Standard tier for higher concurrency
- Tuning Flex Consumption instance memory and max scale in `core/host/functions.bicep`
- Configuring Log Analytics retention policies

## Security

- Functions use system-assigned managed identity (no connection strings in code)
- Web PubSub access keys are NOT stored in app settings; Functions mint tokens via managed identity
- All endpoints enforce HTTPS
- Storage account disables blob public access
- Function App enforces TLS 1.2 minimum

## Customization

To modify resources, edit the relevant module:

- `core/messaging/webpubsub.bicep` - Web PubSub service configuration
- `core/host/functions.bicep` - Function App and Flex Consumption hosting plan

## Flex Consumption Notes

- The Functions host is deployed on the Linux-only Flex Consumption plan (`FC1`).
- Flex Consumption uses `functionAppConfig` with a blob deployment container instead of the legacy content share settings used by classic Consumption.
- If you previously deployed this environment with a `Y1` Consumption plan, run `azd down --purge` before reprovisioning to avoid drift between the old and new hosting models.
- `core/monitor/monitoring.bicep` - Application Insights and Log Analytics
- `core/security/role.bicep` - RBAC role assignments

## Cleanup

To delete all deployed resources:

```bash
# With azd
azd down

# With az CLI
az group delete -g rg-<environmentName> --yes --no-wait
```
