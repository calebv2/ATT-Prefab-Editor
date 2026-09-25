@echo off
rem ATT String Workbench - full mode (in-game spawn/capture/replace enabled).
rem Needs Node.js: https://nodejs.org (LTS). Opens http://localhost:1767
cd /d "%~dp0"
set "NODE_CMD=node"
where node >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\nodejs\node.exe" (
        set "NODE_CMD=%ProgramFiles%\nodejs\node.exe"
    ) else (
        echo Node.js is not installed. Get the LTS from https://nodejs.org and run this again.
        pause
        exit /b 1
    )
)
if not exist node_modules (
    echo First run: installing the one dependency...
    call npm install --omit=dev
    if errorlevel 1 ( echo npm install failed - are you online? & pause & exit /b 1 )
)
start "" http://localhost:1767
"%NODE_CMD%" server.js
pause
