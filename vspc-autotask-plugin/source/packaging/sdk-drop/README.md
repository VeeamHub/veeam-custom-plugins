# Drop the KB4311 SDK archive here

Place the **"SDK & Plugin examples"** archive from Veeam KB4311 in this folder (any filename,
.zip as downloaded is fine — no need to extract it).

- Get it at: https://www.veeam.com/kb4311 → *VSPC: Integration with Custom Plugins* →
  **SDK & Plugin examples** (sign-in with a Veeam account required)
- Direct (SSO-gated) link: https://www.veeam.com/download_add_packs/availability-console/kb4311/

It contains the *Veeam Service Provider Console Plugin Template Guide* and example plugin
packages — the authoritative definition of the NUPKG layout, manifest schema, and how a plugin's
UI pages surface inside the VSPC portal. Once it's here, the packaging step turns the Autotask
integration into an uploadable custom plugin (Configuration → Catalog → Upload Custom Plugin).
