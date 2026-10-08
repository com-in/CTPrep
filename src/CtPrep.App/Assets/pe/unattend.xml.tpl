<?xml version="1.0" encoding="utf-8"?>
<!-- CTPrep unattend answer file (generated automatically - do not edit) -->
<!--
  Compatible with Windows 7 / 8 / 10 / 11. Version-specific OOBE nodes are
  added or removed by the generator according to the detected image version:
  newer-only nodes (HideOnlineAccountScreens) are omitted for older builds,
  and the Windows 7-only SkipMachineOOBE / SkipUserOOBE pair is added for
  Windows 7 only. An unknown node makes Windows Setup reject the whole file.
-->
<unattend xmlns="urn:schemas-microsoft-com:unattend">

  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup"
               processorArchitecture="amd64"
               publicKeyToken="31bf3856ad364e35"
               language="neutral"
               versionScope="nonSxS"
               xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <ComputerName>{{COMPUTER_NAME}}</ComputerName>
      <TimeZone>{{TIME_ZONE}}</TimeZone>
    </component>

    <component name="Microsoft-Windows-Deployment"
               processorArchitecture="amd64"
               publicKeyToken="31bf3856ad364e35"
               language="neutral"
               versionScope="nonSxS"
               xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Description>Skip the OOBE network requirement</Description>
          <Path>reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE" /v BypassNRO /t REG_DWORD /d {{BYPASS_NRO}} /f</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>

  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core"
               processorArchitecture="amd64"
               publicKeyToken="31bf3856ad364e35"
               language="neutral"
               versionScope="nonSxS"
               xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <InputLocale>0804:00000804</InputLocale>
      <SystemLocale>zh-CN</SystemLocale>
      <UILanguage>zh-CN</UILanguage>
      <UserLocale>zh-CN</UserLocale>
    </component>

    <component name="Microsoft-Windows-Shell-Setup"
               processorArchitecture="amd64"
               publicKeyToken="31bf3856ad364e35"
               language="neutral"
               versionScope="nonSxS"
               xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <OOBE>
{{OOBE_BLOCK}}
      </OOBE>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>{{USER_NAME}}</Name>
            <DisplayName>{{USER_NAME}}</DisplayName>
            <Group>Administrators</Group>
{{PASSWORD_BLOCK}}
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
{{AUTOLOGON_BLOCK}}
      <TimeZone>{{TIME_ZONE}}</TimeZone>
    </component>
  </settings>

</unattend>
