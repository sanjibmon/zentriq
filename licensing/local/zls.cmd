@echo off
rem Zentriq License Server (local edition). Usage: zls setup ^| start [-Background] ^| stop ^| status ^| backup ^| key-info
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0zls.ps1" %*
