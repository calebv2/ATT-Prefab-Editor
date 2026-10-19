# Changelog

## 1.0.6 (2026-10-19)

- Add owner-managed personal admin tokens, revocation and per-tab server permissions. Active sessions use current permissions; revocation and token replacement disconnect them. Store only token hashes. Player mod 2.3.0 and server core 1.6.0 are required together.

## Prefabulator 1.0.5 — 2026-10-19

- Add Server Settings with 11 gameplay toggles and numeric settings.
- Restore Admin player kick/kill controls; add broadcasts and persistent server spawn with default restore.
- Remember the server address and optionally save the console token with Windows account encryption for automatic connection.
- Connect through Tavern Launcher; remove legacy tunnel setup.
- Player mod 2.2.0; server core 1.5.2.


## Unreleased — 2026-10-19

- Package 1.0.3 adds Prefabulator directly to the main client launcher. Its dedicated connection window reuses Tavern's console authentication and automatically starts the in-game editor bridge. It contains no launcher-side editing tools and no longer requires opening TavernKeeper. Server installation remains mod-only.

- Package 1.0.2 separates Player-Addon-and-Mod from Server-Mod-Only. The server ZIP contains Mods/PrefabEditorCore.dll, one instruction file, and the license; no launcher add-on is required on the server. Updated package names, launcher labels, and setup guidance.

- Tavern add-on 1.0.1 / server core 1.5.1: preload silently on platform contact and teleport on the first destination-ready update. Remove the loading message and artificial 0.2-second settling delay; retain client/server readiness and floor checks.

- Add Tavern Launcher player/server add-ons. The TavernKeeper Prefab Editor tab shares its authenticated console session, installs player files, checks server capabilities, and starts the bundled loopback panel/workbench without a tunnel or separate Node installation. Server setup installs the core DLL; Info Board reads/updates travel through console commands to the server. Remove the in-game Connection tab. Console timeouts are errors and mutations are never automatically retried. Player mod 2.1.0, server core 1.5.0.

- Start linked customization-platform travel on the native step-on/pressing message. Teleport automatically as soon as the destination is ready, without waiting for the platform countdown; suppress late native activation to prevent a duplicate trip. Stepping off while loading cancels without moving the player. Server Kit r17; no companion-client update required.

- Delay server ForceTeleportTo calls (including linked customization platforms and editor player teleports) until the owning client's native chunk acknowledgement, server chunk readiness, and a stable five-point floor check. Keep source chunks subscribed during loading; cancel after 30 seconds and show a message. Loading position stays pinned to the destination, following Housing's travel approach. Unlike Housing's companion-client protocol, this uses the base game's chunk acknowledgement and does not verify floor colliders on the client. Client-only native TeleportationPoint travel is unchanged.

## Unreleased — 2026-10-18

- Move trade editing into a dedicated Trading tab; show status/reload above the form, allow new-offer entry after failed reads, and support older panels through their existing console endpoint.

- Default new trade offers to one item per purchase and one stock. Add stock to the current server quantity without changing price or purchase size, subject to native capacity and number limits.

- Select a trade deck to edit its item, coin price, items per purchase and total stock using native server-side trade/dock saving. Existing different stock must be removed first; failed updates restore previous settings.

- Wire teleporter destination typing to the game's new Input System, including selection, deletion and clipboard shortcuts; add a Paste coordinates button.

- Support the Teleporter To Customization platform as well as native teleport points. Linked platforms use the chosen destination through the native teleport/loading path; links persist in server UserData/PrefabEditor/teleporter-links.json. Relink after moving a platform.

- Teleporter linking now supports older panels through their existing console endpoint and explains when the game server needs an updated core DLL.

## Unreleased — 2026-10-17

- Added automatic SSH provisioning as a choice alongside existing SSH, Tailscale and same-PC setup: player request generation, dedicated server accounts, restricted forwarding, pinned host keys, validation and configuration backups.

