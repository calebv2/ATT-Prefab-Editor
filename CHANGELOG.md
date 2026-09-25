# Changelog

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
