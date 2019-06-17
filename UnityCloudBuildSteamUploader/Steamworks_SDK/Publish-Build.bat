echo off
set username=%1
set password=%2
set appId=%3
set appScript=%4
set appExe=%5
set useDRM=%6

if "%useDRM%"=="true" (
  echo Uploading with DRM wrap
  "%~dp0\builder\steamcmd" +login %username% %password% +drm_wrap %appId% "%appExe%" "%appExe%" drmtoolp 0 +run_app_build ..\scripts\%appScript% +quit
) else (
  echo Uploading without DRM wrap
  "%~dp0\builder\steamcmd" +login %username% %password% +run_app_build ..\scripts\%appScript% +quit
)