# BetterJoyForCemu v1.5
Allows the Nintendo Switch Pro Controller to be used with the Cemu Emulator

# Changelog
### v1.5
* Added USB support
* Eliminated gyro shaking
* Improved bluetooth support
    * It's no longer necessary to completely remove and then reconnect the pro controller from your bluetooth devices when you turn it off
    * Pro controller reconnects to computer normally now
* Added 32-bit release (untested)
* Cleaned up code
### v1
* Initial release

# How to use
1. Connect pro controller via bluetooth/usb
1. Run BetterJoyForCemu.exe
    1. If the controller recongised, the first LED should light up.
1. Start Cemu and ensure CemuHook has the controller selected.
1. Enable "Also use for buttons/axes"
1. Please press enter in the console box once you're done with the program - closing it by clicking "x" does not stop the services correctly.

# Problems
If the controller does not work after restarting the exe too many times - shut down the exe, disconnect your pro controller and connect it again.

If you get weird lag/stuttering - restart your computer; or try running the program multiple times, closing it properly (by pressing enter) each time.

Feel free to open a new issue if you have any comments or questions.

# Connecting and Disconnecting the Controller
## Bluetooth Mode
Hold down the small button on the top of the controller for 5 seconds - this puts the controller into broadcasting mode.

Search for it in your bluetooth settings and pair normally.

To disconnect the controller - press down the button once. To reconnect - press any button on your controller.

## USB Mode
Plug the controller into your computer.

# Acknowledgements
A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!
