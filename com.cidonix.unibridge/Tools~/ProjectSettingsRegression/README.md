# Native ProjectSettings qualification

Copy both fixture source files into one UUID-owned Editor-only folder in an approved test project; reference the MCP Editor assembly and Newtonsoft.Json. Refresh and wait for actual compilation readiness, then execute `Tools/UniBridge Validation/Extensions Settings 1/Run` once through ordinary MCP. The immutable result appears under `Library/AgentValidation/ExtensionsSettings1-*/result.json`.

The fixture compares actual QualitySettings values with a separate native SerializedObject. An owned in-memory ScriptableObject additionally tests signed/unsigned64, double, Vector4, typed asset reference identity, canonical array drilldown, global/depth/array/text limits, surrogate boundaries, cancellation and truthful negatives. It never saves settings or authored scenes. Quarantine only the exact imported fixture folder after SHA verification.

Separately call `UniBridge_ProjectSettings` through ordinary MCP for List, scalar Inspect, capped Inspect, unavailable settings/property and invalid-limit requests. Validate business results, `isError`, structured/text agreement, advertised output schema and unchanged project identity. A SDK compile is structural evidence, not native execution.
