#!/usr/bin/env bash
# Install Prefab Editor on native Linux or in a Docker Compose server.
# Usage: bash install-linux.sh [server-root] [game | game-server | game-source]
set -euo pipefail

KIT="${1:-$HOME/att-server-docker}"
KIT="${KIT%/}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SK="$(dirname "$HERE")"
PANEL_HOME="${ATT_PANEL_HOME:-$HOME/att-prefab-panel}"
GAME_CHOICE="${ATT_GAME_DIR:-${2:-}}"

say()  { printf '\n== %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

[ -d "$KIT" ] || fail "Server folder not found: $KIT"
[ -f "$SK/dist/PrefabEditorCore.dll" ] || fail "Missing server-kit/dist/PrefabEditorCore.dll. Re-extract the complete server kit."
[ -f "$SK/panel/server.js" ] || fail "Missing server-kit/panel/server.js. Re-extract the complete server kit."
command -v apt-get >/dev/null 2>&1 || fail "This installer currently supports Debian/Ubuntu Linux (apt-get). On Windows, use install-windows.ps1."

COMPOSE_FILE=""
DOCKER_MODE=0
DC=()
if [ "${ATT_NATIVE:-0}" != "1" ]; then
    for name in compose.yaml compose.yml docker-compose.yaml docker-compose.yml; do
        if [ -f "$KIT/$name" ]; then COMPOSE_FILE="$KIT/$name"; break; fi
    done
    if [ -n "$COMPOSE_FILE" ]; then
        if docker compose version >/dev/null 2>&1; then
            DC=(docker compose)
        elif command -v docker-compose >/dev/null 2>&1; then
            DC=(docker-compose)
        else
            fail "A Compose file exists under $KIT, but Docker Compose is not installed. Use ATT_NATIVE=1 for a manually managed server."
        fi
        DOCKER_MODE=1
    fi
fi

if [ -n "$GAME_CHOICE" ]; then
    case "$GAME_CHOICE" in
        /*) GAME_DIR="$GAME_CHOICE" ;;
        *)  GAME_DIR="$KIT/$GAME_CHOICE" ;;
    esac
    [ -d "$GAME_DIR" ] || fail "Selected game folder does not exist: $GAME_DIR"
else
    CANDIDATES=()
    for name in game-source game-server game; do
        path="$KIT/$name"
        if [ -d "$path" ] && [ -d "$path/MelonLoader" ] && [ -f "$path/version.dll" ]; then
            CANDIDATES+=("$path")
        fi
    done
    if [ -d "$KIT/MelonLoader" ] && [ -f "$KIT/version.dll" ]; then
        CANDIDATES+=("$KIT")
    fi

    if [ "${#CANDIDATES[@]}" -eq 1 ]; then
        GAME_DIR="${CANDIDATES[0]}"
    elif [ "${#CANDIDATES[@]}" -gt 1 ]; then
        GAME_DIR=""
        RUNNING_ID=""
        if [ "$DOCKER_MODE" -eq 1 ]; then
            RUNNING_ID="$(cd "$KIT" && "${DC[@]}" ps -q 2>/dev/null | head -n 1 || true)"
        fi
        if [ -n "$RUNNING_ID" ]; then
            MOUNT_SOURCE="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/game-files"}}{{.Source}}{{end}}{{end}}' "$RUNNING_ID" 2>/dev/null || true)"
            for path in "${CANDIDATES[@]}"; do
                if [ -n "$MOUNT_SOURCE" ] && [ "$(readlink -f "$path")" = "$(readlink -f "$MOUNT_SOURCE")" ]; then
                    GAME_DIR="$path"
                    break
                fi
            done
        fi
        [ -n "$GAME_DIR" ] || fail "More than one game folder is ready. Choose one explicitly, for example:\n  ATT_GAME_DIR=game-server bash install-linux.sh '$KIT'\nSupported folder names are game, game-server, and game-source."
    else
        EXISTING=()
        for name in game-source game-server game; do
            [ ! -d "$KIT/$name" ] || EXISTING+=("$name")
        done
        if [ -d "$KIT/MelonLoader" ] && [ -f "$KIT/version.dll" ]; then
            GAME_DIR="$KIT"
        elif [ "${#EXISTING[@]}" -eq 1 ]; then
            GAME_DIR="$KIT/${EXISTING[0]}"
        else
            fail "Could not choose the game folder under $KIT. Set ATT_GAME_DIR or pass game, game-server, or game-source as the second argument."
        fi
    fi
fi

GAME_DIR="$(cd "$GAME_DIR" && pwd)"
GAME_NAME="$(basename "$GAME_DIR")"
CONSOLE_DIR="${ATT_CONSOLE_DIR:-$GAME_DIR/UserData/att_console}"
LOG="${ATT_GAME_LOG:-$GAME_DIR/MelonLoader/Latest.log}"

RUNNING_ID=""
if [ "$DOCKER_MODE" -eq 1 ]; then
    RUNNING_ID="$(cd "$KIT" && "${DC[@]}" ps -q 2>/dev/null | head -n 1 || true)"
fi
if [ -n "$RUNNING_ID" ]; then
    MOUNT_SOURCE="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/game-files"}}{{.Source}}{{end}}{{end}}' "$RUNNING_ID" 2>/dev/null || true)"
    if [ -n "$MOUNT_SOURCE" ] && [ "$(readlink -f "$GAME_DIR")" != "$(readlink -f "$MOUNT_SOURCE")" ]; then
        fail "Compose mounts '$MOUNT_SOURCE' at /game-files, but the selected game folder is '$GAME_DIR'. Update Compose or pass the mounted game folder."
    fi
fi

[ -d "$GAME_DIR/MelonLoader" ] || fail "MelonLoader was not found in $GAME_DIR. Put the mod loader in the selected game folder first."
[ -f "$GAME_DIR/version.dll" ] || fail "version.dll was not found in $GAME_DIR. Confirm ATT_GAME_DIR points to the game folder."

SUDO=()
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null 2>&1 || fail "Run as root or install sudo for prerequisite installation."
    SUDO=(sudo)
fi

for clash in TavernCore.dll TavernConsole.dll; do
    if [ -f "$GAME_DIR/Mods/$clash" ]; then
        fail "$clash already exists in $GAME_DIR/Mods and provides overlapping console modules. Remove it or skip this kit."
    fi
done

say "Using game folder: $GAME_DIR"
say "Console trust directory: $CONSOLE_DIR"
say "Installing system prerequisites"
"${SUDO[@]}" apt-get update -qq >/dev/null || true
"${SUDO[@]}" apt-get install -y -qq curl ca-certificates python3-cryptography >/dev/null

NEED_RESTART=0
TARGET_DLL="$GAME_DIR/Mods/PrefabEditorCore.dll"
if ! cmp -s "$SK/dist/PrefabEditorCore.dll" "$TARGET_DLL" 2>/dev/null; then
    say "Installing PrefabEditorCore.dll"
    mkdir -p "$GAME_DIR/Mods"
    if [ -f "$TARGET_DLL" ]; then
        cp -p "$TARGET_DLL" "$TARGET_DLL.before-$(date +%Y%m%d-%H%M%S)"
    fi
    if [ -d "$CONSOLE_DIR" ]; then
        BACKUP="$HOME/att_console-backup-$(date +%Y%m%d-%H%M%S).tgz"
        tar czf "$BACKUP" -C "$(dirname "$CONSOLE_DIR")" "$(basename "$CONSOLE_DIR")"
        echo "Saved console trust backup: $BACKUP"
    fi
    cp "$SK/dist/PrefabEditorCore.dll" "$TARGET_DLL"
    NEED_RESTART=1
else
    say "PrefabEditorCore.dll already matches the package"
fi
cp "$SK/tools/attcmd.py" "$KIT/attcmd.py"

if [ "$NEED_RESTART" -eq 1 ] && [ "$DOCKER_MODE" -eq 1 ]; then
    say "Recreating the game container to load the updated mod"
    (cd "$KIT" && "${DC[@]}" up -d --force-recreate)
elif [ "$NEED_RESTART" -eq 1 ]; then
    say "The server DLL is installed. Restart your native game server, then run this installer again to finish panel setup."
    exit 0
elif [ "$DOCKER_MODE" -eq 1 ]; then
    say "Applying the existing Compose configuration"
    (cd "$KIT" && "${DC[@]}" up -d)
else
    say "Using the native game server process"
fi

say "Waiting for PrefabEditorCore to start"
booted=0
for _ in $(seq 1 60); do
    if grep -aq "PrefabEditorCore.*up:" "$LOG" 2>/dev/null; then booted=1; break; fi
    sleep 5
done
if [ "$booted" -ne 1 ]; then
    if [ "$DOCKER_MODE" -eq 1 ]; then
        fail "PrefabEditorCore did not report ready in $LOG. Check the game container log and confirm Compose mounts ./$GAME_NAME at /game-files."
    else
        fail "PrefabEditorCore did not report ready in $LOG. Check the native server log and confirm the game server restarted after the core DLL was installed."
    fi
fi
grep -a "Provision\|PrefabEditorCore.*up:\|console server on" "$LOG" | tail -4 || true

NODE_MAJOR="$(node -v 2>/dev/null | sed 's/^v\([0-9]*\).*/\1/' || true)"
if [ -z "${NODE_MAJOR:-}" ] || [ "$NODE_MAJOR" -lt 20 ]; then
    say "Installing Node.js 22"
    curl -fsSL https://deb.nodesource.com/setup_22.x | "${SUDO[@]}" bash - >/dev/null
    "${SUDO[@]}" apt-get install -y -qq nodejs >/dev/null
fi
command -v pm2 >/dev/null 2>&1 || { say "Installing PM2"; "${SUDO[@]}" npm install -g pm2 >/dev/null; }

CONTAINER_NAME=""
if [ "$DOCKER_MODE" -eq 1 ]; then
    CONTAINER_ID="$(cd "$KIT" && "${DC[@]}" ps -q | head -n 1)"
    [ -n "$CONTAINER_ID" ] || fail "Compose did not start a game container under $KIT"
    CONTAINER_NAME="$(docker inspect -f '{{.Name}}' "$CONTAINER_ID" | sed 's#^/##')"
fi

say "Installing panel files to $PANEL_HOME"
mkdir -p "$PANEL_HOME/panel" "$PANEL_HOME/data"
cp -R "$SK/panel/." "$PANEL_HOME/panel/"
cp "$SK/data/prefabs.json" "$PANEL_HOME/data/prefabs.json"
pm2 delete att-prefab-panel >/dev/null 2>&1 || true
if [ "$DOCKER_MODE" -eq 1 ]; then
    (cd "$PANEL_HOME" && ATT_CONSOLE_DIR="$CONSOLE_DIR" ATT_CONSOLE_DOCKER_CONTAINER="$CONTAINER_NAME" pm2 start panel/server.js --name att-prefab-panel --update-env >/dev/null)
else
    (cd "$PANEL_HOME" && env -u ATT_CONSOLE_DOCKER_CONTAINER ATT_CONSOLE_DIR="$CONSOLE_DIR" pm2 start panel/server.js --name att-prefab-panel --update-env >/dev/null)
fi
pm2 save >/dev/null 2>&1 || true

HEALTH=""
for _ in $(seq 1 30); do
    HEALTH="$(curl -fsS http://127.0.0.1:1766/api/health 2>/dev/null || true)"
    [[ "$HEALTH" == *'"reachable":true'* ]] && break
    sleep 2
done
if [[ "$HEALTH" == *'"reachable":true'* ]]; then
    echo "Panel and game console are responding on 127.0.0.1:1766."
else
    echo "Panel or console health check is not ready yet: ${HEALTH:-no response}"
    echo "Check with: pm2 logs att-prefab-panel"
fi

say "Install complete"
cat <<EOF
Server folder : $KIT
Game folder   : $GAME_DIR
Panel         : http://127.0.0.1:1766 (editor: /editor)
Remote access : ssh -L 1766:127.0.0.1:1766 $(whoami)@YOUR.SERVER.IP

Keep game console port 1764 private. Do not expose panel port 1766 publicly;
use an SSH tunnel or a private Tailscale Serve URL. Run 'pm2 save' after
changing the panel process. Configure 'pm2 startup' once if it should return
automatically after the host reboots.
EOF
