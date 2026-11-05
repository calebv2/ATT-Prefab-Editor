## 1.0.22 - Connection diagnostics and moderator access

- Add Connection diagnostics before and after connecting. Check the selected game folder for matching DLLs, identify the current helper and additional matching processes, and report local listener ownership and stale game connection files.
- Improve the owner workflow for creating and editing moderator profiles and copying access codes.
- This update replaces the launcher add-on. Player DLL 2.3.8 and server core 1.6.6 are included.

## 1.0.21 - Prefabs wording and string library spawning

- Rename the in-game prefab import headings, picker and actions to Prefabs. Existing prefab layout files stay compatible.
- Add Build > Strings for local text files, folders and clipboard data. Show each labeled variant separately, search by name and spawn only the selected variant 3 m ahead of the editor camera.
- Extract complete numeric save-string lines from mixed documents; skip prose, generators and source code. Keep authored warning notes visible beside the selected item. Library scans and payload sizes stay bounded.
- Use the existing server-authoritative paste/undo path and Build permission. Block overlapping clicks, select the spawned entity, and never retry an uncertain result automatically.
- Allow paste/replace HTTP bodies to carry strings up to the existing 200,000-character limit and give native paste requests longer to return.
- Player DLL is now 2.3.8; server core remains 1.6.6. Existing users must update the player mod after replacing the add-on. A separate server update is not needed for this release.

## 1.0.20 - In-game Console tab

- Add an in-game Console tab for single server commands and replies, with clipboard paste/copy, previous/next command history and clear output. History/output are bounded and kept in memory only.
- Show text, structured replies and native errors without rich-text interpretation. Send commands once, block overlapping submissions, and wait up to 35 seconds for a console reply without automatic retries.
- Add a separate Console moderator permission. Existing profiles do not gain it automatically. Console allows broad native commands, while owner-only access management and current permission/revocation checks remain enforced.
- Update BOTH mods: player 2.3.7 and server core 1.6.6. Restart the server after updating its DLL. Existing links, access codes and world data are preserved.

## 1.0.19 - Restore broadcast typing

- Accept Unity IMGUI keyboard events as well as Input System text callbacks in the editor input bridge. Previously the bridge discarded IMGUI keys even when the separate text callback never arrived.
- Suppress duplicate characters and edit shortcuts when both streams arrive, including across separate GUI layout/repaint calls in the same frame. Keep numeric filtering, focus isolation and keyboard cleanup.
- Add Paste message beside the broadcast field. The same compatibility fix applies to teleporter, trading, spawn-coordinate and server-setting fields using this bridge.
- Player DLL is now 2.3.6; server core stays 1.6.5. Existing users must install/update the player mod after replacing the add-on. A shared Mods folder requires both server and player stopped while replacing its DLLs.

## 1.0.18 - Local console with a separate game address

- Add an explicit local-console option for people who host the server on their playing computer but join through a public or LAN address. Join the selected game server while connecting its console through 127.0.0.1.
- Save the console/game pairing together with optional encrypted credentials. Reuse and reconnect only for the paired game address; changing servers clears the local override and credential. Moderator codes select their own destination.
- Freeze the route during connection so changing the launcher address or editing the token mid-connection cannot retarget the authenticated session.
- Add-on-only update; player DLL remains 2.3.5 and server DLL remains 1.6.5. No server restart or port forwarding is needed for this local-console option.

## 1.0.17 - Use the server already selected in Tavern Launcher

- Prefabulator follows the Client launcher server selection instead of asking users to type the same address twice. A blank launcher address means this computer (127.0.0.1).
- Moderator codes select their embedded server in the launcher before connecting. Saved owner tokens are only restored for the same server.
- Show the connected and selected server addresses immediately when they differ, with Change server / reconnect on the connection dashboard. Keep the launch-time server check to avoid editing a different server.
- Add-on-only update; player DLL remains 2.3.5 and server DLL remains 1.6.5. No server restart is needed.

## 1.0.16 - Restore the button in packaged Tavern launchers

- Remove the uuid import introduced in 1.0.15, which is unavailable in the shipped Tavern client executable and prevented the add-on from loading. Use the already available os.urandom for unique session and temporary-file identifiers.
- Test enabled-addon loading, button creation and opening the connection form with uuid imports deliberately blocked. Keep descriptor ownership and atomic writes intact.
- Add-on-only fix; player DLL remains 2.3.5 and server DLL remains 1.6.5. No server restart is needed.

## 1.0.15 - Repair the launcher-to-game connection handoff