- Restored selected-player keep-alive movement every five minutes while the editor is open, without changing homes.
- Added a dedicated Teleporter tab to link an existing selected teleporter to a coordinate destination (default -596.447,275.657,240.869), using native chunk streaming and world saving.

- Added guided Windows player setup, game-folder detection, connection-file import, SSH tunnel launch, readiness checks, and a desktop Connect shortcut.
- Added an in-game Connection tab and separate saved connection settings without overwriting Melon preferences.
- Added Windows/Linux server connection helpers for private Tailscale, SSH, and same-PC profiles, with simple START HERE guides.

## Unreleased — 2026-10-16

- Replaced Return trips with one-click teleports to a saved area/camera, to a selected player, or from that player to the local avatar. Saved homes restore automatically, with recovery for old trips or failed restores.
- Allow large addressable network prefabs through the collider-size filter for click, box, and nearby selection.

## 2.0.8 — 2026-09-29 (local test build)

- Reduced Admin to time-of-day controls and removed the shared custom-model / GLB upload feature and sync service.
- Added a spawn-count setting for batches of up to 100 items per camera spawn or placement click.
- Multi-item spawns now use the game's native exact-position counted command, allowing game-managed pouch grouping without a mapping for every item type.

## 2.0.7 — 2026-09-27 (local test build)

- Added quiet detached-camera watch for online players. It requests validated map chunks, follows the selected player's server-reported position, and leaves the local avatar in place.
- Added confirmed player kill, temporary or permanent account-ID blacklist, blacklist refresh, and unban actions to Admin.
- The server persists its mod-managed blacklist at `UserData/PrefabEditor/blacklist.json` and disconnects listed accounts when they join.
- Added server panel endpoints and commands for kill, blacklist, list, and unban.
- Added server-stored GLB assets and persistent custom-model placements, plus a separate read-only viewer sync service.
- Added a Build-tab custom model library for upload, placement, transform, duplicate, and delete; clients cache and render server placements.

## 2.0.6 — 2026-09-26

- Added an Admin tab for setting server time, kicking players, and teleporting players, with confirmation for disruptive actions.
- Added an Info Boards tab to edit board text and timed message rotation through the server panel and LiveInfoBoards registry.
- Improved Info Board recognition and made long text fields expand and scroll independently.
- Hour-only time values are accepted; `12` sets noon.

## 2.0.5 — 2026-09-25

- Scaling now previews and applies to all selected items together; the replacements are reselected afterward.
- Group scaling uses the existing server-authoritative scale operation for each selected item.


## 2.0.4 — 2026-09-25

- The in-game spawn picker now shows every live server spawnable prefab when the search box is empty, with the list scrollable beyond the first 11 entries.
- The packaged-catalog fallback also requests the complete result range.


## 2.0.3 — 2026-09-25

- The server panel's prefab picker now shows the full catalog when search is empty and scrolls through all results.
- The “spawnable only” filter is optional and off by default; non-spawnable catalog entries are visibly marked.


## 2.0.2 — 2026-09-25

- Refreshed both prefab catalogs from the connected server's `spawn list` snapshot.
- Added 8 catalog entries and updated spawnability from the current server; older reference entries remain available.
- Snapshot contains 1,119 unique spawnable hashes (1,121 catalog entries, including two existing aliases). Client and server DLLs are unchanged.


## 2.0.1 — 2026-09-25

- Updated server console auth to let an installed Harmony auth prefix validate its own non-RS256 token, including TavernLib's HS256 owner token.
- Kept the Prefab Editor's RS256 signature, expiry, and allowlist validation for its own console tokens.
- Rebuilt the server core and refreshed both downloadable kits. The client mod is unchanged from 2.0.0.

## 2.0.0 — 2026-09-24

- Published the player and server kits with Windows and Linux installation instructions.
- Added selection, group transforms, saved layouts, work areas, player travel, and the optional local String Workbench.
- Fixed JSON import validation for comma-separated prefab save strings and expanded the import limit to 1,000 entries.
