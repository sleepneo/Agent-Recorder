# Schema sources

Canonical Agent Plugins 1.0.0 schemas retrieved from the official URLs on
2026-11-15. Used by the PowerShell build's `Test-Json` validation; these files are
build inputs and are not bundled in the plugin.

- [plugin.schema.json](https://agent-plugins.org/schemas/1.0.0/plugin.schema.json)
  SHA256 `0A4AAD95CE337878AD38802EBF0DAA3FDE76ABE3F65400C86BCBB1EC0B3AB883`
- [mcp.schema.json](https://agent-plugins.org/schemas/1.0.0/mcp.schema.json)
  SHA256 `6539175BFCDF43085855183E86DA40EA94B166547A72B47AE9A0A390516D3ACB`

Packaging and local marketplace semantics were checked against
[OpenAI's plugin packaging documentation](https://developers.openai.com/plugins/build/plugins)
and [local stdio guidance](https://developers.openai.com/plugins/deploy/desktop-only).
No compatibility overlay is required by the current portable specification;
actual desktop installation remains a separate client validation.
