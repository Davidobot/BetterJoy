# Smart Auto-Calibration

How BetterJoy silently recalibrates a controller's gyro/accelerometer (and,
optionally, its stick centers) without any prompt or user action - triggered by
detecting genuine stillness in the background instead of a human running the
manual calibration wizard.

All code below is from `BetterJoyForCemu/Joycon.cs` and
`BetterJoyForCemu/CalibrationState.cs` unless noted otherwise.

## Why it exists

The manual calibration wizard has always worked fine - but only if a user
notices something is wrong, knows the wizard exists, and runs it. In practice
that means a controller with a bad or entirely absent calibration (common on
first connection) just feels janky - drifting gyro-mouse, a gyro-stick that
won't sit at rest, a physical stick reading off-center in `joy.cpl` - until
someone happens to go looking for a fix. Auto-calibration exists to close that
gap: watch for the controller sitting genuinely still, and when it does,
quietly run the same calibration math the manual wizard runs, with no
notification at all. It should just start feeling right.

It writes to the same on-disk data (`settings`, via `Config.SaveCaliData`) the
manual wizard writes to - not a session-only correction - so a controller only
ever needs to earn a good calibration once.

## The hard requirement: no cross-controller contamination

Before any of the detection logic, there's a correctness problem the manual
wizard never had to solve: it always ran exactly one calibration flow at a
time, driven by a human clicking through a dialog. Auto-calibration runs
independently, per controller, on each controller's own Poll thread. That
means a manual calibration could start on controller B while controller A's
auto-cal window is mid-flight, or two controllers could go still at the same
moment - and under no circumstance can one controller's sample data end up
published under a *different* controller's serial number.

`CalibrationState` used to have `Calibrating`/`CalibratingController` as plain
static fields anyone could write directly - safe only because there was always
exactly one caller. That's now a real claim/release API:

```csharp
public static bool TryClaim(Joycon controller) {
    lock (samplesLock) {
        if (Calibrating)
            return false;
        Calibrating = true;
        CalibratingController = controller;
        XG.Clear(); YG.Clear(); ZG.Clear();
        XA.Clear(); YA.Clear(); ZA.Clear();
        return true;
    }
}

// Human-initiated calibration always wins - unconditionally takes the claim from
// whatever (if anything) currently holds it, discarding any in-flight auto-calibration
// window rather than blocking or failing the explicit user action. The displaced side
// discovers this on its own next check (IsClaimedBy returns false) and aborts cleanly,
// publishing nothing under its own serial.
public static void ForceClaim(Joycon controller) { /* same body, unconditional */ }

public static bool IsClaimedBy(Joycon controller) {
    lock (samplesLock) {
        return Calibrating && CalibratingController == controller;
    }
}

public static void Release(Joycon controller) {
    lock (samplesLock) {
        if (CalibratingController == controller) {
            Calibrating = false;
            CalibratingController = null;
        }
    }
}
```

The manual wizard (`MainForm`/`HeadlessJoyconHost`) calls `ForceClaim` - a
human pressing "calibrate" always wins immediately, never blocked by a
background auto-cal window. Auto-calibration only ever calls the non-forcing
`TryClaim`, and re-checks `IsClaimedBy(this)` at multiple points before it's
allowed to publish - including inside `FinishCalibration` itself, as a last
line of defense independent of every caller:

```csharp
public static void FinishCalibration(Joycon controller) {
    List<int> xg, yg, zg, xa, ya, za;
    lock (samplesLock) {
        if (CalibratingController != controller)
            throw new InvalidOperationException("Calibration claim lost before publish.");
        ...
    }
    ...
}
```

Sample admission is gated the same way, so even if a caller forgot to check
`IsClaimedBy`, a preempted controller's samples simply stop being recorded:

```csharp
public static void AddSample(Joycon source, List<int> accList, List<int> gyroList,
                              int accValue, int gyroValue) {
    lock (samplesLock) {
        if (!Calibrating || source != CalibratingController)
            return;
        accList.Add(accValue);
        gyroList.Add(gyroValue);
    }
}
```