- Re-publish a live helper's descriptor to the actual game folder before reusing a connection for launch, including sessions without saved access tokens.
- Retry editor connection now verifies/repairs the descriptor for a running helper. Write descriptors atomically, track ownership per target folder, and avoid deleting a newer session's file on shutdown.
- Monitor the bridge belonging to the window even after it closes, clearing stale CONNECTED text. Explain a mismatch between the game join address and console address instead of silently dropping the helper.
- Display the local descriptor path and helper address after connection. Live monitors can repair a missing owned descriptor without recreating the helper.
- Add-on-only fix. Player DLL remains 2.3.5; server DLL remains 1.6.5. Shared-folder and separate packages remain available.

## 1.0.14 - All-in-one shared Mods package

- Add an All-in-One-Shared-Mods ZIP alongside the separate player and server packages. Includes both DLLs in Mods, player catalog/images, and the complete launcher add-on with both DLLs in its update payload. One START HERE.txt explains shared-folder setup.
- Core 1.6.5 boots server services only in a dedicated batch process or a process hosting a world. A player process sharing the Mods folder stays idle, preventing duplicate console listeners and key provisioning.
- Player mod 2.3.5 stays idle in dedicated batch processes. Local hosting can run both halves in the same interactive process.
- The shared add-on identifies its installation mode explicitly and updates both DLLs together. The regular player package continues installing only player files, even if an old server payload remains on disk.

## 1.0.13 - Delete world-anchored fixtures and explain import failures

- Separate deletion from movement checks: a standalone prefab joint anchored to the world no longer prevents deleting its root. Keep player/held/docked/external-mount guards, all-or-nothing group preflight, confirmation and undo capture.
- Redo of deletion uses the same deletion guard, allowing restored world-anchored fixtures to be deleted again.
- Import reporting distinguishes objects that never spawned from placed objects whose rotation failed. Log each failing item, stage and reason, and retain the first error in the completion status.
- Includes server core 1.6.4 and player mod 2.3.4. Production-method regressions and game-assembly builds pass. The reported smelter prefab still needs live verification; its exact joint configuration and original import failure are not visible in the screenshots.

## 1.0.12 - Square movement handles and teleport compatibility

- Add colored XY/XZ/YZ square handles and a white center square for movement in the camera plane. Object clicks remain selection-only; handles preserve group preview, snapping, cancellation and a single server commit.
- Scope safe loading to explicit Prefabulator requests instead of wrapping other mods' ForceTeleportTo calls. Release Prefabulator pins when another teleport system requests chunks.
- Finish canceled contact preloads before unloading their chunks, so their native load acknowledgement can complete. Queue behind prior unfinished native load requests instead of throwing during platform activation.
- Retain linked-pad moves during the native client teleport cooldown, then execute once it ends and fade in only after arrival/loading readiness.
- Player mod 2.3.3 and server core 1.6.3. Update both sides; existing links are preserved. Deterministic input/teleport regressions and game-assembly builds pass; live multiplayer verification remains necessary.

## 1.0.11 - Move using gizmo handles

- Clicking a prefab or imported model selects it without starting a drag. To move it, release the mouse and drag an arrow handle. Rotation rings and box selection remain available.
- Includes player mod 2.3.2. This movement change only requires updating the player mod.

## 1.0.10 - Linked teleporter black-screen recovery

- Keep departure and destination chunks subscribed until the remote player actually arrives, rather than releasing them when the teleport request is sent.
- Ignore platform exit events during arrival; roll back transfers that do not confirm arrival within ten seconds.
- Player mod 2.3.1 releases the customization-platform fade after an in-world teleport arrives in a loaded chunk, respecting native fade locks. Runs with the editor closed.
- Includes server core 1.6.2. Update the server and affected players. Existing teleporter-links.json destinations are preserved; no relinking is required.
- Checked with chunk/arrival and fade regression harnesses and builds against the installed game assemblies. Live multiplayer verification is still required.

# Changelog

## 1.0.9 (2026-10-22)

- Fix blank owner editor tabs after a successful connection: route the in-game access lookup through the authenticated WebSocket handler. Ordinary owner editor commands continue using HTTP. Share the launcher management transport without stacking or prematurely closing handlers.

## 1.0.8 (2026-10-22)

- Restore personal moderator connections through the server's restricted WebSocket handler. Owner editor commands use HTTP; owner access management uses its authenticated WebSocket handler.
- Owners can copy a private moderator code containing the server address and a personal token. Moderators paste the code and connect; existing tab permissions, revocation, and optional encrypted credential saving apply. The owner console token is never included in these codes.

## 1.0.7 (2026-10-20)

- Linked teleporters begin loading destination chunks when touched, then wait for the platform's native finished activation before moving the player. Arrival still requires client acknowledgement, loaded server chunks and a solid floor. Server core 1.6.1; player mod remains 2.3.0.

## 1.0.6 (2026-10-19)

- Add owner-managed personal admin tokens, revocation and per-tab server permissions. Active sessions use current permissions; revocation and token replacement disconnect them. Store only token hashes. Player mod 2.3.0 and server core 1.6.0 are required together.

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
