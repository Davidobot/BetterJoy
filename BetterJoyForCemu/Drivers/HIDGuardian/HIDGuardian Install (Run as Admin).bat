@echo off 

cd /d "%~dp0\_drivers"

devcon.exe install .\HidGuardian\HidGuardian.inf Root\HidGuardian
devcon.exe classfilter HIDClass upper -HidGuardian

cd .\HidCerberus.Srv
echo Installing HidCerberus.Srv...
HidCerberus.Srv.exe install

ping 127.0.0.1 -n 2 > nul

net start "HidCerberus Service"

ping 127.0.0.1 -n 2 > nul

echo Done

ECHO.
ECHO.
pause