`Config.SaveCaliData`/`SaveStickCaliData` are synchronous file writes.
Auto-calibration can trigger `FinishCalibration`/`PublishStickCenter` from a
controller's own Poll thread - unlike the manual wizard, which only ever ran
on the UI thread - so both publish paths snapshot under the lock and defer the
actual write via `Task.Run`, rather than blocking HID reads/rumble/output for
the duration of a disk write.

## `TryAutoCalibrate` - the watcher

Called once per report from `DoThingsWithButtons` (not the 3x-per-report
sub-sample loop - no sub-sample responsiveness is needed for a multi-second-or-
less window, and it deliberately stays out of `ExtractIMUValues`, the exact
function a real reversed-yaw calibration bug lived in earlier in this
project's history).

### Gate 1: enabled, not already done, past the arm delay

```csharp
if (!AutoCalibrationEnabled || autoCalCompleted)
    return;
if (!Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]))
    return;

double sinceConnectSeconds = (now - autoCalConnectTimestamp) / (double)Stopwatch.Frequency;
if (sinceConnectSeconds < AutoCalArmDelaySeconds)
    return;
```

`autoCalCompleted` latches permanently true the moment one calibration
publishes for this connection - never attempted again until the next
reconnect (a fresh `Joycon` instance), so a well-calibrated controller doesn't
keep re-writing its calibration every time it sits idle. `AutoCalArmDelaySeconds`
(default 3s) is just a grace period after connect, not sensor settling time -
it covers the ordinary case of a controller sitting motionless for a couple
seconds between pairing and being picked up.

### Gate 2: the button-inactivity override

```csharp
double sinceLastButtonSeconds = (now - inactivity) / (double)Stopwatch.Frequency;
bool buttonInactiveOverride = sinceLastButtonSeconds >= AutoCalButtonInactivitySeconds;
```

`inactivity` is a per-controller timestamp already maintained elsewhere
(`ProcessButtonsAndStick`), updated only on a button down/up edge. Buttons
don't drift the way sensors do, so a long stretch (default 120s) with
literally nothing pressed is *stronger* proof of genuine stillness than
anything the sensors themselves can offer - a human can't go that long
without touching anything if they're actually holding or using the
controller. This doesn't just corroborate the sensor-based check below, it
**overrides** it entirely once satisfied: it exists specifically to let
calibration through when a controller's own drift is bad enough to otherwise
keep failing that check - which is exactly the case auto-calibration is for.

### Opening a window

```csharp
if (!autoCalWindowOpen) {
    if (!CalibrationState.TryClaim(this))
        return;

    autoCalWindowOpen = true;
    autoCalWindowStartTimestamp = now;
    autoCalGyroWindowMin = autoCalGyroWindowMax = gyroMouseSensorRate;
    autoCalAccelWindowMin = autoCalAccelWindowMax = gyroMouseSensorAccel;
    autoCalGyroFirstHalfSum = autoCalGyroSecondHalfSum = Vector3.Zero;
    autoCalGyroFirstHalfCount = autoCalGyroSecondHalfCount = 0;
    autoCalAccelFirstHalfSum = autoCalAccelSecondHalfSum = Vector3.Zero;
    autoCalAccelFirstHalfCount = autoCalAccelSecondHalfCount = 0;
    ClearAutoCalStickSamples();
    return;
}
```

There is **no instantaneous "does this already look calibrated" pre-filter**
on either sensor before opening. Earlier versions of this feature required
the reading to already look near-zero (gyro) or near-1g (accel) before even
trying - which is backwards: those are exactly the controllers that don't
need fixing. A badly miscalibrated controller can read a large constant
offset on either sensor and still be perfectly still; requiring it to already
look correct would make auto-calibration structurally unable to fix the
controllers that need it most (see "Why not just check against a threshold?"
below). A window opens unconditionally the moment a claim is available. Real
motion is caught later, once the window completes - opening during genuinely
active use costs nothing but a claim/release cycle.

### Accumulating a window

Every report while the window is open, both sensors update a running
per-axis min/max (the window's own noise "spread," used as the detection
yardstick) and accumulate into a first-half or second-half running sum,
split by elapsed time:

```csharp
autoCalGyroWindowMin = Vector3.Min(autoCalGyroWindowMin, gyroRate);
autoCalGyroWindowMax = Vector3.Max(autoCalGyroWindowMax, gyroRate);
autoCalAccelWindowMin = Vector3.Min(autoCalAccelWindowMin, accel);
autoCalAccelWindowMax = Vector3.Max(autoCalAccelWindowMax, accel);

double elapsedSeconds = (now - autoCalWindowStartTimestamp) / (double)Stopwatch.Frequency;
if (elapsedSeconds < AutoCalStillDurationSeconds / 2.0) {
    autoCalGyroFirstHalfSum += gyroRate;
    autoCalGyroFirstHalfCount++;
    autoCalAccelFirstHalfSum += accel;
    autoCalAccelFirstHalfCount++;
} else {
    autoCalGyroSecondHalfSum += gyroRate;
    autoCalGyroSecondHalfCount++;
    autoCalAccelSecondHalfSum += accel;
    autoCalAccelSecondHalfCount++;
}
```

If `AutoCalibrateStickCenter` is on, the same window also accumulates raw
stick ADC readings (see "Stick-center auto-calibration" below) - riding the
exact same claim, no separate gate.

## Why not just check against a threshold?

This is the part that went through several iterations before landing here,
and it's worth documenting *why* the obvious approach doesn't work, since
it's tempting to reintroduce.

**First attempt**: require the accelerometer magnitude to stay within some
tolerance of 1g, and the gyro rate to stay within some fixed range (e.g.
`0.3` deg/s), for the whole window. This is the same shape of check
`ApplyGyroMouseStationaryBias` (gyro-mouse's own stillness-based bias
learner, see `DOCS/GYRO-TO-STICK.md`) already uses successfully. It doesn't
transfer: that learner is correcting a *small* residual on an
*already-roughly-correct* calibration. Auto-calibration exists for
controllers where the bias might be large and is, by definition, unknown in
advance. A fixed threshold tight enough to prove genuine stillness for an
already-good controller will simply never pass for a badly miscalibrated
one - not because it isn't still, but because a badly miscalibrated
controller doesn't just shift where its reading sits, it can also scale up
the sensor's own apparent noise floor. Real hardware logs
(`gyro_mouse_debug.log`, an uncalibrated unit, genuinely untouched, connected
via `GyroMouseDebugLogging`) confirmed this directly: real per-axis noise on
that unit - while completely stationary - was already bigger than the fixed
threshold being tested.

**Second attempt**: widen the threshold proportionally to the reading's own
magnitude (`effective limit = max(absolute floor, fraction × magnitude)`, in
the style of `math.isclose`'s `abs_tol`/`rel_tol`). Better in theory, but it
didn't actually help in practice on that same real-hardware case: the
observed bias was modest (a few deg/s per axis, not "off the walls"), so a
5% relative allowance was *smaller* than the absolute floor and never
engaged - the fixed floor was still what mattered, and it was still wrong.

**The actual fix**: stop trying to pick a number that describes "how still is
still enough" in absolute or relative physical units at all. There's a
signature of a sensor bias that's true regardless of its size: **it sits on a
fixed value**. A human - or anything driven by real motion - cannot hold a
perfectly constant reading, on any axis, for the length of the window, no
matter how large or small that reading happens to be. So instead of asking
"is this number small," the check asks "is this number *constant*," measured
entirely against itself:

```csharp
Vector3 gyroFirstMean = autoCalGyroFirstHalfSum / autoCalGyroFirstHalfCount;
Vector3 gyroSecondMean = autoCalGyroSecondHalfSum / autoCalGyroSecondHalfCount;
gyroDrift = AbsVector3(gyroSecondMean - gyroFirstMean);
gyroSpread = autoCalGyroWindowMax - autoCalGyroWindowMin;
```

The window is split into a first half and a second half by time. If the
reading is genuine sensor bias, both halves land in the same place -
whatever that place is - so the difference between their means (`gyroDrift`)
stays small *relative to the window's own observed noise spread*
(`gyroSpread`, the min/max range already being tracked). Real motion, or a
deliberate attempt to hold something artificially steady, measurably drifts
the second half away from the first instead. `AutoCalTrendFraction` (default
`0.5`) is the one remaining tunable, and it's dimensionless - a ratio of the
signal to itself, not tied to deg/s, g, or any other physical unit - so the
same value should hold regardless of how badly any given controller happens
to be miscalibrated:

```csharp
passesTrendCheck =
    gyroDrift.X <= gyroSpread.X * AutoCalTrendFraction &&
    gyroDrift.Y <= gyroSpread.Y * AutoCalTrendFraction &&
    gyroDrift.Z <= gyroSpread.Z * AutoCalTrendFraction &&
    accelDrift.X <= accelSpread.X * AutoCalTrendFraction &&
    accelDrift.Y <= accelSpread.Y * AutoCalTrendFraction &&
    accelDrift.Z <= accelSpread.Z * AutoCalTrendFraction;
```

Applied per-axis, to both gyro rate and accelerometer, independently - never
combined into a single magnitude. A constant *magnitude* with a slowly
changing *direction* (a smooth hand-driven arc) would pass a magnitude-only
check while still being real motion; requiring every individual axis to hold
still closes that gap.

The evaluation only happens once, at the end of the fixed
`AutoCalStillDurationSeconds` window (default `0.5`s - see "Why such a short
window?" below), not continuously - a genuinely moving controller simply
never accumulates two halves that agree, so there's no need to poll for
early abort mid-window.

### Why such a short window?

Nothing about the trend check requires a long window to be reliable - a real
sensor bias sits flat from the very first sample, so even a short window
(confirmed on real hardware at `0.5`s, roughly 100 samples per half at
typical poll rate) cleanly separates it from real motion. Window length
mostly just trades off how fast a calibration can commit against margin for
an unlucky run of outlier samples; it isn't buying additional certainty about
constancy the way it would for a fixed-threshold check.

### Publishing

```csharp
if (!passesTrendCheck) {
    CalibrationState.Release(this);
    autoCalWindowOpen = false;
    ClearAutoCalStickSamples();
    return;
}

if (!CalibrationState.IsClaimedBy(this)) {   // belt-and-suspenders re-check
    autoCalWindowOpen = false;
    ClearAutoCalStickSamples();
    return;
}

CalibrationState.FinishCalibration(this);
getActiveData();
if (AutoCalibrateStickCenter)
    PublishAutoCalStickCenter();
autoCalWindowOpen = false;
autoCalCompleted = true;
```

`buttonInactiveOverride` (from Gate 2) short-circuits `passesTrendCheck`
straight to `true`, skipping the trend comparison entirely - by the time
nothing has been pressed for minutes, stillness is already proven on
stronger grounds than the sensors could offer.

## Stick-center auto-calibration

Separately toggleable (`AutoCalibrateStickCenter`, default `true`), riding
the exact same stillness window as gyro auto-calibration above - no separate
gate, no separate claim. It fixes a common, unrelated first-connection
symptom: a physical stick reading off-center in `joy.cpl` even though the
controller itself is fine.

Deliberately narrow in scope: it only ever replaces a stick's **center**
(`stick_cal`/`stick2_cal` index `2,3`), never its range (`0,1,4,5`). A
stillness-only pass can never produce genuine range data - that needs the
user actually rotating the stick out to its physical edges, the way the
manual wizard's own range phase does - so whatever range is currently active
(factory SPI data, or an earlier manual/auto calibration) is always kept
exactly as-is:

```csharp
public static void PublishStickCenter(string serialNumber, bool secondary,
                                      ushort[] currentCal, int centerX, int centerY) {
    ushort[] cal = new ushort[] {
        currentCal[0], currentCal[1],
        (ushort)centerX, (ushort)centerY,
        currentCal[4], currentCal[5],
    };
    ...
}
```

Needs no shared/global sample buffers the way gyro's does - raw stick
position is already a private per-controller instance field
(`stick_precal`/`stick2_precal`), so samples just accumulate directly with no
cross-controller claim or race concerns at all. The published value is the
**median** of the accumulated samples (matching the manual wizard's own
center-phase computation), not the average - robust against a stray outlier
reading skewing the result:

```csharp
private static int Median(List<int> values) {
    List<int> sorted = new List<int>(values);
    sorted.Sort();
    return sorted[sorted.Count / 2];
}
```

## Diagnostics

Two independent surfaces, since a background feature with no prompt is
otherwise a black box:

- **On-screen console**: every state transition (window opened, each abort
  reason, completion) goes through `DebugPrint(message, DebugType.IMU)` -
  visible live if `DebugType` (`App.config`) is set to `IMU` (`4`) or `ALL`
  (`1`).
- **File log**: `autocal_debug.log` under the data folder
  (`%ProgramData%\BetterJoy` or `%AppData%\BetterJoy`, whichever this install
  uses), gated by `AutoCalDebugLogging` (default `false`, like every other
  debug-logging flag). Written via the same async queue + background-writer
  pattern as the gyro-stick CSV diagnostics (`DOCS/GYRO-TO-STICK.md`), so it
  can never block a controller's own Poll thread on file I/O:

```csharp
private void AutoCalLog(string message) {
    DebugPrint(message, DebugType.IMU);
    if (!AutoCalDebugLogging)
        return;

    EnsureAutoCalDiagWriterStarted();
    autoCalDiagQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
        "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
}
```

The abort-on-drift log line includes the actual measured drift/spread values
per axis, not just "aborted" - real numbers were what actually diagnosed the
threshold-based approach's failure on real hardware, so the replacement kept
that habit.

**Both of these are field initializers**, read once when a `Joycon` instance
is constructed - editing `DebugType`/`AutoCalDebugLogging` (or any other
`AutoCal*` setting) takes effect only for controllers that connect
*afterward*. An already-connected controller needs a reconnect (or app
restart) to pick up a config change.

## Config reference (`App.config`)

| Key | Default | Meaning |
|---|---|---|
| `AutoCalibrationEnabled` | `true` | Master on/off switch |
| `AutoCalStillDurationSeconds` | `0.5` | Window length; split in half for the trend comparison |
| `AutoCalTrendFraction` | `0.5` | Max allowed first-half/second-half drift, as a fraction of the window's own spread |
| `AutoCalArmDelaySeconds` | `3` | Grace period after connect before the watcher engages at all |
| `AutoCalButtonInactivitySeconds` | `120` | No-button-press duration that overrides the trend check entirely |
| `AutoCalibrateStickCenter` | `true` | Also auto-correct stick center (never range) in the same window |
| `AutoCalDebugLogging` | `false` | Mirror every state transition to `autocal_debug.log` |

All of the above are per-`Joycon` field initializers in `Joycon.cs`, not
per-profile settings - they apply globally, the same for every controller.

## Interaction with the manual wizard

Manual calibration (`MainForm`/`HeadlessJoyconHost`) and auto-calibration
share the exact same `CalibrationState` claim, sample buffers, and
`FinishCalibration`/publish path - there is only one calibration
implementation. The only difference is what triggers it: a human working
through `StartPhase()`'s prompt sequence (`ForceClaim`, always wins), versus
`TryAutoCalibrate` noticing stillness on its own (`TryClaim`, always yields).
Manual calibration also now splits a joined L+R pair's gyro step per physical
half, matching how the stick steps already worked - a gap that existed before
auto-calibration was added and was fixed alongside it, since auto-calibration
made the same per-half claim/release machinery load-bearing for the first
time.
