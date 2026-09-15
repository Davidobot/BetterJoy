# Gyro-to-Stick

How BetterJoy turns physical controller rotation into virtual left/right analog
stick output. This covers the default (Rate mode), the two additional modes added
later (Absolute tilt, Hybrid), the axis-source/invert system, ratcheting, and the
recenter mechanics they all share with gyro-mouse.

All code below is from `BetterJoyForCemu/Joycon.cs` unless noted otherwise.

## Why it's not "just another gyro-to-stick"

Most gyro-aiming implementations treat gyro-to-mouse and gyro-to-stick as separate
features with separate math. BetterJoy's default (Rate) mode doesn't - it's the
same **Player Space** orientation tracker gyro-mouse uses, aimed at a different
output. Two instances of the same class:

```csharp
// gyro-mouse
private readonly GyroMousePlayerSpace gyroMousePlayerSpace = new GyroMousePlayerSpace();
// gyro-stick - same class, independent state
private readonly GyroMousePlayerSpace gyroStickPlayerSpace = new GyroMousePlayerSpace();
```

Both consume the same canonical, Y-up sensor frame (`gyroMouseSensorRate`/
`gyroMouseSensorAccel`, built once per report by `UpdateCanonicalGyroMouseImu`) and
both integrate all **three** IMU sub-samples per report (Nintendo reports arrive in
batches of 3, each representing a real 5 ms tick) instead of snapshotting once
every ~15 ms report. That's the difference between smooth continuous tracking and
a slightly choppy, stepped feel - and it's why gyro-to-stick output can read as
"bordering on gyro-mouse" quality rather than a cheaper approximation.

## Player Space: the core technique

`GyroMousePlayerSpace.Map()` (`GyroMousePlayerSpace.cs`) is adapted from Julian
"Jibb" Smart's `GamepadMotionHelpers` reference implementation. The key idea:
**yaw is measured as rotation around true gravity-up**, not around whatever axis
happens to be "up" for the controller's current tilt. Pitch uses the controller's
own local pitch axis, but projected onto the plane perpendicular to gravity.

```csharp
public void Map(Vector3 gyroDegPerSec, float deltaTime, out float yawRate,
                out float pitchRate, out float rollRadians) {
    Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
        ? Vector3.Normalize(gravity)
        : new Vector3(0.0f, -1.0f, 0.0f);

    // Horizontal motion is rotation around gravity. Vertical motion uses the
    // controller's LOCAL pitch axis (+X) projected onto the plane perpendicular
    // to gravity. This projection is important: choosing an arbitrary horizontal
    // axis such as Z x gravity follows wrist roll past the side-on pose and can
    // redirect compound roll/yaw motion into one-way mouse Y.
    yawRate = Vector3.Dot(gravityDirection, gyroDegPerSec);

    float gravityAlongLocalPitch = gravityDirection.X;
    Vector3 worldPitchAxis = new Vector3(1.0f, 0.0f, 0.0f) -
                             gravityDirection * gravityAlongLocalPitch;
    float pitchAxisLengthSquared = worldPitchAxis.LengthSquared();
    if (pitchAxisLengthSquared > 0.0f) {
        worldPitchAxis /= (float)Math.Sqrt(pitchAxisLengthSquared);
        float flatness = Math.Abs(gravityDirection.Y);
        float upness = Math.Abs(gravityDirection.Z);
        float sideReduction = Clamp01(
            (Math.Max(flatness, upness) - WorldPitchSideReductionThreshold) /
            WorldPitchSideReductionThreshold);
        pitchRate = sideReduction * Vector3.Dot(worldPitchAxis, gyroDegPerSec);
    } else {
        pitchRate = 0.0f;
    }

    ApplyEvenYawLeakCorrection(ref pitchRate, yawRate, deltaTime);

    // Diagnostic only: zero while the canonical controller frame is flat.
    rollRadians = (float)Math.Atan2(gravityDirection.X, -gravityDirection.Y);
}
```

Practical effect: turning left/right stays consistent no matter how the
controller happens to be tilted at the moment - no distortion from incidental
wrist roll mid-turn, which is the thing that makes naive local-axis gyro
implementations feel swimmy. `rollRadians` is a genuine **absolute angle**
(gravity-referenced bank, always zero at physical level), not a rate - that
distinction matters later for the roll axis source.

