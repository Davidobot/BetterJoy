@echo off 

cd /d "%~dp0\_drivers"

cd HidCerberus.Srv
echo Uninstalling HidCerberus.Srv...

HidCerberus.Srv.exe uninstall

cd /d "%~dp0\_drivers"

echo Removing system drivers...

devcon.exe remove Root\HidGuardian
devcon.exe classfilter HIDClass upper !HidGuardian

pause 