@echo off
REM ninjatrader-mcp — installation helper
REM Installs Node dependencies and prints the .mcp.json snippet to register
REM the server in Claude Code.

setlocal

echo.
echo ninjatrader-mcp installer
echo =========================
echo.

where node >nul 2>nul
if errorlevel 1 (
    echo ERROR: Node.js not found on PATH. Install Node 18+ from https://nodejs.org/
    exit /b 1
)

for /f "delims=" %%v in ('node -v') do set NODE_VERSION=%%v
echo Node version: %NODE_VERSION%
echo.

pushd "%~dp0.."
echo Installing dependencies (npm install)...
call npm install
if errorlevel 1 (
    echo.
    echo ERROR: npm install failed.
    popd
    exit /b 1
)

echo.
echo Running connection test...
call npm run test-connection
echo.

set "SCRIPT_DIR=%~dp0"
set "PROJECT_DIR=%SCRIPT_DIR:~0,-9%"
set "SERVER_PATH=%PROJECT_DIR%\src\server.js"
set "SERVER_PATH_FWD=%SERVER_PATH:\=/%"

echo.
echo =====================================================
echo Add this to your Claude Code .mcp.json to register:
echo =====================================================
echo {
echo   "mcpServers": {
echo     "ninjatrader": {
echo       "command": "node",
echo       "args": ["%SERVER_PATH_FWD%"],
echo       "env": {
echo         "NT_DATA_WS": "ws://localhost:8000/ws",
echo         "NT_ORDERS_WS": "ws://localhost:8002"
echo       }
echo     }
echo   }
echo }
echo =====================================================
echo.

popd
endlocal