`Update()` (called every sub-sample, unconditionally, even while gyro-stick is
inactive) keeps the gravity estimate current via accelerometer fusion, so
reactivating gyro-stick after it's been idle doesn't start from a stale reference
frame. Only `Map()`'s gyro-rate terms actually produce output.

## The two independent code paths

Gyro-to-stick has always had two separate implementations, selected once
globally by `UseFilteredIMU` in `App.config` (default `true`):

| | Filtered (`UseFilteredIMU=true`) | Raw (`UseFilteredIMU=false`) |
|---|---|---|
| Reference frame | Gravity (Player Space) | None - raw local axes |
| Modes | Rate / Absolute tilt / Hybrid | Rate only |
| Axis source (yaw/roll) | Selectable | Always yaw |
| Invert | Supported | Not supported |
| Sample rate | 3 sub-samples/report | 1/report |

Raw mode is the historical fallback and intentionally was **not** touched when
Absolute tilt/Hybrid/axis-source/invert were added - it has no gravity or AHRS
reference to build any of that on, and stays exactly as it always behaved:

```csharp
if (!UseFilteredIMU &&
    (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport)) {
    float dx = 0.0f;
    float dy = 0.0f;
    if (!gyroStickRatcheted) {
        dx = GyroStickSensitivityX * (gyr_g.Z * dt); // yaw
        dy = -GyroStickSensitivityY * (gyr_g.Y * dt); // pitch
    }
    ...
    if (gyroLeftStickActiveThisReport)
        ApplyGyroToStick(stick, dx, dy);
    if (gyroRightStickActiveThisReport)
        ApplyGyroToStick(stick2, dx, dy);
    ...
}
```

Everything below describes the filtered path, in `ProcessGyroStickSample`.

## Rate mode (the default)

Stick deflection continuously reflects **current angular velocity** - twist fast,
get a hard deflection; twist gently, get a soft one. No deadzone, no
quantization, nothing artificial between "how fast is the wrist actually moving"
and "how far is the stick deflected." That's the mouse-like feel: a continuous,
proportional signal, just clamped to `[-1, 1]` instead of mapped to screen pixels.

Per sub-sample, the mapped rate accumulates into a pending delta (independently
per stick, since axis source can differ - see below):

```csharp
if (gyroLeftStickActiveThisReport) {
    float xRate = GyroStickAxisXLeft == "roll" ? stickGyroRate.Z : yawRate;
    pendingGyroStickDxLeft += GyroStickSensitivityX * xRate *
                              subSamplePeriod * degreesToRadians;
    pendingGyroStickDyLeft += GyroStickSensitivityY * pitchRate *
                              subSamplePeriod * degreesToRadians;
}
```

At the report boundary (the 3rd sub-sample), the accumulated delta is applied
once and reset:

```csharp
private void ApplyGyroToStick(float[] controlStick, float dx, float dy) {
    float stickReduction = EffectiveGyroStickReduction();
    controlStick[0] = Math.Max(-1.0f, Math.Min(1.0f,
        controlStick[0] / stickReduction + dx));
    controlStick[1] = Math.Max(-1.0f, Math.Min(1.0f,
        controlStick[1] / stickReduction + dy));
}
```

`controlStick` (`stick`/`stick2`) already holds this report's raw physical stick
reading - reset fresh from hardware every report by `ProcessButtonsAndStick`
*before* this runs. So the formula is "physical stick (optionally divided down by
`GyroStickReduction`) plus this report's gyro contribution" - you can still nudge
with the real thumbstick while gyro-aiming. Because the physical component resets
every report and only the gyro `dx`/`dy` accumulate turn-by-turn, holding a
steady rotation rate produces a steady deflection (continuous turning), and
releasing the rotation drops output back toward whatever the physical stick alone
reads - no separate "stop" state to manage.

Rate mode with the default axis (`yaw`) and no invert is **byte-for-byte
identical** to the implementation before Absolute tilt/Hybrid existed - that was
a hard requirement when the extra modes were added.

## Absolute tilt mode

For driving/flight-sim, rate control is wrong: you want stick position to track
*current tilt angle* and self-center when leveled, like a motion steering wheel
or a HOTAS stick, not a camera.

This reuses `Joycon.cur_rotation` - **not** a new orientation tracker. Every
report, unconditionally:

