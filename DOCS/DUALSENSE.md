# DualSense (baseline)

How BetterJoy reads a Sony DualSense (PS5 controller) and drives it through
the existing virtual Xbox 360 output. This covers buttons, D-pad, sticks, and
triggers only - gyro, adaptive triggers, lightbar, and touchpad are later
phases, not implemented yet.

All code below is from `BetterJoyForCemu/Joycon.cs` and
`BetterJoyForCemu/Program.cs` unless noted otherwise.

## Credits

The report offsets this started from were taken from
[ds4windowsapp/DS4Windows](https://github.com/ds4windowsapp/DS4Windows)
(`DS4Library/InputDevices/DualSenseDevice.cs` and `DS4Sixaxis.cs`, for the
VID/PID, initial byte offsets, and gyro/accel scaling constants) and
[nondebug/dualsense](https://github.com/nondebug/dualsense) (a community
report-format reference, cross-checked against DS4Windows for the button
bit layout). **Both agreed with each other on field order and both turned
out to be wrong about it on real hardware** - see "Why the byte layout
doesn't match either reference" below; this isn't a knock on either project,
just a real gap between documented wire format and what actually arrives
through this codebase's HID path. The architecture decision (one flagged
device kind on `Joycon` rather than a separate class) and the general
survey of the DualSense-on-Windows landscape were informed by reviewing
[Paliverse/DualSenseX](https://github.com/Paliverse/DualSenseX),
[JibbSmart/JoyShockMapper](https://github.com/JibbSmart/JoyShockMapper),
[00fox/Ds2vJoy](https://github.com/00fox/Ds2vJoy), and
[Denellyne/PCXSense](https://github.com/Denellyne/PCXSense) - all
user-supplied starting points for this work.

## Architecture: a flag on `Joycon`, not a separate class

`Joycon` has no base class or interface, and is referenced by concrete type
across `Program.cs` (the connection/pairing engine), `MainForm.cs`,
`Reassign.cs`, `ControllerMappings.cs`, `HeadlessJoyconHost.cs`,
`IJoyconHost.cs`, and `UpdServer.cs` - `Program.j` is a strictly-typed
`ConcurrentList<Joycon>`. A separate `DualSenseController` class (the
pattern DS4Windows itself uses, via an `InputDeviceFactory`) would mean
generalizing all of that: large, high-regression-risk, and unnecessary here.

`Joycon` already has this exact pattern for different physical shapes -
`isPro`/`isSnes`/`is64` coexist as flags on one class, with
`this.isPro = isPro || isSnes || is64` meaning "isPro" really means
"single-unit, non-Joy-Con-pair controller." `isDualSense` was added the
same way, folded into that same convention:

```csharp
public bool isDualSense = false;
public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2, DualSense only
```

```csharp
this.isPro = isPro || isSnes || is64 || isDualSense;
```

This reuses the entire existing Poll-thread/lifecycle/HidHide/output-wiring/
profile machinery for free - the only new code needed is DualSense-specific
report parsing and output mapping, not a new connection/lifecycle path.

## Detection: VID/PID, ahead of the generic auto-add path

`Program.cs` already has a vendor-agnostic "auto-add unrecognized
controller" path (`IsGameController`/`AutoAddControllers`) that catches any
HID gamepad, including a DualSense - but it can only *guess* a Nintendo
shape (`SController.type`: 1=Pro/2=Left Joy-Con/3=Right Joy-Con), a dead end
for a device that can be identified exactly. DualSense is checked explicitly
before that path ever runs:

```csharp
private const ushort vendor_sony = 0x054C;
private const ushort product_dualsense = 0x0CE6;
private const ushort product_dualsense_edge = 0x0DF2;
```

```csharp
bool isDualSenseDevice = enumerate.vendor_id == vendor_sony &&
    (enumerate.product_id == product_dualsense || enumerate.product_id == product_dualsense_edge);
bool validController = isDualSenseDevice ||
    ((enumerate.product_id == product_l || enumerate.product_id == product_r ||
      enumerate.product_id == product_pro || enumerate.product_id == product_snes || enumerate.product_id == product_n64) && enumerate.vendor_id == vendor_id);
```

**A real bug found during testing**: a DualSense connected *before* this
check existed could already have a stale, guessed entry (`type=1`/"Pro") in
the persisted third-party controller list. The lookup loop against that list
ran unconditionally after the check above and would silently overwrite
`thirdParty` with the stale guess - resolving `prod_id` to `product_pro`
instead of the real DualSense PID, so every `isDualSense` branch in the
whole pipeline would silently never engage. Fixed by skipping that lookup
entirely once a device is already identified as a DualSense:

```csharp
foreach (SController v in isDualSenseDevice ? Enumerable.Empty<SController>() : Program.thirdPartyCons) {
```

## Why the connection kept dropping

Two independent causes, both from unconditionally running Joy-Con-only setup
against a controller that doesn't speak that protocol:

1. **`Attach()` blocked for seconds per (re)connect attempt.** It normally
   sends several Joy-Con subcommands (enable IMU, enable rumble, enable
   input reports, home-light blink, player LED) - `Subcommand()` blocks up
   to ~1s each waiting for a reply a DualSense will never send.
2. **The read loop misread DualSense bytes as a Joy-Con report.** Joy-Con's
   fixed 49-byte report format doesn't match DualSense's 64/78-byte reports,
   and a byte the Joy-Con path treats as a report timestamp would trip an
   existing "report stream stalled" safeguard
   (`MaxConsecutiveDuplicateTimestamps`) within a few reads.

`Program.cs`'s `CleanUp()` removes a `DROPPED` controller every scan tick,
and the same raw device gets rediscovered next tick - that churn was the
observed "kept dropping." Both are fixed with a single early return, right
after `Attach()` sets its initial state:

```csharp
public int Attach() {
    state = state_.ATTACHED;

    if (isDualSense) {
        HIDapi.hid_set_nonblocking(handle, 1);
        form.AppendTextBox("DualSense attached (baseline mode).\r\n");
        return 0;
    }
    // ... existing Joy-Con body, never reached for a DualSense ...
```

No handshake is currently sent to enable "full" DualSense reports - baseline
button/stick/trigger data has arrived without one. `SetHomeLight`/
`BlinkHomeLight`/`SetLEDByPlayerNum` (called from a few places outside
`Attach()` too) are separately guarded the same way, since a DualSense has
no home-light/player-LED subcommand equivalent to send.

The read loop itself gets its own branch in `ReceiveRaw()`, using the
device's own report length (64 bytes USB / 78 Bluetooth) to size the read
and pick the Bluetooth-vs-USB byte offset - no separate transport query
needed, and more reliable than the Joy-Con-only placeholder-serial heuristic
`isUSB` otherwise depends on:

```csharp
if (isDualSense) {
    byte[] dsBuf = new byte[DualSenseMaxReportLen]; // 78
    int dsRet = HIDapi.hid_read_timeout(handle, dsBuf, new UIntPtr((uint)DualSenseMaxReportLen), 5);

    if (dsRet == 64 || dsRet == 78) {
        isUSB = dsRet == 64;
        int reportOffset = dsRet == 78 ? 1 : 0;
        ParseDualSenseReport(dsBuf, reportOffset);
        ...
    }
    ...
}
```

This entirely bypasses the duplicate-timestamp check - that code lives
further down in the Joy-Con-only tail of the method and is never reached for
a DualSense, so it isn't weakened for real Joy-Cons at all.

## Why the byte layout doesn't match either reference

Both DS4Windows and the community wire-format doc describe the same field
order: sticks, then L2/R2 analog, then a sequence counter, then the button
bytes. The first implementation used exactly that order (shifted by one
byte - see below) and was wrong on real hardware, confirmed by a raw hex
dump captured while pressing specific controls one at a time (see
`LogDualSenseRawDump`/`dualsense_raw_debug.log`, described further down):

- Pressing triggers lit up face buttons and the D-pad in the output.
- Face buttons moved the trigger axis.
- The D-pad showed "Up" held at rest with no input at all.

The dump showed the **actual** order is sticks, buttons byte 1, buttons
byte 2, the sequence counter, *then* L2/R2 analog - triggers and buttons are
swapped relative to both references, not merely shifted:

- Byte 4 (see offsets below): constant `0x08` at rest - dpad nibble `8`
  (neutral, matching the real PlayStation dpad convention: `0`-`7` are the
  eight compass directions, `8`+ is centered) with the face-button nibble
  at `0` (nothing pressed). The original code was reading a different byte
  that happened to read `0x00` at rest - `0x00 & 0x0F == 0`, which decodes
  as "Up" under the same table, exactly matching the "dpad shows Up at
  rest" symptom.
- Byte 5: toggles `0x04`/`0x08` in exact sync with L2/R2 reaching their
  digital end-of-travel click.
- Byte 6: free-runs `0x00`-`0x3C` regardless of what's pressed - the
  sequence counter, unrelated to input, skipped.
- Bytes 7/8: ramp with squeeze depth precisely when byte 5's matching click
  bit is set - the real L2/R2 analog values.

Separately, every offset needed to be **one lower** than what both
references describe: they read raw HID directly and assume byte 0 is the
report ID, but `HIDapi.hid_read_timeout` (what this codebase uses) doesn't
hand that leading byte back at all, so every field sits one byte earlier
than the reference offsets. The genuine Bluetooth-vs-USB protocol byte
(`o`, 1 or 0) is unrelated to this and still applies on top.

```csharp
private void ParseDualSenseReport(byte[] r, int o) {
    stick[0] = Math.Max(-1f, Math.Min(1f, (r[0 + o] - 128) / 127f));   // LX
    stick[1] = Math.Max(-1f, Math.Min(1f, -(r[1 + o] - 128) / 127f));  // LY
    stick2[0] = Math.Max(-1f, Math.Min(1f, (r[2 + o] - 128) / 127f));  // RX
    stick2[1] = Math.Max(-1f, Math.Min(1f, -(r[3 + o] - 128) / 127f)); // RY

    triggerVal[0] = r[7 + o]; // L2 analog
    triggerVal[1] = r[8 + o]; // R2 analog

    byte btn1 = r[4 + o]; // Triangle/Circle/Cross/Square (bits 7-4) + D-pad (bits 3-0)
    byte btn2 = r[5 + o]; // R3/L3/Options/Share/R2-click/L2-click/R1/L1
    // byte 6 (+o) is the sequence counter - skipped
    byte btn3 = r[9 + o]; // PS button (bit 0) - position NOT yet confirmed from real data
    ...
}
```

Sticks are raw `0-255`, center `~128`, mapped with a plain linear formula -
DualSense has no SPI-style factory calibration data to read the way
Joy-Con's `CenterSticks` does, and this milestone doesn't attempt to
auto-detect true center/range.

**One byte still unconfirmed**: `btn3` (PS button) never went non-zero in
the capture used to derive the rest of this layout, so its position is a
best-available inference, not a confirmed offset. Re-verify with the same
raw-dump approach if the PS button doesn't register.

**A false lead worth documenting**: after buttons/triggers were fixed, a
`joy.cpl` reading suggested L2/R2 were swapped left-for-right, which led to
a swap of `triggerVal[0]`/`[1]` and the matching click bits. Real in-game
testing (fire/ADS bindings - unambiguous, unlike the abstract Z-axis
display) showed that swap was wrong and the original assignment was
correct all along; it was reverted. `joy.cpl`'s legacy DirectInput view is
good for presence/range but has proven easy to misread for left/right
trigger identity specifically - prefer a real game's bindings to confirm
trigger handedness if it's ever in question again.

## Output: mapping into the existing Xbox 360 pipeline

Buttons, D-pad, and sticks need **no DualSense-specific code** in
`MapToXbox360Input` - `isDualSense` folds into `isPro`'s existing branch
there (button/D-pad/shoulder/stick-click mapping, keyed off the shared
`Button` enum), and `CastStickValue(float)` (already used by every other
device) converts `stick`/`stick2`'s `-1..1` floats into the output struct's
signed 16-bit axes unchanged.

Triggers are the one exception: Joy-Con/Pro have no analog trigger sensor,
so the existing `isPro` branch only ever derives a **digital** 0-or-max
value from a button bit. DualSense's L2/R2 are genuinely analog, so
`MapToXbox360Input` gets a dedicated leading branch:

```csharp
if (isDualSense) {
    output.trigger_left = input.triggerVal[0];
    output.trigger_right = input.triggerVal[1];
} else if (!is64) {
    // ... existing digital-only logic for Joy-Con/Pro, unchanged ...
```

## Diagnostics: the raw byte dump

Still present in the code (`LogDualSenseRawDump`, called from
`ReceiveRaw`'s DualSense branch), unconditional and throttled to ~4/sec.
Writes a timestamped hex dump of every raw report to
`dualsense_raw_debug.log` under the data folder (`%ProgramData%\BetterJoy`
or `%AppData%\BetterJoy`), using the same async queue + background-writer
pattern as `autocal_debug.log` (`DOCS/SMART-AUTO-CALIBRATION.md`) so it
can't block a controller's own Poll thread on file I/O. On-screen debug
output (`DebugType`) turned out not to be a reliable way to actually see
this in practice, hence a dedicated file from the start.

Marked as **temporary** in its own code comment - it exists to finish
confirming the PS button byte position and any future report-format work
(gyro, touchpad), not as a shipped feature. Remove once no longer needed.

## What's out of scope this pass

Gyro, adaptive triggers, lightbar/RGB, touchpad, and rumble output are all
unimplemented. `Program.server.NewReportIncoming` (the Cemu motion server
hook, gyro-dependent) and `out_ds4`/`MapToDualShock4Input` (DS4 output -
shares the same trigger-mapping gap Xbox 360 output had before this pass)
are deliberately not wired up for DualSense yet either - each is a
reasonable, clearly-scoped next milestone rather than something
half-implemented here.
