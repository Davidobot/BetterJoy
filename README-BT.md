# BetterJoy2 Bluetooth Pre-Authentication Notes

## Purpose

This document captures the current theory and proposed implementation strategy for making Windows Bluetooth HID pairing more deterministic when BetterJoy2 already has:

- The controller Bluetooth MAC address.
- A valid Bluetooth bond/link key.
- The ability to inject that key into Windows.
- The ability to enable the HID service.
- USB access to the controller for out-of-band pairing/bootstrap data.

The remaining problem is that Windows does not always fully authenticate and enumerate the controller as a usable Bluetooth HID device immediately after the bond/key is injected.

The current behavior appears to be a race between:

1. Link-key/bond state being present in Windows.
2. Creation of a real Bluetooth ACL connection.
3. Authentication/encryption of that live connection.
4. HID service discovery/enumeration.
5. `BthEnum` creating the Bluetooth service PDO.
6. `HidBth` attaching and starting the HID device.

The goal is to deliberately drive these steps in order rather than relying on Windows to eventually complete them asynchronously.

---

# Core Observation

## A stored bond is not the same thing as a live authenticated Bluetooth link

Windows can know that a remote device is paired, remembered, or authenticated without there necessarily being a currently established and authenticated BR/EDR ACL connection.

A device can therefore be in a state conceptually similar to:

```text
LINK KEY:      present
REMEMBERED:    yes
AUTHENTICATED: yes (Windows bookkeeping)
ACL LINK:      not yet established/authenticated
HID SERVICE:   being enumerated
HidBth:        attempting connection
```

This distinction is likely the source of the current race.

`BLUETOOTH_DEVICE_INFO.fAuthenticated` should not be treated as proof that the radio currently has an authenticated and encrypted live connection.

---

# Windows HID Enumeration Path

For a classic Bluetooth HID device, the approximate path is:

```text
Bluetooth Radio
    ↓
BthPort
    ↓
BthEnum
    ↓
Remote HID SDP service
UUID 00001124-0000-1000-8000-00805F9B34FB
    ↓
BTHENUM PDO
    ↓
HidBth
    ↓
Windows HID device
```

Calling:

```text
BluetoothSetServiceState(... HID ..., BLUETOOTH_SERVICE_ENABLE)
```

is therefore not equivalent to authenticating the Bluetooth link.

It primarily causes Windows to enable/install/enumerate the corresponding Bluetooth profile service.

If HID is enabled before the underlying Bluetooth link is actually authenticated, `BthEnum`, `HidBth`, and Bluetooth authentication can all begin racing each other.

---

# Desired Pairing Sequence

The preferred architecture is:

```text
USB/OOB pairing data acquired
    ↓
Controller MAC + link key known
    ↓
Inject link key
    ↓
Create/force Bluetooth ACL connection
    ↓
Force authentication
    ↓
Force encryption
    ↓
Verify live authenticated link
    ↓
Enable HID service
    ↓
Wait for BTHENUM HID service PDO
    ↓
Wait for HID devnode to become started
    ↓
Pairing complete
```

The important change is:

> Do not expose/enable HID until the underlying Bluetooth link has reached the required security state.

---

# First User-Mode Experiment

## `BluetoothAuthenticateDeviceEx`

The first API to test is:

```text
BluetoothAuthenticateDeviceEx()
```

Its purpose is to send an authentication request to a remote Bluetooth device.

A proposed sequence is:

```text
Inject link key
    ↓
BluetoothGetDeviceInfo()
    ↓
BluetoothAuthenticateDeviceEx()
    ↓
Poll BluetoothGetDeviceInfo()
    ↓
Verify Bluetooth state
    ↓
BluetoothSetServiceState(HID)
    ↓
Wait for BTHENUM/HID PnP device
```

Instrumentation should record:

```text
fConnected
fRemembered
fAuthenticated
BluetoothAuthenticateDeviceEx return code
last-seen timestamps
last-used timestamps
```

