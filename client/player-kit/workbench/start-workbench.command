#!/bin/bash
# ATT String Workbench - full mode (in-game spawn/capture/replace enabled).
# Needs Node.js: https://nodejs.org (LTS). Opens http://localhost:1767
# First run on macOS: right-click this file -> Open (Gatekeeper).
cd "$(dirname "$0")"
if ! command -v node >/dev/null 2>&1; then
    echo "Node.js is not installed. Get the LTS from https://nodejs.org and run this again."
    read -r -p "press enter to close"
    exit 1
fi
if [ ! -d node_modules ]; then
    echo "First run: installing the one dependency..."
    npm install --omit=dev || { echo "npm install failed - are you online?"; read -r -p "press enter to close"; exit 1; }
fi
( sleep 1; open http://localhost:1767 ) &
node server.js
