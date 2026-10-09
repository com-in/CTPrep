@echo off
rem ===========================================================================
rem  CTPrep PE-side deployment script - generated automatically, do not edit.
rem
rem  Argument %%1 : staging volume, for example E: or E:\CTPREP
rem  This script runs from the RAM disk at X:\ctprep.
rem
rem  KEEP THIS FILE ASCII-ONLY. The PE console runs codepage 437; non-ASCII
rem  bytes can be decoded as cmd operators and silently corrupt the line.
rem ===========================================================================
setlocal DisableDelayedExpansion
chcp 437 >nul 2>&1
if not exist "%SystemRoot%\System32\wpeutil.exe" exit /b 2

set "WORK=%~dp0"
if "%WORK:~-1%"=="\" set "WORK=%WORK:~0,-1%"

set "STAGE=%~1"
if "%STAGE%"=="" set "STAGE=%~dp0"
if "%STAGE:~-1%"=="\" set "STAGE=%STAGE:~0,-1%"

rem Drive letter of the staging volume, used later to delete it again
set "STAGE_VOL=%STAGE:~0,1%"
set "TARGET_VOL={{TARGET_LETTER}}"
set "LOG=%WORK%\deploy.log"
set "TARGET={{TARGET_LETTER}}:"
set "INSTALL_MODE={{INSTALL_MODE}}"
set "RESULT=OK"
set "DISM=%SystemRoot%\System32\dism.exe"
set "BOOT_TARGET={{BOOT_LETTER}}:"

rem ---- Initialise the log ----
> "%LOG%" echo ============================================================
>>"%LOG%" echo  CTPrep deployment started   %DATE% %TIME%
>>"%LOG%" echo  Staging volume : %STAGE%\
>>"%LOG%" echo  Target volume  : %TARGET%
>>"%LOG%" echo  Install mode   : %INSTALL_MODE%
>>"%LOG%" echo ============================================================

call :log "[1/9] Checking the deployment environment"
call :ui 5 CHECK_ENV
if not exist "%STAGE%\{{IMAGE_FILE}}" ( set "RESULT=IMAGE_MISSING" & goto :fail )
if not exist "%WORK%\diskpart-target.txt" ( set "RESULT=DISKPART_SCRIPT_MISSING" & goto :fail )
if not exist "%DISM%" (
    set "RESULT=NO_DISM"
    goto :fail
)
if not exist "%SystemRoot%\System32\bcdboot.exe" ( set "RESULT=NO_BCDBOOT" & goto :fail )
if not exist "%SystemRoot%\System32\diskpart.exe" ( set "RESULT=NO_DISKPART" & goto :fail )
rem Validate the image before touching any partition. Keep the real exit code.
"%DISM%" /English /Get-WimInfo /WimFile:"%STAGE%\{{IMAGE_FILE}}" /Index:{{IMAGE_INDEX}} >>"%LOG%" 2>&1
if errorlevel 1 ( set "RESULT=INVALID_IMAGE_INDEX" & goto :fail )

call :log "[2/9] Resolving the staging volume drive letter"
call :ui 10 STAGING_LETTER
if "%STAGE_VOL%"=="" ( set "RESULT=STAGING_LETTER_UNKNOWN" & goto :fail )
call :log "      staging drive letter: %STAGE_VOL%"
rem Drive letters are reassigned by PE. Never assume the host letters survived.
rem Fail before formatting if a reserved destination letter is already occupied.
if exist "%TARGET%\" ( set "RESULT=TARGET_LETTER_IN_USE" & goto :fail )
if /i not "%BOOT_TARGET%"=="%TARGET%" if exist "%BOOT_TARGET%\" ( set "RESULT=BOOT_LETTER_IN_USE" & goto :fail )

call :log "[3/9] Partitioning and formatting the target disk"
call :ui 20 PARTITIONING
"%SystemRoot%\System32\diskpart.exe" /s "%WORK%\diskpart-target.txt" >>"%LOG%" 2>&1
if errorlevel 1 ( set "RESULT=DISKPART_FAILED" & goto :fail )
if not exist "%TARGET%\" ( set "RESULT=TARGET_VOLUME_MISSING" & goto :fail )
if not exist "%BOOT_TARGET%\" ( set "RESULT=BOOT_VOLUME_MISSING" & goto :fail )

{{KEEP_FILES_BLOCK}}

{{BOOT_CLEANUP_BLOCK}}

call :log "[4/9] Applying the system image (index {{IMAGE_INDEX}})"
call :ui 45 APPLYING_IMAGE
"%DISM%" /English /Apply-Image /ImageFile:"%STAGE%\{{IMAGE_FILE}}" /Index:{{IMAGE_INDEX}} /ApplyDir:%TARGET%\ >>"%LOG%" 2>&1
set "APPLY_RC=%ERRORLEVEL%"
if not "%APPLY_RC%"=="0" ( set "RESULT=APPLY_FAILED_%APPLY_RC%" & goto :fail )
if not exist "%TARGET%\Windows\System32\config\SYSTEM" ( set "RESULT=APPLIED_SYSTEM_MISSING" & goto :fail )
if not exist "%TARGET%\Windows\System32\Config\BCD-Template" ( set "RESULT=BCD_TEMPLATE_MISSING" & goto :fail )

{{DEFENDER_REMOVE}}