before and after the authentication request.

---

# Important `BluetoothAuthenticateDeviceEx` Caveat

A major concern is that Windows may already consider the device authenticated because BetterJoy2 has injected enough pairing state into the registry.

In that case:

```text
BluetoothAuthenticateDeviceEx()
```

may return:

```text
ERROR_NO_MORE_ITEMS
```

because Windows believes authentication has already happened.

That does not necessarily prove that a real authenticated ACL link currently exists.

This creates a possible failure mode:

```text
BetterJoy2 injects key + pairing state
    ↓
Windows bookkeeping says "authenticated"
    ↓
BluetoothAuthenticateDeviceEx sees already-authenticated device
    ↓
API does not initiate useful on-air authentication
    ↓
HID service is enabled
    ↓
HidBth/BthEnum race the actual Bluetooth connection
```

---

# Critical Experiment: Inject Credential, Not Final State

A useful experiment is to reduce what BetterJoy2 injects.

Instead of making Windows immediately look as though pairing is completely finished:

1. Inject only the required link-key material.
2. Avoid prematurely setting state that causes Windows to report the device as fully authenticated.
3. Call `BluetoothAuthenticateDeviceEx`.
4. Allow Windows to perform the actual authentication transition itself.
5. Enable HID only after that succeeds.

Conceptually:

```text
Inject link key
    ↓
fAuthenticated should ideally still be false
    ↓
BluetoothAuthenticateDeviceEx()
    ↓
Windows performs real authentication using preloaded key
    ↓
fAuthenticated becomes true
    ↓
Enable HID
```

This would be cleaner than directly spoofing the completed pairing state.

---

# HID Service Enablement

The HID service UUID is:

```text
00001124-0000-1000-8000-00805F9B34FB
```

Only after Bluetooth authentication should BetterJoy2 call:

```text
BluetoothSetServiceState(
    radio,
    device,
    HID_UUID,
    BLUETOOTH_SERVICE_ENABLE
)
```

Then BetterJoy2 should explicitly wait for the resulting Bluetooth/HID PnP devices.

Do not assume that `BluetoothSetServiceState()` returning successfully means the HID device is ready.

---

# Recommended PnP Synchronization

After enabling HID, BetterJoy2 should enumerate device nodes and wait for the expected Bluetooth HID stack to appear.

Useful milestones include:

```text
BTHENUM device exists
    ↓
HID child exists
    ↓
HidBth attached
    ↓
Device node is started
```

The final condition should be based on actual PnP state, for example:

```text
CM_Get_DevNode_Status()
```

and verifying:

```text
DN_STARTED
```

rather than using arbitrary sleeps.

---

# Proposed BetterJoy2 Bluetooth Pairing State Machine

The pairing process should be implemented as a real state machine.

## State 1 — USB Data Acquired

```text
Controller MAC known
Link key known
USB/OOB exchange complete
```

## State 2 — Link Key Installed

```text
Required Windows link-key material written successfully
```

## State 3 — Bluetooth Device Known

```text
BluetoothGetDeviceInfo() succeeds
Remote device can be resolved by Windows
```

## State 4 — ACL Pre-Authentication

Attempt to create or stimulate a real Bluetooth connection.

Then explicitly request authentication.

## State 5 — Link Verified

Verify that the real Bluetooth connection has reached the expected security state.

Desired properties:

```text
connected
authenticated
encrypted
```

## State 6 — HID Service Enabled

Call:

```text
BluetoothSetServiceState(... HID ...)
```

only after State 5 succeeds.

## State 7 — BTHENUM HID PDO Present

Wait for Windows to enumerate:

```text
BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}...
```

or the corresponding Bluetooth HID service device.

## State 8 — HID Device Started

Wait for the actual HID device node to become started.

## State 9 — Pairing Complete

Only now report automatic pairing as successful.

---

# Avoid Fixed Sleeps

The current design should avoid logic such as:

```text
inject key
sleep 500 ms
enable HID
sleep 1000 ms
hope
```

