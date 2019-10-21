# BetterJoyForCemu v5 (v6 Nightly)
Allows the Nintendo Switch Pro Controller and Joycons to be used with [Cemu](http://cemu.info/) using [Cemuhook](https://sshnuke.net/cemuhook/), [Citra](https://citra-emu.org/), and system-wide with generic XInput support.

If anyone would like to donate (for whatever reason), [you can do so here](https://www.paypal.me/DavidKhachaturov/5). 

## Nightly builds (updated 28/04/19)
Due to popular request (and my fear of releasing full builds without many new features), I will post newer "unofficial" builds here so you can test out the latest features and fixes.
* [64-bit](https://drive.google.com/open?id=1-OEP7yV9RkTOzNhjiISl_9KDxpMAu9yR)
* [32-bit](https://drive.google.com/open?id=1gFknJU6RO6SR_ddHTWxs6qdStl8gLGY0)

### Screenshots
Collapsed             |  Expanded
:-------------------------:|:-------------------------:
![Example](./Examples/example2.png)|![Example](./Examples/example3.png)

# Changelog
### v6
* Added option from starting the application minimized to tray
   * thanks [marco-calautti](https://github.com/marco-calautti)
* Fixed gyro drift on some controllers
   * thanks [brakhane](https://github.com/brakhane)
* Added option to config to remove affected devices at application shutdown
   * Should prevent any more issues of the controller being unusable after the program (even though this can be fixed if you read the README)
* Added battery level indicator by changing background colour of respective controller icon
* Fixed multi-joycon lag
   * thanks [quark-zju](https://github.com/quark-zju)
* Allow for more than one pair of joycons to be joined up
* Fixed stick casting overflow
   * thanks [idan-weizman](https://github.com/idan-weizman)
* Separated swap buttons into swapAB and swapXY; hid BetterJoy from Alt+Tab when minimised
* Added way to automatically enumerate options and enable to control them directly from the UI. Any further options can be supported.
   * Click the arrow to open config panel.
   * thanks [StarryTony](https://github.com/StarryTony)
* Fixed joycon LED bug and minimising behaviour.
   * thanks [agustinmorantes](https://github.com/agustinmorantes)
* Added option to calibrate gyroscope for 3rd (and 1st) party controllers.
   * Experimental - only supports pro controllers at the moment
   * thanks [xqdoo00o](https://github.com/xqdoo00o)
   * see _NonOriginalController_ option

### v5
* Progressive scanning
   * You can keep BetterJoyForCemu running and just connect controllers to your PC - it will detect them.
* UI rework
   * Buttons for locating controllers through vibration
   * Click on the joycon controller buttons to **toggle single/joint Joycon mode**.
* Improved rumble
* Added options to turn off HidGuardian and XInput emulation
   * Allows BetterJoy to be used exclusively for gyro (for example when using Citra + Steam)
* Improved driver install batch files (thanks BetaLeaf)
* General system stability improvements to enhance the user's experience

### v4
 * Implemented a GUI
 * Added application icon
 * Added HidGuardian support.
    * Weird jittering / Windows / steam glitching shouldn't happen anymore
    * Streamlined driver install process
    * Installs HidGuardian as a Windows process - don't move the BetterJoyForCemu folder after installation without uninstalling first.

### v3
* Added XInput Support using ViGEm.
   * No longer need to use "Also use for axes/buttons"
   * System-wide compatibility (use your Joycons with Steam, or something)
   * Requires ViGEm driver (provided in release)
* Rumble support
* Ability to rebind keys
* __v3a__
   * Added more app settings
      * Ability to disable rumble
      * Option to swap A-B and X-Y (on request of Paul)
   * CemuHook gets fed correct data about the kind of connection the controller is on
* __v3b__
  * Fixed button swapping not working on Joycons

### v2
* Added Joycon support
   * Exposes two CemuHook controllers - both have all the buttons, selecting one or the other will just pick whether to use the right or left Joycon for the motion controls
   * Make sure both controllers are connected beforehand
* Further gyro fixes
* Code cleanup

### v1.51
* Added USB support
* Eliminated gyro shaking
* Improved bluetooth support
    * It's no longer necessary to completely remove and then reconnect the pro controller from your bluetooth devices when you turn it off
    * Pro controller reconnects to computer normally now
* Added 32-bit release (untested)
* Cleaned up code
* __v1.51__
   * More gyro fixes (runs at max UDP now - as precise as one can get)
   * Especially noticeable in USB mode
### v1
* Initial release

# How to use
1. Install drivers
    1. Run *! Driver Install (Run as Admin).bat*
2. Run BetterJoyForCemu.exe
    1. If running for the first time, things might glitch out - just close BetterJoyForCemu normally and restart your computer for the drivers to take effect.
    2. If not, see the __Problems__ section.
3. Connect your controllers.
4. Start Cemu and ensure CemuHook has the controller selected.
    1. If using Joycons, CemuHook will detect two controllers - each will give all buttons, but choosing one over the other just chooses preference for which hand to use for gyro controls.
5. Go into *Input Settings*, choose XInput as a source and assign buttons normally.
    1. If you don't want to do this for some reason, just have one input profile set up with *Wii U Gamepad* as the controller and enable "Also use for buttons/axes" under *GamePad motion source*. **This is no longer required as of version 3**
    2. Turn rumble up to 70-80% if you want rumble.

* As of version 3, you can use the pro controller and Joycons as normal xbox controllers on your PC - try it with Steam!

# App Settings
Feel free to edit *BetterJoyForCemu.exe.config* before running the program to configure it to your liking.

For example, for use with [Citra and Steam simultaneously](https://community.citra-emu.org/t/improvements-to-motion-control/42125), you may consider turning off *UseHIDG* and *ShowAsXInput*.

Current settings are:
* IP address of CemuHook motion server  *(default: 127.0.0.1)*
* Port number of CemuHook motion server *(default: 26760)*
* Rumble Period of motor in ms          *(default: 300)*
* Frequency of low rumble in Hz         *(default: 20)*
* Frequency of high rumble in Hz        *(default: 400)*
* Rumble - en/disables rumble           *(default: true)*
* Swap A-B                              *(default: false)*
  * Swaps the A-B buttons to mimick the Xbox layout by button name, rather than physical layout 
* Swap X-Y                              *(default: false)*
  * Swaps the X-Y buttons to mimick the Xbox layout by button name, rather than physical layout 
* PurgeWhitelist                        *(default: true)*
  * Determines whether or not HidGuardian's process whitelist is purged on start-up
* UseHIDG                               *(default: true)*
  * Determines whether or not to use HidGuardian (improves compatibility with other programs, like Steam, when set to "false")
* ShowAsXInput                          *(default: true)*
  * Determines whether or not the program will expose detected controllers as Xbox 360 controllers
* PurgeAffectedDevices                  *(default: true)*
  * Determines whether or not the program should purge the affected devices list upon exit
  * Should prevent any more issues of the controller being unusable after the program
* NonOriginalController                 *(default: false)*
  * When "true", click the "Calibrate" button once to calibrate the gyroscope on your connect pro controller
# Problems
__Make sure you installed the drivers!!__

__Controller is not recognised after using the program__

Before uninstalling the drivers, navigate to http://localhost:26762/ and remove all the devices from the "Currently affected devices" list and then restart your computer.

__Make pro controller or Joycons visible to other programs again without uninstalled HidGuardian__

BetterJoyForCemu automatically adds Joycons and Pro Controllers to HidGuardian's blacklist upon start-up.

However, to manually remove the devices from the blacklist, one can navigate to this page: http://localhost:26762/

__Calibration Issues (ex: sticks don't have full range)__

Switch off "also use for axes/buttons" under motion settings and set the input deadzones to 0.

__Motion controls don't work/work badly__

While the program is running, turn off your controller (if USB - unplug, if BT - press the sync button) and then turn it back on (press any button).

__No Joycons detected__

If using Bluetooth - see the "How to properly disconnect the controller" section and follow the steps listed there. Then, reconnect the controller.

If using USB - try unplugging the controller and then plugging it back in, making sure to let Windows set it up before launching the program.

__Getting stuck at "Using USB" or "Using factory.."__

Close the program and then start it again. If it doesn't work, see the "No joycons detected" section and try that.

__CemuHook not recognising the controller__

Make sure that CemuHook settings are at their default state, which are -

```
serverIP = 127.0.0.1
serverPort = 26760
```

__Plugging controller into USB port does nothing__

Solution found courtresy of reddit user BFCE -  go into Device Manager, go to the Universal Serial Bus Controllers, select the properties of the eXtreme (or other USB) controllers, and toggle the setting that allows you to disable the USB ports to save power when not in use. Even some desktops have this on by default.

___Note that for Joycons to work properly, you need a decent Bluetooth adapter that is comfortable with handling 3/4 connections at a time.___

Feel free to open a new issue if you have any comments or questions.

# Connecting and Disconnecting the Controller
## Bluetooth Mode
Hold down the small button on the top of the controller for 5 seconds - this puts the controller into broadcasting mode.

Search for it in your bluetooth settings and pair normally.

To disconnect the controller - press down the button once. To reconnect - press any button on your controller.

## USB Mode
Plug the controller into your computer.

## How to properly disconnect the controller
### Windows 10
1. Go into "Bluetooth and other devices settings"
1. Under the first category "Mouse, keyboard, & pen", there should be the pro controller.
1. Click on it and a "Remove" button will be revealed.
1. Press the "Remove" button

# Acknowledgements
A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

Many thanks to [nefarius](https://github.com/nefarius/ViGEm) for his ViGEm project! Apologies and appreciation go out to [epigramx](https://github.com/epigramx), creator of *WiimoteHook*, for giving me the driver idea and for letting me keep using his installation batch script even though I took it without permission. Thanks go out to [MTCKC](https://github.com/MTCKC/ProconXInput) for inspiration and batch files.

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!

Icons (modified): "[Switch Pro Controller](https://thenounproject.com/term/nintendo-switch/930119/)", "[
Switch Detachable Controller Left](https://thenounproject.com/remsing/uploads/?i=930115)", "[Switch Detachable Controller Right](https://thenounproject.com/remsing/uploads/?i=930121)" icons by Chad Remsing from [the Noun Project](http://thenounproject.com/).
