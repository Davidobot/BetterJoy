# JoyShock timing harness

This small x64 console program measures controller callback cadence through
[JoyShockLibrary](https://github.com/JibbSmart/JoyShockLibrary), independently
of BetterJoy's HID reader, gyro processing, service IPC, and mouse output.

It prints one row per controller per second and a final aggregate. `wall_ms`
is the interval measured at the managed callback. `jsl_ms` is the `deltaTime`
reported by JoyShockLibrary. Counts such as `ge75` are the number of callback
intervals at or above that many milliseconds.

## Native dependency

Download the official
[JoyShockLibrary v3.0 archive](https://github.com/JibbSmart/JoyShockLibrary/releases/tag/v3.0)
and copy its x64 `JoyShockLibrary.dll` beside `JoyShockTiming.exe` after
building.

Hashes used for the original test:

- `JSL_3_0.zip`: `861BBC0A7805B7A57DA80EB8EC8EE007865DFC03387D7A036ADCA8EDC22DE4D7`
- x64 `JoyShockLibrary.dll`: `81A1095BC2E61CE61CED0C2D3F4CA00E37653437AAEEB266B46FCA29B7B795ED`

## Build and run

```powershell
dotnet build Tools\JoyShockTiming\JoyShockTiming.csproj -c Release
Tools\JoyShockTiming\bin\Release\net10.0\JoyShockTiming.exe --seconds 90 --output joyshock-timing.log
```

The default duration is 60 seconds. Press Ctrl+C to stop early.

## Isolated comparison with BetterJoy installed

JoyShockLibrary and BetterJoy should not read the same physical controller at
the same time. For a controlled capture:

1. Stop the BetterJoy service.
2. If HidHide cloaking is enabled, temporarily add the full path of
   `JoyShockTiming.exe` to its application list. Keep the physical controllers
   hidden so unrelated programs cannot open them.
3. Wake the controllers before starting the harness. It discovers devices only
   at startup.
4. Run the capture, then remove the temporary HidHide application entry and
   restart BetterJoy.

Nintendo controllers normally deliver one report containing three IMU samples
about every 15 ms. Occasional 30 ms intervals can be normal scheduling jitter;
repeated 75-100+ ms intervals are large enough to produce visible pointer
stalls. Compare controllers simultaneously when possible so system-wide
scheduling delays are distinguishable from device-specific delivery gaps.