Bluetooth and PnP initialization timing varies by:

- Bluetooth adapter.
- Driver stack.
- USB controller timing.
- Existing Windows cache/state.
- Controller behavior.
- Whether the remote controller is already attempting a Bluetooth connection.
- Power management.
- Whether HID devices are hidden from normal Windows input enumeration.
- PnP scheduling.

Instead, every stage should wait for a concrete condition with a bounded timeout.

Example:

```text
wait until Bluetooth device exists OR timeout
wait until authentication completes OR timeout
wait until BTHENUM device exists OR timeout
wait until HID devnode is DN_STARTED OR timeout
```

Retries and short backoff delays are fine.

Blind sleeps should not be the synchronization primitive.

---

# Kernel-Level Deterministic Option

If the public Bluetooth APIs cannot force Windows into the required state, the Windows Bluetooth driver interface exposes a much stronger mechanism.

A Bluetooth profile driver can issue:

```text
BRB_L2CA_OPEN_CHANNEL
```

with channel flags such as:

```text
CF_LINK_AUTHENTICATED
CF_LINK_ENCRYPTED
```

These flags explicitly require the underlying Bluetooth link to satisfy those security requirements.

Conceptually:

```text
BtAddress = controller MAC
PSM       = remote service PSM

Flags:
    CF_LINK_AUTHENTICATED
    CF_LINK_ENCRYPTED
```

The channel should not become usable until BthPort has established the required Bluetooth security state.

This is much closer to a true "pre-authenticate this controller now" primitive.

---

# Bluetooth HID L2CAP PSMs

Classic Bluetooth HID normally uses two fixed L2CAP protocol/service multiplexers:

```text
0x11 — HID Control
0x13 — HID Interrupt
```

A deterministic pre-authentication helper could theoretically attempt an outbound L2CAP connection to the controller using one of these remote PSMs with authenticated/encrypted link requirements.

Conceptual flow:

```text
Inject link key
    ↓
Kernel Bluetooth helper
    ↓
BRB_L2CA_OPEN_CHANNEL
    BtAddress = controller MAC
    PSM       = 0x11
    flags     = AUTHENTICATED | ENCRYPTED
    ↓
BthPort establishes ACL
    ↓
BthPort authenticates using injected key
    ↓
BthPort enables encryption
    ↓
Temporary connection succeeds
    ↓
Close helper channel if appropriate
    ↓
Enable Windows HID service
    ↓
BthEnum/HidBth take over
```

---

# Important HID PSM Caveat

The HID PSMs are part of the Windows Bluetooth HID stack.

BetterJoy2 should **not** attempt to register or replace HID PSMs `0x11` or `0x13`.

The useful approach, if supported, would be an outbound client operation against the remote PSM purely to force link establishment/security before normal HID enumeration.

This needs testing before committing to a kernel-driver design.

---

# `BRB_L2CA_UPDATE_CHANNEL`

Another relevant Windows Bluetooth driver primitive is:

```text
BRB_L2CA_UPDATE_CHANNEL
```

The Windows Bluetooth stack explicitly supports situations where:

```text
L2CAP channel established
```

and:

```text
authentication/encryption fully active
```

do not happen atomically.

That is important because it supports the current race hypothesis:

> A Bluetooth connection can exist for some period before its final authentication/encryption state is available.

A kernel helper could therefore treat link-security establishment as an explicit stage rather than assuming that successful connection establishment means authentication is complete.

---

# User-Mode Socket Authentication

Windows Bluetooth sockets expose options including:

```text
SO_BTH_AUTHENTICATE
SO_BTH_ENCRYPT
```

A typical RFCOMM sequence looks conceptually like:

```c
setsockopt(sock, SOL_RFCOMM, SO_BTH_AUTHENTICATE, ...);
setsockopt(sock, SOL_RFCOMM, SO_BTH_ENCRYPT, ...);
connect(...);
```

This can force authentication/encryption as part of Bluetooth connection establishment.

