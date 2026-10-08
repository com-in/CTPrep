@echo off
rem ===========================================================================
rem  CTPrep WinPE automatic deployment entry point.
rem
rem  winpeshl runs this file as soon as the PE has finished booting. The same
rem  file is baked into the CTPrep-built PE and injected into third-party PE
rem  images by the CTPrep application, so it must stay generic.
rem
rem  KEEP THIS FILE ASCII-ONLY. The PE console runs codepage 437; non-ASCII
rem  bytes can be decoded as cmd operators (for example a GBK trail byte 0x7C
rem  becomes a pipe) and silently corrupt the line.
rem ===========================================================================
setlocal
chcp 437 >nul 2>&1
title CTPrep

echo.
echo  ============================================================
echo   CTPrep deployment environment
echo   Automatic deployment is running - DO NOT power off
echo  ============================================================
echo.

rem ---- Initialise the PE: load drivers and assign drive letters ----
wpeinit

rem ---- Escape hatch: drop a file named ctprep.manual on any drive root to
rem ---- skip the automatic run and go straight to the rescue menu. ----
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if exist "%%d:\ctprep.manual" goto :menu
)

rem ===========================================================================
rem  Locate the staging volume: a folder holding both ctprep.marker and
rem  deploy.cmd. It sits at a drive root or one level below it, because CTPrep
rem  stages the payload into a folder named after the volume label.
rem ===========================================================================
:locate
set "CTSTAGE="

for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if not defined CTSTAGE (
        if exist "%%d:\ctprep.marker" if exist "%%d:\deploy.cmd" set "CTSTAGE=%%d:"
    )
)

for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if not defined CTSTAGE if exist "%%d:\" (
        for /d %%s in (%%d:\*) do (
            if not defined CTSTAGE if exist "%%s\ctprep.marker" if exist "%%s\deploy.cmd" set "CTSTAGE=%%s"
        )
    )
)

if not defined CTSTAGE goto :notfound

echo  [CTPrep] Staging volume : %CTSTAGE%\
echo.

rem ---- Run deploy.cmd from the RAM disk. cmd.exe re-reads a batch file line
rem ---- by line, and the staging volume is deleted at the end of the run, so
rem ---- the script must not live on the volume being removed. ----
if not exist "X:\ctprep" md "X:\ctprep"
for %%f in (deploy.cmd CTPrep.PeUi.exe diskpart-target.txt unattend.xml SetupComplete.cmd) do (
    copy /y "%CTSTAGE%\%%f" "X:\ctprep\%%f" >nul 2>&1
    if errorlevel 1 goto :copyfailed
)

rem Language picked in the main program. Optional: without it the deployment
rem UI falls back to Simplified Chinese.
copy /y "%CTSTAGE%\lang.txt" "X:\ctprep\lang.txt" >nul 2>&1

start "" /wait "X:\ctprep\CTPrep.PeUi.exe" "%CTSTAGE%"

echo.
echo  [CTPrep] The deployment script has returned. Opening the rescue menu.
goto :menu

:copyfailed
echo  [CTPrep] Failed to copy deployment files. Installation has not started.
pause
goto :menu

:notfound
echo  [CTPrep] Staging volume not found.
echo  [CTPrep] Expected a folder holding both ctprep.marker and deploy.cmd.
echo  [CTPrep] Typical causes: the volume is BitLocker-encrypted, the disk went
echo            offline, or CTPrep never staged the payload.
echo.
pause
goto :menu

rem ===========================================================================
rem  Rescue menu - manual tools for troubleshooting
rem ===========================================================================
:menu
echo.
echo  ============================================================
echo   CTPrep rescue menu
echo  ============================================================
echo    1. Run the automatic deployment again
echo    2. DiskGenius   - partitioning and data recovery
echo    3. WinNTSetup   - manual Windows installation
echo    4. Dism++       - image and system maintenance
echo    5. Command prompt
echo    6. Reboot
echo.
set "SEL="
set /p "SEL=Select [1-6]: "
if "%SEL%"=="1" goto :locate
if "%SEL%"=="2" goto :diskgenius
if "%SEL%"=="3" goto :winntsetup
if "%SEL%"=="4" goto :dismpp
if "%SEL%"=="5" goto :shell
if "%SEL%"=="6" goto :reboot
goto :menu

:diskgenius
call :tool "X:\Tools\DiskGenius" "DiskGenius.exe DiskGenius_x64.exe DiskGenius64.exe" "DiskGenius"
goto :menu

:winntsetup
call :tool "X:\Tools\WinNTSetup" "WinNTSetup_x64.exe WinNTSetup.exe WinNTSetup_x86.exe" "WinNTSetup"
goto :menu

:dismpp
call :tool "X:\Tools\Dism++" "Dism++x64.exe Dism++x86.exe Dism++.exe" "Dism++"
goto :menu

:shell
cmd /k
goto :menu

:reboot
echo  [CTPrep] Rebooting ...
wpeutil reboot

rem ---------------------------------------------------------------------------
rem  Runs the first executable that exists.
rem  %1 = tool folder, %2 = candidate file names, %3 = display name
rem ---------------------------------------------------------------------------
:tool
set "TOOLEXE="
for %%n in (%~2) do if not defined TOOLEXE if exist "%~1\%%n" set "TOOLEXE=%~1\%%n"
if defined TOOLEXE (
    echo.
    echo  [CTPrep] Starting %~3 ...
    pushd "%~1"
    start /wait "" "%TOOLEXE%"
    popd
    exit /b 0
)
echo.
echo  [CTPrep] %~3 was not found in "%~1" - this PE build did not bundle it.
pause
exit /b 1
