Driver installer provided for convenience.

FakerInput (https://github.com/Ryochan7/FakerInput) is an optional signed virtual
keyboard/mouse driver. BetterJoy2 uses its mouse, keyboard, and consumer-control interfaces when
available so gyro mouse movement, controller clicks, custom shortcuts, and media actions continue
to work in elevated applications and on the UAC desktop, where Windows blocks ordinary SendInput
injection. Its virtual keyboard also provides the Ctrl+Alt+Delete custom-bind preset. When
BetterJoy2 runs as a service, the same virtual devices work before login and after logoff. Select it in BetterJoy Setup,
or run FakerInput_Setup_0.1.1_x64.msi manually. Set UseFakerInput=false to keep using the
legacy input path even when the driver is installed. Its MIT license is included in
FakerInput-LICENSE.txt.
The bundled v0.1.1 MSI is the official Ryochan7 release (signed by Ryodigi Solutions
LLC), SHA-256 4c0aefb7340051a91d606776243298b5cd1143ef5508bbae6800c474f9ed0840.

DualSense Bluetooth microphone capture requires two optional components. VIIPER
(https://github.com/Alia5/VIIPER) presents a virtual DualSense USB audio device and
usbip-win2 (https://github.com/vadimgrn/usbip-win2) supplies its signed virtual USB host
controller. Select "Install the Bluetooth microphone backend" in BetterJoy Setup; a
restart may be requested after the driver is installed. BetterJoy2 starts the bundled
VIIPER server only while its microphone bridge needs it. Controller speaker/headset
audio remains available when this optional backend is not installed.

The bundled VIIPER 0.1.0 executable is distributed under GPL-3.0; its license is in
VIIPER-LICENSE.txt. The bundled usbip-win2 0.9.7.7 installer and license are from the
hbashton/VIIPER-compatible release used by hbashton/DS4Windows. Their SHA-256 hashes are:
VIIPER-0.1.0-x64.exe  AD14F2C9048D61B3447F2F79D7A122EDEA81E5DB52A1AC803D294E5BC9CD2324
USBip-0.9.7.7-x64.exe 51620FA5F9F8BE5932BC9D786DEEE557CE06D5407A99CAB490DCFAC71F185FEA

ViGEmBus was archived by its author in November 2023 (trademark dispute, unrelated to
code quality) - v1.22.0, bundled here, is the final release and will not receive further
updates: https://github.com/nefarius/ViGEmBus/releases

If you're on Win7, please read the instructions on the page.

HidHide (https://github.com/nefarius/HidHide) hides the Pro Controller/Joycons from
other programs entirely (they won't even see the device), which avoids conflicts with
programs like Steam that fight BetterJoy2 over the raw HID device the moment they start.
It's optional - enable it with the "UseHidHide" setting (off by default) and run
HidHide_1.5.230_x64.exe in this folder.

Note: this replaces HidGuardian, which used to serve the same purpose. HidGuardian was
archived/deprecated by its own author, superseded by HidHide, and is no longer bundled
or supported here.
