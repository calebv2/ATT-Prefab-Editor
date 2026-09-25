# Server install and access

## Supported setups

- Debian/Ubuntu Linux with Docker Compose
- Debian/Ubuntu Linux with a manually managed game server
- Native Windows server

The installed game folder can be named `game`, `game-server`, or `game-source`.
The installer looks for MelonLoader and `version.dll` to identify it. If there
are several matches, select the folder your server actually starts.

## Debian/Ubuntu Linux

Unzip the kit on the server and run from the kit folder:

```sh
bash install-linux.sh /path/to/server-folder
```

For a typical `game-server` folder:

```sh
ATT_GAME_DIR=game-server bash install-linux.sh /path/to/server-folder
```

You may also pass a game folder name as the second argument:

```sh
bash install-linux.sh /path/to/server-folder game-source
```

The installer chooses Docker Compose mode when it finds a Compose file. It
checks that a running container mounts the selected game folder at
`/game-files`, installs the core, recreates the container if the DLL changed,
then installs the panel under `~/att-prefab-panel`. The panel uses `docker exec`
to reach the game container's loopback console, so port 1764 stays private.

Without a Compose file, the installer uses native mode and the console's local
loopback address. If a Compose file exists but the server is manually managed,
set `ATT_NATIVE=1`. When the core DLL changes, restart a native server and run
the installer again to finish setting up the panel.

The installer saves previous core DLL and console trust files before replacing
them. First boot provisions keys at `<game-folder>/UserData/att_console`.

## Windows

Install MelonLoader in the server game folder, unzip the kit on the Windows
machine, then open PowerShell in the kit folder:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-windows.ps1
```

Paste the server folder when prompted. If it contains `game`, `game-server`, or
`game-source`, the installer finds the game folder. Otherwise, it treats the
chosen folder as the game folder. You can provide it directly:

```powershell
.\install-windows.ps1 -GamePath 'D:\ATT\game-server'
```

The script backs up any existing core DLL, installs the new one, copies the
panel under `%USERPROFILE%\att-prefab-panel`, and creates `start-panel.bat`.
Restart the game server to load the core. After the first boot creates the
console keys, double-click `start-panel.bat` to run the panel. Keep that window
open. Install Node.js 20 or later if needed. Run the panel under the same
Windows account that can read `UserData\att_console`.

## Remote access

The panel has no login and grants full server-console access. Keep ports 1764
and 1766 closed to the public internet.

For SSH access, leave this tunnel open on your PC while you use the panel:

```sh
ssh -L 1766:127.0.0.1:1766 USER@SERVER-IP
```

Set the client's `PanelUrl` to `http://127.0.0.1:1766` (the default). For
Tailscale, configure Tailscale Serve and grant the trusted person tailnet
access. Set their `PanelUrl` to your private `https://…ts.net` Serve address;
use Tailscale ACLs to restrict who can reach it.

## Restart and check

Linux with PM2:

```sh
curl http://127.0.0.1:1766/api/health
pm2 status
pm2 logs att-prefab-panel
pm2 restart att-prefab-panel --update-env
```

Run `pm2 startup` once and follow its instructions, then `pm2 save`, to restore
the panel after a Linux host reboot. On Windows, close and reopen `start-panel.bat`
to restart the panel.
