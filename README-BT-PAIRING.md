# BetterJoy2 Bluetooth Pairing Notes

## Purpose

This document captures the current understanding of BetterJoy2's automatic DualSense Bluetooth pairing implementation and the remaining reliability problem.

The important conclusion from the current commit history and real-hardware testing is:

> **BetterJoy2 is not taking 3–4 rounds to build the bond. The Windows bond is already complete in round 1. The extra rounds are attempts to get the live Bluetooth HID link to remain up long enough to become a stable working controller.**

That distinction changes where debugging effort should be focused.

---

# Current Architecture

BetterJoy2 performs a genuinely low-level pairing/bootstrap sequence over USB.

It currently has the ability to:

- Read the DualSense controller MAC.
- Enumerate local Windows Bluetooth radios.
- Reuse an existing Classic Bluetooth link key when one already exists.
- Generate a cryptographically random 16-byte Classic Bluetooth link key when needed.
- Write that key directly into the Windows BthPort key store.
- Send the same host Bluetooth MAC + link key to the DualSense over USB.
- Verify the controller-side host record using DualSense feature report `0x09`.
- Trigger the controller's Bluetooth connect behavior with feature report `0x08`.
- Enable the standard Bluetooth HID service in Windows.
- Detect when the actual `00001124` Bluetooth HID interface appears.
- Confirm real input by waiting for the controller to reach `IMU_DATA_OK`.

This is not normal Windows pairing-dialog automation.

Both sides are being given the same Classic Bluetooth credential out-of-band over USB before the final Bluetooth connection is established.

---

# Windows Link-Key Storage

BetterJoy2 directly uses the BthPort registry key store:

```text
HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\<adapter>\<controller>
```

The stored value is the 16-byte Classic Bluetooth link key for the controller.

BetterJoy2 also inspects the Windows Bluetooth device record under:

```text
HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<controller>
```

That record contains Windows-side Bluetooth device/service state such as:

```text
Name
Class of Device
SSP / pairing flags
CachedServices
ServicesFor\<adapter>\{00001124-0000-1000-8000-00805F9B34FB}
```

---

# DualSense Pairing Reports

## Report `0x0A` — Set pairing information

BetterJoy2 sends the controller:

```text
Report ID:   0x0A
Host MAC:    bytes 1..6
Link key:    bytes 7..22
CRC:         zero on USB
```

This writes the PC host address and Classic Bluetooth link key into the controller.

The controller-side host address can later be read back using report `0x09`.

The link key itself is effectively write-only from BetterJoy2's point of view.

---

## Report `0x09` — Read pairing information

BetterJoy2 uses report `0x09` to verify the controller MCU has actually committed the host-address transition.

This was an important improvement over fixed delays.

The pairing flow now explicitly waits until the expected host address appears instead of assuming the previous `0x0A` write completed after some arbitrary sleep.

---

## Report `0x08`

The relevant commands currently observed are:

```text
0x08 / 0x01  -> Bluetooth connect / ON trigger
0x08 / 0x02  -> low-power / OFF
```

The low-power command is particularly important to the current reliability investigation.

---

# What the Commit History Proved

## 1. USB feature-report traffic must stay on the controller Poll thread

Pairing originally suffered from concurrent HID operations against the same USB handle.

The pairing sequence performs multiple:

```text
hid_get_feature_report
hid_send_feature_report
```

operations while the normal controller Poll loop also performs reads and output operations.

Running pairing from a separate manager/timer thread created a real concurrent-HID race.

The fix was to queue the pairing request from the manager but execute the actual pairing sequence on the controller's Poll thread.

This is considered solved.

---

## 2. USB must step off immediately after the Bluetooth connect trigger

Real-hardware testing showed that leaving the normal USB Poll traffic running after sending:

```text
0x08 / ON
```

could prevent the newly-started Bluetooth connection from being serviced correctly.

The current path therefore does:

```text
write/verify pairing data
    ↓
send 0x08 / ON
    ↓
immediately drop the active USB controller object
    ↓
stop normal USB Poll traffic
```

This significantly improved reliability and has produced real first-attempt successes.

---

## 3. Active Bluetooth inquiry was tested and reverted

A version of the finalizer changed:

```text
issueInquiry = false
```

to:

```text
issueInquiry = true
timeoutMultiplier = 4
```

to force an actual Bluetooth inquiry instead of checking only the Windows device cache.

That was experimentally useful in some tests but was later reverted.

The current code is back to cache-based device enumeration.

This should not be treated as the primary remaining issue because later testing provided stronger evidence elsewhere.

---

## 4. Rewriting `0x0A` after Windows had already progressed can break the live HID connection

