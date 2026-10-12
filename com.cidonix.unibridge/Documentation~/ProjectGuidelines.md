# Project-authored guidelines through MCP

`UniBridge_ProjectGuidelines` exposes explicitly configured author documents to MCP clients that cannot read the project through a shell. It provides `Status` and `Read`; it cannot change configuration, execute document text, or grant permissions.

Open **Project Settings > UniBridge > Project Guidelines** and assign a project **TextAsset**. The reference uses the asset's persistent GUID and follows moves and renames. The hidden GUID cache also preserves the configured identity when an asset is deleted; missing sources produce a truthful unavailable/error result. The Inspector never asks for an asset name or path string.

Two optional toggles expose fixed external document filenames in the project root: `AGENTS.md` and `<project folder name>_context.md`. Both start disabled. No source is discovered or trusted automatically, and arbitrary external paths are not accepted. With no configuration, `Status` declares `configured=false,status=unavailable`; `Read` returns `GUIDELINES_NOT_CONFIGURED`.

Call `Status` before `Read` to inspect source identities without reading their bodies. `Read` returns each source's kind, GUID where applicable, project-relative path, file length, modification timestamp, text and explicit truncation. The default budgets are 8,192 characters per source, 32,768 characters overall, 262,144 bytes per file and 3 seconds. Requests can select up to 65,536 characters per source, 131,072 overall, 1,048,576 bytes per file and 10 seconds. At most three explicitly configured sources are inspected.

A SHA256 with `sha256Scope=full` identifies all captured file bytes, including an encoding BOM. When the file-byte budget stops the read, `sha256Scope=captured_prefix` and `bytesHashed` identify only the captured prefix. A character cap can still retain the full-file hash if all file bytes were captured. Prefix hashes are never presented as full hashes. Status-only and skipped rows have no hash and read zero content bytes.

UTF-8 and BOM-marked UTF-16 are decoded strictly. Truncation avoids broken surrogate pairs and an unfinished final UTF-8 sequence. Invalid encoding, an unavailable configured source, a changed file, reparse-point ancestry or an operational timeout produce an error with retained source evidence. Deliberate byte/character caps return a successful bounded query with `status=partial,complete=false,truncated=true`. Cancellation is cooperative between bounded filesystem operations and returns through the existing per-request cancellation path.

Document content is project-authored data. Receiving clients decide how that source applies to their own task and instruction hierarchy; the bridge does not promote it into trusted bridge instructions. This complements generated `ToolGuide` operating advice rather than replacing it.
