# Autotask PSA Plugin for Veeam Service Provider Console

Integrates **Veeam Service Provider Console 9.2 and 9.3** with **Datto Autotask PSA**: company mapping, consolidated billing into Autotask PSA recurring service contracts, and automated service tickets created from Veeam Service Provider Console alarms. The plugin brings the feature set of the built-in ConnectWise Manage integration to service providers who run Autotask PSA as their business management platform.

> **Disclaimer**
>
> This plugin, its package and its documentation were created with AI tools (Anthropic's Claude Code), with human review and live testing by the author. As with every plugin in this repository, it is a community contribution: it was not created by Veeam R&D, has not been validated by Veeam QA, and is not supported by Veeam Customer Support. Evaluate it in a test environment first, and use the billing **Preview** (dry run) before the first live synchronization.

## Contents of this directory

| File | Description |
|---|---|
| `VspcAutotaskPlugin.1.5.6.nupkg` | Signed, self-contained plugin package (win-x64). Upload it through the Veeam Service Provider Console Catalog. |
| [`docs/Autotask-PSA-Integration-Guide.pdf`](docs/Autotask-PSA-Integration-Guide.pdf) | Integration guide for service providers: requirements, installation, configuration of every feature, monitoring, troubleshooting, terminology and the plugin REST endpoints. |
| [`docs/Autotask-PSA-Integration-Guide.html`](docs/Autotask-PSA-Integration-Guide.html) | The same guide as a single self-contained HTML page. |

Package SHA-256: `F53ABE4D5DE18E214C8044EA17B5BF2CE49DD340B5D586FA6E7B912D773B1264`

## Integration features

| Feature | What it does |
|---|---|
| **Companies** | Maps Veeam Service Provider Console companies to Autotask PSA companies, automatically by name or manually from the toolbar. A company that is not mapped is never billed and never generates tickets. |
| **Billing** | Maps Veeam-powered services to Autotask PSA services, attaches them to a recurring service contract per mapped company, and reconciles the billed quantities with measured usage once a day. Re-running a synchronization is idempotent, and a dry-run preview shows the changes before anything is written. |
| **Ticketing** | Creates Autotask PSA service tickets from alarms triggered for mapped companies after a configurable delay window, with the queue, statuses, priorities, source and ticket type chosen from live Autotask PSA picklists. Closes the ticket when the alarm resolves, resolves the alarm when the ticket is closed in Autotask PSA, and adds a note when an alarm re-triggers. |

Integration features control synchronization, not navigation: all plugin pages stay available in the menu, and a page whose feature is switched off shows an informational message.

## How it works

The plugin is a self-contained service that Veeam Service Provider Console starts, hosts and supervises. It is not installed separately and never asks for Veeam Service Provider Console credentials: the plugin host provisions the plugin REST API key automatically at installation time. The only credentials you enter are the Autotask PSA API user, secret and integration code. The service listens on the loopback interface only (`http://127.0.0.1:33847`), and the portal proxies the plugin UI and API. Autotask PSA is reached outbound over HTTPS, and the API zone is detected automatically.

## Requirements

| Item | Requirement |
|---|---|
| Veeam Service Provider Console | Version 9.2 or 9.3. The plugin is tested and working on both versions; it registers with the plugin host and uses REST API v3. |
| Operating system | Runs on the machine that hosts the Veeam Service Provider Console Server component; any Windows Server version supported by Veeam Service Provider Console. |
| .NET runtime | Not required. The service is published as a self-contained win-x64 application. |
| Autotask PSA | An instance with REST API v1.0 and at least one API-only user (security level *API User (system)*) with access to Companies, Contracts, Services, Contract Services and Adjustments, Billing Codes, Tickets and Ticket Notes. |
| Connectivity | Outbound HTTPS (TCP 443) from the Veeam Service Provider Console server to the Autotask PSA web services endpoints, including `webservices.autotask.net` for zone detection. |
| Portal roles | Portal Administrator to install, upgrade or remove the plugin. Portal Administrator or Portal Operator to open plugin pages and change settings; all other roles are rejected. |

## Installation

1. Log in to Veeam Service Provider Console as a Portal Administrator.
2. Click **Configuration** in the top right corner, then **Catalog** in the menu on the left.
3. Click **Upload Custom Plugin** and select `VspcAutotaskPlugin.1.5.6.nupkg`.
4. Wait until installation completes. The **Autotask PSA** tile appears on the Catalog page; click it to open the plugin.

**Upgrade:** upload a newer package the same way. The plugin identifier (`7A4C3E529D144F6BB7A152AE23C90D11`) never changes, so Veeam Service Provider Console replaces the installed version in place. Mappings, settings, ticket links and the activity log are preserved because they live in `C:\ProgramData\VspcAutotaskPlugin`, outside the package folder.

**Removal:** remove the plugin from the Catalog page. Veeam Service Provider Console instructs the plugin to purge its local data (database and stored credentials). Nothing is deleted in Autotask PSA, but the links between alarms and tickets are lost.

## Configuration

1. **Obtain Autotask PSA API credentials.** In Autotask PSA, create an API-only user with the *API User (system)* security level, generate its key and secret, and create a *Custom (Internal Integration)* API tracking identifier.
2. **Configure the plugin connection.** The **Autotask PSA Integration** window opens automatically the first time you open the plugin. Enter the API user, secret and integration code and click **Connect**; the zone is detected automatically. The window is available later on the **Plugin Status** page, via **Change** next to *Autotask zone*, and also offers **Disconnect**.
3. **Enable integration features.** On the **Plugin Status** page, switch on **Companies**, **Ticketing** and **Billing**, or click **Enable All**. Changes apply immediately.
4. Continue with company mapping, product mapping and contracts, and ticket settings as described in the [Integration Guide](docs/Autotask-PSA-Integration-Guide.pdf).

## Stored data and security

* Data folder: `C:\ProgramData\VspcAutotaskPlugin` (`plugin.db` SQLite database, `secret.key`, `logs\`). Preserved across upgrades, purged on removal.
* Autotask PSA credentials are encrypted at rest with Windows DPAPI and bound to the Veeam Service Provider Console server machine. The secret is never returned to the browser.
* Every request to the plugin is authenticated as the signed-in portal user; only the Portal Administrator and Portal Operator roles are admitted.
* Log files may contain company names, alarm messages and Autotask PSA entity identifiers, but never the Autotask PSA secret. Logs are also available through the plugin host log collection (last 30 days).

## Considerations and limitations

* Companies must exist on both sides. The plugin does not create companies in either product and does not create Autotask PSA configuration items.
* Only recurring service contracts can carry the services the plugin maintains.
* Units are not converted. Make sure the unit of measure of the mapped Autotask PSA service matches the Veeam Service Provider Console service.
* Veeam Cloud Connect sites are not part of the mapping model.
* Alarm state is polled (default every 120 seconds), company lists refresh every 4 hours, and billing reconciles once a day at a configurable UTC hour.

## Troubleshooting and support

Check the **Activity** page first, then the log files in `C:\ProgramData\VspcAutotaskPlugin\logs` (a failed startup is written to `startup-error.log`). The Troubleshooting section of the Integration Guide lists common issues and their resolution. For bugs and feature requests, open an issue in this repository.

## License

Distributed under the MIT License of this repository (see [LICENSE](../LICENSE)).