One experimental version re-opened the USB path after Windows had authenticated/registered HID and re-sent the pairing data.

Real hardware showed that this could knock the controller back into pairing/low-power broadcast behavior while Windows still displayed it as connected.

Important conclusion:

> **Once the live Bluetooth transition is in progress, unnecessary pairing-record writes can move the controller backward.**

Therefore the controller should not be repeatedly rewritten simply because the live connection has not yet become stable.

---

## 5. "A link key exists" is not proof that pairing succeeded

The implementation originally treated:

```text
created == false
```

as evidence that the controller was already paired.

That is wrong.

An existing BthPort key proves only that the credential exists.

The stronger success signal is now:

```text
actual Bluetooth HID interface
+
service UUID path contains 00001124
+
controller reaches IMU_DATA_OK
```

That is much closer to actual proof that Windows has a usable HID connection.

---

## 6. One `IMU_DATA_OK` appearance is still not proof of success

Testing showed that the controller can briefly appear as a working Bluetooth HID device and then drop again.

A single `IMU_DATA_OK` transition therefore represents only a momentary successful lap through the connection sequence.

The current implementation requires the Bluetooth controller to remain in `IMU_DATA_OK` for a dwell period before confirming the pairing.

Current values:

```text
BluetoothPairingStableDwellSeconds = 3
BluetoothPairingConfirmWindowSeconds = 10
BluetoothPairingMaxAttempts = 6
```

---

# Most Important Finding: The Bond Is Complete in Round 1

A diagnostic commit dumped the BthPort pairing state before and after every pairing round.

It logged:

```text
Parameters\Keys\<adapter>\<controller>
Parameters\Devices\<controller>
CachedServices
ServicesFor\...
SSP / paired state
Name
Class of Device
```

The result was extremely important:

> **The Windows pairing record goes from absent/incomplete to complete during round 1 and is byte-for-byte unchanged in later rounds.**

The repeated attempts are therefore **not progressively building Windows pairing state**.

The persistent bond is done.

The repeated cost is the live connection.

Conceptually:

```text
ROUND 1
=======
Create key
Write controller pairing record
Windows Devices record appears
HID service registration appears
Persistent bond complete
Live link fails to hold

ROUND 2
=======
Persistent bond already complete
Live link fails to hold

ROUND 3
=======
Persistent bond still unchanged
Live link fails to hold

ROUND 4
=======
Persistent bond still unchanged
Live link finally holds
```

This means future debugging should stop assuming that retries are filling in missing registry/device state.

They are not.

---

# The Pairing Problem Is Now a Live-Link Problem

The remaining failure window is approximately:

```text
0x08 / ON
    ↓
controller begins Bluetooth activity
    ↓
Windows sees/opens the remote controller
    ↓
ACL / authentication / encryption / HID setup progresses
    ↓
BTHENUM / HidBth path becomes live
    ↓
00001124 HID interface appears
    ↓
actual input begins
    ↓
connection must remain stable
```

Something in this transition often fails to hold on the first attempt.

The key itself is already correct.

---

# Current Full Enabled Flow

The current first-time/full pairing path is approximately:

```text
Create/reuse Windows link key
    ↓
clear controller pairing record
    ↓
verify empty host via 0x09
    ↓
write host + key using 0x0A
    ↓
verify host via 0x09
    ↓
reassert host + key using 0x0A
    ↓
verify again
    ↓
send 0x08 / ON
    ↓
record live-pairing attempt
    ↓
suppress USB path
    ↓
immediately drop USB controller object
    ↓
background Windows HID finalization
    ↓
wait for actual Bluetooth 00001124 controller
    ↓
require stable IMU_DATA_OK dwell
```

---

# Current Retry Behavior

When a live pairing attempt times out:

```text
attempt.awaitingReattempt = true
```

The USB path suppression is released.

The next scan adopts the wired controller again.

Because the attempt is now considered escalated, the next USB-side pairing pass can run the full clear/write/reassert ceremony again.

This is now questionable.

Why?

Because the registry diagnostic already proved the Windows-side pairing state was complete after round 1.

Repeatedly running the full pairing ceremony after that may be unnecessary and may actually disturb the controller's Bluetooth state machine.

---

# Pairing Harness Evidence

The separate PairingHarness produced one of the strongest real-hardware findings so far.

A tested sequence achieved:

```text
9 / 9 successful real hardware pairs
```

across meaningfully different conditions including:

- More than one physical controller.
- A controller with a real previous PS5 bond.
- Concurrent pairing while another controller remained connected.
- Double-pair testing.

The critical finding from that harness was:

