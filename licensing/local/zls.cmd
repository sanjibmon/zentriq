@echo off
rem Zentriq License Server (local edition). Usage: zls setup ^| start [-Background] ^| stop ^| status ^| backup ^| key-info
rem A module path inherited from PowerShell 7 breaks Windows PowerShell 5.1: start it with its own defaults.
set "PSModulePath="
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0zls.ps1" %*
