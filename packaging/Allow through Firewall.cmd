@echo off
rem Double-click to let RaCMAN Reloaded receive the PS3's telemetry over UDP.
rem This runs windows-firewall.ps1, which will ask for administrator approval.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows-firewall.ps1"