Unfortunately, ordinary WinSock Bluetooth support is primarily useful for RFCOMM.

Bluetooth HID controllers communicate over L2CAP HID PSMs instead.

If the controller exposes no useful RFCOMM service, this is unlikely to solve the problem directly.

Windows does not provide normal user-mode applications with an obvious generic equivalent of:

```text
connect to arbitrary L2CAP PSM 0x11
require authentication
require encryption
```

The lower-level Bluetooth driver DDI does provide such primitives.

---

# Likely BetterJoy2 Failure Mode

The current implementation may effectively be doing this:

```text
USB pairing data obtained
    ↓
Link key injected
    ↓
Windows pairing/authentication state injected
    ↓
HID service enabled immediately
    ↓
BthEnum starts HID service enumeration
    ↓
HidBth attempts connection
    ↓
Bluetooth ACL/authentication also begins
    ↓
PnP + HID + radio authentication race each other
```

Depending on timing, Windows may:

- Fully enumerate the HID device.
- Partially enumerate it.
- Remember the device but never start HID correctly.
- Require reconnect.
- Require disable/enable.
- Work only after the controller retries.
- Work only when Windows visibility of USB/Bluetooth devices is manipulated.
- Behave differently depending on adapter timing.

This matches the observed flaky behavior.

---

# Proposed Architecture

A more deterministic architecture would be:

```text
┌──────────────────────────────────────┐
│ USB Controller Connection            │
│                                      │
│ Read MAC / pairing information       │
│ Generate or obtain link key          │
└───────────────────┬──────────────────┘
                    │
                    ▼
┌──────────────────────────────────────┐
│ Install Bluetooth Credential         │
│                                      │
│ Inject only required link-key data   │
└───────────────────┬──────────────────┘
                    │
                    ▼
┌──────────────────────────────────────┐
│ Force Bluetooth Connection           │
│                                      │
│ User mode first:                     │
│ BluetoothAuthenticateDeviceEx        │
│                                      │
│ Kernel fallback:                     │
│ BRB_L2CA_OPEN_CHANNEL                │
└───────────────────┬──────────────────┘
                    │
                    ▼
┌──────────────────────────────────────┐
│ Verify Link Security                 │
│                                      │
│ Connected                            │
│ Authenticated                        │
│ Encrypted                            │
└───────────────────┬──────────────────┘
                    │
                    ▼
┌──────────────────────────────────────┐
│ Enable HID Service                   │
│                                      │
│ BluetoothSetServiceState             │
└───────────────────┬──────────────────┘
                    │
                    ▼
┌──────────────────────────────────────┐
│ Wait for Windows PnP                 │
│                                      │
│ BTHENUM                              │
│ HidBth                               │
│ HID devnode DN_STARTED               │
└───────────────────┬──────────────────┘
                    │
                    ▼
             PAIRING COMPLETE
```

---

# Recommended Immediate Test

Before writing a driver, instrument the existing BetterJoy2 pairing code with this exact sequence:

```text
1. Inject link key.

2. Immediately dump:
   - fConnected
   - fRemembered
   - fAuthenticated

3. Call:
   BluetoothAuthenticateDeviceEx()

4. Log:
   - return value
   - GetLastError()
   - device flags immediately afterward

5. Poll BluetoothGetDeviceInfo().

6. Do NOT enable HID yet.

7. Determine whether Windows establishes/authenticates the real Bluetooth link.

8. Only after successful authentication:
   BluetoothSetServiceState(HID)

9. Enumerate BTHENUM devices.

10. Enumerate HID devices.

11. Wait until the desired HID devnode reports DN_STARTED.
```

The return value from:

```text
BluetoothAuthenticateDeviceEx()
```

immediately after key injection is particularly important.

---

# Expected Diagnostic Outcomes

## Case A — Authentication API Actually Starts Authentication

Example:

