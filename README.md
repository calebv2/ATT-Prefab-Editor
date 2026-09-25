# ATT Prefab Editor

An in-game prefab editor for A Township Tale. The client mod drives edits through the server-side editor core and private web panel.

## Downloads

Get the current files from [GitHub Releases](https://github.com/calebv2/ATT-Prefab-Editor/releases/latest):

- **Player Kit** — recommended for players; includes the client mod, install script, item previews, and optional String Workbench.
- **Server Kit** — recommended for server owners; includes the server core, private panel, prefab catalog, and Linux and Windows installers.
- **Standalone DLLs** — for manual installs when the rest of the matching kit is already installed.

Use the full kits unless you already know where each standalone DLL and its supporting files belong. The server DLL needs the panel files from the Server Kit to provide the web panel.

## Guides

- [Player controls and setup](client/USER-GUIDE.md)
- [Player Kit installation](client/README.md)
- [Server installation and remote access](server/INSTALL.md)
- [Server Kit overview](server/README.md)

## Repository layout

- `client/player-kit/mod/src/` — client C# source and build script.
- `client/player-kit/dist/PrefabEditorImages/` — item preview images used by the game mod and workbench.
- `client/player-kit/workbench/` — optional local prefab string editor.
- `server/server-kit/core/` — server C# source and build script.
- `server/server-kit/panel/` — private web panel and its console bridge.
- `server/server-kit/data/` — prefab catalog data.
- `client/` and `server/` — kit installers and user guides.

Generated DLLs, dependency folders, and complete distribution ZIPs are kept out of the source tree. Each release includes the standalone client and server DLLs plus the complete Player and Server Kit ZIPs.

## Build from source

Build scripts use the game's own MelonLoader-patched assemblies. The game is not included in this repository.

Client DLL, from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\client\player-kit\mod\build.ps1 -GamePath 'C:\Games\A Township Tale'
```

Server DLL:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\server\server-kit\core\build.ps1 -GamePath 'C:\Games\A Township Tale'
```

The client workbench needs Node.js 20 or later. From `client/player-kit/workbench`, run `npm ci` before starting it when using this source checkout; the downloadable Player Kit already includes its dependencies.

## Server access

The panel can issue server console commands and has no user login. Keep it on loopback or a private SSH/Tailscale connection, and do not expose ports 1764 or 1766 to the public internet. See [server/INSTALL.md](server/INSTALL.md) for setup details.

## License

MIT. See [LICENSE](LICENSE).