call :log "[5/9] Staging the unattend answer files"
call :ui 70 ANSWER_FILES
if not exist "%TARGET%\Windows\Panther" md "%TARGET%\Windows\Panther"
copy /y "%STAGE%\unattend.xml" "%TARGET%\Windows\Panther\unattend.xml" >>"%LOG%" 2>&1
if errorlevel 1 ( set "RESULT=ANSWER_COPY_FAILED" & goto :fail )
if not exist "%TARGET%\Windows\Setup\Scripts" md "%TARGET%\Windows\Setup\Scripts"
copy /y "%STAGE%\SetupComplete.cmd" "%TARGET%\Windows\Setup\Scripts\SetupComplete.cmd" >>"%LOG%" 2>&1
if errorlevel 1 ( set "RESULT=SETUP_SCRIPT_COPY_FAILED" & goto :fail )

call :log "[6/9] Copying the driver packages into the new system"
call :ui 78 DRIVERS
if exist "%STAGE%\drivers" (
    if not exist "%TARGET%\Windows\Temp\CTPrep\drivers" md "%TARGET%\Windows\Temp\CTPrep\drivers"
    xcopy /e /i /y /q "%STAGE%\drivers" "%TARGET%\Windows\Temp\CTPrep\drivers\" >>"%LOG%" 2>&1
    if errorlevel 2 ( set "RESULT=DRIVER_COPY_FAILED" & goto :fail )
)

call :log "[7/9] Rebuilding the boot configuration"
call :ui 88 BOOT_CONFIG
{{BCDBOOT}} >>"%LOG%" 2>&1
set "BOOT_RC=%ERRORLEVEL%"
>>"%LOG%" echo BCDBOOT exit=%BOOT_RC% target=%BOOT_TARGET%
if not "%BOOT_RC%"=="0" ( set "RESULT=BCDBOOT_FAILED_%BOOT_RC%" & goto :fail )

call :log "[8/9] Removing the WinPE boot entry"
call :ui 94 CLEAN_BOOT_ENTRY
{{BCD_CLEANUP}}
{{OLD_BOOT_CLEANUP}}

call :log "[9/9] Saving the log and removing the staging partition"
call :ui 98 FINALIZING
if not exist "%TARGET%\Windows\Temp\CTPrep" md "%TARGET%\Windows\Temp\CTPrep"
copy /y "%LOG%" "%TARGET%\Windows\Temp\CTPrep\deploy.log" >nul 2>&1

{{CLEANUP_STAGING_CALL}}

>>"%LOG%" echo  CTPrep deployment finished   %DATE% %TIME%
copy /y "%LOG%" "%TARGET%\Windows\Temp\CTPrep\deploy.log" >nul 2>&1

echo.
call :ui 100 DONE
echo  ============================================================
echo   Deployment finished. The computer restarts in 10 seconds.
echo   DO NOT power off.
echo  ============================================================
echo.
"%SystemRoot%\System32\ping.exe" -n 11 127.0.0.1 >nul
{{SHUTDOWN_OR_REBOOT}} >>"%LOG%" 2>&1
if errorlevel 1 ( set "RESULT=RESTART_FAILED" & goto :fail )
exit /b 0

rem ---------------------------------------------------------------------------
rem  Deletes the staging partition and returns its space to the system volume.
rem  When the staging volume lives on another disk than the target (installing
rem  to a second disk), the space is returned to the partition it was carved
rem  from instead of the target volume.
rem ---------------------------------------------------------------------------
:cleanup_staging
if not defined STAGE_VOL exit /b 0
if /i "%STAGE_VOL%"=="%TARGET_VOL%" exit /b 0
> "%WORK%\diskpart-cleanup.txt" echo select volume=%STAGE_VOL%
>>"%WORK%\diskpart-cleanup.txt" echo delete volume override
{{CLEANUP_EXTEND_LINES}}
>>"%WORK%\diskpart-cleanup.txt" echo exit
diskpart /s "%WORK%\diskpart-cleanup.txt" >>"%LOG%" 2>&1
if errorlevel 1 (
    call :log "!!!!! WARNING: failed to remove the staging volume %STAGE_VOL%; please reclaim it manually"
) else (
    call :log "      staging volume %STAGE_VOL%: removed"
)
exit /b 0

rem ---------------------------------------------------------------------------
rem  Failure handler
rem ---------------------------------------------------------------------------
:fail
call :log "!!!!! DEPLOYMENT FAILED: %RESULT% !!!!!"
call :ui -1 FAILED^|%RESULT%
if exist "%TARGET%\Windows\Temp\CTPrep" copy /y "%LOG%" "%TARGET%\Windows\Temp\CTPrep\deploy.log" >nul 2>&1
if exist "%STAGE%\" copy /y "%LOG%" "%STAGE%\deploy-failed.log" >nul 2>&1
echo.
echo  ============================================================
echo   CTPrep deployment failed: %RESULT%
echo   Full log: %LOG%
echo   The disk may already have been modified. Preserve this log.
echo   Use the maintenance menu to inspect the failure before rebooting.
echo  ============================================================
echo.
exit /b 1

rem ---------------------------------------------------------------------------
rem  Echoes a message and appends it to the log
rem ---------------------------------------------------------------------------
:log
echo %~1
>>"%LOG%" echo %~1
exit /b 0

rem ---------------------------------------------------------------------------
rem Atomic progress message consumed by the native full-screen host.
rem %%2 is an ASCII stage id (CHECK_ENV, APPLYING_IMAGE, ...), optionally
rem followed by "|detail" (used by FAILED to carry the result code).
rem The file must stay ASCII: the PE console runs codepage 437, so the native
rem host translates the id into the language chosen in the main program.
rem ---------------------------------------------------------------------------
:ui
>"%WORK%\progress.tmp" echo %~1^|%~2
move /y "%WORK%\progress.tmp" "%WORK%\progress.txt" >nul
exit /b 0
