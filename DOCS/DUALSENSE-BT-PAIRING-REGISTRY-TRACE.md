# DualSense Bluetooth pairing registry trace

The `OMG 2 attempts` pairing work keeps the production handoff unchanged while adding an
optional, passive registry timeline for diagnosing Windows Bluetooth races.

## Pairing order preserved

For a fresh bond, the controller path remains:

1. Clear the controller's previous host/key with feature report `0x0A` and verify the host MAC
   through report `0x09`.
2. Write the selected host MAC and generated 16-byte link key to the controller with `0x0A` and
   verify the host MAC through `0x09`.
3. Reassert the same `0x0A` record and verify it again.
4. Commit that exact key to Windows' BTHPORT key store.
5. Send Bluetooth control report `0x08` (Bluetooth ON), suppress the USB controller path, and
   release the USB HID owner immediately.
6. Let Windows create the live Bluetooth device/HID records, then require the existing sustained
   Bluetooth input dwell before treating pairing as confirmed.

Existing Windows bonds still use the gentle reconnect path. This diagnostic feature does not add
delays, extra `0x0A` writes, adapter restarts, inquiries, service toggles, PnP changes, or HID
handle operations to that flow.

## What is captured

When the existing `DebugLogging` option is enabled, BetterJoy starts one background trace for the
controller MAC. It runs for up to 90 seconds and uses read-only `RegNotifyChangeKeyValue`
subscriptions. The pairing/HID thread only starts the worker and queues short stage markers.

Snapshots include:

- `BTHPORT\Parameters\Keys` values for every local adapter;
- `BTHPORT\Parameters\Devices\<controller>`;
- `BTHPORT\Parameters\PerDevices` adapter records;
- the controller's `Enum\BTHENUM\Dev_<MAC>` record;
- DualSense/DualSense Edge HID service branches under `Enum\BTHENUM` and `Enum\HID`.

The trace records both registry-triggered changes and pairing milestones such as
`controller-bond-cleared`, `controller-bond-written`, `windows-link-key-committed`,
`bluetooth-on-sent`, HID setup completion, retry timeouts, and sustained pairing confirmation.
Identical change-triggered snapshots are deduplicated; explicit milestone snapshots are retained.

## Log location and security

The output is:

```text
%APPDATA%\BetterJoy2\dualsense_pairing_registry_debug.log
```

When service/shared configuration is enabled, it is instead under:

```text
%PROGRAMDATA%\BetterJoy2\dualsense_pairing_registry_debug.log
```

This is temporary diagnostic data. For comparison with a known-good registry capture it includes
the raw Classic Bluetooth link-key value. Delete or protect the file after testing; the trace is
disabled automatically when `DebugLogging` is off.

## Why this is useful

The trace distinguishes “the controller accepted the pairing record,” “Windows wrote a BTHPORT
key/device record,” “BTHENUM/HID nodes appeared,” and “the live HID link held.” Those events can
occur at different times, and a failed first attempt may leave records that make a second attempt
look like an existing bond. Capturing them asynchronously avoids turning the observer into another
timing-sensitive participant in the pairing procedure.
