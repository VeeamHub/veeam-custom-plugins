# Veeam UIKit packages (not redistributed)

`../package.json` references five Veeam packages as `file:` dependencies in this folder:

| File | Package |
|---|---|
| `veeam-vspc-shared-6.14.0.tgz` | `@veeam-vspc/shared` |
| `uikit-components-6.18.3.tgz` | `@veeam/components` |
| `icons-1.12.0.tgz` | `@veeam/icons` |
| `veeam-vspc-configs-3.2.5.tgz` | `@veeam-vspc/configs` (dev dependency) |
| `veeam-extjs-dev-server-2.0.0.tgz` | `@veeam-extjs/dev-server` (dev dependency) |

They are part of the Veeam Service Provider Console plugin SDK ("SDK & Plugin examples" on [Veeam KB4311](https://www.veeam.com/kb4311), Veeam account required) and are not redistributed in this repository. Copy them from the SDK's plugin UI template into this folder before running `npm install` or the top-level `build.ps1`.