```text
Before:
Connected      = false
Remembered     = true
Authenticated  = false

BluetoothAuthenticateDeviceEx:
SUCCESS

After:
Connected      = true
Remembered     = true
Authenticated  = true
```

This is ideal.

BetterJoy2 can probably solve the race entirely in user mode.

---

## Case B — `ERROR_NO_MORE_ITEMS`

Example:

```text
Before:
Connected      = false
Remembered     = true
Authenticated  = true

BluetoothAuthenticateDeviceEx:
ERROR_NO_MORE_ITEMS
```

This would strongly suggest that the injected pairing state is causing Windows to treat authentication as already complete before a useful radio-level authentication operation occurs.

Next experiment:

> Inject less state.

Specifically, preload the credential but allow Windows to perform the final authentication transition itself.

---

## Case C — API Succeeds but HID Still Races

Example:

```text
BluetoothAuthenticateDeviceEx:
SUCCESS

fAuthenticated:
true

BluetoothSetServiceState:
SUCCESS

HID:
intermittent
```

Then `fAuthenticated` is still insufficient as a synchronization point.

The next step is to explicitly verify:

```text
ACL connection
encryption/security state
BTHENUM enumeration
HID devnode start state
```

and possibly move the preauthentication phase to the Bluetooth kernel DDI.

---

# Relationship to HIDHide

A particularly strange existing observation is that automatic pairing behaves differently when BetterJoy2 completely hides USB/Bluetooth controller visibility from normal Windows consumers using HIDHide.

That suggests additional races involving:

- Competing HID opens.
- Windows input-stack enumeration.
- Existing stale HID devnodes.
- Driver attachment order.
- Controller switching transport state between USB and Bluetooth.
- Windows attempting its own pairing/service connection while BetterJoy2 is manipulating state.

The new state-machine implementation should therefore log PnP events around both the USB and Bluetooth HID instances.

Useful events to capture:

```text
USB HID arrives
USB HID hidden
Bluetooth remote device appears
BTHENUM HID service PDO appears
HidBth attaches
Bluetooth HID child starts
USB transport disappears or changes state
Bluetooth transport becomes active
```

This may explain why HIDHide currently improves reliability.

---

# Debug Logging Recommendations

Add a dedicated Bluetooth pairing trace.

Example:

```text
[BT][PAIR] Controller MAC: BC:C7:46:86:8C:47
[BT][PAIR] USB pairing data acquired
[BT][PAIR] Link key installed

[BT][STATE] Connected=0 Remembered=1 Authenticated=0

[BT][AUTH] Calling BluetoothAuthenticateDeviceEx
[BT][AUTH] Result=0
[BT][AUTH] GetLastError=0

[BT][STATE] Connected=1 Remembered=1 Authenticated=1

[BT][HID] Enabling HID service 00001124-0000-1000-8000-00805F9B34FB

[BT][PNP] Waiting for BTHENUM HID PDO
[BT][PNP] BTHENUM device found

[BT][PNP] Waiting for HidBth/HID child
[BT][PNP] HID devnode found
[BT][PNP] DevNodeStatus=DN_STARTED

[BT][PAIR] COMPLETE
```

Timeout/failure example:

```text
[BT][AUTH] Authentication timeout after 5000 ms
[BT][AUTH] Connected=0 Remembered=1 Authenticated=1
[BT][AUTH] Windows reports authenticated without live connection
```

That state would be extremely useful diagnostically.

---

# Design Principle

The pairing code should distinguish these concepts:

```text
credential installed
device remembered
device logically paired
ACL connected
link authenticated
link encrypted
HID profile enabled
BTHENUM enumerated
HID device started
```

Do not collapse all of them into a single boolean such as:

```text
paired = true
```

They are distinct stages in the Windows Bluetooth stack.

---

# Preferred Implementation Order

## Phase 1 — Better Instrumentation

Add detailed Bluetooth and PnP state logging.

No architectural changes yet.

## Phase 2 — User-Mode Authentication

Try:

