# BetterJoyForCemu v4
Allows the Nintendo Switch Pro Controller to be used with the [Cemu Emulator](http://cemu.info/) and [Cemuhook](https://sshnuke.net/cemuhook/).

__Note that this program allows the Pro Controller and Joycons to be used system-wide without installation of Cemu - just follow the *How to Use* instructions until point 3__

# Changelog
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
1. Connect pro controller via bluetooth/usb
1. Install drivers
    1. Run *! Driver Install (Run as Admin).bat*
1. Run BetterJoyForCemu.exe
    1. If the controller recongised, the first LED should light up.
    1. If running for the first time, things might glitch out - just close BetterJoyForCemu normally and restart your computer for the drivers to take effect.
    1. If not, see the __Problems__ section.
1. Minimise BetterJoyForCemu.
1. Start Cemu and ensure CemuHook has the controller selected.
    1. If using Joycons, CemuHook will detect two controllers - each will give all buttons, but choosing one over the other just chooses preference for which hand to use for gyro controls.
1. Go into *Input Settings*, choose XInput as a source and assign buttons normally.
    1. If you don't want to do this for some reason, just have one input profile set up with *Wii U Gamepad* as the controller and enable "Also use for buttons/axes" under *GamePad motion source*. **This is no longer required as of version 3**
    1. Turn rumble up to 70-80% if you want rumble.
1. Please press enter in the console box once you're done with the program - closing it by clicking "x" does not stop the services correctly.

* As of version 3, you can use the pro controller and Joycons as normal xbox controllers on your PC - try it with Steam!

# App Settings
Feel free to edit *BetterJoyForCemu.exe.config* before running the program to configure it to your liking.

Current settings are:
* IP address of CemuHook motion server  *(default: 127.0.0.1)*
* Port number of CemuHook motion server *(default: 26760)*
* Rumble Period of motor in ms          *(default: 100)*
* Frequency of low rumble in Hz         *(default: 160)*
* Frequency of high rumble in Hz        *(default: 320)*
* Rumble - en/disables rumble           *(default: true)*
* Swap buttons                          *(default: false)*
  * Swaps the A-B and X-Y buttons to mimick the Xbox layout by button name, rather than physical layout 
* PurgeWhitelist                        *(default: true)*
  * Determines whether or not HidGuardian's process whitelist is purged on start-up

# Problems
__Make sure you installed the drivers!!__

If the controller does not work after restarting the exe too many times - shut down the exe, disconnect your pro controller and connect it again.

If you get weird lag/stuttering - restart your computer; or try running the program multiple times, closing it properly (by pressing enter) each time.

If something isn't working but it looks like it should be - try running the program as administrator.

__Note that for Joycons to work properly, you need a decent Bluetooth adapter that is comfortable with handling 3/4 connections at a time.__

__If while using a pro controller in USB mode, the program hangs on *Using USB*, just close the console window and open it again.__

## Make pro controller or Joycons visible to other programs again without uninstalled HidGuardian
BetterJoyForCemu automatically adds Joycons and Pro Controllers to HidGuardian's blacklist upon start-up.

However, to manually remove the devices from the blacklist, one can navigate to this page: http://localhost:26762/

## No Joycons detected
If using Bluetooth - see the "How to properly disconnect the controller" section and follow the steps listed there. Then, reconnect the controller.

If using USB - try unplugging the controller and then plugging it back in, making sure to let Windows set it up before launching the program.

## Getting stuck at "Using USB" or "Using factory.."
Close the program and then start it again. If it doesn't work, see the "No joycons detected" section and try that.

## CemuHook not recognising the controller
Make sure that CemuHook settings are at their default state, which are -

```
serverIP = 127.0.0.1
serverPort = 26760
```

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