```csharp
// Filtered IMU data
this.cur_rotation = AHRS.GetEulerAngles();
```

`AHRS` is a per-Joycon `MadgwickAHRS` instance (`MadgwickAHRS.cs`) - a full
quaternion sensor-fusion filter, already used elsewhere (raw-mode roll
compensation, `GyroAnalogSliders`' trigger tilt). `GetEulerAngles()` returns
pitch/yaw/roll **relative to whatever pose `Recenter()` last captured**:

```csharp
public float[] GetEulerAngles() {
    float[] pitchYawRoll = new float[3];
    float r0 = referenceQuaternion[0], ...;
    float c0 = Quaternion[0], ...;

    // relative = conjugate(reference) * current. At the instant Recenter()
    // captures the reference this is identity, regardless of the controller's
    // physical tilt.
    ...
    pitchYawRoll[0] = (float)Math.Asin(sinPitch);   // Pitch
    pitchYawRoll[1] = (float)Math.Atan2(...);       // Yaw
    pitchYawRoll[2] = (float)Math.Atan2(...);       // Roll

    float[] returnAngles = new float[6];
    Array.Copy(pitchYawRoll, returnAngles, 3);
    Array.Copy(old_pitchYawRoll, 0, returnAngles, 3, 3);
    old_pitchYawRoll = pitchYawRoll;
    return returnAngles;
}
```

**Important, and easy to get wrong**: `returnAngles[3..5]` is *not* a recenter
baseline - it's simply the *previous call's* `[0..2]` (`old_pitchYawRoll`,
overwritten every call), i.e. a frame-to-frame rate approximation. That's what
`GyroAnalogSliders` actually uses (`cur_rotation[0] - cur_rotation[3]`, a rate).
Absolute tilt mode instead uses `cur_rotation[0]`/`[1]` **un-subtracted** - that's
the real "relative to last recenter" value, straight from the comment above.

```csharp
if (mode == "absolute" || mode == "hybrid") {
    float absoluteX = axisX == "roll" ? gyroStickLatestWorldRoll : cur_rotation[1];
    float absoluteY = cur_rotation[0];
    dx = Math.Max(-1.0f, Math.Min(1.0f, absoluteX / EffectiveGyroStickTiltRangeX()));
    dy = Math.Max(-1.0f, Math.Min(1.0f, absoluteY / EffectiveGyroStickTiltRangeY()));
    ...
}
```

`EffectiveGyroStickTiltRangeX/Y()` convert "degrees of tilt for full deflection"
(`GyroStickTiltRangeX`/`Y` in `App.config`, default 45°/35°) into radians, with
the same zero/NaN/Infinity guard used everywhere else in this file:

```csharp
private float EffectiveGyroStickTiltRangeX() {
    return GyroStickTiltRangeX > 0.0f &&
           !float.IsNaN(GyroStickTiltRangeX) &&
           !float.IsInfinity(GyroStickTiltRangeX)
        ? GyroStickTiltRangeX * DegreesToRadiansGyroStick
        : 45.0f * DegreesToRadiansGyroStick;
}
```

### Recenter integration

Absolute tilt is meaningless without a neutral pose. Rather than add a second
calibration flow, it reuses the existing `RecenterGyro()`/"Re-center gyro" bind
wholesale - two new triggers were added alongside gyro-mouse's existing ones:

```csharp
// Gyro-stick's own activation edge also recenters when Absolute/Hybrid mode is
// configured, mirroring gyro-mouse's auto-recenter-on-activation - checked
// independently per stick since mode is per-stick.
bool gyroStickActivationRecenter = UseFilteredIMU &&
    ((gyroLeftStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeLeft)) ||
     (gyroRightStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeRight)));

if (gyroMouseJustEnabled || manualRecenterRequested ||
    gyroStickActivationRecenter || gyroStickManualRecenterRequested) {
    if (gyroMouseJustEnabled || manualRecenterRequested)
        form.SimulateMoveToScreenCenter();   // never for a stick-only recenter

    RecenterGyro();
    dt = 0.0f;
    ...
}
```

So: pressing the gyro-stick activation bind automatically declares "this pose is
neutral" the moment Absolute/Hybrid mode is active, and the "Re-center gyro" bind
also works while only gyro-stick (no gyro-mouse) is in use - without ever moving
the mouse pointer.

**Known, accepted limitation**: `RecenterGyro()` also resets gyro-mouse's neutral
frame, gravity estimate, and stationary-bias-learning window unconditionally
(it's one shared `AHRS`/gravity state per Joycon). Running gyro-mouse *and* an
Absolute/Hybrid gyro-stick output at the same time will cross-reset whenever
either side recenters. Uncommon combination; not currently isolated.

## Hybrid mode

Absolute tilt's position, plus a slice of Rate mode's velocity signal layered on
top - more responsiveness near the edges of the tilt range without losing the
self-centering behavior:

```csharp
if (mode == "hybrid") {
    dx += pendingDx * GyroStickHybridRateWeight;
    dy += pendingDy * GyroStickHybridRateWeight;
}
```

`GyroStickHybridRateWeight` (`App.config`, default `0.3`) is explicitly a
starting point to tune by feel, not a validated constant. The final clamp in
`ApplyGyroToStick` bounds the combined result, so no extra clamping is needed
here even though the sum is momentarily unbounded.

## Axis source: yaw vs. roll, two different reference frames on purpose

By default, stick X comes from yaw (twisting the controller like a doorknob).
`GyroStickAxisXLeft`/`Right` can instead select **roll** (banking the controller
side-to-side) - for flight-sim-style aileron input. Y always follows pitch; there
is no roll-as-Y option.

Roll deliberately uses a *different* reference frame depending on mode:

- **Rate mode**: raw local Z-axis gyro rate (`stickGyroRate.Z`), no gravity
  reference at all. Rotation around the controller's own pointing axis is the
  same physical motion regardless of current tilt, so a rate doesn't need one.
- **Absolute/Hybrid mode**: `gyroStickLatestWorldRoll`, i.e. `Map()`'s
  gravity-referenced `rollRadians` - always zero at true physical level,
  **independent of `RecenterGyro()`**.

```csharp
// Rate mode with roll selected as the X source uses the raw local roll rate
// directly - unlike yaw/pitch, roll needs no gravity reference to be a
// well-defined rotation rate, and Map() only ever reports rollRadians as an
// absolute angle, not a rate.
float xRate = GyroStickAxisXLeft == "roll" ? stickGyroRate.Z : yawRate;
```

Practical consequence: recentering moves the pitch/Y neutral point (and the
yaw/X neutral point, if axis source is yaw), but **never** the roll/X neutral
point - a "steering wheel" always centers at true level, while throttle/pitch
centers wherever you like. Deliberate, not an oversight.

## Invert

Applied last, after mode dispatch, independently per stick and per axis:

```csharp
bool invertX = isLeftStick ? GyroStickInvertXLeft : GyroStickInvertXRight;
bool invertY = isLeftStick ? GyroStickInvertYLeft : GyroStickInvertYRight;
if (invertX)
    dx = -dx;
if (invertY)
    dy = -dy;
```

Composes orthogonally with mode and axis source - e.g. invert Y for a
flight-sim pitch convention, on either stick, in any mode.

## Everything is independent per stick

`GyroStickModeLeft`/`Right`, `GyroStickAxisXLeft`/`Right`, and all four invert
flags are fully separate settings - a common pattern is steering with one stick
in Absolute tilt and camera/throttle with the other in Rate mode. Because axis
source can differ between sticks, the rate accumulation itself has to be
per-stick too (`pendingGyroStickDxLeft/DyLeft` and `...Right`, not one shared
pair) - otherwise a "roll" choice on one stick would corrupt a "yaw" choice on
the other.

`ComputeFilteredGyroStickOutput(bool isLeftStick, ...)` is called once per side,
each reading its own settings:

```csharp
if (gyroLeftStickActiveThisReport && !gyroStickRatcheted)
    ComputeFilteredGyroStickOutput(true, pendingGyroStickDxLeft, pendingGyroStickDyLeft,
                                   out leftDx, out leftDy);
if (gyroRightStickActiveThisReport && !gyroStickRatcheted)
    ComputeFilteredGyroStickOutput(false, pendingGyroStickDxRight, pendingGyroStickDyRight,
                                   out rightDx, out rightDy);
```

`GyroStickTiltRangeX/Y`, `GyroStickHybridRateWeight`, `GyroStickSensitivityX/Y`,
and `GyroStickReduction` remain **global** (App.config-backed, not per-profile) -
only Mode/Axis/Invert are per-stick settings.

## Ratchet gyro

Gyro-to-stick output is a per-report *rate signal*, not an accumulated position -
a stick held at constant nonzero deflection reads to the game as "keep turning at
this rate." That matters for repositioning: sustaining a turn longer than a
comfortable wrist twist requires untwisting back to a neutral angle mid-aim,
which - without ratcheting - registers as a reverse turn and fights the input you
just made.

`ratchet_gyro` is a bindable action (controller buttons only, like `clench_gyro`)
that, while held, **zeroes** gyro-to-stick output instead of tracking live
rotation - matching a real ratchet wrench: disengaging it stops applying new
rotation while you reposition your grip, it doesn't keep spinning the bolt on its
own. (An earlier version froze output at its *last* value instead of zeroing it -
that kept turning in whatever direction the wrist was already moving, confirmed
wrong on hardware via `joy.cpl`, and corrected.)

```csharp
string ratchetGyroVal = MappingValue("ratchet_gyro");
gyroStickRatcheted = (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport) &&
    ratchetGyroVal != "0" && IsComboHeld(ratchetGyroVal);
```

While ratcheted, live rotation still isn't integrated into the pending delta (so
releasing resumes from the live angle, not a replay of whatever happened while
ratcheted), and `ComputeFilteredGyroStickOutput` is skipped entirely - `dx`/`dy`
stay at their zero-initialized default for that stick.

## Config reference (`App.config`)

| Key | Default | Scope | Meaning |
|---|---|---|---|
| `GyroStickSensitivityX`/`Y` | 100 / 85 | global | Rate-mode gain |
| `GyroStickReduction` | 1 | global | Physical-stick divisor while gyro-stick active |
| `GyroStickTiltRangeX`/`Y` | 45 / 35 | global | Degrees of tilt for full deflection (Absolute/Hybrid) |
| `GyroStickHybridRateWeight` | 0.3 | global | Rate contribution layered on top in Hybrid |
| `GyroStickModeLeft`/`Right` | `rate` | per-profile | `rate` \| `absolute` \| `hybrid` |
| `GyroStickAxisXLeft`/`Right` | `yaw` | per-profile | `yaw` \| `roll` |
| `GyroStickInvertXLeft`/`YLeft`/`XRight`/`YRight` | `false` | per-profile | Sign flip after mode+axis |
| `ratchet_gyro` | `0` (unbound) | per-profile bind | Zeroes stick output while held |

All per-profile keys are registered in `ControllerMappings.OptionKeys`
(`ControllerMappings.cs`) and fall back to their `App.config` default via
`LegacyOptionValue`'s generic `ConfigurationManager.AppSettings[key]` lookup - no
special-case code needed per key. A joined L+R pair resolves to one shared
profile ID, so these settings (like `GyroAnalogSliders`) are naturally shared
across both physical halves.

## UI (`Reassign.cs`, Gyro tab → "Stick mapping")

Four dropdowns (Left/Right stick response, Left/Right turn axis) and four
checkboxes (Left/Right × Invert X/Y), grayed out with an explanatory note when
`UseFilteredIMU` reads `false` - the new controls have no effect in raw mode.
Every dropdown is built through one small helper so they all share the exact
same column position, width, and label offset as every other dropdown in the
dialog (`gyroActivationModeSelector`, etc.):

```csharp
private ComboBox CreateStickModeRow(Panel page, string label, int top, object[] items) {
    page.Controls.Add(CreateLabel(label, 24, top + 6, ProfileText, false));
    ComboBox selector = CreateProfileComboBox(180, top, 180);
    selector.DropDownStyle = ComboBoxStyle.DropDownList;
    selector.Items.AddRange(items);
    selector.SelectedIndexChanged += ProfileOptionControlChanged;
    page.Controls.Add(selector);
    return selector;
}
```

## What Rate mode does *not* inherit from gyro-mouse

Gyro-mouse layers extra shaping on top of the raw Player Space rate that
gyro-stick currently skips entirely:

- `SmoothGyroMouseRates` - adaptive low-speed smoothing
- the "tightening" curve - soft ramp-up below a small angular-speed threshold,
  instead of a hard deadzone
- stationary-bias learning - drifts out small resting sensor bias while idle
  (detailed below)

None of that is wired into `ProcessGyroStickSample` today; gyro-stick gets the
raw Player Space rate straight through. Likely fine in practice (a stick's own
in-game deadzone/curve often covers for it), but if future "responsiveness" work
turns out to mean small-motion precision or settling behavior at rest, porting a
version of tightening/smoothing over from the gyro-mouse path is the natural next
place to look - same lineage, so the tuning intuition mostly carries over.

### Stationary-bias learning, in detail

`ApplyGyroMouseStationaryBias` (`Joycon.cs:2172`) exists because every real gyro
reports a small nonzero rate even sitting perfectly still - temperature drift,
manufacturing tolerance. Uncorrected, that reads as a slow constant rotation: a
cursor that quietly crawls even with the controller resting on a table.

**Stillness detection** requires two conditions at once - accelerometer
magnitude close to exactly 1g (not being shaken or accelerated), and the
residual gyro rate (rate minus whatever bias is currently known) already below a
threshold:

```csharp
private const int GyroMouseBiasWindowSamples = 100; // 0.5s at 200 Hz
private const float GyroMouseInitialStillRateLimit = 2.0f; // degrees/sec per axis
private const float GyroMouseLearnedStillRateLimit = 1.25f;
private const float GyroMouseStillRangeLimit = 1.0f;
private const float GyroMouseStillAccelTolerance = 0.15f;
```

```csharp
float stillRateLimit = gyroMouseBiasInitialized
    ? GyroMouseLearnedStillRateLimit   // 1.25 deg/s once locked
    : GyroMouseInitialStillRateLimit;  // 2.0 deg/s before first lock
float accelMagnitude = gyroMouseSensorAccel.Length();
bool stillCandidate = Math.Abs(accelMagnitude - 1.0f) <= GyroMouseStillAccelTolerance &&
                      MaxAbsComponent(residual) <= stillRateLimit;
```

The rate threshold is generous (2.0°/s) before any bias is known yet, then
tightens to 1.25°/s once locked - so a rough first estimate can't reinforce its
own error by making the still-detector too permissive around a wrong offset.

**Accumulation** runs over a 100-sample window (0.5s at the 200 Hz sub-sample
rate), tracking running min/max and sum. It only commits if the observed range
stayed under `GyroMouseStillRangeLimit` (1.0°/s) for the *entire* window - proof
it was genuinely still throughout, not just briefly dipping under the rate
threshold:

```csharp
if (MaxAbsComponent(range) <= GyroMouseStillRangeLimit) {
    Vector3 measuredBias = gyroMouseBiasWindowSum / gyroMouseBiasWindowCount;
    gyroMouseBias = gyroMouseBiasInitialized
        ? gyroMouseBias + 0.2f * (measuredBias - gyroMouseBias)  // slow EMA after first lock
        : measuredBias;                                          // take it outright, first time
    gyroMouseBiasInitialized = true;
    ...
}
```

First lock takes the window average outright; every later update blends in only
20% of the new measurement - a slow moving average so one noisy half-second
can't yank the calibration, while genuine thermal drift over a session still
gets tracked.

**Learning is explicitly forbidden while gyro-mouse is actively producing
output**:

```csharp
if (gyroMouseBiasInitialized && !allowBiasLearning) {
    ResetGyroMouseBiasWindow();
    return residual;
}
```

called as `ApplyGyroMouseStationaryBias(gyroMouseSensorRate, !gyroPointerActive)`
from `ProcessGyroMouseSample`. A controller held in hand and rotated slowly on
purpose looks statistically identical to "bias plus noise," and locking onto
that would create a bias that fights the exact motion being made. Instead it
keeps learning continuously *between* uses (gyro-mouse inactive but the
controller connected), so by the time the activation bind is pressed, the
offset has usually already converged from however long the controller sat idle
beforehand - no live half-second lock delay on activation.

**Why gyro-stick doesn't have this**: the estimator is only ever called from
`ProcessGyroMouseSample`, feeding `mouseGyroRate`. `ProcessGyroStickSample` reads
`gyroMouseSensorRate` directly, uncorrected. It matters less there structurally -
a stick has no persistent position to crawl, since the physical component resets
fresh every report - but a tiny uncorrected residual still adds noise into the
accumulated per-report delta. Since `gyroMouseBias` is computed continuously
regardless of gyro-mouse's own activation state, gyro-stick could read the same
already-learned value with very little new code if it ever turns out to matter.
