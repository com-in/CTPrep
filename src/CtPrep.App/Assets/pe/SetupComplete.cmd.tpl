@echo off
rem ===========================================================================
rem  CTPrep first-boot finalisation.
rem
rem  Windows runs this automatically as SYSTEM after OOBE:
rem    1. installs the staged driver packages
rem    2. clears the password flags of the first account
rem    3. optionally disables Windows Defender
rem    4. removes the answer file and the temporary files
rem  The installer switches below are generic; adjust them for unusual packages.
rem
rem  KEEP THIS FILE ASCII-ONLY: it is written by the PE (codepage 437) and read
rem  by the freshly installed system, whose codepage may differ.
rem ===========================================================================
setlocal

set "LOG=%WINDIR%\Temp\CTPrep\setupcomplete.log"
set "PKG=%WINDIR%\Temp\CTPrep\drivers"
if not exist "%WINDIR%\Temp\CTPrep" md "%WINDIR%\Temp\CTPrep"

echo. >>"%LOG%"
echo ===== CTPrep SetupComplete %DATE% %TIME% ===== >>"%LOG%"

rem ---------- 1. Install .inf drivers ----------
if exist "%PKG%" (
    echo [1/5] Installing the staged driver packages >>"%LOG%"
    for /d %%d in ("%PKG%\*") do (
        echo    -- folder %%~fd >>"%LOG%"
        pnputil /add-driver "%%~fd\*.inf" /subdirs /install >>"%LOG%" 2>&1
    )
    echo    -- package root >>"%LOG%"
    pnputil /add-driver "%PKG%\*.inf" /subdirs /install >>"%LOG%" 2>&1

    rem ---------- 2. Run .exe driver installers silently ----------
    echo [2/5] Running the silent driver installers >>"%LOG%"
    for %%f in ("%PKG%\*.exe") do (
        echo    -- %%~nxf >>"%LOG%"
        "%%~ff" /S /silent /quiet /norestart >>"%LOG%" 2>&1
    )
) else (
    echo [1/5] No driver packages were staged, skipping >>"%LOG%"
)

rem ---------- 3. Keep the first account usable without a forced password change ----------
echo [3/5] Clearing the password expiry policy and the per-account flags >>"%LOG%"
rem Two different things can stop a fresh account from going straight to the
rem desktop:
rem   a) machine policy  -- the password expires, or a minimum length is enforced
rem   b) per-account     -- "user must change password at next logon" and
rem                         "a password is required"
rem The answer file has no node for either of them. net accounts covers (a);
rem (b) needs "net user <name>", and the name has to survive the trip: this
rem console runs codepage 437, so a non-ASCII account name read by cmd would be
rem mangled. PowerShell talks to WMI in Unicode instead and passes the real
rem name to net.exe, so enumerate the accounts there and never type the name.
net accounts /maxpwage:unlimited >>"%LOG%" 2>&1
net accounts /minpwlen:0 >>"%LOG%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-WmiObject Win32_UserAccount -Filter 'LocalAccount=True' | Where-Object { 'Administrator','Guest','DefaultAccount','WDAGUtilityAccount' -notcontains $_.Name } | ForEach-Object { net user $_.Name /logonpasswordchg:no /passwordreq:no | Out-Null }" >>"%LOG%" 2>&1

rem ---------- 4. Disable Windows Defender (optional) ----------
{{DEFENDER_BLOCK}}

rem ---------- 5. Clean up ----------
echo [5/5] Cleaning up the temporary files >>"%LOG%"
del /f /q "%WINDIR%\Panther\unattend.xml" >nul 2>&1
rd /s /q "%PKG%" >nul 2>&1

echo CTPrep finalisation complete >>"%LOG%"

rem Delete this script so that nothing is left behind
del /f /q "%~f0" >nul 2>&1
exit /b 0
