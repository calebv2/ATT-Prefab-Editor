# ATT Prefab Editor — Player Kit

The Player Kit release installs the in-game editor on your Windows gaming PC. The same DLL works with
the local panel, an SSH tunnel, or a private Tailscale Serve URL.

## Install

1. Install MelonLoader for A Township Tale if it is not already installed.
2. Open PowerShell in this folder and run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\install-windows.ps1
   ```

   Paste the game folder when asked. Or provide it directly:
   `.\install-windows.ps1 -GamePath 'C:\Games\A Township Tale'`.
3. Launch the game and press **F1** in the world.

By default, `PanelUrl` is `http://127.0.0.1:1766`. Use that for a local panel
or while an SSH tunnel is open. For Tailscale, set `PanelUrl` to your server's
private `https://…ts.net` Serve address and make sure the PC is authorized on
your tailnet. The setting is in `<game>\UserData\MelonPreferences.cfg` under
`[PrefabEditor]`; restart the game after changing it.

## Optional: String Workbench

Install Node.js 20 or later, then double-click
`player-kit/workbench/start-workbench.bat`. The local workbench opens at
`http://localhost:1767`; the package includes its JavaScript dependencies.

See [USER-GUIDE.md](USER-GUIDE.md) for controls and the connection options. In this source checkout, generated DLLs are not tracked; use the Player Kit ZIP from [Releases](https://github.com/calebv2/ATT-Prefab-Editor/releases/latest), or build the DLL with `player-kit/mod/build.ps1` first.
