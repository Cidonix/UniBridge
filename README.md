# UniBridge

UniBridge is a local Unity MCP bridge for AI-assisted game development.

It gives AI coding agents real tools to work inside Unity Editor projects: inspect scenes and assets, create and validate UI, edit scripts safely, work with prefabs, capture previews, author animation, physics, input actions, timeline content, and more.

## Status

UniBridge is currently distributed as a packaged Unity Editor tool through Patreon.

This repository is used as a public project page for overview, documentation links, release notes, and issue tracking. The Unity package source, release archives, and relay binaries are not published in this repository.

## What UniBridge Does

- Connects Unity Editor projects to MCP-compatible AI agents.
- Supports per-project MCP relay configuration.
- Allows one agent to work with multiple open Unity projects when each project has its own `--project-id`.
- Provides tools for scenes, assets, scripts, prefabs, UI, captures, validation, animation, rendering, physics, navigation, tilemaps, input actions, timeline, audio, VFX, and more.
- Lets agents inspect prefab and loaded-scene asset structure with compact hierarchy list/search/read, duplicate-safe indexed paths, and optional serialized field matching before they edit.
- Lets agents preflight proposed C# source changes before applying them, including syntax, API, serialized field, Unity callback, and reload-risk checks.
- Lets agents export bounded profiler marker hierarchy/top-sample data to identify likely hot runtime nodes, not just frame counters.
- Includes visual self-check tools so agents can verify Unity output before reporting success.

## Requirements

- Unity Editor 6000.0 or newer.
- An MCP-compatible AI agent or client.
- A local machine where the Unity Editor and agent can run together.

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
