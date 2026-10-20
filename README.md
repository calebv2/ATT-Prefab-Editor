# ATT Prefab Editor / Prefabulator

Prefabulator connects the in-game editor through Tavern Launcher and the server remote console.
All editing happens in game (F1). Keep Tavern Launcher open while playing.

- Players: use Player-Addon-and-Mod. Follow its single START HERE.txt.
- Server owners: use Server-Mod-Only. Copy the DLL to the server game Mods folder and restart.
- The address is remembered. Saving the access token is optional. Windows encrypts saved tokens for your account; they enable auto-connect when launching that server through Tavern.

Source instructions: [player](tavern-addon/player-instructions.txt), [server](tavern-addon/server-instructions.txt).
Build mods using their build.ps1 scripts with -GamePath. Package using tools/package_tavern_addon.py --output <folder>.

## Build from source

Use Windows, Python 3, Node.js 24 with npm, .NET Framework 4.x, and your own MelonLoader-patched game installation. Game assemblies are read locally and are not included in this repository.

```powershell
powershell -ExecutionPolicy Bypass -File client/player-kit/mod/build.ps1 -GamePath "C:/path/to/client-game"
powershell -ExecutionPolicy Bypass -File server/server-kit/core/build.ps1 -GamePath "C:/path/to/server-game"
npm ci --prefix client/player-kit/workbench
python tools/package_tavern_addon.py --output release --node "C:/Program Files/nodejs/node.exe"
```

The packaging script includes the staged mods, launcher add-on, local editor runtime, required assets and third-party license notices. Player and server ZIPs each contain one START HERE.txt. Runtime executables and installed dependencies belong in release assets, not source commits.

Owners can use Manage Admin Access in Prefabulator to issue personal admin tokens, select allowed tabs, change permissions or revoke access. Update both packages to 1.0.6 first. All tabs never grants access management.

Version 1.0.7 fixes linked teleporter timing: destination chunks start loading on contact, and travel waits for the native platform animation to finish. The server mod update is required; player mod remains 2.3.0.