> **Explicitly sending the DualSense low-power/OFF command after the bond/connect sequence was what drove reliability.**

Specifically:

```text
0x08 / 0x02
```

The harness conclusion was that the controller entering a genuinely low-power paired state is likely normal Sony behavior, not a failure.

The controller appears to want to reach a stable paired-but-dormant state before being cleanly woken into Bluetooth operation.

This matters because the current production `Enabled` path does not deliberately reproduce that known-good transition.

---

# Strongest Current Hypothesis

The production path is currently trying to go directly from:

```text
active USB
    ↓
write bond
    ↓
Bluetooth ON
    ↓
drop USB
    ↓
live Bluetooth HID
```

The harness suggests a more reliable controller-native sequence may be:

```text
active USB
    ↓
write bond
    ↓
verify bond
    ↓
controller intentionally enters low-power paired state
    ↓
USB is no longer actively driving it
    ↓
controller is woken / Bluetooth connect is triggered
    ↓
live Bluetooth HID
```

In other words:

> **The repeated rounds may be accidentally providing the controller with the settle/reset cycles that should have been deliberately provided once.**

This is currently the highest-value experiment.

---

# Recommended Next Experiment #1: Explicit Paired Low-Power Settle

Instead of:

```text
WRITE BOND
    ↓
0x08 ON
    ↓
drop USB
    ↓
hope the live link holds
```

test:

```text
WRITE BOND
    ↓
VERIFY 0x09
    ↓
REASSERT BOND
    ↓
VERIFY 0x09
    ↓
0x08 OFF
    ↓
allow controller to reach its native paired low-power state
    ↓
step off normal USB ownership
    ↓
wake / send 0x08 ON once
    ↓
immediately release USB ownership
    ↓
let Windows own the Bluetooth connection
    ↓
confirm real 00001124 HID
```

The exact ordering around when the wake-monitor handle opens should be tested carefully so the USB side does not accidentally prevent the low-power transition.

The goal is not "add another arbitrary delay."

The goal is to deliberately force the controller through the same state that the harness identified as reliable.

---

# Recommended Next Experiment #2: Stop Rebuilding a Bond That Already Exists

After round 1, if all of these are true:

```text
Windows link key exists
Windows Devices\<MAC> record exists
HID service registration exists
controller report 0x09 points to this PC
```

then a retry should not automatically do:

```text
clear controller bond
write 0x0A
verify
reassert 0x0A
verify
```

Instead, retry only the live connection transition.

Conceptually:

```text
persistent bond already valid
    ↓
do not clear
do not rewrite 0x0A
do not re-register HID unnecessarily
    ↓
controlled low-power settle
    ↓
connect/wake
    ↓
observe live HID
```

This avoids touching the pairing record during a phase where previous tests already showed that unnecessary `0x0A` writes can destabilize the controller.

---

# Recommended Next Experiment #3: Move Stability Confirmation Onto the Bluetooth Poll Thread

The current dwell logic is checked from the manager's periodic reconciliation path.

That manager is driven on roughly a two-second cadence.

The code currently says:

```text
BluetoothPairingStableDwellSeconds = 3
```

but the actual observation is quantized by the manager timer.

A real sequence can look like:

```text
BT reaches IMU_DATA_OK
    ↓
manager notices up to ~2 seconds later
    ↓
stableSince starts
    ↓
next manager pass ~2 seconds later
    ↓
only ~2 seconds observed, not enough
    ↓
next manager pass ~2 seconds later
    ↓
~4 seconds observed, finally confirm
```

So the nominal 3-second dwell can effectively become much longer.

That matters because:

```text
BluetoothPairingConfirmWindowSeconds = 10
```

is running at the same time.

A slow-but-valid first connection can therefore collide with the retry timeout.

Better approach:

```text
Bluetooth Poll thread receives first valid live input
    ↓
stableSince = now

each subsequent successful Bluetooth input packet
    ↓
if continuously valid for required dwell:
    CONFIRM

read failure / disconnect
    ↓
reset stability state
```

The Bluetooth Poll thread sees the real input stream continuously and is therefore the correct place to measure continuity.

The manager should receive a simple event/state:

```text
this Bluetooth HID connection has proven continuously healthy
```

rather than sampling that health every two seconds.

---

# Windows HID Finalization Is Not the Final Truth

BetterJoy2 currently has a background routine:

```text
TryFinalizeClassicHidPairing()
```

which finds the device and calls:

```text
BluetoothSetServiceState(... HID ..., ENABLE)
```

The routine can report success when the HID service is registered/enabled.

That is useful, but it should not be treated as equivalent to:

```text
the live Bluetooth HID link is stable
```

