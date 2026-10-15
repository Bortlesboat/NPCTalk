@echo off
rem NPCTalk CLI launcher for cmd and PowerShell (add this bin folder to PATH). Uses uv when it's installed.
where uv >nul 2>nul
if %ERRORLEVEL%==0 (
  uv run --quiet --project "%~dp0.." python -m npctalk %*
) else (
  set "PYTHONPATH=%~dp0..;%PYTHONPATH%"
  python -m npctalk %*
)
