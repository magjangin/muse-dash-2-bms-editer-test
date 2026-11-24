@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_publish.ps1"
if errorlevel 1 pause
