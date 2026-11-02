# ATT Prefab Editor / Prefabulator

Prefabulator connects the in-game editor through Tavern Launcher and the server remote console.
All editing happens in game (F1). Keep Tavern Launcher open while playing.

- Players: use Player-Addon-and-Mod. Follow its single START HERE.txt.
- Server owners: use Server-Mod-Only. Copy the DLL to the server game Mods folder and restart.
- Shared server/player game folder: use All-in-One-Shared-Mods. It includes both DLLs, player assets and the launcher add-on. Follow its single START HERE.txt.
- The address is remembered. Saving the access token is optional. Windows encrypts saved tokens for your account; they enable auto-connect when launching that server through Tavern.

Source instructions: [player](tavern-addon/player-instructions.txt), [server](tavern-addon/server-instructions.txt), [shared Mods](tavern-addon/shared-instructions.txt).
Build mods using their build.ps1 scripts with -GamePath. Package using tools/package_tavern_addon.py --output <folder>.

Owners can use Manage Admin Access in Prefabulator to issue a personal token to each admin, choose allowed tabs, change permissions or revoke access. Update both packages to 1.0.6 first. All tabs never grants access management.
