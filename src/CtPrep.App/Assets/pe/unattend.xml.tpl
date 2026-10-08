<?xml version="1.0" encoding="utf-8"?>
<!-- CTPrep unattend answer file (generated automatically - do not edit) -->
<!--
  CTPrep answer file; the generator tailors it to the selected image:

    - processorArchitecture follows the image architecture: amd64 for x64,
      x86 for 32-bit images. A component whose architecture does not exist
      in the image makes Windows Setup reject the whole file.

    - HideOEMRegistrationScreen and HideOnlineAccountScreens were added in
      Windows 8, so they are written only for Windows 8 and later. Windows 7
      gets exactly the settings its own automation reference lists. A node
      the target does not know makes Windows Setup reject the whole file;
      a missing one merely leaves an extra OOBE page.
-->
<unattend xmlns="urn:schemas-microsoft-com:unattend">

  <!-- Target: Windows major version {{MAJOR}}, architecture {{ARCH}} -->

  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup"
               processorArchitecture="{{ARCH}}"
               publicKeyToken="31bf3856ad364e35"
               language="neutral"
               versionScope="nonSxS"
               xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State"
               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <ComputerName>{{COMPUTER_NAME}}</ComputerName>
      <TimeZone>{{TIME_ZONE}}</TimeZone>
    </component>

    <component name="Microsoft-Windows-Deployment"
               processorArchitecture="{{ARCH}}"
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
               processorArchitecture="{{ARCH}}"
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
               processorArchitecture="{{ARCH}}"
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
