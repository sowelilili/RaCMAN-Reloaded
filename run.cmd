@echo off
rem Build RaCMAN Reloaded with its assets and run it. Double-click, or pass app args:
rem   run.cmd --connect 192.168.1.50
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
