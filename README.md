# BetterJoyForCemu v2.0
Allows the Nintendo Switch Pro Controller to be used with the [Cemu Emulator](http://cemu.info/) and [Cemuhook](https://sshnuke.net/cemuhook/).

# Changelog
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
1. Make sure CEMU has at least one input profile set up already (so that CemuHook can feed data into it)
    1. If you don't, go into _Input settings_ (under _Options_) on Cemu and set the first controller to be a Wii U Gamepad, you can leave the rest of the settings blank (or set the device to your keyboard)
1. Connect pro controller via bluetooth/usb
1. Run BetterJoyForCemu.exe
    1. If the controller recongised, the first LED should light up.
    1. If not, see the __Problems__ section.
1. Start Cemu and ensure CemuHook has the controller selected.
    1. If using Joycons, the program will show two controllers - each will give all buttons, but choosing one over the other just chooses preference for which hand to use for gyro controls.
1. Enable "Also use for buttons/axes"
1. Please press enter in the console box once you're done with the program - closing it by clicking "x" does not stop the services correctly.

# Problems
If the controller does not work after restarting the exe too many times - shut down the exe, disconnect your pro controller and connect it again.

If you get weird lag/stuttering - restart your computer; or try running the program multiple times, closing it properly (by pressing enter) each time.

## No Joycons detected
If using Bluetooth - see the "How to properly disconnect the controller" section and follow the steps listed there. Then, reconnect the controller.

If using USB - try unplugging the controller and then plugging it back in, making sure to let Windows set it up before launching the program.

## Getting stuck at "Using USB" or "Using factory.."
Close the program and then start it again. If it doesn't work, see the "No joycons detected" section and try that.

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

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!