The implementation already proved these are different conditions.

A better conceptual name would be:

```text
EnsureWindowsHidServiceRegistered()
```

rather than:

```text
FinalizePairing()
```

The actual pairing success signal should remain the real live controller.

---

# Why `BluetoothAuthenticateDeviceEx` Is Not Currently the Best Next Step

An authentication-window implementation was already tested.

It included combinations of:

```text
BluetoothRegisterForAuthenticationEx
BluetoothAuthenticateDeviceEx
authenticated + remembered durability checks
```

Real-hardware testing found:

- The authentication callback never fired.
- Additional durability/authentication gating correlated with worse outcomes.
- The simpler live-link path worked better.

Also, Windows' logical authenticated/bonded state is not equivalent to proof that the current HID ACL link is stable.

Therefore more effort should not currently be spent on trying to make `BluetoothAuthenticateDeviceEx` the center of the design.

The stronger experimental evidence is on controller power-state sequencing and live-link timing.

---

# Potential Kernel Pre-Authentication Fallback

If the controlled low-power settle and cleaner retry model still cannot produce first-round reliability, the Windows Bluetooth driver DDI exposes a much stronger primitive.

A kernel Bluetooth client/profile driver can issue:

```text
BRB_L2CA_OPEN_CHANNEL
```

with flags:

```text
CF_LINK_AUTHENTICATED
CF_LINK_ENCRYPTED
```

For Bluetooth HID the relevant remote PSMs are normally:

```text
0x11  HID Control
0x13  HID Interrupt
```

Conceptually:

```text
matching key already installed on both sides
    ↓
controller placed in correct Bluetooth-ready state
    ↓
kernel helper opens outbound L2CAP channel
    ↓
require authenticated + encrypted link
    ↓
BthPort must satisfy link security
    ↓
close helper channel
    ↓
normal HidBth takes over
```

That would provide a true host-side security barrier instead of relying on device-record bookkeeping.

However:

> **Do not jump to a kernel helper until the controller-native low-power settle sequence has been tested in the production path.**

The harness evidence is too strong to ignore.

---

# Important Timing Lesson

A diagnostic commit added full registry dumps before and after every pairing round.

That instrumentation itself changed behavior:

```text
with synchronous registry dumps:
    ~5 rounds

without them:
    ~4 rounds
```

This proves that the live-link transition is highly timing-sensitive.

Any debugging added to this path should therefore be:

- lightweight,
- timestamped,
- asynchronous where possible,
- and not perform expensive registry/PnP walks inline with the Bluetooth transition.

Instrumentation can change the outcome.

---

# What Success Should Mean

Pairing should only be considered truly complete when all of the following have happened:

```text
Windows link key present
controller host record points to this PC
Windows HID service registration present
actual Bluetooth HID interface 00001124 exists
actual input is flowing
connection remains continuously healthy for a defined dwell
```

Do not collapse these into one boolean.

Recommended internal states:

```text
CREDENTIAL_PRESENT
CONTROLLER_BOND_WRITTEN
CONTROLLER_BOND_VERIFIED
WINDOWS_DEVICE_REGISTERED
HID_SERVICE_REGISTERED
BT_INTERFACE_PRESENT
BT_INPUT_LIVE
BT_INPUT_STABLE
PAIRING_COMPLETE
```

---

# Recommended State Machine

```text
STATE 1
USB controller identified

STATE 2
Windows link key acquired or created

STATE 3
Controller host/key written with 0x0A

STATE 4
Controller host verified with 0x09

STATE 5
Windows HID service ensured

STATE 6
Controller intentionally moved into stable paired low-power state

STATE 7
Bluetooth wake/connect triggered

STATE 8
USB normal Poll ownership released immediately

STATE 9
00001124 Bluetooth HID appears

STATE 10
real input begins

STATE 11
input remains continuously healthy for dwell

STATE 12
PAIRING COMPLETE
```

If State 9–11 fails:

```text
DO NOT rebuild States 2–5 unless evidence says they are invalid.

Retry States 6–11 first.
```

That is the architectural change most strongly supported by the current evidence.

---

# Immediate Priority

The next production experiment should be:

```text
first-time bond
    ↓
verify
    ↓
explicit low-power paired settle
    ↓
clean wake/connect
    ↓
no repeated 0x0A writes during live-link establishment
    ↓
measure stable input directly on the Bluetooth Poll thread
```

The working theory is now:

> **BetterJoy2 already knows how to pair the controller correctly. The remaining problem is transitioning the controller and Windows from a valid persistent bond into a stable live HID session in one clean pass.**

That is what the next iteration should optimize.
