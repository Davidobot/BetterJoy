# Controller architecture refactor plan

Status: **all 7 steps done and committed.** The class-hierarchy refactor this
document describes is complete. Written after the DualSense baseline work
exposed real structural risk in how `Joycon.cs` shares code across device
types - that risk is resolved: `JoyconController`/`ProController`/
`SnesController`/`N64Controller`/`DualSenseController` are real, independent
types under `NintendoController`/`Controller`, and the `isPro`-superset-flag
pattern that caused a real incident is gone from the type system. Step 6
(`GyroMath.cs` extraction) was done last, out of numeric order, since it was
explicitly the highest-risk piece (this gyro/IMU pipeline has a documented
history of "looked correct, broke real hardware behavior" - see "What must
not regress" below) and got its own dedicated planning pass rather than
being rushed through after step 5's six sub-steps. See "Suggested migration
approach" below for the full per-step breakdown and commit references, and
`clever-wiggling-rocket.md`'s git history for step 5's and step 6's
sub-step-level plans (the file gets overwritten between planning sessions,
so only the current plan is there - git log the file if a past plan's exact
text is needed).

Remaining, explicitly out of scope for this document (see "Known issues" and
"Related, deferred" below for what's left): the `HasGyro`/`HasDualSticks`
SNES/N64 capability-accuracy gap step 5 found and deliberately didn't fix,
the settings-architecture migration (a separate, unrelated plan later in
this doc), and the doc's original "true static functions" vision for the
gyro pipeline (step 6 did a pure mechanical move only - see its entry
below for why).

## Guiding principles for how this gets executed

- **Get each step right the first time, not "ship and iterate through
  regressions."** The DualSense baseline work that motivated this refactor
  moved fast and fixed several real regressions along the way - acceptable
  for a fast-moving feature build, not acceptable for restructuring code
  three other controller types already depend on correctly. Each migration
  step below should be verified against the checklists before moving to the
  next one, not batched up and debugged together at the end.
- **Readability and human-understandability are explicit goals of this
  refactor, not just a side effect of reducing risk.** A large part of why
  this is worth doing at all is that `Joycon.cs` today is hard for a human
  to reason about - device-identity checks interleaved through shared
  logic, one class doing five things. Prefer clear, well-named
  abstractions (the capability properties, the `GyroMath.cs` split below)
  over clever ones, even where a cleverer approach might be marginally
  shorter.
- **Treat close reading of this code during each step as a chance to catch
  latent bugs, not just move code around.** This refactor already surfaced
  two real ones just from the investigation that produced this plan (see
  "Known issues found so far" below) - moving/reading code this closely is
  a genuine opportunity to catch things that would otherwise sit
  undiscovered until they cause a real problem later. The rule from
  [[leave-ahrs-stick-alone]] still applies: notice and document a
  suspicious behavior, don't silently fix it in the same pass unless it's
  squarely inside what that step is already touching and the user has
  confirmed it - especially anywhere near gyro/IMU. Add anything found to
  the running list below so it isn't lost between sessions.

## Known issues found so far (not yet fixed, not part of this plan's scope to fix)

- `MapToDualShock4Input` has no `isDualSense` branch - DualSense triggers
  are digital-only on DS4 output. **Not a bug to fix** - explicit product
  decision, DualSense doesn't get DS4-output support at all (see "What
  moves into `DualSense.cs`" below).
- ~~`connection` (transport byte, USB/BT) is set once in the constructor
  from the *initial* `isUSB` value and never updated again~~ - **fixed, step
  7**: `DualSenseController.ReceiveRaw` now updates `connection` alongside
  `isUSB` at the same place `isUSB` itself gets corrected per-packet.
- `Detach()`'s Nintendo-only "let the controller talk to Bluetooth again"
  handshake (the `0x80,0x05`/`0x80,0x06` output report) is gated on
  `isUSB`, not on `UsesNintendoProtocol` - and `isUSB` is `true` for a
  USB-connected DualSense too (see its own `ReceiveRaw` branch). A
  DualSense detached while connected over USB would get sent these
  Nintendo-specific bytes. Found while extracting `Detach()`'s shared
  shell into `Controller` (step 2); preserved as-is rather than fixed,
  since fixing it is a real behavior change, not a mechanical move -
  worth fixing with a one-line gate change (`UsesNintendoProtocol` instead
  of `isUSB`) whenever DualSense-over-USB detach is actually exercised.
- `SnesController.HasGyro`/`HasDualSticks` and `N64Controller.HasGyro`
  answer `true` even though SNES has no gyro/accel hardware and no sticks
  at all, and N64 has no gyro/accel hardware either - both leaf types
  deliberately restate the exact (wrong-for-them) answers the old
  `isPro`-superset-flag `Joycon` gave, rather than correcting them, when
  they were split out in step 5. The real, currently-exercised gates
  (`HasSticks`/`HasImuHardware` on `NintendoController`) are already
  correct for both types - only the *public* capability contract is
  inaccurate. `N64Controller.HasDualSticks` specifically can't just be
  flipped to `false` without a closer look first: `Controller.
  MapToXbox360Input`'s paired-button-population branch (inside
  `NintendoController.ProcessButtonsAndStick`) is what actually populates
  the `SHOULDER2_2`/`Y`/`MINUS` button bits N64's own `is64`-specific
  output mapping reads, so that coupling needs untangling before
  `HasDualSticks` can be made honest for N64 without breaking real N64
  button output.

## Why this is needed

`Joycon.cs` (4595 lines) represents five physical controller types - Joy-Con,
Pro Controller, SNES Controller, N64 Controller, and now DualSense - through
boolean flags (`isPro`, `isSnes`, `is64`, `isDualSense`) on one class, not
separate types. This grew organically: Joy-Con came first, Pro/SNES/N64 were
added as flag variants of it, and DualSense was bolted on the same way when
it was added, on the explicit reasoning (see `DOCS/DUALSENSE.md`) that a
separate class would require generalizing seven other files that reference
`Joycon` by concrete type - too large a refactor for a baseline milestone.

That reasoning was sound for getting DualSense working. It stopped being
sound once DualSense grew past baseline: during stick-calibration and
profile-identity work, a change scoped to DualSense (`PadMacAddress` becoming
a property that invalidated a caching field on every write) leaked into
Joy-Con's own internal MAC-reassignment path inside `Attach()` and broke
Joy-Con auto-join - two Joy-Cons would show combined in the UI while each
kept its own virtual controller. The fix was reverting to a narrower,
explicit invalidation call, but the incident is the actual motivating
problem: **there is no compiler-enforced boundary between "this code is
DualSense-only" and "this code is shared,"** so a correctly-scoped-looking
change can silently affect every device type.

The deeper structural cause, found while investigating: `isPro` is a
**superset flag**, not a category -

```csharp
this.isPro = isPro || isSnes || is64 || isDualSense;   // Joycon.cs:662
```

Every `if (isPro)` branch in the file - and there are dozens, including
`DoThingsWithButtons`, `OwnsGyroMouse`, `CalibrationConfirmPressed`,
`ExtractIMUValues`'s axis-offset selection, and both `MapToXbox360Input` and
`MapToDualShock4Input` - silently also fires for DualSense (and SNES/N64).
This is the real mechanism behind "a DualSense change touched Joy-Con
behavior": DualSense doesn't need to be explicitly checked to be affected by
a change to shared, `isPro`-gated logic.

Separately motivating this: the user wants to add controllers beyond
Nintendo/Sony over time. Doing that by adding a sixth boolean flag to
`Joycon` each time is exactly the pattern that caused this incident, and gets
worse with each addition.

## What must not regress

There are **three** highest-stakes surfaces in this codebase, not one - all
three get the same "re-verify after every step, not just at the end"
treatment, and none is subordinate to the others:

**1. PadId reassignment and Joy-Con auto-join/split.** Confirmed working
again as of this plan, after today's incident. This logic currently lives in
`Program.cs` (`CleanUp`, `ReassignPadIds`, `AssignPadId`, the auto-join
block) and in `Joycon.cs`'s `other` property and `virtualControllerSequence`
- unlike the gyro/mapping-profile pipelines, this is not staying where it is:
see "Virtual controller lifecycle" under "Target architecture" below for the
plan to extract it into its own module, with the Joy-Con-only split/join
quirk deliberately kept out of that module. Three prior commits (`fb3dca1`,
`3d1c38a`, `156dcf3`) each fixed a real regression in this exact area before
DualSense existed; this refactor must not reopen any of them - the
extraction is a chance to make this subsystem cleaner, not an excuse to
touch its actual behavior.

**2. Button/stick mapping to the virtual XInput (ViGEmBus) output** - for
Joy-Con-family (all four Nintendo types) *and* DualSense. This is
`MapToXbox360Input` and everything feeding it: the canonical `Button` enum/
`buttons[20]` array every device's parser populates, `stick`/`stick2`/
`CenterSticks`, and `triggerVal[]` for DualSense's analog triggers. Splitting
device-specific parsers apart must not change what any physical button/
stick/trigger produces on the virtual controller - this is the actual
player-facing output, more visible to the user than any internal state.
**DS4-output emulation (`MapToDualShock4Input`) is explicitly out of scope
for DualSense** - see the note under "Tier 3" below; do not add it, and
don't treat its absence as a bug.

**3. Gyro/IMU calibration and reading** - gyro-mouse, gyro-stick, the
auto-calibration trend-detection system (`DOCS/SMART-AUTO-CALIBRATION.md`),
roll compensation, and orientation handling (vertical/horizontal, solo vs.
paired Joy-Con axis handling). This pipeline is classified as "Tier 1 -
unambiguously shared" below because no device type branches its logic - but
shared-and-complex is not the same as low-risk, and this specific pipeline
has already burned a session: a prior gyro "fix" attempt (roll-compensation
sampling changed from continuous accel-trust-gated tracking to sample-once-
at-activation) was a net negative on real hardware and was fully reverted.
Treat any change that touches `ExtractIMUValues`, the gyro-mouse/gyro-stick
processing methods, `TryAutoCalibrate`, or the AHRS/`cur_rotation`/`gyr_g`
code with the same caution as PadId reassignment - it has a documented
history of "looked correct, broke real behavior," not just a theoretical
risk. The user has additional gyro work already planned (see "Related,
deferred" below); that work depends on this pipeline staying intact through
the refactor, not just on it compiling.

**Explicit, non-negotiable constraint**: gyro/IMU behavior for Joy-Con and
Pro Controller must keep working exactly as it does today, unchanged,
through every part of this refactor - both the class-hierarchy split above
and the settings-architecture migration below. This isn't just "don't
introduce bugs" - it's "the actual runtime numbers a Joy-Con/Pro user
experiences (sensitivity, deflection, roll compensation, auto-cal timing)
must not shift at all" as a side effect of restructuring where the code or
its config values live. The settings-architecture migration in particular
(moving `GyroMouseSensitivityX/Y` and the other gyro `AppSettings` keys to
per-profile) is the likeliest place for this to go wrong by accident - see
the explicit seeding requirement added there.

**Verification checklist to run after every step**, not just at the end:

*PadId / auto-join:*
1. Two Joy-Cons connect and auto-join into one logical pair; joy.cpl shows
   one virtual controller, not two.
2. Splitting the pair (double-click / physical gesture) restores two
   independent virtual controllers, each on its own distinct identity.
3. With three controllers connected (players 1/2/3), disconnecting player 2
   compacts player 3 down to player 2's slot without an unplug/replug.
4. Dropping to one controller reads as player 1.
5. A DualSense connecting over USB while already connected over Bluetooth
   still triggers the auto-disconnect-stale-BT-link behavior and resolves to
   one PadId, one virtual controller.

*XInput/ViGEmBus button-stick mapping:*
6. Every physical button on a solo Joy-Con, a joined pair, a Pro Controller,
   an SNES/N64 controller, and a DualSense produces the correct button on
   the virtual Xbox 360 controller in `joy.cpl`'s test page - re-run the
   full per-device button/stick/trigger list from `DOCS/DUALSENSE.md`'s
   verification section for DualSense specifically.
7. DualSense trigger analog range (0-max, not snapping) is preserved on the
   XInput output.
8. Quantitative stick range/deadzone check for Joy-Con/Pro/SNES/N64, not
   just "produces the correct button" - `stick_cal`/`deadzone` has three
   different init strategies feeding one consuming contract (`CenterSticks`,
   see "Fields shared by all types but initialized differently per type"
   above), and DualSense already gets an explicit range check (item 7) that
   the Nintendo family doesn't; confirm full-range deflection and centered-
   at-rest behavior numerically (or via `joy.cpl`'s axis readout), not just
   qualitatively.
9. No DS4 output target is created for a DualSense (confirms the
   out-of-scope decision above didn't get silently reversed).

*Gyro / IMU:*
10. Gyro-mouse movement direction and sensitivity are unchanged for a solo
    Joy-Con, a joined pair, and a Pro Controller - all three have distinct
    axis-selection code paths (`ExtractIMUValues`'s `isPro`/`isLeft`/solo-mode
    branches) that must each still be reachable and correct.
11. Gyro-to-stick mode (both left-stick and right-stick targets) still
    produces correct axis mapping and deflection limits.
12. Auto-calibration still triggers only on genuine stillness (trend
    detection), converges to a sane center, and does not fire for DualSense
    (`TryAutoCalibrate`'s guard) or contend across multiple connected
    controllers.
13. Roll compensation still uses continuous accel-trust-gated tracking, not
    sample-once-at-activation - re-read `DOCS/SMART-AUTO-CALIBRATION.md`
    and the roll-compensation history before changing anything here.
14. Vertical (self-paired) orientation and horizontal orientation both still
    produce correct gyro-mouse/gyro-stick output.

*Settings migration (distinct failure mode from "gyro feels right" above -
a pre-existing customized value silently reverting to default would pass
every item 10-14 above on a fresh/default profile and still be a real
regression):*
15. Before running the settings-architecture migration, dump every existing
    profile's resolved values for all newly-profileized keys
    (`GyroMouseSensitivityX/Y`, `StickScalingFactor`/`2`, the full list
    under "The sensitivity/behavior settings..." below) from
    `controller_mappings.xml`. After migration, dump the same values again
    and diff byte-for-byte - not "feels about right," an exact match for
    every profile that existed before the migration ran.
16. The eager whole-file backfill pass (see "Target design" below) actually
    ran and populated every pre-existing profile, not just newly-created
    ones - verify by inspecting `controller_mappings.xml` directly after
    first launch on the new code, not just by testing runtime behavior.

*Stress/races:*
17. Rapid repeated connect/disconnect/reconnect of the same controller
    (not just a single clean connect) - covers the documented
    `mappingProfileId volatile` race between the join/split thread and a
    controller's own poll thread (`Joycon.cs:108-110`), which a single-pass
    functional test would not exercise.

## Current-state map (from direct investigation, not assumption)

### Coupling to `Joycon` from the rest of the app - wide but shallow

Every external file that references `Joycon` by concrete type -
`Program.cs`, `MainForm.cs`, `Reassign.cs`, `ControllerMappings.cs`,
`HeadlessJoyconHost.cs`, `UpdServer.cs`, `IJoyconHost.cs` - only ever touches
generic, already-computed state: `PadId`, `PadMacAddress`, `state`/`state_`,
`other` (pairing), `battery`, `virtualControllerSequence`, `out_xbox`/
`out_ds4`, `GetButton(Button)`, `SetRumble(...)`, and the kind flags
themselves (`isLeft`/`isPro`/`isSnes`/`is64`/`isDualSense`) for icon/label/
profile-ID selection. None of it calls into Joy-Con protocol internals (SPI
reads, subcommands, calibration-dump parsing) - those stay fully encapsulated
inside `Joycon.cs` already. There is exactly **one** `new Joycon(...)` call
site (`Program.cs:623`).

This means the external coupling is not the hard part of this refactor - it
can realistically be satisfied by an interface/base-class reference instead
of the concrete type, with limited changes to those seven files (mostly:
change a field/parameter type, and replace kind-flag checks with a
`ControllerKind`-style property or pattern match).

One existing pattern already does exactly this kind of abstraction:
`ServiceControlProtocol.cs`'s `ControllerKind` enum (`Left, Right, Pro, Snes,
N64, DualSense` - explicitly append-only, wire-protocol-stable) and
`ControllerRecord` struct, built from a live `Joycon` in
`HeadlessJoyconHost.cs`. Adding a new physical controller type already means
adding one `ControllerKind` value today; this refactor should keep that
pattern, not replace it.

### Inside `Joycon.cs` - three tiers

**Tier 1 - unambiguously shared, device-agnostic (~78% of the file, ~3600
lines).** No device-specific logic; every device type uses this identically:
- The `state_` state machine and `Poll()`/`Begin()` thread lifecycle.
- The canonical `Button` enum and `buttons[20]` array - every device's report
  parser populates this same shape; everything downstream (mapping profiles,
  `MapTo*Input`, the UDP server) is built on it.
- `PadId`, `PadMacAddress`, `out_xbox`/`out_ds4`, the `other` pairing
  property (three-state contract: `null` = solo, `== this` = self-paired
  vertical, `== <other instance>` = real pair).
- The entire gyro-mouse/gyro-stick pipeline (~1200 lines) - structurally
  shared even though DualSense doesn't feed it real data yet.
- The mapping-profile/bind-simulation engine (`MappingValue`,
  `ProfileBoolOption`, `IsComboHeld`, `Simulate*`, ~350 lines).
- `CenterSticks()` and `CommitButtonState()` - already deliberately written
  as shared helpers both Joy-Con's `ProcessButtonsAndStick` and DualSense's
  `ParseDualSenseReport` call into, rather than duplicating.
- Auto-calibration (`TryAutoCalibrate`, ~180 lines) - DualSense opts out via
  one guard at the top; otherwise fully shared.

**Tier 2 - device-specific, near-zero shared code (~330-350 lines for
DualSense; ~630 lines for Joy-Con/Pro/SNES/N64).** Clean split candidates:
- DualSense: `ParseDualSenseReport`, `SendDualSenseRumble`,
  `SendDualSenseLightbar`, the CRC32 helpers, `Attach()`'s DualSense
  early-return branch, `ReceiveRaw()`'s DualSense branch, the raw-dump
  diagnostic logger.
- Joy-Con/Pro/SNES/N64: `Subcommand()`, `ReadSPI()`, the SPI-reading portion
  of `dump_calibration_data()`, the `Rumble` struct's HD-rumble encoding,
  `SendRumble()`, `Getn64StickValues`/`GetNormalizedValue` (N64-only).

**Tier 3 - mostly shared logic with a device-specific tweak spliced in.**
This is the actual danger zone - not the cleanly-separable Tier 2 code, but
methods that are *mostly* shared and have one device-specific branch mixed
into otherwise-generic logic, exactly the shape of today's incident:
- `RetireDuplicateConnections` - generic MAC-based dedup logic for every
  device type, with a DualSense-only Bluetooth-auto-disconnect tail spliced
  into the same method body.
- `MappingValue` - the single most shared method in the class (every bind
  lookup for every device type funnels through it), with a temporary
  DualSense-only diagnostic block inline inside it today.
- `DoThingsWithButtons`, `OwnsGyroMouse`, `CalibrationConfirmPressed`,
  `ExtractIMUValues` - not DualSense-specific themselves, but `isPro`-gated
  in a way that silently includes DualSense via the superset flag.
- `MapToXbox360Input` / `MapToDualShock4Input` - shared output-object
  skeleton, with per-device branches for buttons/sticks/triggers.
  `MapToDualShock4Input` has no `isDualSense` branch at all, so DualSense
  triggers are digital-only on DS4 output. **Explicit product decision (not
  a bug to fix): DualSense does not need DS4-output emulation at all** -
  emulating a DS4 from a controller that already speaks DS4-shaped input
  natively is redundant. `DualSenseController` should only ever implement
  the XInput/ViGEmBus (`MapToXbox360Input`-equivalent) output path; do not
  add DS4-output support for it during this refactor, and don't treat the
  missing branch as something that needs fixing.

### Fields shared by all types but initialized differently per type

The trickiest category - same field, same downstream consumers, different
setup per device:
- `stick_cal`/`stick2_cal`/`deadzone`/`deadzone2`: Joy-Con/Pro from SPI
  flash, SNES/N64 from `App.config`, DualSense hardcoded identity default in
  `Attach()` (no factory source exists). Three init strategies, one
  consuming contract (`CenterSticks`).
- `acc_neutral`/`acc_sensiti`/`gyr_neutral`/`gyr_sensiti`: SPI or config for
  Joy-Con-family, **never initialized at all** for DualSense (fine today
  since nothing reads gyro/accel off a DualSense, but a footgun for future
  gyro support - see "Related, deferred" below).
- `isUSB`: set once at connect time for Joy-Con (placeholder-serial
  heuristic), but re-derived every packet for DualSense based on observed
  report length. Same field, two different "who updates it and when"
  contracts.
- `connection` (transport byte): set once from the *initial* `isUSB` value
  and never updated again - can silently disagree with DualSense's
  per-packet-corrected `isUSB` after a transport switch. **Latent bug,
  independent of this refactor**, worth fixing while touching this code.

## Target architecture

### Shape: abstract base class, not a bare interface

Given how much is Tier 1 (genuinely shared state and behavior, not just a
shared contract), a bare interface would force re-implementing identical
logic in every subclass. An abstract base class carrying all of Tier 1,
with `protected virtual`/`abstract` hooks for Tier 2, fits what's actually
here.

```
Controller (abstract base - most of today's Joycon.cs Tier 1 content)
├── NintendoController (abstract - Subcommand/ReadSPI/SPI calibration plumbing shared
│   │                    by every Nintendo device; today's Tier 2 Joy-Con-family code)
│   ├── JoyconController   (Joy-Con-pair-specific: other/pairing, dual physical units)
│   ├── ProController      (single-unit, dual sticks, no pairing)
│   ├── SnesController     (single-unit, no sticks)
│   └── N64Controller      (single-unit, N64 stick remap)
└── DualSenseController    (new DualSense.cs - today's Tier 2 DualSense code)
```

`NintendoController` exists as an intermediate layer because Joy-Con/Pro/
SNES/N64 genuinely share the SPI/subcommand protocol layer with each other,
just not with DualSense - collapsing them straight into `Controller` would
either duplicate that protocol code four times or force DualSense to
inherit it unused. This mirrors what Tier 2's size comparison already showed
(Joy-Con-family code is ~630 lines of real shared protocol, not incidental
overlap).

### `GyroMath.cs` - core gyro/IMU algorithms as their own module, not class hierarchy

A second, orthogonal extraction alongside the `Controller` hierarchy above:
pull the actual gyro/IMU math - AHRS orientation update, gyro-mouse cursor
computation, gyro-stick axis mapping, roll compensation, the auto-
calibration trend-detection math - out of `Joycon.cs` into its own file,
as a set of algorithms that don't live on any device object at all. Today
this ~1200-line pipeline is already correctly device-agnostic *logic*
(Tier 1), but it's still written as instance methods on the device class,
with device-specific selection (which offset array, which axis, which sign
flip - see `ExtractIMUValues`'s `isPro`/`isLeft` branches) interleaved
directly into the math rather than applied as a distinct step before or
after it runs.

The target shape: `GyroMath.cs` holds the base algorithms as (ideally
static, input-in/output-out) functions that take already-normalized sensor
values and known parameters - no `isPro`/`isLeft`/device-identity checks
inside the math itself anywhere. Each `Controller` subclass is responsible
only for supplying its own **quirks** - which raw fields map to which axis,
what sign convention its hardware uses, any device-specific offset/bias -
as data or a thin adapter, before handing off to the shared algorithm. This
is the same "base algorithm + per-device quirk" shape the meaningful-scales
work (see "Related, deferred" below) already needs a home for - the
normalization step that work requires belongs naturally in this module, at
the boundary between "device quirk" and "base algorithm."

Benefits beyond code organization: algorithms with no device-identity
branching are far easier to reason about correctness for (matches the
priority below on getting gyro/IMU right the first time, not iterating
through regressions), and are natural candidates for isolated testing
against known input/output pairs, which nothing in this pipeline has today.

This is a genuinely separate extraction from the `Controller` class split -
worth sequencing deliberately (probably after `Controller`/
`DualSenseController` exist, since "what counts as a device quirk vs. base
algorithm" is easier to see clearly once DualSense's own gyro data actually
exists to compare against Joy-Con's) rather than assumed to happen in the
same pass.

**Cheap regression net worth capturing before this extraction, given this
pipeline has no tests today**: record a set of real sensor input -> output
pairs (raw gyro/accel values in, gyro-mouse cursor delta or gyro-stick axis
output out) from current Joy-Con/Pro behavior before touching any of this
code, the same way `dualsense_raw_debug.log` captured real hardware bytes
for the DualSense offset investigation this session. Diffing `GyroMath.cs`'s
output against these recorded pairs after the extraction is a much stronger
correctness signal than "moved the code, still compiles, feels right by
hand" - directly serving the "get it right the first time" guiding
principle above.

### Virtual controller lifecycle - PadId assignment/compaction/creation/destruction as its own module

A third extraction, alongside `Controller` and `GyroMath.cs`: the PadId
compaction/reassignment logic and virtual controller (ViGEmBus)
creation/destruction/teardown-and-recreate logic currently living in
`Program.cs` (`CleanUp`, `ReassignPadIds`, `AssignPadId`,
`CreateOutputControllers`) moves into its own module too - call it
`VirtualControllerLifecycle.cs` or similar. This is highest-stakes-surface
#1 from "What must not regress" above, but that section was about
*preserving* it as-is; this is about actively giving it the same clean,
standalone home `GyroMath.cs` gets, for the same reason - it's already
confirmed fully generic today (no `isPro`/`isSnes`/`is64` conditionals
anywhere in it), but it's still physically embedded in `Program.cs`
alongside device-scanning/enumeration code it has nothing to do with.

**The Joy-Con split/join ("pairing") quirk must not live in this module.**
Joy-Con is, as far as is known, the only device type that supports two
physical units combining into one logical controller - that's a
`JoyconController`-specific quirk, not something every controller needs to
understand. Concretely: the generic lifecycle module should only need to
know "does this controller currently want an active virtual controller, or
is it passively parked without one" (a state any controller type can be in,
e.g. also relevant to a solo Joy-Con that's temporarily inactive) - not
*why* a controller might be passive. The auto-join block's "which half is
the loser, destroy its virtual controller" logic stays firmly inside
`JoyconController`/wherever Joy-Con pairing lives, and calls into the
generic lifecycle module's primitives (create/destroy/reassign) rather than
duplicating them or the generic module knowing anything about pairing.

**This is harder than it sounds, verified against the real code, not just
assumed clean.** `ReassignPadIds` (`Program.cs:211-240`) is flag-free but
*not* actually pairing-agnostic today - it computes pairing-aware
active/passive state inline (`bool isPair = jc.other != null && jc.other !=
jc; ... active = jcHasOutput ? jc : jc.other; passive = ...`). Worse, the
auto-join block's loser-destroy logic (`Program.cs:740-799`) doesn't call
`AssignPadId`/`CreateOutputControllers` at all - it hand-rolls its own
`out_xbox`/`out_ds4` `Disconnect()` inline, duplicating rather than reusing
the primitives this new module is supposed to centralize. A naive verbatim
move of today's method bodies into the new module would relocate exactly
the pairing-shaped logic this section says must stay out of it.

**Resolution: step 3 (see "Suggested migration approach" below) explicitly
includes rewiring the auto-join block to call the new module's create/
destroy/reassign primitives instead of duplicating them**, while the
*decision* of which half is the loser stays wherever Joy-Con code currently
lives - still the undifferentiated `Joycon` class at step 3, since
`JoyconController` doesn't exist until step 5. This is a deliberately
accepted interim state, not a contradiction: the module's primitives become
pairing-ignorant at step 3 (satisfying the design goal below), but the
*caller* of those primitives for the pairing-aware decision doesn't move to
its own dedicated pairing-only class until step 5. Re-verify the full
PadId/auto-join checklist at step 3 specifically because this touches the
exact code three prior regressions (`fb3dca1`, `3d1c38a`, `156dcf3`) already
happened in.

**Design goal**: make compaction specifically an isolated, skippable step
in this module - not inlined into `CleanUp`'s removal loop the way it is
today - so that a future feature like "disable compaction" (leave gaps in
PadId numbering instead of closing them on disconnect) is a small, obvious
change to make, not a rewrite. This is a concrete test of whether the
extraction actually achieved the modularity goal: if adding that toggle
later requires touching more than this one module, the extraction wasn't
clean enough.

### Replacing the `isPro` superset flag: explicit capabilities, not inherited booleans

The root cause of today's incident was a boolean that means "this device
happens to share a code path with Pro Controller," checked in dozens of
places that didn't intend to reason about DualSense at all. Replace it with
explicit capability properties on `Controller`, defaulted per-subclass, that
callers check for what they actually mean:

```csharp
public abstract class Controller {
    public virtual bool SupportsPairing => false;   // only JoyconController overrides true
    public virtual bool HasDualSticks => true;
    public virtual bool HasAnalogTriggers => false;  // DualSenseController overrides true
    public virtual bool HasGyro => false;            // DualSenseController stays false until gyro lands
    public ControllerKind Kind { get; protected set; }  // maps directly to the existing wire-protocol enum
}
```

Every current `if (isPro)` / `if (isSnes)` / `if (is64)` / `if (isDualSense)`
call site gets re-examined against what it's actually testing for (pairing
eligibility? trigger encoding? gyro availability?) and rewritten against the
matching capability - not against a device-identity flag. This is the change
that actually prevents a repeat of today's incident: a future controller type
can only affect behavior it explicitly opts into, not behavior it happens to
inherit through a shared flag.

### What moves into `DualSense.cs`

`DualSenseController : Controller` gets everything already identified as
Tier 2 DualSense-specific: report parsing, rumble/lightbar output, CRC32
helpers, the raw-dump diagnostic logger, calibration-identity seeding. The
`RetireDuplicateConnections` Bluetooth-auto-disconnect tail and the
`MappingValue` diagnostic hook move here too, as overrides/hooks called from
the shared base rather than inline branches in shared methods - directly
fixing the Tier 3 danger-zone pattern that caused today's incident.

### What stays as shared infrastructure (do not move, do not fork)

- The gyro-mouse/gyro-stick pipeline and mapping-profile engine - stay on
  `Controller` as shared methods. Not worth splitting further right now;
  they're already correctly device-agnostic and splitting them adds risk
  without fixing anything broken.
- `state_`, `Button`, `buttons[20]` - the canonical wire contract every
  subclass's parser must still populate. Do not let any subclass introduce
  its own button representation.
- `ServiceControlProtocol.cs`'s `ControllerKind` enum - keep append-only as
  it already documents itself; map `Controller.Kind` to it directly.

## Modularity for future controller types

**The success criterion for this whole refactor, stated precisely**: once
someone can already interpret a new controller's raw byte stream (its HID
report layout - the genuinely unavoidable, external reverse-engineering
work, same as this session's DualSense byte-offset investigation), wiring
that up in BetterJoy should be relatively painless from there - not another
architectural fight. The hard part of adding a controller should be reading
its protocol, never fighting this codebase to plug a known protocol in.
Concretely: writing one new `XyzController : Controller` (or
`: NintendoController` if it's SPI/subcommand-based) file, registering its
VID/PID at the single `Program.cs:623` construction site, and adding one
`ControllerKind` value - not touching `Joycon.cs`, not risking every other
device type's behavior. That means:

1. A `Controller` reference (not `new Joycon(...)`) is what `Program.cs`
   constructs, via a small factory keyed on VID/PID - the one existing
   construction site becomes a dispatch point instead of a single
   constructor call.
2. `Program.j` becomes `ConcurrentList<Controller>` (or an interface if one
   still makes sense once the base class is designed) - a mechanical type
   change across the seven external files, not a logic change, per the
   coupling map above. This isn't optional or deferrable to "whenever" - C#
   generics are invariant, so it's forced the moment a second concrete leaf
   type needs to coexist in `j`, which is migration step 4 specifically
   (see "Suggested migration approach" below) - the single largest
   mechanical diff in this whole plan.
3. Every capability a new device type might need (pairing, dual sticks,
   analog triggers, gyro, adaptive triggers, touchpad, lightbar) is a
   virtual property or method on `Controller` with a safe default, not a
   boolean flag callers have to know to check.

### Before reverse-engineering a new controller's protocol, check for one first

For most major/licensed controller brands, the byte-stream reverse
engineering this section assumes as a given is usually already done
somewhere else - this session's DualSense work found real, working
Windows-native reference implementations (DS4Windows, DualSenseX,
JoyShockMapper, Ds2vJoy, PCXSense) that had already solved the exact byte-
offset problems being investigated from scratch, and licensed controllers
for a given platform brand tend to share a standardized report protocol
across models/vendors rather than each reinventing one. Before treating a
new controller's protocol as unknown: look for an existing open-source
Windows implementation (same reasoning as
[[verify-against-real-platform-data]] - same-platform reference source,
not a different OS's driver) and cross-check any offsets/layouts found
there against real captured bytes from the actual device, the same way
DS4Windows's DualSense offsets were confirmed against a real idle capture
this session rather than trusted blind. The reverse-engineering-from-
scratch case should be the exception, not the default assumption, for
"another major/licensed controller."

## Settings architecture: moving off global App.config toward per-profile settings

A second, related consolidation the user wants done as part of this same
planning effort: BetterJoy currently has **three overlapping config
surfaces**, and the oldest of them keeps accumulating settings that should
never have been global in the first place.

### Current state (found via direct grep, not assumption)

1. **`App.config`/the deployed `.exe.config`**, read via
   `ConfigurationManager.AppSettings[key]` scattered across essentially every
   file (60+ distinct call sites found in `Joycon.cs`, `Program.cs`,
   `MainForm.cs`, `Reassign.cs`, `ControllerMappings.cs`, `UpdServer.cs`,
   `DesktopInputBackend.cs`). `MainForm.cs:99`'s legacy Settings UI edits a
   `displayedConfigKeys`-driven list of these directly.
2. **A separate `settings` file**, read via a distinct `Config` class
   (`Config.Value`, `Config.GetDefaultValue`, `Config.ReloadSettingsOnly`,
   `Config.SaveCaliData`/`SaveStickCaliData`) - used for calibration data and
   as a fallback inside `ControllerMappings.LegacyValue`.
3. **`controller_mappings.xml`**, read/written via `ControllerMappings`
   (`Value`/`OptionValue`/`BoolOption`/`IntOption`, per `profileId`) - the
   modern, per-controller-profile store this whole refactor is built around.

Bridging (2)/(1) into (3): `ControllerMappings.AppConfigBackedKeys` (a fixed
set: `left_click`, `right_click`, `center_click`, `scroll_up`,
`scroll_down`, `clench_gyro`, `ratchet_gyro`) plus `GyroActivationKeys`
(`active_gyro_mouse`, `active_gyro_left_stick`, `active_gyro_right_stick`)
fall back to reading `App.config`/`Config` directly (`LegacyValue`/
`LegacyGyroActivationValue`/`LegacyOptionValue`) whenever a profile doesn't
have its own stored value - i.e. whenever a brand-new profile is created.
This is fragile in exactly the way the user is objecting to: it was the
direct cause of a real bug this session (`GyroToJoyOrMouse`'s stale
`"mouse"` default silently propagating `active_gyro_mouse=always` into every
newly-created profile, contradicting the file's own "Default: none"
comment) and it only covers a handful of keys - every other AppSettings key
below has **no per-profile override path at all today**, global is the only
option.

### The sensitivity/behavior settings that are global today but shouldn't be

Grepping every `ConfigurationManager.AppSettings[...]` call site turns up
roughly 60 keys. The large majority are gyro-mouse/gyro-stick/auto-
calibration tuning values that are read fresh, globally, every time they're
used - meaning **every connected controller, regardless of type or the
user's own per-controller preference, shares identical sensitivity**:
`GyroMouseSensitivityX/Y`, `GyroMouseScreenTraversalDegrees`,
`GyroMouseTighteningThreshold`, `GyroMouseSmoothingTimeMs/Threshold`,
`GyroStickSensitivityX/Y`, `GyroStickReduction`, `GyroStickTiltRangeX/Y`,
`GyroStickHybridRateWeight`, `GyroAnalogSensitivity`,
`GyroMouseRollCompensation`, `GyroMouseDirectCursor`, `GyroMouseScreenWrap`,
`GyroMouseLeftHanded`, `ChangeOrientationDoubleClick`, the `AutoCal*` family
(`AutoCalibrationEnabled`, `AutoCalStillDurationSeconds`,
`AutoCalTrendFraction`, `AutoCalArmDelaySeconds`,
`AutoCalButtonInactivitySeconds`, `AutoCalibrateStickCenter`),
`StickScalingFactor`/`StickScalingFactor2` (directly relevant to this
session's DualSense stick-calibration work), `EnableShakeInput`/
`ShakeInputSensitivity`/`ShakeInputDelay`, `LowFreqRumble`/`HighFreqRumble`/
`EnableRumble`. These belong on `Controller`/per-profile, not global - a
user should be able to want different gyro-mouse sensitivity on their
DualSense than on their Joy-Con, or shake-input on one controller and not
another, without it affecting every other connected device.

A smaller set is genuinely app-wide and correctly belongs in a global
section, not per-controller: `UseHidHide`, `IP`/`Port` (the Cemu motion
server), `AutoAddControllers`/`BlockAutoAddUSB`/`BlockAutoAddBluetooth`,
`PassiveScan`, `DoNotRejoinJoycons`, `HideStatus`, `StartInTray`,
`UnhideOnExit`, `MotionServer`, `UseFakerInput`, `AllowCalibration` (gates
whether the wizard is reachable at all), and the various `*DebugLogging`
toggles (`DualSenseDebugLogging`, `GyroMouseDebugLogging`,
`GyroStickDebugLogging`, `AutoCalDebugLogging`).

`ShowAsXInput`/`ShowAsDS4`/`GyroToJoyOrMouse` are already dead weight once
Default-profile seeding (below) exists - they only exist today as
`LegacyOptionValue`/`LegacyGyroActivationValue` migration sources, not as
settings anyone should edit directly going forward.

**This grep is a starting inventory, not a final classification** - a real
pass through this refactor should re-examine every one of these ~60 keys
individually, not just sort them into the two buckets above by pattern-
matching their names.

### Target design

- **A reserved `Default` profile** (sentinel profile ID, e.g. `"default"` -
  distinct in shape from every real device-derived ID like `pro:xxxxx` or
  `dualsense:xxxxx`, so it can never collide with one) replaces
  `AppConfigBackedKeys`/`LegacyValue`/`LegacyGyroActivationValue`/
  `LegacyOptionValue` entirely. `EnsureProfileSaved`/
  `SnapshotMissingProfileValues` seeds a brand-new profile from the `Default`
  profile's stored values instead of reading `App.config`/`Config`
  key-by-key. Per the user's framing ("if enabled"), this seeding is itself
  a toggle - when off, new profiles fall back to hardcoded in-code defaults
  instead of the user-edited `Default` profile. The `Default` profile is
  editable through the same Controller Profiles UI (`Reassign.cs`) real
  controllers use today - exact UI entry point (a permanent extra row in the
  controller dropdown? a separate button?) is a real design decision to
  settle before implementing, not assumed here.
- **A reserved global-settings profile** (another sentinel ID, e.g.
  `"__global__"`) reuses the exact same storage/persistence/file-watcher/
  `Reload()` machinery `ControllerMappings` already has - not a fourth
  config surface. This holds the app-wide keys listed above.
- **The sensitivity/behavior keys move to being genuine profile `OptionKey`s**
  (like `HomeLEDOn`/`SwapAB`/the `GyroStick*` deflection settings already
  are today) - each controller's own profile can override them, with the
  `Default` profile providing the seed value for new ones. `Controller`
  reads these the same way it already reads `MappingValue`/
  `ProfileBoolOption`/etc. today, not via `ConfigurationManager.AppSettings`
  at the point of use.
- `App.config` itself doesn't disappear entirely - whatever is genuinely
  process-bootstrap config (read once before any profile store could exist,
  e.g. very early startup behavior) can reasonably stay there, but that
  should end up being a small, deliberately-justified remainder, not the
  default assumption for a new setting the way it is today.
- **The legacy Settings UI's actual removal mechanism, stated precisely.**
  Verified: `MainForm.cs`'s `displayedConfigKeys` is built dynamically from
  `ConfigurationManager.AppSettings.AllKeys` (`MainForm.cs:91`), not a
  hardcoded list - simply changing what the *runtime* reads does not remove
  a key from this UI, since it just reflects whatever's still physically
  present in the deployed config file. The key must be physically removed
  from `App.config`/`.exe.config` (once its runtime read is fully migrated
  and the eager backfill below has run), or this UI must be repointed at
  the new profile-based store instead of `AllKeys`. Pick one explicitly
  when this is implemented - "either goes away or becomes a thin editor"
  isn't a mechanism by itself.
- **Reserved-profile UI rendering.** `IncludeDisconnectedProfiles`/
  `DisconnectedDisplayName` (existing, generic machinery) will render the
  `Default`/`__global__` sentinel profiles as ordinary "disconnected
  controller" entries (e.g. literally `"default (disconnected)"`) if reused
  naively for the Controller Profiles dropdown - these need to be
  explicitly special-cased there, not just left to "a real design decision
  to settle" as stated above; the failure mode if not special-cased is
  concrete and already knowable now, not a design ambiguity to defer.
- **Orphaned/stale profile cleanup is explicitly out of scope for this
  migration.** `ProfileIdFor` already carries a comment documenting the
  historical failure class that produces these (a device silently landing
  in the wrong prefix's branch before an ordering fix, e.g. this session's
  `pro:xxxxx` entry left over from an old DualSense mis-detection bug) -
  `DeleteProfile` already exists and is manual-only via `Reassign.cs`'s
  delete button. This migration does not add automatic pruning; orphans
  remain harmless and user-deletable exactly as they are today. Revisit
  only if it becomes an actual complaint, not preemptively here.
- **Non-negotiable migration-correctness requirement, with a concrete
  mechanism (not just an intention).** Moving a sensitivity/behavior key
  off `ConfigurationManager.AppSettings` must not change what an existing
  Joy-Con/Pro/DualSense profile actually does the moment this ships.
  Verified there's no existing precedent for this in the codebase:
  `ControllerMappings.Save()` writes `<controllerMappings version="2">`,
  but `Reload()` never reads or branches on that attribute - no version-
  gated migration pass exists anywhere today, and
  `SnapshotMissingProfileValues` only backfills a specific profile's
  specific key *lazily, on next write* - it does not proactively touch
  every profile already on disk at load time. If `AppSettings` reads are
  removed from the runtime path before some other mechanism guarantees
  every existing profile already has the newly-profileized keys, an
  existing user's customized `GyroMouseSensitivityX` (etc.) silently resets
  to a hardcoded default the instant this ships - exactly the outcome this
  requirement forbids. **Required mechanism**: on first load under the new
  code (detected via the reserved `Default`/`__global__` profiles not yet
  existing), run one eager, whole-file backfill pass over every profile
  currently in `controller_mappings.xml`, applying today's
  `SnapshotMissingProfileValues` semantics to all of them at once - not
  lazily, not per-key-on-next-write - persist the result, and only then is
  it safe to remove the `AppSettings` read from the runtime path. This
  backfill pass, and verifying it ran correctly, is itself a concrete
  implementation task for whichever step does this migration, not an
  assumed side effect.

### Suggested settings-migration steps

Given equal risk-bearing to gyro correctness (see the "non-negotiable"
requirement above), this gets the same step-by-step, checklist-gated
treatment as the class-hierarchy work below, not a single big-bang change:

1. Add the reserved `Default` and `__global__` profile plumbing to
   `ControllerMappings.cs` - storage/read/write only, no runtime call sites
   changed yet. Self-contained, doesn't touch `Joycon.cs`'s device branching
   at all.
2. Implement and run the eager whole-file backfill pass (see "Target
   design" above) against a copy of a real `controller_mappings.xml` -
   verify checklist items 15-16 before this ever touches a call site that
   currently reads `ConfigurationManager.AppSettings`.
3. Move the genuinely app-wide keys (`UseHidHide`, `IP`/`Port`,
   `AutoAddControllers`, etc.) to reading from the `__global__` profile.
   Lower risk than the sensitivity keys - not gyro/IMU-adjacent.
4. Move the sensitivity/behavior keys (`GyroMouseSensitivityX/Y`,
   `StickScalingFactor`/`2`, the `AutoCal*` family, etc.) to reading from
   each controller's own profile via `Default`-seeded `OptionKey`s. This is
   the step the gyro/IMU "must not regress" constraint applies to most
   directly - run checklist items 10-16 in full here, not just at the end.
5. Repoint or remove the legacy Settings UI (`MainForm.cs`'s
   `displayedConfigKeys`/`AllKeys` mechanism) once its underlying keys have
   actually been removed from the deployed config, per the mechanism
   decided under "Target design" above.
6. Physically remove the migrated keys from `App.config`/the deployed
   `.exe.config`, and delete the now-dead `AppConfigBackedKeys`/
   `LegacyValue`/`LegacyGyroActivationValue`/`LegacyOptionValue` bridge
   code and the `ShowAsXInput`/`ShowAsDS4`/`GyroToJoyOrMouse` keys it
   existed to serve.

### Relationship to the `Controller` class refactor above

These are two separable concerns (class hierarchy vs. settings storage) but
they touch the same surface - every sensitivity read this section moves off
`ConfigurationManager.AppSettings` is a read that would otherwise need to be
re-homed again when `Controller`/`DualSenseController` are extracted.
Sequencing worth considering once both plans are final: doing settings-
migration steps 1-2 above first (self-contained, lower risk, doesn't touch
`Joycon.cs`'s device branching at all) before or alongside class-hierarchy
migration step 1 below, so the capability-property pass and the
sensitivity-key read migration (settings-migration step 4) happen together
per call site instead of touching the same lines twice.

## Suggested migration approach

Given the stakes (PadId reassignment breaking is the specific failure mode
to avoid, and it's already broken once this session), this should be
incremental and independently testable, not a single large rewrite:

1. Introduce the capability properties on the existing `Joycon` class first,
   *without* creating any new class - replace `isPro`/`isSnes`/`is64`/
   `isDualSense` checks at each call site with the matching capability
   check. **Scope includes the two external files with the same ordering-
   dependent pattern**, not just `Joycon.cs`: `HeadlessJoyconHost.cs:820-826`
   (`ControllerKind` derivation) and `ControllerMappings.ProfileIdFor`/
   `ProfileFor` both already have `isDualSense`-checked-ahead-of-`isPro`
   comments documenting this exact hazard - fold them into the same
   capability-property pass rather than leaving them on the old flag
   pattern. Verify the full checklist above after this step alone, since it
   touches every `isPro`-gated method in the file plus these two.
2. Extract `Controller` as a base class with `Joycon` (renamed or not) as
   its first/only subclass initially - a pure mechanical move of Tier 1
   content, zero behavior change. Verify again.
3. Extract the virtual controller lifecycle module (`CleanUp`/
   `ReassignPadIds`/`AssignPadId`/`CreateOutputControllers`) out of
   `Program.cs` - doesn't depend on DualSense specifics, only on the
   `Controller` base existing to operate over, so it can happen here rather
   than waiting for step 4. **Explicitly includes rewiring the auto-join
   block's loser-destroy logic** (`Program.cs:740-799`, today hand-rolls its
   own `out_xbox`/`out_ds4` disconnect instead of calling the primitives
   this module centralizes) to call into the new module instead of
   duplicating it - see "Virtual controller lifecycle" above for why this
   is required, not optional, for the extraction to actually be clean. The
   *decision* of which half is the loser stays wherever Joy-Con code
   currently lives (still the undifferentiated `Joycon` class at this
   point, since `JoyconController` doesn't exist until step 5 - an accepted
   interim state). Verify the PadId/auto-join checklist thoroughly here -
   this is the module directly responsible for it, and the exact code three
   prior regressions (`fb3dca1`, `3d1c38a`, `156dcf3`) already happened in.
4. Extract `DualSenseController`/`DualSense.cs` as a second subclass,
   moving Tier 2 DualSense code out of the now-slimmer base. **This is the
   step where `Program.j` must change from `ConcurrentList<Joycon>` to
   `ConcurrentList<Controller>`** - C# generic collections are invariant, so
   this can't be deferred once a second concrete leaf type needs to coexist
   in `j`; it forces every one of the seven coupling files' `Joycon`-typed
   signatures/loops/lambdas to change in the same commit, making this the
   single largest mechanical diff in the whole plan. **Also grep all seven
   coupling files for `GetType() ==`/`GetType() !=` against `typeof(Joycon)`
   before this step** - found so far: `MainForm.cs:557` (gates test-rumble-
   on-click) and `MainForm.cs:594` (gates ALL left/right-click behavior on a
   controller icon - opening Controller Profiles, join/split, orientation
   double-click). These are exact-type checks, not `is Joycon` (which the
   same file correctly uses elsewhere, lines 693/1148/1277) - invisible to
   an `isPro`/`isDualSense` grep, and would silently make clicking a
   DualSense icon do nothing the moment `DualSenseController` exists as a
   real sibling type (no crash, just a dead click). Fix to `is`/pattern-
   match as part of this step's scope. Verify the full checklist across
   *every* device type here, not just the DualSense-specific item - this
   step's blast radius is genuinely all seven files at once.
5. **Done.** Split Joy-Con/Pro/SNES/N64 apart into their own subclasses
   under `NintendoController` - completed as six sub-steps (A/B/C/D1/D2a/
   D2b, one commit each), not the single step this line originally
   described; full breakdown in `clever-wiggling-rocket.md`. By the time
   this step started, steps 1-4 had already shrunk `Joycon.cs` from 4595
   lines down to 910, so it was considerably less risky than "the largest,
   most interleaved code in the file" implied when this line was written.
   `Controller.other`/`gyroMouseOrientationPartner` now type as
   `JoyconController` specifically (only Joy-Con pairs), not the wider
   `NintendoController` an earlier code comment had suggested as the
   "natural home." `Controller.MapToXbox360Input`/`MapToDualShock4Input`
   deliberately kept their existing inline `is64`/pairing branches rather
   than becoming per-leaf overridable hooks - not required for the split,
   revisit only if it becomes an actual problem. See the "Known issues"
   section above for the `HasGyro`/`HasDualSticks` SNES/N64 accuracy gap
   this step found and deliberately didn't fix.
6. **Done**, last (deliberately, as the highest-risk step remaining - see
   `clever-wiggling-rocket.md`'s git history for the full plan). An Explore
   pass first confirmed most of this section's original ambition had
   already happened as a side effect of steps 1-5: `MadgwickAHRS.cs`/
   `GyroMouseOrientation.cs`/`GyroMousePlayerSpace.cs` were already separate,
   fully generic files, and the real device-quirk boundary (`isLeft`/
   `SupportsPairing`/offset-array selection) was already correctly isolated
   inside `NintendoController.ExtractIMUValues`, not smeared through shared
   code. What was left was a large block of already-generic gyro-mouse/
   gyro-stick/auto-calibration methods still physically living in
   `Controller.cs` - a pure organizational problem, not a device-coupling
   one. Executed as **the pure mechanical move only** (`GyroMath.cs` as a
   second `partial class Controller` file, same pattern
   `VirtualControllerLifecycle.cs` established for `JoyconManager` in step
   3) - `Controller.cs` dropped from 3580 to 1767 lines, zero logic changes,
   zero call-site changes, independently re-verified against the pre-move
   source before committing given this pipeline's regression history. The
   doc's original "ideally static, input-in/output-out functions" end state
   for this pipeline is **explicitly not done** - that's a materially
   larger, higher-risk transformation (per-instance state like bias
   windows/neutral frames/smoothing filters would need to become explicitly
   threaded parameters) left as a genuine future candidate, not attempted
   here. Device ownership/coordination logic (`OwnsGyroMouse`,
   `PairHasActiveGyroMouse`, `GetButtonsForVigem`, `CanonicalButtonToLocal
   VigemIndex`) and `DoThingsWithButtons` itself stay on `Controller.cs` -
   they're pairing-specific coordination, not gyro algorithm.
7. **Done**, ahead of step 6 - fixed the `connection`/`isUSB` staleness bug
   as a small, self-contained change rather than waiting for another step
   to happen to touch those fields, since it was already precisely
   diagnosed and low-risk in isolation. Do **not** add DS4-output support
   to `DualSenseController` - see the explicit decision above.

Each step should be its own commit, buildable and testable independently -
not one large branch merged all at once.

## Related, deferred (not part of this refactor, noted so they aren't lost)

- **Converting `GyroMath.cs` to true static/parameterized functions.** Step
  6 did only a pure mechanical move (same instance methods, same fields,
  just relocated to a second `partial class Controller` file) - it did not
  attempt the doc's original "ideally static, input-in/output-out
  functions" target shape. That would mean threading substantial
  per-instance state (bias windows, neutral frames, smoothing filters,
  pending deltas) through explicit parameters instead of reading instance
  fields - a materially larger, higher-risk change than solving the
  "these methods are stuck in a 3580-line file" problem the mechanical
  move already fixed. Worth doing eventually if/when isolated unit testing
  of this pipeline becomes a real goal (there's no test project in this
  repo today - see the diagnostic CSV/log writers, `gyro_stick_debug.csv`
  in particular, as the closest thing to a regression-capture mechanism
  currently available), not assumed necessary on its own.
- Gyro support for DualSense - user has specific plans "in my head" not yet
  written down; surface them into this document (or a new one) before
  starting, since `HasGyro`/the acc/gyr-neutral initialization gap above
  directly affects how that gets designed.
- **Meaningful, device-agnostic gyro sensitivity scales.** Explicitly not
  part of this rebase, but directly relevant to DualSense gyro support
  landing later: today's gyro tuning values (`GyroMouseSensitivityX/Y`,
  `GyroStickSensitivityX/Y`, `GyroStickReduction`, `GyroAnalogSensitivity`,
  `AHRS_beta`, and friends) are numbers tuned against Joy-Con's raw gyro
  output - there's no reason to assume they'd produce equivalent real-world
  feel if fed unchanged from a different controller's gyro (different
  sensor scale/sample rate). The goal: define a real, physically-meaningful
  scale (e.g. true degrees/second, normalized before any sensitivity
  multiplier is applied) that every device's gyro pipeline converts into,
  then transpose today's Joy-Con-tuned defaults onto that scale so a
  Joy-Con/Pro user's experience is unchanged - **and**, if the scale is
  chosen well, those same transposed defaults should feel sane on DualSense
  too without separate per-device tuning. This is the actual test of
  whether the new scale is meaningful, not just renamed. Worth doing before
  or alongside DualSense gyro support, not before the class-hierarchy/
  settings-architecture work above - but wherever `ExtractIMUValues`/
  `gyr_g` (the point raw sensor data enters the shared pipeline today) ends
  up living after this refactor is exactly where a normalization step would
  need to be inserted, so keep this in mind when placing that code, even
  though the scale work itself isn't happening now.
- The three near-identical async diagnostic-log-writer implementations
  (`DualSenseRawDumpWriterLoop`, `AutoCalDiagWriterLoop`,
  `GyroStickDiagWriterLoop`) are a good candidate for a single shared
  utility once `Controller` exists to hang it off of - not urgent.
- `DOCS/DUALSENSE.md`'s "out of scope this pass" section is stale (rumble
  has since shipped) - update whenever this refactor touches that area.
