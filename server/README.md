# ATT Prefab Editor — Server Kit

The Server Kit release installs the server mod and private web panel for A Township Tale. It
supports Debian/Ubuntu Linux with or without Docker and native Windows servers. The game
folder may be named `game`, `game-server`, or `game-source`.

## Debian/Ubuntu Linux

Unzip the kit on the server and run:

```sh
bash install-linux.sh /path/to/server-folder
```

If it is a Docker Compose server, the installer uses Docker to reach the
container's loopback console. With a manually managed server, it connects to
loopback directly. Set `ATT_NATIVE=1` to choose native mode when a Compose file
is present. If more than one game folder is installed, select one with
`ATT_GAME_DIR=game-server bash install-linux.sh /path/to/server-folder`.

## Windows

Open PowerShell in this folder and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-windows.ps1
```

Paste the server game folder when prompted. The script installs the core and
panel, then creates `start-panel.bat`. See [INSTALL.md](INSTALL.md) for details.

## What is included

- The release Server Kit ZIP includes `server-kit/dist/PrefabEditorCore.dll` — ready to install
- `server-kit/panel/` and `server-kit/data/` — private web panel and prefab catalog
- `server-kit/tools/attcmd.py` — optional command-line console client
- `server-kit/core/` — C# source and build script

This source checkout does not track generated DLLs. Download the Server Kit ZIP from [Releases](https://github.com/calebv2/ATT-Prefab-Editor/releases/latest), or build `server-kit/core/PrefabEditorCore.dll` before running the installer.

## Admin and Info Boards tabs

The in-game **Admin** tab sets a custom time or noon, kicks a selected online
player after confirmation, and teleports one online player beside another.
Those commands run on the server through the panel's existing owner-authorized
console connection.

The in-game **Info boards** tab edits text and timed message rotation for a
selected `Info_Board`. It requires the separate server-only
`LiveInfoBoards.dll` mod and its `UserData/InfoBoards.json` registry. The panel writes that registry atomically;
LiveInfoBoards notices the change and applies it while the server stays up.
If you do not have `LiveInfoBoards.dll`, it is available on the ATT modding
Discord. Install it on the server and restart once so it creates the registry.

By default, the panel looks for `InfoBoards.json` beside the `UserData`
directory implied by `ATT_CONSOLE_DIR` (for example, if that is
`game-server/UserData/att_console`, it uses
`game-server/UserData/InfoBoards.json`). If the file is elsewhere, start the
panel with `INFOBOARDS_CONFIG` set to its full path. Only trusted users should
have access to the panel, since its existing console connection can administer
the server.
