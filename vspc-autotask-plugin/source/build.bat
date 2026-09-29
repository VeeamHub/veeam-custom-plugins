@echo off
rem Builds, tests and packages the VSPC Autotask plugin (see build.ps1 for options).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
