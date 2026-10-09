# UniBridge

UniBridge is a local Unity MCP bridge for AI-assisted game development.

It gives AI coding agents real tools to work inside Unity Editor projects: inspect scenes and assets, create and validate UI, edit scripts safely, work with prefabs, capture previews, author animation, physics, input actions, timeline content, and more.

## Status

UniBridge is currently distributed as a packaged Unity Editor tool through Patreon.

This repository contains the Unity package source, bundled relay binaries, documentation, release notes, and issue tracking. Packaged builds and updates are distributed through Patreon under the proprietary license below.

## What UniBridge Does

- Connects Unity Editor projects to MCP-compatible AI agents.
- Supports per-project MCP relay configuration.
- Allows one agent to work with multiple open Unity projects when each project has its own `--project-id`.
- Provides tools for scenes, assets, scripts, prefabs, UI, captures, validation, animation, rendering, physics, navigation, tilemaps, input actions, timeline, audio, VFX, and more.
- Lets agents inspect prefab and loaded-scene asset structure with compact hierarchy list/search/read, duplicate-safe indexed paths, and optional serialized field matching before they edit.
- Lets agents compare Unity YAML/text asset revisions semantically, including YAML document, fileID, property, GUID, and script-reference deltas.
- Lets agents preflight proposed C# source changes before applying them, including syntax, API, serialized field, Unity callback, and reload-risk checks.
- Lets agents export bounded profiler marker hierarchy/top-sample data to identify likely hot runtime nodes, not just frame counters.
- Lets agents inspect or ensure editability for one or many version-controlled assets before mutation.
- Gives fresh agents compact playbooks for read-before-modify, safe execution, scope awareness, and verification workflows.
- Includes visual self-check tools so agents can verify Unity output before reporting success.
- Recovers lost command results through an Editor-session journal and reports
  unknown outcomes without automatically repeating project mutations.
- Reports Editor wait timeouts as failures, including after reload/reconnect,
  and preserves MCP error flags instead of wrapping failed waits in success.
- Resolves the Windows named-pipe user SID from the Editor process token,
  without depending on account-name resolution.
- Gives semantic obsolete API hints with actual symbols, signatures and
  advertised replacement/result type guidance from the current Unity assembly.
- Guards work-session reverts with explicit write receipts, current/baseline
  fingerprints and one-use previews; preserves dirty scenes during Editor restore.
- Validates UXML/USS structure before writing and reports actual import diagnostics,
  with source readback and guards that preserve concurrent edits.
- Isolates Editor JSON and tracing from project-wide Newtonsoft defaults; cleans
  partial Bridge startup and pending writers across stop/restart boundaries.

- Provides opted-in, read-only Windows MCP latency qualification with warm and
  fresh clients in externally prepared foreground, background or minimized states.

## Requirements

- Unity Editor 6000.0 or newer.
- An MCP-compatible AI agent or client.
- A local machine where the Unity Editor and agent can run together.

A focused dependency-free compatibility adapter is also included for Unity
5.3.2+ (5.3.x), Unity 5.6.x, Unity 2017.4.x, and Unity 2018.4.x projects that cannot load the full
Unity 6 package. It uses the same relay and protocol, supports guarded scene and
component writes with Undo/Redo, and can capture Scene View, Game View, and live
Camera output to PNG.

## Important Setup Note

After adding UniBridge to your AI agent or MCP client configuration, restart the AI agent/MCP client itself.

Restarting Unity is not enough, because most MCP clients only load server configuration when the client starts.

## Access

UniBridge builds and updates are available through Patreon:

https://patreon.com/unibridge

## License

UniBridge is proprietary software distributed by Cidonix.

The public contents of this repository are provided for project information and documentation only. The UniBridge package, source code, relay binaries, and release archives are not licensed for redistribution unless a separate written agreement says otherwise.

## Created By

UniBridge is created and maintained by Cidonix.