```text
link-key injection
    ↓
BluetoothAuthenticateDeviceEx
    ↓
verify state
    ↓
BluetoothSetServiceState
```

## Phase 3 — Reduce Injected Pairing State

If Windows says the device is already authenticated before the real link exists, stop injecting final-state metadata where possible.

Inject the credential and let Windows complete authentication.

## Phase 4 — Deterministic PnP Waits

Replace fixed delays with:

```text
wait for condition
bounded timeout
retry/backoff
```

## Phase 5 — Kernel Preauth Helper

Only if user-mode APIs cannot force the required live security transition, investigate a very small Bluetooth profile/client helper driver using:

```text
BRB_L2CA_OPEN_CHANNEL
CF_LINK_AUTHENTICATED
CF_LINK_ENCRYPTED
```

The helper should exist solely to force/verify the underlying Bluetooth security state before handing normal operation back to the built-in Windows HID stack.

---

# Relevant Windows APIs / Structures

User-mode Bluetooth:

```text
BluetoothGetDeviceInfo
BluetoothAuthenticateDeviceEx
BluetoothSetServiceState
BLUETOOTH_DEVICE_INFO
```

PnP / Configuration Manager:

```text
CM_Get_DevNode_Status
```

Bluetooth driver DDI:

```text
BRB_L2CA_OPEN_CHANNEL
BRB_L2CA_UPDATE_CHANNEL
CF_LINK_AUTHENTICATED
CF_LINK_ENCRYPTED
```

Bluetooth socket options:

```text
SO_BTH_AUTHENTICATE
SO_BTH_ENCRYPT
```

Bluetooth HID service:

```text
UUID:
00001124-0000-1000-8000-00805F9B34FB

HID Control PSM:
0x11

HID Interrupt PSM:
0x13
```

---

# Microsoft Documentation References

Bluetooth device information:

https://learn.microsoft.com/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_info_struct

`BluetoothAuthenticateDeviceEx`:

https://learn.microsoft.com/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothauthenticatedeviceex

`BluetoothSetServiceState`:

https://learn.microsoft.com/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothsetservicestate

Bluetooth socket options:

https://learn.microsoft.com/windows/win32/bluetooth/bluetooth-and-socket-options

Creating an L2CAP client connection:

https://learn.microsoft.com/windows-hardware/drivers/bluetooth/creating-a-l2cap-client-connection-to-a-remote-device

`BRB_L2CA_OPEN_CHANNEL`:

https://learn.microsoft.com/windows-hardware/drivers/ddi/bthddi/ns-bthddi-_brb_l2ca_open_channel

`BRB_L2CA_UPDATE_CHANNEL`:

https://learn.microsoft.com/windows-hardware/drivers/ddi/bthddi/ns-bthddi-_brb_l2ca_update_channel

Bluetooth PSM definitions:

https://learn.microsoft.com/windows-hardware/drivers/ddi/bthddi/ns-bthddi-_brb_psm

Accessing SDP service information:

https://learn.microsoft.com/windows-hardware/drivers/bluetooth/accessing-sdp-service-information

---

# Current Working Theory

The most likely root cause is not that BetterJoy2 lacks the correct bond or link key.

The likely problem is that BetterJoy2 has successfully created the **credential and logical pairing state**, but Windows has not necessarily completed the **real radio-level authentication/encryption and HID PnP lifecycle** before HID is enabled.

Therefore the core fix is:

```text
PRELOAD CREDENTIAL
      ↓
FORCE REAL BT CONNECTION
      ↓
FORCE/VERIFY AUTHENTICATION
      ↓
FORCE/VERIFY ENCRYPTION
      ↓
ENABLE HID
      ↓
WAIT FOR BTHENUM
      ↓
WAIT FOR HID START
```

rather than:

```text
INJECT PAIRING STATE
      ↓
ENABLE HID
      ↓
HOPE WINDOWS FINISHES EVERYTHING
```

That distinction should be the foundation of the next BetterJoy2 Bluetooth pairing implementation.
