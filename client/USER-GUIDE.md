# Player guide

## In-game editor

Press **F1** to open or close the flat editor camera. WASD moves, Space goes
up, Left Ctrl goes down, Shift moves faster, and right mouse looks around.
The game stores settings in `<game>\UserData\MelonPreferences.cfg`.

Use **Select** to pick objects or select a group. Shift-click toggles items in
or out; drag a box over objects to select them. The selected-items list lets
you remove one item or change the primary item.

Use **Arrange** to move, rotate, resize, align, space, duplicate, or restore
transforms. Group rotation turns up to 100 items around the primary item. Use
**Build** for spawn search, saved layouts, and import/export. **World** contains
work areas, player travel, keep-player-active, and nearby outlines.

Nearby outlines can only show objects that the game has streamed to your
player. If the count is zero in a distant area, move a player there first.

## Panel URL options

The DLL never needs to be rebuilt to change servers. Set `PanelUrl` in the
Melon preferences file:

| Setup | PanelUrl |
|---|---|
| Panel on the same PC | `http://127.0.0.1:1766` |
| Remote server through an SSH tunnel | `http://127.0.0.1:1766` while the tunnel runs |
| Remote server through Tailscale Serve | The server's private `https://…ts.net` URL |

Tailscale users must be signed into an authorized tailnet. The panel gives full
server-console access, so only share it with trusted people.

## String Workbench

The optional workbench runs locally at `http://localhost:1767`. It edits prefab
save strings and can send spawn, capture, or replace requests through the same
panel connection. The package includes its JavaScript dependency, so only
Node.js 20 or later is needed.
