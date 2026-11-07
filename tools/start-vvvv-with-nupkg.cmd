@echo off
rem Doppelklick-Starter; Argumente werden durchgereicht, z.B. start-vvvv-with-nupkg.cmd -Help "Draw a Rectangle"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-vvvv-with-nupkg.ps1" %*
if errorlevel 1 pause
