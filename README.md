# BetterJoyForCemu
Allows the Pro Controller to be used with the Cemu Emulator

Currently at version 1. Only for 64-bit systems for now.

# How to use
1. Connect pro controller via bluetooth
2. Run BetterJoyForCemu.exe (please don't run this without a pro controller connected)
2.5. If the controller paired correctly, the first LED should light up.
3. Start Cemu and ensure CemuHook has the controller selected.
4. Enable "Also use for buttons/axes"

# Problems
If the controller does not work after restarting the exe too many times - shut down the exe, disconnect your pro controller and connect it again.

# Connecting the Controller
Hold down the small button on the top of the controller for 5 seconds - this puts the controller into broadcasting mode.

Search for it in your bluetooth settings and pair normally.

To disconnect the controller - hold down the button once again. You'll have to remove the controller from your saved devices when you want to reconnect it.

# Acknowledgements
A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!
