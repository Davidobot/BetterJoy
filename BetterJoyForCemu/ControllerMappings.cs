using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    // One logical controller shown in Controller Profiles. A joined L+R pair is one profile;
    // the same two Joy-Cons when split are two different profiles. ConnectionSequence is the
    // newest physical member's creation order and lets the dialog select the most recently
    // connected logical controller without relying on PadId (which is only a transient slot).
    public sealed class ControllerProfileInfo {
        // The concrete stored row this entry edits - still orientation-specific for a Joy-Con
        // (solo-left:/vertical-left:), because that is what every read and write in the profile
        // system is keyed by. GroupId below is what the list shows and selects on.
        public string ProfileId { get; set; }
        // Identity of the physical device, which for a Joy-Con folds its two orientation rows
        // into one entry (see ControllerMappings.ProfileGroupId). Equal to ProfileId for every
        // other kind, including a joined pair.
        public string GroupId { get; set; }
        public string DisplayName { get; set; }
        public long ConnectionSequence { get; set; }
        public bool IsConnected { get; set; }
        public ControllerKind? Kind { get; set; }
        public int PadId { get; set; } = -1;
        public bool IsUsb { get; set; }
        public string AudioEndpointNameHint { get; set; }

        public override string ToString() {
            return DisplayName;
        }
    }

    // Per-logical-controller special-button mappings. The old settings/App.config values remain
    // the fallback for controllers which do not have a profile yet, preserving every existing
    // user's mappings. The first edit to a profile snapshots all fallback values so controllers
    // become fully independent rather than retaining a hidden dependency on later global edits.
    public static class ControllerMappings {
        public sealed class CustomBinding {
            public string Input { get; private set; }
            public string Output { get; private set; }
            public bool Rebind { get; private set; }

            public CustomBinding(string input, string output) {
                Input = input ?? String.Empty;
                Output = output ?? String.Empty;
            }

            public CustomBinding(string input, string output, bool rebind) {
                Input = input ?? String.Empty;
                Output = output ?? String.Empty;
                Rebind = rebind;
            }
        }

        public sealed class CustomActionChoice {
            public string Value { get; private set; }
            public string Label { get; private set; }
            public string Category { get; private set; }
            public DesktopInputAction Action { get; private set; }
            public string DisplayBinding { get; private set; }

            public CustomActionChoice(string value, string label, string category,
                    DesktopInputAction action, string displayBinding = null) {
                Value = value;
                Label = label;
                Category = category;
                Action = action;
                DisplayBinding = displayBinding;
            }
        }

        // Stable, readable values are persisted in controller_mappings.xml. Keeping the display
        // label separate lets the UI improve wording without invalidating saved profiles.
        public static readonly CustomActionChoice[] CustomActionChoices = {
            new CustomActionChoice("act_media_play", "Play", "Media", DesktopInputAction.MediaPlay),
            new CustomActionChoice("act_media_pause", "Pause", "Media", DesktopInputAction.MediaPause),
            new CustomActionChoice("act_media_play_pause", "Play / Pause", "Media", DesktopInputAction.MediaPlayPause),
            new CustomActionChoice("act_media_stop", "Stop", "Media", DesktopInputAction.MediaStop),
            new CustomActionChoice("act_media_next", "Next track", "Media", DesktopInputAction.MediaNextTrack),
            new CustomActionChoice("act_media_previous", "Previous track", "Media", DesktopInputAction.MediaPreviousTrack),
            new CustomActionChoice("act_volume_up", "Volume up", "Media", DesktopInputAction.VolumeUp),
            new CustomActionChoice("act_volume_down", "Volume down", "Media", DesktopInputAction.VolumeDown),
            new CustomActionChoice("act_volume_mute", "Mute", "Media", DesktopInputAction.VolumeMute),
            new CustomActionChoice("act_ctrl_alt_delete", "Ctrl + Alt + Delete", "Windows", DesktopInputAction.CtrlAltDelete, "key_17+key_18+key_46"),
            new CustomActionChoice("act_ctrl_shift_escape", "Task Manager (Ctrl + Shift + Esc)", "Windows", DesktopInputAction.CtrlShiftEscape, "key_17+key_16+key_27"),
            new CustomActionChoice("act_alt_tab_next", "Next window (Alt + Tab)", "Windows", DesktopInputAction.AltTabNext, "key_18+key_9"),
            new CustomActionChoice("act_alt_shift_tab_previous", "Previous window (Alt + Shift + Tab)", "Windows", DesktopInputAction.AltShiftTabPrevious, "key_18+key_16+key_9"),
            new CustomActionChoice("act_alt_tab_left", "Alt + Tab + Left", "Windows", DesktopInputAction.AltTabLeft, "key_18+key_9+key_37"),
            new CustomActionChoice("act_alt_tab_right", "Alt + Tab + Right", "Windows", DesktopInputAction.AltTabRight, "key_18+key_9+key_39"),
        };

        public const string FileName = "controller_mappings.xml";
        public const string DefaultLightColor = "#0000FF";
        public const string ModeEnable = "enable";
        public const string ModeDisable = "disable";
        // Automatic BT third mode: restore/repair an existing PC bond a controller lost (e.g. to a
        // PS5), rather than the full from-scratch pairing "enable" does. See
        // AutomaticBluetoothPairingMode / DualSense's Repair fast path.
        public const string ModeRepair = "repair";
        public const string RumbleModeDisableWithGyro = "disable_with_gyro";
        public const string AudioModeRequireHeadphones = "require_headphones";
        public const string MicrophoneModeStartMuted = "start_muted";
        public const string PreferredTransportUsb = "usb";
        public const string PreferredTransportBluetooth = "bluetooth";
        public const string MicIndicatorModeDisabled = "disabled";
        public const string MicIndicatorModeInverted = "inverted";
        public const string MicIndicatorModeEnabled = "enabled";
        public const string MicIndicatorModeEnabledWhileDisabled = "enabled_while_disabled";
        // Initial USB-connect sleep policy for DualSense. Bluetooth = only sleep after an automatic
        // Bluetooth pair/repair/connect establishes while the controller was first plugged in over
        // USB; Enabled = same plus immediate USB-only sleep when no Bluetooth copy is already live;
        // Disabled = never sleep on initial USB connect. Long-press/inactivity/app-exit power-off
        // are separate options and do not read this key.
        public const string USBSleepOnConnectBluetooth = "bluetooth";
        public const string USBSleepOnConnectEnabled = "enabled";
        public const string USBSleepOnConnectDisabled = "disabled";
        // Default: BetterJoy never sends a lighting command at all for this profile - see
        // Program.cs's ApplyControllerProfileLighting. Disabled: same forced-black output as the
        // LightingOff runtime toggle, but as a persistent saved mode instead. OpenRGB: identical
        // hands-off behavior to Default, plus BetterJoy asks a locally running OpenRGB to rescan
        // for devices (OpenRgbRescan.cs) on connect and whenever HidHide visibility changes, so
        // OpenRGB picks the controller up without the user manually clicking Rescan there.
        public const string LightingModeDefault = "default";
        public const string LightingModeUser = "user";
        public const string LightingModeWheel = "wheel";
        public const string LightingModeWheelToggle = "wheel_toggle";
        public const string LightingModeBattery = "battery";
        public const string LightingModeDisabled = "disabled";
        public const string LightingModeOpenRgb = "openrgb";

        // Single source of truth for every "cycle through the modes of a dropdown" binding
        // (lt_haptics/rt_haptics/toggle_haptics/cycle_lighting) as well as the dropdowns themselves
        // (Reassign.cs populates .Items from these instead of a separately hand-written list) -
        // adding, removing, or reordering an entry here is the only place that needs to change
        // for both the UI and the binding's cycle order to pick it up.
        public static readonly (string Value, string Label)[] AdaptiveTriggerModes = {
            ("off", "Off"), ("resistance", "Resistance"), ("weapon", "Weapon"),
            ("vibration", "Vibration"),
        };
        public static readonly (string Value, string Label)[] RumbleModes = {
            (ModeEnable, "Enable"), (ModeDisable, "Disable"),
            (RumbleModeDisableWithGyro, "Disable with gyro"),
        };
        public static readonly (string Value, string Label)[] PreferredTransports = {
            (PreferredTransportBluetooth, "Bluetooth"),
            (PreferredTransportUsb, "USB"),
        };
        public static readonly (string Value, string Label)[] AutomaticBluetoothPairingModes = {
            (ModeEnable, "Enabled"), (ModeRepair, "Repair"), (ModeDisable, "Disabled"),
        };
        public static readonly (string Value, string Label)[] USBSleepOnConnectModes = {
            (USBSleepOnConnectBluetooth, "Bluetooth"), (USBSleepOnConnectEnabled, "Enabled"),
            (USBSleepOnConnectDisabled, "Disabled"),
        };
        // The lightbar pulse BetterJoy draws itself while a controller is parked in charge-only
        // USB waiting for a PS press (DualSenseController.MonitorChargeOnlyUsbWake). The firmware
        // never reaches its own charging state there, so without this the profile colour just
        // stays lit. Amber by default, matching what the real charge animation looks like.
        public const string ChargingIndicatorGlow = "glow";
        public const string ChargingIndicatorBattery = "battery";
        public const string ChargingIndicatorDisabled = "disabled";
        public static readonly (string Value, string Label)[] ChargingIndicators = {
            (ChargingIndicatorGlow, "Glow"), (ChargingIndicatorBattery, "Battery"),
            (ChargingIndicatorDisabled, "Disabled"),
        };
        public const string DefaultChargeGlowColor = "#A03000";
        public const int DefaultChargeGlowPeriodSeconds = 10;
        public const int MinimumChargeGlowPeriodSeconds = 1;
        public const int MaximumChargeGlowPeriodSeconds = 60;

        public static readonly (string Value, string Label)[] LightingModes = {
            (LightingModeDefault, "Default"), (LightingModeUser, "User"),
            (LightingModeWheel, "Wheel"), (LightingModeWheelToggle, "Wheel (toggle)"),
            (LightingModeBattery, "Battery"), (LightingModeDisabled, "Disabled"),
            (LightingModeOpenRgb, "OpenRGB"),
        };
        // The player-number indicator LEDs - DualSense's small ones below the touchpad
        // (DualSenseController.SetLEDByPlayerNum) and Joy-Con/Pro's SL/SR-adjacent ones
        // (NintendoController.SetLEDByPlayerNum). See PlayerLedEnabled's own comment for why the
        // unset/never-touched default differs by controller family instead of one dropdown
        // default applying everywhere: DualSense's have always been silently forced off (an
        // unintended side effect of WriteRetainedRumbleAndTriggerState's own valid_flag1 byte
        // claiming control of this field before this dropdown existed), while Nintendo
        // controllers have always shown them.
        public static readonly (string Value, string Label)[] PlayerLedModes = {
            (ModeDisable, "Disabled"), (ModeEnable, "Enabled"),
        };

        // Shared by every mode-cycling binding - advances from whichever entry matches current
        // (case-insensitively) to the next, wrapping around; an unrecognized/unset current value
        // is treated as if it were index 0, so the first press always lands on modes[1] rather
        // than silently doing nothing.
        public static string NextCycleValue((string Value, string Label)[] modes, string current) {
            int index = Array.FindIndex(modes,
                m => String.Equals(m.Value, current, StringComparison.OrdinalIgnoreCase));
            return modes[(Math.Max(0, index) + 1) % modes.Length].Value;
        }

        public static readonly string[] Keys = {
            "capture", "home", "guide", "mic_mute", "toggle_built_in_mic",
            "volume_up", "volume_down",
            "lt_haptics", "rt_haptics", "toggle_haptics", "cycle_lighting",
            "toggle_lighting", "color_wheel",
            "brightness_up", "brightness_down", "modifier",
            "sl_l", "sl_r", "sr_l", "sr_r", "shake",
            // active_gyro is retained only to migrate existing per-profile bindings from the
            // former global GyroToJoyOrMouse selector. New runtime/UI code uses the three
            // independent activation keys below.
            "reset_mouse", "active_gyro", "active_gyro_mouse",
            "active_gyro_left_stick", "active_gyro_right_stick",
            "left_click", "right_click",
            "center_click", "scroll_up", "scroll_down", "clench_gyro", "ratchet_gyro",
            "touchpad_click", "touchpad_tap", "touchpad_two_finger_tap",
            "touchpad_two_finger_scroll_up", "touchpad_two_finger_scroll_down",
            "active_touchpad_mouse", "active_touchpad_left_stick",
            "active_touchpad_right_stick",
            "touchpad_left_click", "touchpad_right_click", "touchpad_center_click",
            "touchpad_scroll_up", "touchpad_scroll_down", "touchpad_pointer_lock",
        };

        // Profile-owned behavior which historically lived in App.config. App.config remains the
        // migration/default source for profiles without an explicit option, but these values are
        // persisted beside bindings once a profile is edited.
        public static readonly string[] OptionKeys = {
            "UseAs", "PreferredTransport", "AutomaticBluetoothPairing",
            "USBSleepOnConnect",
            "AutoPowerOff", "PowerOffInactivity", "HomeLongPowerOff",
            "HomeLongPowerOffHoldSeconds",
            "EnableRumble", "ControllerAudioEnabled", "ControllerAudioVolume",
            "ControllerAudioEndpointId", "ControllerAudioRouteHeadphones",
            "ControllerAudioUsbLoopback",
            "ControllerBluetoothMicrophoneEnabled", "MicIndicatorMode",
            "GyroHoldToggle", "GyroMouseInhibitButtons", "DragToggle",
            "TouchpadMouseInhibitButtons", "TouchpadSensitivity",
            "TouchpadStickSensitivity",
            "TouchpadHorizontalScale", "TouchpadVerticalScale",
            "TouchpadTapAndHold", "TouchpadClickMovementLockout",
            "TouchpadTwoFingerScroll",
            "ChargingIndicator", "ChargeGlowColor", "ChargeGlowPeriodSeconds",
            "SwapAB", "SwapXY", "HomeLEDOn", "LightColor", "LightBrightness",
            "LightingOff", "LightingMode",
            "PlayerLedMode",
            "GyroAnalogSliders", "DefaultOrientation",
            "GyroStickModeLeft", "GyroStickModeRight",
            "GyroStickAxisXLeft", "GyroStickAxisXRight",
            "GyroStickInvertXLeft", "GyroStickInvertYLeft",
            "GyroStickInvertXRight", "GyroStickInvertYRight",
            "GyroStickMaxDeflectionXLeft", "GyroStickMaxDeflectionYLeft",
            "GyroStickMaxDeflectionXRight", "GyroStickMaxDeflectionYRight",
            "GyroStickMinDeflectionXLeft", "GyroStickMinDeflectionYLeft",
            "GyroStickMinDeflectionXRight", "GyroStickMinDeflectionYRight",
            "GyroStickReductionXLeft", "GyroStickReductionYLeft",
            "GyroStickReductionXRight", "GyroStickReductionYRight",
            "AdaptiveTriggerModeLeft", "AdaptiveTriggerStartLeft",
            "AdaptiveTriggerSecondaryLeft", "AdaptiveTriggerStrengthLeft",
            "AdaptiveTriggerModeRight", "AdaptiveTriggerStartRight",
            "AdaptiveTriggerSecondaryRight", "AdaptiveTriggerStrengthRight",
            // Per-mode Start/Secondary/Strength (AdaptiveTriggerFieldValue/AdaptiveTriggerFieldKey
            // below) - the four keys above stay registered only so an existing profile's one
            // shared set of values can still be read as a migration fallback, never written to by
            // new code.
            "AdaptiveTriggerStartLeftResistance", "AdaptiveTriggerSecondaryLeftResistance",
            "AdaptiveTriggerStrengthLeftResistance",
            "AdaptiveTriggerStartLeftWeapon", "AdaptiveTriggerSecondaryLeftWeapon",
            "AdaptiveTriggerStrengthLeftWeapon",
            "AdaptiveTriggerStartLeftVibration", "AdaptiveTriggerSecondaryLeftVibration",
            "AdaptiveTriggerStrengthLeftVibration",
            "AdaptiveTriggerStartRightResistance", "AdaptiveTriggerSecondaryRightResistance",
            "AdaptiveTriggerStrengthRightResistance",
            "AdaptiveTriggerStartRightWeapon", "AdaptiveTriggerSecondaryRightWeapon",
            "AdaptiveTriggerStrengthRightWeapon",
            "AdaptiveTriggerStartRightVibration", "AdaptiveTriggerSecondaryRightVibration",
            "AdaptiveTriggerStrengthRightVibration",
        };

        // Only meaningful on a solo-Joycon profile (see ProfileIdFor) - whether a newly-connected
        // lone Joycon with no available join partner should self-pair into vertical orientation
        // automatically (see Program.cs's auto-join pass) instead of staying solo/horizontal.
        public const string OrientationHorizontal = "horizontal";
        public const string OrientationVertical = "vertical";

        public const string UseAsXbox360 = "xbox360";
        public const string UseAsXbox360Viiper = "xbox360_viiper";
        public const string UseAsDualShock4 = "dualshock4";
        public const string UseAsDualSenseViiper = "dualsense_viiper";
        public const string UseAsNone = "none";
        // Same as UseAsNone (no virtual output) except the physical controller is also unhidden
        // from HidHide instead of staying blocked from every other program - see
        // VirtualControllerLifecycle.cs's CreateOutputControllers/ReconcileHidHideForProfile. Lets
        // a native application (Steam Input, a game with real DualSense/Joy-Con support) use the
        // controller directly under its own true identity, while BetterJoy still runs in the
        // background for gyro/touchpad/audio/lighting - none of which need a virtual controller.
        public const string UseAsPassthrough = "passthrough";

        private static readonly HashSet<string> AppConfigBackedKeys = new HashSet<string>(StringComparer.Ordinal) {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro", "ratchet_gyro",
        };

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(Keys, StringComparer.Ordinal);
        private static readonly HashSet<string> KnownOptionKeys =
            new HashSet<string>(OptionKeys, StringComparer.Ordinal);
        private static readonly HashSet<string> GyroActivationKeys = new HashSet<string>(StringComparer.Ordinal) {
            "active_gyro_mouse", "active_gyro_left_stick", "active_gyro_right_stick",
        };
        private static readonly HashSet<string> TouchpadActivationKeys =
            new HashSet<string>(StringComparer.Ordinal) {
                "active_touchpad_mouse", "active_touchpad_left_stick",
                "active_touchpad_right_stick",
            };
        private static readonly object writeLock = new object();
        // Variable-length profile data shares the fixed mappings' copy-on-write dictionary so
        // reloads, deletes, and concurrent readers retain the same synchronization guarantees.
        // Save/Reload expose it on disk as readable <customBind/> elements.
        private const string CustomBindingsStorageKey = "__custom_bindings";
        private static volatile Dictionary<string, Dictionary<string, string>> profiles =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static bool loaded;

        // Physical-controller calibration, keyed by raw serial (not profile ID - a paired
        // profile's two physical halves each need their own independent entry, same as
        // CalibrationState.cs's own List<KeyValuePair> model this mirrors). Lives alongside the
        // profile binds/options in the same XML file and shares its atomic-write/tolerant-reload
        // machinery - moved here from Config.cs's old settings file, whose non-atomic
        // File.ReadAllLines+File.WriteAllLines saves could lose calibration data on a crash
        // mid-write (see Config.Init's comment for the full history).
        private static volatile Dictionary<string, float[]> gyroCalibration =
            new Dictionary<string, float[]>(StringComparer.Ordinal);
        private static volatile Dictionary<string, ushort[]> stickCalibration =
            new Dictionary<string, ushort[]>(StringComparer.Ordinal);
        private static volatile Dictionary<string, ushort[]> stick2Calibration =
            new Dictionary<string, ushort[]>(StringComparer.Ordinal);

        private static string PathOnDisk => Path.Combine(AppPaths.DataDir, FileName);

        public static string Value(string profileId, string key) {
            EnsureLoaded();

            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            Dictionary<string, string> profile;
            string value;
            if (!String.IsNullOrEmpty(profileId) &&
                snapshot.TryGetValue(profileId, out profile)) {
                if (profile.TryGetValue(key, out value))
                    return value;

                // Profiles saved before independent gyro outputs only contain active_gyro.
                // Preserve that controller's custom bind for whichever output the old global
                // selector targeted, instead of falling all the way back to another controller's
                // global/default bind.
                if (GyroActivationKeys.Contains(key) &&
                    profile.TryGetValue("active_gyro", out value))
                    return MigrateGyroActivationValue(key, value);
            }

            return LegacyValue(key);
        }

        // Permanently forgets a profile's binds/options - used by Reassign's Delete button for a
        // disconnected profile. Only removes the mapping entry itself; the caller is responsible
        // for also clearing any physical calibration data via CalibrationState.DeleteCalibrationData
        // for the serial(s) SerialsForProfileId returns, since that's keyed separately (by raw
        // physical serial, not logical profile ID) and may still be referenced by a sibling
        // profile for the same physical unit (e.g. a solo profile and a paired profile for the
        // same Joy-Con) - deleting one profile doesn't imply the physical unit itself is gone.
        // A no-op (not an error) if profileId isn't currently known, matching the delete button's
        // "gone either way" semantics.
        public static void DeleteProfile(string profileId) {
            if (String.IsNullOrEmpty(profileId))
                return;

            EnsureLoaded();
            lock (writeLock) {
                if (!profiles.ContainsKey(profileId))
                    return;
                var next = CloneProfiles(profiles);
                next.Remove(profileId);
                profiles = next;
            }
            Save();
        }

        // Persists a bare, all-defaults profile entry as soon as a controller's identity is
        // known (solo attach, or pair/self-pair formation), rather than leaving it unsaved until
        // the user happens to open Controller Profiles and change something or click Apply.
        // Calibration data is written immediately regardless (it lives in a separate store), so
        // the previous behavior wasn't actually losing anything - it just looked like nothing had
        // been saved at all, which was confusing. A no-op, no-disk-write fast path once the
        // profile already exists, so this is safe to call on every connect/reconcile pass, not
        // just a genuine first-ever connection.
        public static void EnsureProfileSaved(string profileId) {
            if (String.IsNullOrEmpty(profileId)) {
                DebugLog.Write("EnsureProfileSaved: skipped, empty profileId");
                return;
            }

            EnsureLoaded();
            bool created;
            lock (writeLock) {
                created = !profiles.ContainsKey(profileId);
                if (created) {
                    var next = CloneProfiles(profiles);
                    var profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    SnapshotMissingProfileValues(profile);
                    next[profileId] = profile;
                    profiles = next;
                }
            }
            if (!created) {
                DebugLog.Write("EnsureProfileSaved: already present, no write: id=" + profileId);
                return;
            }

            // Save() has no caller-side guard here and this runs from the connect loop, outside
            // the try/catch that wraps Attach - an exception would take the whole scan pass down
            // with it and look like "profiles just don't save". Log the outcome either way so a
            // silent failure is visible instead of inferred.
            try {
                Save();
                DebugLog.Write("EnsureProfileSaved: created and saved: id=" + profileId);
            } catch (Exception ex) {
                DebugLog.Write("EnsureProfileSaved: SAVE FAILED: id=" + profileId +
                    " exception=" + ex.GetType().Name + " message=\"" + ex.Message + "\"");
                throw;
            }
        }

        // Populates CalibrationState.cs's own CaliData/StickCaliData/Stick2CaliData lists -
        // called from Config.Init with those exact lists, mirroring the shape that method
        // already had before calibration moved here, so CalibrationState.cs and its callers
        // (Program.cs, MainForm.cs) need no changes at all. Runs the one-time legacy migration
        // first (see MigrateLegacyCalibrationIfNeeded) so a fresh install of this version still
        // picks up whatever an existing user had calibrated under the old settings-file store.
        public static void LoadCalibrationInto(
                List<KeyValuePair<string, float[]>> caliData,
                List<KeyValuePair<string, ushort[]>> stickCaliData,
                List<KeyValuePair<string, ushort[]>> stick2CaliData) {
            EnsureLoaded();
            MigrateLegacyCalibrationIfNeeded();

            lock (writeLock) {
                caliData.Clear();
                // CalibrationState.ActiveCaliData falls back to CaliData[0] when no entry matches
                // a given serial - this default MUST stay element 0 of the list it's given,
                // exactly as CalibrationState.cs's own static initializer always put it, whether
                // or not anything real has ever been calibrated.
                caliData.Add(new KeyValuePair<string, float[]>("0", new float[6] { 0, 0, 0, -710, 0, 0 }));
                foreach (KeyValuePair<string, float[]> entry in gyroCalibration.OrderBy(e => e.Key, StringComparer.Ordinal))
                    caliData.Add(entry);

                stickCaliData.Clear();
                foreach (KeyValuePair<string, ushort[]> entry in stickCalibration.OrderBy(e => e.Key, StringComparer.Ordinal))
                    stickCaliData.Add(entry);

                stick2CaliData.Clear();
                foreach (KeyValuePair<string, ushort[]> entry in stick2Calibration.OrderBy(e => e.Key, StringComparer.Ordinal))
                    stick2CaliData.Add(entry);
            }
        }

        // Called from Config.SaveCaliData - same forwarding relationship as
        // LoadCalibrationInto/Config.Init, just for the save direction. The "0" default entry
        // (see LoadCalibrationInto) is never actually written back out here, matching how it was
        // never a real persisted entry in the old settings-file format either - it's
        // CalibrationState.cs's own built-in fallback, not something to round-trip.
        public static void SaveGyroCalibration(List<KeyValuePair<string, float[]>> caliData) {
            EnsureLoaded();
            lock (writeLock) {
                var next = new Dictionary<string, float[]>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, float[]> entry in caliData)
                    if (entry.Key != "0")
                        next[entry.Key] = entry.Value;
                gyroCalibration = next;
            }
            Save();
        }

        public static void SaveStickCalibration(
                List<KeyValuePair<string, ushort[]>> stickCaliData,
                List<KeyValuePair<string, ushort[]>> stick2CaliData) {
            EnsureLoaded();
            lock (writeLock) {
                var nextStick = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, ushort[]> entry in stickCaliData)
                    nextStick[entry.Key] = entry.Value;
                stickCalibration = nextStick;

                var nextStick2 = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, ushort[]> entry in stick2CaliData)
                    nextStick2[entry.Key] = entry.Value;
                stick2Calibration = nextStick2;
            }
            Save();
        }

        // One-time pull of whatever calibration data is still salvageable from Config.cs's old
        // settings-file store (see Config.TryTakeLegacyCalibration) - only runs while this file's
        // own calibration is still completely empty, so it can never overwrite anything a user
        // has already calibrated (or re-calibrated) under the new store. Config.
        // TryTakeLegacyCalibration blanks the legacy lines it reads as its own one-time-only
        // guarantee, so calling this on every startup is safe and cheap once migration has
        // actually happened once - the legacy read finds nothing left and returns immediately.
        private static void MigrateLegacyCalibrationIfNeeded() {
            lock (writeLock) {
                if (gyroCalibration.Count > 0 || stickCalibration.Count > 0 || stick2Calibration.Count > 0)
                    return;
            }

            List<KeyValuePair<string, float[]>> legacyGyro;
            List<KeyValuePair<string, ushort[]>> legacyStick, legacyStick2;
            if (!Config.TryTakeLegacyCalibration(out legacyGyro, out legacyStick, out legacyStick2))
                return;

            bool changed = false;
            lock (writeLock) {
                var nextGyro = new Dictionary<string, float[]>(gyroCalibration, StringComparer.Ordinal);
                foreach (KeyValuePair<string, float[]> entry in legacyGyro) {
                    if (entry.Key == "0")
                        continue; // the hardcoded default, never a real migrated entry
                    nextGyro[entry.Key] = entry.Value;
                    changed = true;
                }
                gyroCalibration = nextGyro;

                var nextStick = new Dictionary<string, ushort[]>(stickCalibration, StringComparer.Ordinal);
                foreach (KeyValuePair<string, ushort[]> entry in legacyStick) {
                    nextStick[entry.Key] = entry.Value;
                    changed = true;
                }
                stickCalibration = nextStick;

                var nextStick2 = new Dictionary<string, ushort[]>(stick2Calibration, StringComparer.Ordinal);
                foreach (KeyValuePair<string, ushort[]> entry in legacyStick2) {
                    nextStick2[entry.Key] = entry.Value;
                    changed = true;
                }
                stick2Calibration = nextStick2;
            }

            if (changed)
                Save();
        }

        // Extracts the physical serial(s) embedded in a profile ID (see ProfileIdFor for the
        // inverse) - one for every topology except "pair:", which embeds both physical halves
        // joined by "+".
        public static string[] SerialsForProfileId(string profileId) {
            if (String.IsNullOrEmpty(profileId))
                return new string[0];

            int colonIndex = profileId.IndexOf(':');
            string idPart = colonIndex >= 0 ? profileId.Substring(colonIndex + 1) : profileId;
            return idPart.Split('+');
        }

        public static void SetValue(string profileId, string key, string value) {
            if (String.IsNullOrEmpty(profileId))
                throw new ArgumentException("A connected controller profile is required.", nameof(profileId));
            if (!KnownKeys.Contains(key))
                throw new ArgumentException("Unknown controller mapping key: " + key, nameof(key));

            EnsureLoaded();
            lock (writeLock) {
                var next = CloneProfiles(profiles);
                Dictionary<string, string> profile;
                if (!next.TryGetValue(profileId, out profile)) {
                    profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    next[profileId] = profile;
                }

                SnapshotMissingProfileValues(profile);
                profile[key] = String.IsNullOrEmpty(value) ? "0" : value;
                profiles = next;
            }
        }

        public static string CustomBindingsValue(string profileId) {
            EnsureLoaded();
            Dictionary<string, string> profile;
            string value;
            return !String.IsNullOrEmpty(profileId) &&
                   profiles.TryGetValue(profileId, out profile) &&
                   profile.TryGetValue(CustomBindingsStorageKey, out value)
                ? value
                : String.Empty;
        }

        public static CustomBinding[] CustomBindings(string profileId) {
            return ParseCustomBindings(CustomBindingsValue(profileId));
        }

        public static void SetCustomBindings(string profileId, IEnumerable<CustomBinding> bindings) {
            if (String.IsNullOrEmpty(profileId))
                throw new ArgumentException("A controller profile is required.", nameof(profileId));

            string serialized = SerializeCustomBindings(bindings);
            EnsureLoaded();
            lock (writeLock) {
                var next = CloneProfiles(profiles);
                Dictionary<string, string> profile;
                if (!next.TryGetValue(profileId, out profile)) {
                    profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    next[profileId] = profile;
                }

                SnapshotMissingProfileValues(profile);
                profile[CustomBindingsStorageKey] = serialized;
                profiles = next;
            }
        }

        // A normal custom source is controller-only and requires at least two distinct buttons.
        // Rebind rows may use one button because they consume the source's normal virtual output.
        // Outputs may contain one or more controller, keyboard, or mouse parts.
        public static bool IsValidCustomBindingInput(string value) {
            return IsValidCustomBindingInput(value, false);
        }

        public static bool IsValidCustomBindingInput(string value, bool rebind) {
            string[] parts = String.IsNullOrEmpty(value) ? new string[0] : value.Split('+');
            if (parts.Length < (rebind ? 1 : 2) ||
                    parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
                return false;
            return parts.All(part => IsValidBindPart(part, controllerOnly: true));
        }

        public static bool IsValidCustomBindingOutput(string value) {
            string[] parts = String.IsNullOrEmpty(value) ? new string[0] : value.Split('+');
            if (parts.Length == 1 && IsCustomAction(parts[0]))
                return true;
            return parts.Length > 0 && parts.All(part => IsValidBindPart(part, controllerOnly: false));
        }

        public static bool IsCustomAction(string value) {
            return CustomActionChoices.Any(choice =>
                String.Equals(choice.Value, value, StringComparison.Ordinal));
        }

        public static bool TryGetCustomAction(string value, out DesktopInputAction action) {
            CustomActionChoice choice = CustomActionChoices.FirstOrDefault(candidate =>
                String.Equals(candidate.Value, value, StringComparison.Ordinal));
            if (choice == null) {
                action = default(DesktopInputAction);
                return false;
            }
            action = choice.Action;
            return true;
        }

        public static string CustomActionLabel(string value) {
            CustomActionChoice choice = CustomActionChoices.FirstOrDefault(candidate =>
                String.Equals(candidate.Value, value, StringComparison.Ordinal));
            return choice == null ? value : choice.Label;
        }

        public static string CustomActionDisplayBinding(string value) {
            CustomActionChoice choice = CustomActionChoices.FirstOrDefault(candidate =>
                String.Equals(candidate.Value, value, StringComparison.Ordinal));
            return choice == null ? null : choice.DisplayBinding;
        }

        private static bool IsValidBindPart(string part, bool controllerOnly) {
            if (String.IsNullOrEmpty(part) || part.Length <= 4)
                return false;
            bool controller = part.StartsWith("joy_", StringComparison.Ordinal);
            if (!controller && (controllerOnly ||
                    (!part.StartsWith("key_", StringComparison.Ordinal) &&
                     !part.StartsWith("mse_", StringComparison.Ordinal))))
                return false;
            int code;
            if (!Int32.TryParse(part.Substring(4), out code) || code < 0)
                return false;
            if (controller)
                return Enum.IsDefined(typeof(Controller.Button), code);
            Type type = part.StartsWith("key_", StringComparison.Ordinal)
                ? typeof(WindowsInput.Events.KeyCode)
                : typeof(WindowsInput.Events.ButtonCode);
            try {
                object enumValue = Convert.ChangeType(
                    code, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
                return Enum.IsDefined(type, enumValue);
            } catch (OverflowException) {
                return false;
            }
        }

        private static string SerializeCustomBindings(IEnumerable<CustomBinding> bindings) {
            if (bindings == null)
                return String.Empty;
            return String.Join("\n", bindings.Select(binding =>
                (binding?.Input ?? String.Empty) + "\t" +
                (binding?.Output ?? String.Empty) + "\t" +
                (binding != null && binding.Rebind ? "1" : "0")));
        }

        private static CustomBinding[] ParseCustomBindings(string serialized) {
            if (String.IsNullOrEmpty(serialized))
                return new CustomBinding[0];
            return serialized.Split('\n').Select(row => {
                string[] values = row.Split('\t');
                return new CustomBinding(
                    values.Length > 0 ? values[0] : String.Empty,
                    values.Length > 1 ? values[1] : String.Empty,
                    values.Length > 2 && values[2] == "1");
            }).ToArray();
        }

        public static string OptionValue(string profileId, string key) {
            if (!KnownOptionKeys.Contains(key))
                throw new ArgumentException("Unknown controller profile option: " + key, nameof(key));

            EnsureLoaded();
            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            Dictionary<string, string> profile;
            string value;
            if (!String.IsNullOrEmpty(profileId) &&
                snapshot.TryGetValue(profileId, out profile) &&
                profile.TryGetValue(key, out value))
                return value;
            return LegacyOptionValue(key);
        }

        public static bool BoolOption(string profileId, string key) {
            bool value;
            return Boolean.TryParse(OptionValue(profileId, key), out value) && value;
        }

        public static string RumbleMode(string profileId) {
            string value = OptionValue(profileId, "EnableRumble");
            bool legacyValue;
            if (Boolean.TryParse(value, out legacyValue))
                return legacyValue ? ModeEnable : ModeDisable;
            if (value == RumbleModeDisableWithGyro)
                return value;
            return value == ModeEnable ? ModeEnable : ModeDisable;
        }

        public static string ControllerAudioMode(string profileId) {
            string value = OptionValue(profileId, "ControllerAudioEnabled");
            bool legacyValue;
            if (Boolean.TryParse(value, out legacyValue)) {
                if (!legacyValue)
                    return ModeDisable;
                return BoolOption(profileId, "ControllerAudioRouteHeadphones")
                    ? AudioModeRequireHeadphones
                    : ModeEnable;
            }
            if (value == AudioModeRequireHeadphones)
                return value;
            return value == ModeEnable ? ModeEnable : ModeDisable;
        }

        public static string BluetoothMicrophoneMode(string profileId) {
            string value = OptionValue(profileId, "ControllerBluetoothMicrophoneEnabled");
            bool legacyValue;
            if (Boolean.TryParse(value, out legacyValue))
                return legacyValue ? ModeEnable : ModeDisable;
            if (value == MicrophoneModeStartMuted)
                return value;
            return value == ModeEnable ? ModeEnable : ModeDisable;
        }

        public static string MicIndicatorMode(string profileId) {
            string value = OptionValue(profileId, "MicIndicatorMode");
            if (value == MicIndicatorModeDisabled || value == MicIndicatorModeInverted ||
                    value == MicIndicatorModeEnabledWhileDisabled)
                return value;
            return MicIndicatorModeEnabled;
        }

        // Shared by DualSense and Joy-Con/Pro (NintendoController.SetLEDByPlayerNum,
        // DualSenseController.SetLEDByPlayerNum) - one setting, but their honest historical
        // defaults are opposite: Nintendo controllers have always shown player-number LEDs
        // unconditionally, while DualSense's have always been silently forced off as an
        // unintended side effect of unrelated output writes (see PlayerLedModes' own comment).
        // defaultEnabled lets each caller preserve its own real prior behavior for a profile that
        // has never touched this dropdown, rather than picking one default that regresses the
        // other family.
        public static bool PlayerLedEnabled(string profileId, bool defaultEnabled) {
            string value = OptionValue(profileId, "PlayerLedMode");
            return String.IsNullOrEmpty(value) ? defaultEnabled : value == ModeEnable;
        }

        public static bool ChargeGlowEnabled(string profileId) {
            // Unset means Glow: that is how the indicator behaved before it was configurable.
            return !String.Equals(OptionValue(profileId, "ChargingIndicator"),
                ChargingIndicatorDisabled, StringComparison.OrdinalIgnoreCase);
        }

        // Battery mode ignores ChargeGlowColor and derives the peak from the charge level
        // instead - red at empty through to green at full. Unlike LightingModeBattery's three
        // fixed bands this is a continuous gradient, because the pulse is already animating so
        // there is no chatter argument for quantising it.
        public static bool ChargeGlowUsesBattery(string profileId) {
            return String.Equals(OptionValue(profileId, "ChargingIndicator"),
                ChargingIndicatorBattery, StringComparison.OrdinalIgnoreCase);
        }

        public static string ChargeGlowColor(string profileId) {
            string value = OptionValue(profileId, "ChargeGlowColor");
            byte red, green, blue;
            return TryParseLightColor(value, out red, out green, out blue)
                ? NormalizeLightColor(value)
                : DefaultChargeGlowColor;
        }

        public static int ChargeGlowPeriodSeconds(string profileId) {
            int seconds = IntOption(profileId, "ChargeGlowPeriodSeconds",
                DefaultChargeGlowPeriodSeconds);
            if (seconds < MinimumChargeGlowPeriodSeconds ||
                    seconds > MaximumChargeGlowPeriodSeconds)
                return DefaultChargeGlowPeriodSeconds;
            return seconds;
        }

        public static string LightingMode(string profileId) {
            string value = OptionValue(profileId, "LightingMode");
            // Unset/unrecognized falls back to User, matching every profile's behavior before
            // Default/Disabled existed - only an explicit choice opts into either of those.
            if (String.Equals(value, LightingModeBattery, StringComparison.OrdinalIgnoreCase))
                return LightingModeBattery;
            if (String.Equals(value, LightingModeWheel, StringComparison.OrdinalIgnoreCase))
                return LightingModeWheel;
            if (String.Equals(value, LightingModeWheelToggle, StringComparison.OrdinalIgnoreCase))
                return LightingModeWheelToggle;
            if (String.Equals(value, LightingModeDefault, StringComparison.OrdinalIgnoreCase))
                return LightingModeDefault;
            if (String.Equals(value, LightingModeDisabled, StringComparison.OrdinalIgnoreCase))
                return LightingModeDisabled;
            if (String.Equals(value, LightingModeOpenRgb, StringComparison.OrdinalIgnoreCase))
                return LightingModeOpenRgb;
            return LightingModeUser;
        }

        // USB preserves BetterJoy's established behavior for every existing profile. Bluetooth
        // is opt-in and means a matching wired Sony HID interface is treated as charge-only while
        // the same physical controller remains available through its wireless connection.
        public static string PreferredTransport(string profileId) {
            string value = OptionValue(profileId, "PreferredTransport");
            return String.Equals(value, PreferredTransportBluetooth,
                    StringComparison.OrdinalIgnoreCase)
                ? PreferredTransportBluetooth
                : PreferredTransportUsb;
        }

        // The transport actually usable RIGHT NOW. "Bluetooth" with no usable host radio (absent or
        // toggled off) is Bluetooth in name only - the controller can never reach a host - so every
        // decision must take exactly the same path as PreferredTransport = USB: attach over USB,
        // apply normal lighting instead of holding the pad dark for a handoff that will never come,
        // and wake over USB rather than firing a Bluetooth connect at a dead radio.
        //
        // Resolved centrally on purpose. This preference is branched on in a dozen places across
        // DualSense.cs and Program.cs; deciding it per-site is how they drifted out of agreement in
        // the first place. Callers that want the user's literal setting (the UI, persistence) must
        // keep using PreferredTransport above.
        public static string UsablePreferredTransport(string profileId) {
            string preferred = PreferredTransport(profileId);
            if (String.Equals(preferred, PreferredTransportBluetooth, StringComparison.Ordinal) &&
                    !BluetoothRadio.IsLocalRadioAvailable())
                return PreferredTransportUsb;
            return preferred;
        }

        // Raw mode: ModeEnable (full from-scratch pairing), ModeRepair (restore an existing PC
        // bond the controller lost), or ModeDisable. Anything unrecognized is treated as disabled.
        public static string AutomaticBluetoothPairingMode(string profileId) {
            string value = OptionValue(profileId, "AutomaticBluetoothPairing");
            if (String.Equals(value, ModeEnable, StringComparison.OrdinalIgnoreCase))
                return ModeEnable;
            if (String.Equals(value, ModeRepair, StringComparison.OrdinalIgnoreCase))
                return ModeRepair;
            return ModeDisable;
        }

        // True for both Enabled and Repair - either way the controller's cable attach should
        // trigger the automatic-BT flow; the mode above decides which path it takes.
        public static bool AutomaticBluetoothPairingEnabled(string profileId) {
            return AutomaticBluetoothPairingMode(profileId) != ModeDisable;
        }

        // Initial USB-connect sleep policy only. Does not affect long-press, inactivity, or exit.
        public static string USBSleepOnConnectMode(string profileId) {
            string value = OptionValue(profileId, "USBSleepOnConnect");
            if (String.Equals(value, USBSleepOnConnectEnabled, StringComparison.OrdinalIgnoreCase))
                return USBSleepOnConnectEnabled;
            if (String.Equals(value, USBSleepOnConnectDisabled, StringComparison.OrdinalIgnoreCase))
                return USBSleepOnConnectDisabled;
            return USBSleepOnConnectBluetooth;
        }

        public static int LightBrightness(string profileId) {
            return Math.Max(0, Math.Min(100, IntOption(profileId, "LightBrightness", 100)));
        }

        // Uniform RGB scaling preserves the selected hue and saturation. Default and OpenRGB are
        // intentionally excluded: both modes delegate lighting ownership outside BetterJoy, so
        // this helper must never modify their colors even if a caller reaches it accidentally.
        public static (byte Red, byte Green, byte Blue) ApplyLightBrightness(
                string profileId, byte red, byte green, byte blue) {
            string mode = LightingMode(profileId);
            if (mode == LightingModeDefault || mode == LightingModeOpenRgb)
                return (red, green, blue);

            int brightness = LightBrightness(profileId);
            return (
                (byte)Math.Round(red * brightness / 100.0),
                (byte)Math.Round(green * brightness / 100.0),
                (byte)Math.Round(blue * brightness / 100.0));
        }

        // Default and OpenRGB both mean BetterJoy never sends a single lighting command for this
        // profile - OpenRGB additionally triggers OpenRgbRescan, but the "never touch the LED"
        // behavior itself is identical, so every hands-off check (Program.cs's
        // ApplyControllerProfileLighting, DualSenseController's own gating) shares this one
        // predicate instead of repeating the same OR.
        public static bool LightingModeIsHandsOff(string profileId) {
            string mode = LightingMode(profileId);
            return mode == LightingModeDefault || mode == LightingModeOpenRgb;
        }

        // Builds the per-mode Start/Secondary/Strength key for one trigger side - e.g.
        // ("Left", "Start", "weapon") -> "AdaptiveTriggerStartLeftWeapon". Shared by the read
        // path below and by Reassign.cs, which needs the exact same key when committing an edited
        // box, so the two can never drift apart on the naming scheme.
        public static string AdaptiveTriggerFieldKey(string side, string field, string mode) {
            string capitalizedMode = String.IsNullOrEmpty(mode)
                ? ""
                : Char.ToUpperInvariant(mode[0]) + mode.Substring(1).ToLowerInvariant();
            return "AdaptiveTrigger" + field + side + capitalizedMode;
        }

        // Each Adaptive trigger mode (Resistance/Weapon/Vibration) stores its own independent
        // Start/Secondary/Strength values instead of sharing one set - switching modes (via the
        // dropdown, or the LT/RT haptics cycling bindings) no longer clobbers another mode's
        // configured feel. Migrated lazily rather than in a batch pass: an existing profile only
        // ever had the old flat AdaptiveTrigger{Field}{Side} key, holding whatever was configured
        // for whichever mode was active at the time - if the new per-mode key was never
        // explicitly set, and this happens to be the mode the profile's own
        // AdaptiveTriggerMode{Side} is (or was) set to, fall back to that old key so an existing
        // user's current effect is preserved exactly. Any other mode just gets the plain default,
        // matching a fresh profile - there was never a configured value for it to migrate.
        public static int AdaptiveTriggerFieldValue(string profileId, string side, string field,
                string mode, int fallback) {
            // Off has no effect parameters at all, so it was never given per-mode keys
            // (AdaptiveTriggerFieldKey(..., "off") would build a key that was never registered in
            // OptionKeys) - short-circuit here rather than let OptionValue throw for every
            // DualSense profile sitting at the default Off mode, which is the vast majority of
            // them.
            if (String.Equals((mode ?? "").Trim(), "off", StringComparison.OrdinalIgnoreCase))
                return fallback;

            string perModeRaw = OptionValue(profileId, AdaptiveTriggerFieldKey(side, field, mode));
            if (!String.IsNullOrEmpty(perModeRaw)) {
                int value;
                return Int32.TryParse(perModeRaw, out value) ? value : fallback;
            }

            string activeMode = (OptionValue(profileId, "AdaptiveTriggerMode" + side) ?? "off")
                .Trim().ToLowerInvariant();
            if (!String.Equals(activeMode, mode, StringComparison.OrdinalIgnoreCase))
                return fallback;

            string legacyRaw = OptionValue(profileId, "AdaptiveTrigger" + field + side);
            if (String.IsNullOrEmpty(legacyRaw))
                return fallback;

            int legacyValue;
            return Int32.TryParse(legacyRaw, out legacyValue) ? legacyValue : fallback;
        }

        public static int IntOption(string profileId, string key, int fallback = -1) {
            int value;
            return Int32.TryParse(OptionValue(profileId, key), out value) ? value : fallback;
        }

        public static void SetOptionValue(string profileId, string key, string value) {
            if (String.IsNullOrEmpty(profileId))
                throw new ArgumentException("A controller profile is required.", nameof(profileId));
            if (!KnownOptionKeys.Contains(key))
                throw new ArgumentException("Unknown controller profile option: " + key, nameof(key));

            EnsureLoaded();
            lock (writeLock) {
                var next = CloneProfiles(profiles);
                Dictionary<string, string> profile;
                if (!next.TryGetValue(profileId, out profile)) {
                    profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    next[profileId] = profile;
                }

                SnapshotMissingProfileValues(profile);
                profile[key] = value ?? LegacyOptionValue(key);
                profiles = next;
            }
        }

        public static bool AnyVirtualOutputEnabled() {
            EnsureLoaded();
            string legacy = LegacyOptionValue("UseAs");
            if (legacy != UseAsNone && legacy != UseAsPassthrough)
                return true;
            return profiles.Values.Any(profile => {
                string value;
                return profile.TryGetValue("UseAs", out value) &&
                    value != UseAsNone && value != UseAsPassthrough;
            });
        }

        public static string DefaultValue(string key) {
            // Preserve each controller/output topology's established Guide/PS behavior until the
            // user explicitly assigns this new independent virtual-button binding.
            if (key == "guide")
                return "default";
            if (GyroActivationKeys.Contains(key))
                return LegacyGyroActivationValue(key);
            if (TouchpadActivationKeys.Contains(key))
                return "0";
            if (key == "touchpad_two_finger_tap" ||
                key == "touchpad_two_finger_scroll_up" ||
                key == "touchpad_two_finger_scroll_down")
                return "default";
            return AppConfigBackedKeys.Contains(key) ? "0" : Config.GetDefaultValue(key);
        }

        public static void Save() {
            EnsureLoaded();
            lock (writeLock) {
                // This writes the whole in-memory set, so anything on disk that this process
                // never loaded would be erased by it. The service adds profiles behind our back
                // (EnsureProfileSaved on connect), so carry those ids through rather than
                // dropping them. All-defaults is the right content: that is exactly what the
                // service creates, and any value it wrote would have come from this file anyway.
                var missing = ProfileIdsOnDisk();
                missing.ExceptWith(profiles.Keys);
                if (missing.Count > 0) {
                    var next = CloneProfiles(profiles);
                    foreach (string profileId in missing) {
                        var profile = new Dictionary<string, string>(StringComparer.Ordinal);
                        SnapshotMissingProfileValues(profile);
                        next[profileId] = profile;
                    }
                    profiles = next;
                    DebugLog.Write("Save: preserved " + missing.Count +
                        " profile(s) created by another process: " +
                        String.Join(", ", missing.OrderBy(id => id, StringComparer.Ordinal)));
                }

                var root = new XElement("controllerMappings", new XAttribute("version", "5"));
                foreach (KeyValuePair<string, Dictionary<string, string>> profile in profiles.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                    var profileElement = new XElement("profile", new XAttribute("id", profile.Key));
                    foreach (string key in Keys) {
                        string value;
                        if (profile.Value.TryGetValue(key, out value))
                            profileElement.Add(new XElement("bind", new XAttribute("key", key), new XAttribute("value", value ?? "0")));
                    }
                    foreach (string key in OptionKeys) {
                        string value;
                        if (profile.Value.TryGetValue(key, out value))
                            profileElement.Add(new XElement("option", new XAttribute("key", key), new XAttribute("value", value ?? String.Empty)));
                    }
                    string customBindingsValue;
                    if (profile.Value.TryGetValue(CustomBindingsStorageKey,
                            out customBindingsValue)) {
                        foreach (CustomBinding binding in ParseCustomBindings(customBindingsValue)) {
                            profileElement.Add(new XElement("customBind",
                                new XAttribute("input", binding.Input),
                                new XAttribute("output", binding.Output),
                                new XAttribute("rebind", binding.Rebind)));
                        }
                    }
                    root.Add(profileElement);
                }

                // Keyed by raw serial, not profile ID - a union of whichever of the three
                // calibration dictionaries actually have an entry for that serial, since gyro/
                // stick/stick2 are independent (e.g. a solo Joy-Con only ever has gyro+stick,
                // never stick2; a not-yet-recalibrated controller may have gyro but no stick
                // override yet).
                var calibratedSerials = new HashSet<string>(StringComparer.Ordinal);
                calibratedSerials.UnionWith(gyroCalibration.Keys);
                calibratedSerials.UnionWith(stickCalibration.Keys);
                calibratedSerials.UnionWith(stick2Calibration.Keys);
                foreach (string serial in calibratedSerials.OrderBy(s => s, StringComparer.Ordinal)) {
                    var calibrationElement = new XElement("calibration", new XAttribute("serial", serial));
                    float[] gyro;
                    if (gyroCalibration.TryGetValue(serial, out gyro))
                        calibrationElement.Add(new XElement("gyro", new XAttribute("values", String.Join(",", gyro))));
                    ushort[] stick;
                    if (stickCalibration.TryGetValue(serial, out stick))
                        calibrationElement.Add(new XElement("stick", new XAttribute("values", String.Join(",", stick))));
                    ushort[] stick2;
                    if (stick2Calibration.TryGetValue(serial, out stick2))
                        calibrationElement.Add(new XElement("stick2", new XAttribute("values", String.Join(",", stick2))));
                    root.Add(calibrationElement);
                }

                string path = PathOnDisk;
                string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try {
                    new XDocument(root).Save(temporaryPath);
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, null, true);
                    else
                        File.Move(temporaryPath, path);
                } finally {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
        }

        // Parse and publish as one immutable snapshot. A service watcher can observe a save at
        // any point; malformed/locked reads keep the last known-good mappings intact.
        public static void Reload() {
            string path = PathOnDisk;
            if (!File.Exists(path)) {
                lock (writeLock) {
                    profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    loaded = true;
                }
                return;
            }

            Dictionary<string, Dictionary<string, string>> parsed;
            Dictionary<string, float[]> parsedGyro;
            Dictionary<string, ushort[]> parsedStick, parsedStick2;
            try {
                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null || root.Name != "controllerMappings")
                    return;

                parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (XElement profileElement in root.Elements("profile")) {
                    string profileId = (string)profileElement.Attribute("id");
                    if (String.IsNullOrEmpty(profileId) || parsed.ContainsKey(profileId))
                        continue;

                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XElement bindElement in profileElement.Elements("bind")) {
                        string key = (string)bindElement.Attribute("key");
                        string value = (string)bindElement.Attribute("value");
                        if (KnownKeys.Contains(key) && value != null)
                            values[key] = value;
                    }
                    foreach (XElement optionElement in profileElement.Elements("option")) {
                        string key = (string)optionElement.Attribute("key");
                        string value = (string)optionElement.Attribute("value");
                        if (KnownOptionKeys.Contains(key) && value != null)
                            values[key] = value;
                    }
                    XElement[] customBindElements = profileElement.Elements("customBind").ToArray();
                    if (customBindElements.Length > 0) {
                        values[CustomBindingsStorageKey] = SerializeCustomBindings(
                            customBindElements.Select(element => {
                                bool rebind;
                                Boolean.TryParse((string)element.Attribute("rebind"), out rebind);
                                return new CustomBinding(
                                    (string)element.Attribute("input"),
                                    (string)element.Attribute("output"),
                                    rebind);
                            }));
                    }
                    parsed[profileId] = values;
                }

                parsedGyro = new Dictionary<string, float[]>(StringComparer.Ordinal);
                parsedStick = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
                parsedStick2 = new Dictionary<string, ushort[]>(StringComparer.Ordinal);
                foreach (XElement calibrationElement in root.Elements("calibration")) {
                    string serial = (string)calibrationElement.Attribute("serial");
                    if (String.IsNullOrEmpty(serial))
                        continue;

                    float[] gyro = ParseFloatValues((string)calibrationElement.Element("gyro")?.Attribute("values"), 6);
                    if (gyro != null)
                        parsedGyro[serial] = gyro;
                    ushort[] stick = ParseUShortValues((string)calibrationElement.Element("stick")?.Attribute("values"), 6);
                    if (stick != null)
                        parsedStick[serial] = stick;
                    ushort[] stick2 = ParseUShortValues((string)calibrationElement.Element("stick2")?.Attribute("values"), 6);
                    if (stick2 != null)
                        parsedStick2[serial] = stick2;
                }
            } catch {
                return;
            }

            lock (writeLock) {
                profiles = parsed;
                gyroCalibration = parsedGyro;
                stickCalibration = parsedStick;
                stick2Calibration = parsedStick2;
                loaded = true;
            }
        }

        // Null (not a zero-filled array) on anything malformed - a partially-parseable
        // calibration entry is worth discarding entirely rather than silently feeding a
        // half-real array into CenterSticks/gyro math, matching Reload's existing "the whole
        // parse either succeeds or the last known-good state stands" discipline.
        private static float[] ParseFloatValues(string csv, int expectedCount) {
            if (String.IsNullOrEmpty(csv))
                return null;
            string[] parts = csv.Split(',');
            if (parts.Length != expectedCount)
                return null;
            var result = new float[expectedCount];
            for (int i = 0; i < expectedCount; i++) {
                if (!float.TryParse(parts[i], out result[i]))
                    return null;
            }
            return result;
        }

        private static ushort[] ParseUShortValues(string csv, int expectedCount) {
            if (String.IsNullOrEmpty(csv))
                return null;
            string[] parts = csv.Split(',');
            if (parts.Length != expectedCount)
                return null;
            var result = new ushort[expectedCount];
            for (int i = 0; i < expectedCount; i++) {
                if (!ushort.TryParse(parts[i], out result[i]))
                    return null;
            }
            return result;
        }

        public static string ProfileIdFor(Controller controller) {
            if (controller == null)
                return null;

            string ownId = DeviceId(controller);
            // controller.Kind is the single source of truth for this ordering (DualSense before
            // Snes/N64/Pro) - see Controller.cs's Kind property. Previously re-derived inline here
            // via isSnes/is64/isDualSense/isPro directly, which needed its own comment explaining
            // why isDualSense had to be checked ahead of isPro (isDualSense implies isPro, see the
            // Joycon constructor's "single-unit controller" convention) - now impossible to get
            // wrong since there's only one place this ordering is expressed.
            switch (controller.Kind) {
                case ControllerKind.Snes: return "snes:" + ownId;
                case ControllerKind.N64: return "n64:" + ownId;
                case ControllerKind.DualSense: return "dualsense:" + ownId;
                case ControllerKind.DualShock4: return "dualshock4:" + ownId;
                case ControllerKind.Pro: return "pro:" + ownId;
            }

            // Past this point only Joy-Con Left/Right kinds remain - the only pairing-capable
            // topology today (see Controller.other's comment). The is-check is redundant given
            // Kind's fallthrough already narrows to Joy-Con in practice, but keeps this correct
            // if a future non-pairing device somehow reaches here instead of an early return above.
            if (!(controller is JoyconController joycon))
                return ownId;

            if (joycon.other == joycon)
                return (joycon.isLeft ? "vertical-left:" : "vertical-right:") + ownId;
            if (joycon.other == null)
                return (joycon.isLeft ? "solo-left:" : "solo-right:") + ownId;

            JoyconController left = joycon.isLeft ? joycon : joycon.other;
            JoyconController right = joycon.isLeft ? joycon.other : joycon;
            return "pair:" + DeviceId(left) + "+" + DeviceId(right);
        }

        public static ControllerProfileInfo ProfileFor(Controller controller) {
            if (controller == null)
                return null;

            if (controller is JoyconController pairJoycon && pairJoycon.other != null && pairJoycon.other != pairJoycon) {
                JoyconController left = pairJoycon.isLeft ? pairJoycon : pairJoycon.other;
                JoyconController right = pairJoycon.isLeft ? pairJoycon.other : pairJoycon;
                string pairId = ProfileIdFor(controller);
                return new ControllerProfileInfo {
                    ProfileId = pairId,
                    // A joined pair is one logical controller already, so its group is itself.
                    GroupId = pairId,
                    DisplayName = "Joy-Con Pair (L " + DeviceSuffix(left) + " / R " + DeviceSuffix(right) + ")",
                    ConnectionSequence = Math.Max(left.virtualControllerSequence, right.virtualControllerSequence),
                    IsConnected = true,
                    Kind = null,
                };
            }

            string type;
            switch (controller.Kind) {
                case ControllerKind.Snes: type = "SNES Controller"; break;
                case ControllerKind.N64: type = "N64 Controller"; break;
                case ControllerKind.DualSense: type = "DualSense Controller"; break;
                case ControllerKind.DualShock4: type = "DualShock 4 Controller"; break;
                case ControllerKind.Pro: type = "Pro Controller"; break;
                default:
                    // Unreachable for any Kind except Left/Right today (see ProfileIdFor's same
                    // narrowing), which is always a JoyconController - the is-check is defensive,
                    // not load-bearing, same reasoning as ProfileIdFor above.
                    JoyconController soloJoycon = controller as JoyconController;
                    // Deliberately no "(solo)"/"(vertical)" suffix any more: both orientations are
                    // one entry now, and the name would change under the user as they flip the
                    // grip while the same entry stays selected.
                    if (soloJoycon != null)
                        type = soloJoycon.isLeft ? "Left Joy-Con" : "Right Joy-Con";
                    else
                        type = "Controller";
                    break;
            }

            string profileId = ProfileIdFor(controller);
            return new ControllerProfileInfo {
                // Still the live orientation's row - a connected Joy-Con edits whichever
                // orientation it is physically in, which is what "follow the controller" means.
                ProfileId = profileId,
                GroupId = ProfileGroupId(profileId),
                DisplayName = type + " (" + DeviceSuffix(controller) + ")",
                ConnectionSequence = controller.virtualControllerSequence,
                IsConnected = true,
                Kind = controller.Kind,
                PadId = controller.PadId,
                IsUsb = controller.IsUsbConnection,
                AudioEndpointNameHint = controller.UsbAudioEndpointNameHint,
            };
        }

        public static List<ControllerProfileInfo> ConnectedProfiles(IEnumerable<Controller> controllers) {
            var result = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            if (controllers == null)
                return new List<ControllerProfileInfo>();

            foreach (Controller controller in controllers) {
                if (controller == null)
                    continue;
                if (controller is JoyconController joycon && joycon.other != null && joycon.other != joycon && !joycon.isLeft)
                    continue;

                ControllerProfileInfo info = ProfileFor(controller);
                if (info != null)
                    result[info.GroupId ?? info.ProfileId] = info;
            }

            return result.Values.OrderByDescending(p => p.ConnectionSequence).ToList();
        }

        // Profiles are durable configuration, not merely a view of attached hardware. Merge the
        // saved IDs from controller_mappings.xml with the live controller list so the editor can
        // reopen and modify a controller's bindings while that controller is powered off. A live
        // entry replaces its saved-only rendering and retains connection ordering; disconnected
        // entries are sorted by their stable, derived display name after all connected entries.
        public static List<ControllerProfileInfo> IncludeDisconnectedProfiles(
            IEnumerable<ControllerProfileInfo> connectedProfiles) {
            EnsureLoaded();

            var merged = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            // Union with what is actually on disk, not just what this process loaded at startup -
            // otherwise a profile the service auto-created for a controller connected since then
            // is missing from the list until the UI is restarted.
            var knownIds = new HashSet<string>(snapshot.Keys, StringComparer.Ordinal);
            knownIds.UnionWith(ProfileIdsOnDisk());
            // Keyed by group, not by stored id: a Joy-Con's two orientation rows are one physical
            // device and must be one entry. ResolveOrientationProfileId picks which of the two
            // rows this entry edits while the controller is disconnected - the orientation it
            // will come back in. A connected controller overrides that below with its live one.
            foreach (string profileId in knownIds) {
                string groupId = ProfileGroupId(profileId);
                if (merged.ContainsKey(groupId))
                    continue;
                merged[groupId] = new ControllerProfileInfo {
                    ProfileId = ResolveOrientationProfileId(groupId),
                    GroupId = groupId,
                    DisplayName = DisconnectedDisplayName(groupId),
                    ConnectionSequence = -1,
                    IsConnected = false,
                    Kind = KindFromProfileId(groupId),
                };
            }

            if (connectedProfiles != null) {
                foreach (ControllerProfileInfo connected in connectedProfiles) {
                    if (connected != null && !String.IsNullOrEmpty(connected.ProfileId)) {
                        connected.IsConnected = true;
                        if (String.IsNullOrEmpty(connected.GroupId))
                            connected.GroupId = ProfileGroupId(connected.ProfileId);
                        merged[connected.GroupId] = connected;
                    }
                }
            }

            return merged.Values
                .OrderByDescending(p => p.IsConnected)
                .ThenByDescending(p => p.ConnectionSequence)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The service creates profiles in its own process (EnsureProfileSaved, on connect) and
        // writes them straight to disk. This process only ever loads the file once, so anything
        // created after it started is invisible to it - and, worse, Save() writes the whole
        // in-memory set, so applying settings from a stale UI would delete those rows.
        //
        // Deliberately ids only, parsed from the file directly rather than going through
        // Reload(): reloading here would replace `profiles` and discard whatever the user is
        // part-way through editing. Ids are enough - both callers only need to know a profile
        // exists, and an auto-created one is all-defaults anyway.
        private static HashSet<string> ProfileIdsOnDisk() {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try {
                string path = PathOnDisk;
                if (!File.Exists(path))
                    return ids;
                XElement root = XDocument.Load(path).Root;
                if (root == null)
                    return ids;
                foreach (XElement profile in root.Elements("profile")) {
                    string id = (string)profile.Attribute("id");
                    if (!String.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            } catch (Exception ex) {
                // Never let a malformed or momentarily-locked file break listing or saving.
                DebugLog.Write("ProfileIdsOnDisk failed: " + ex.GetType().Name +
                    " message=\"" + ex.Message + "\"");
            }
            return ids;
        }

        private static void EnsureLoaded() {
            if (loaded)
                return;
            lock (writeLock) {
                if (loaded)
                    return;
                Reload();
                loaded = true;
            }
        }

        private static string LegacyValue(string key) {
            if (key == "guide")
                return "default";
            // Was a hardcoded check against the physical mic-mute button (DualSense.cs) before
            // this became a real binding - defaults to that exact same button alone so existing
            // behavior doesn't change for anyone who hasn't touched this, while still letting it
            // be reassigned to a different chord like every other binding.
            if (key == "toggle_built_in_mic")
                return "joy_" + (int)Controller.Button.MIC_MUTE;
            if (GyroActivationKeys.Contains(key))
                return LegacyGyroActivationValue(key);
            if (TouchpadActivationKeys.Contains(key))
                return "0";
            if (key == "touchpad_two_finger_tap" ||
                key == "touchpad_two_finger_scroll_up" ||
                key == "touchpad_two_finger_scroll_down")
                return "default";

            string value = AppConfigBackedKeys.Contains(key)
                ? ConfigurationManager.AppSettings[key]
                : Config.Value(key);
            return String.IsNullOrEmpty(value) ? "0" : value;
        }

        private static string LegacyOptionValue(string key) {
            if (key == "UseAs") {
                bool showAsXbox;
                bool showAsDs4;
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsXInput"], out showAsXbox);
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsDS4"], out showAsDs4);
                return showAsXbox
                    ? UseAsXbox360
                    : (showAsDs4 ? UseAsDualShock4 : UseAsNone);
            }

            if (key == "ControllerAudioEnabled")
                return ModeDisable;
            if (key == "PreferredTransport")
                return PreferredTransportUsb;
            if (key == "AutomaticBluetoothPairing")
                return ModeDisable;
            if (key == "USBSleepOnConnect")
                return USBSleepOnConnectBluetooth;
            if (key == "ControllerAudioVolume")
                return "75";
            if (key == "ControllerAudioEndpointId")
                return String.Empty;
            if (key == "ControllerAudioRouteHeadphones")
                return "false";
            if (key == "ControllerAudioUsbLoopback")
                return "false";
            if (key == "ControllerBluetoothMicrophoneEnabled")
                return ModeDisable;
            if (key == "LightBrightness")
                return "100";
            if (key == "EnableRumble") {
                bool enabled;
                Boolean.TryParse(ConfigurationManager.AppSettings[key], out enabled);
                return enabled ? ModeEnable : ModeDisable;
            }
            if (key == "AdaptiveTriggerModeLeft" || key == "AdaptiveTriggerModeRight")
                return "off";
            if (key == "AdaptiveTriggerStartLeft" || key == "AdaptiveTriggerStartRight")
                return "30";
            if (key == "AdaptiveTriggerSecondaryLeft" ||
                key == "AdaptiveTriggerSecondaryRight")
                return "70";
            if (key == "AdaptiveTriggerStrengthLeft" ||
                key == "AdaptiveTriggerStrengthRight")
                return "50";

            string value = ConfigurationManager.AppSettings[key];
            return value ?? String.Empty;
        }

        private static void SnapshotMissingProfileValues(Dictionary<string, string> profile) {
            foreach (string key in Keys) {
                if (!profile.ContainsKey(key))
                    profile[key] = LegacyValue(key);
            }
            foreach (string key in OptionKeys) {
                if (!profile.ContainsKey(key))
                    profile[key] = LegacyOptionValue(key);
            }
        }

        private static string LegacyGyroActivationValue(string key) {
            return MigrateGyroActivationValue(key, Config.Value("active_gyro"));
        }

        private static string MigrateGyroActivationValue(string key, string legacyBind) {
            string legacyMode = ConfigurationManager.AppSettings["GyroToJoyOrMouse"] ?? "none";
            string matchingMode = key == "active_gyro_mouse"
                ? "mouse"
                : (key == "active_gyro_left_stick" ? "joy_left" : "joy_right");
            if (!String.Equals(legacyMode, matchingMode, StringComparison.Ordinal))
                return "0"; // disabled

            // The old active_gyro convention used 0 for always enabled. The new independent
            // mappings need 0 to mean disabled, otherwise removing the selector would turn all
            // three outputs on together. Preserve the old behavior explicitly for its one target.
            return String.IsNullOrEmpty(legacyBind) || legacyBind == "0"
                ? "always"
                : legacyBind;
        }

        private static Dictionary<string, Dictionary<string, string>> CloneProfiles(
            Dictionary<string, Dictionary<string, string>> source) {
            var clone = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, string>> profile in source)
                clone[profile.Key] = new Dictionary<string, string>(profile.Value, StringComparer.Ordinal);
            return clone;
        }

        private static string DeviceId(Controller controller) {
            string mac = AddressString(controller.PadMacAddress);
            if (IsUsableMac(mac))
                return mac.ToLowerInvariant();

            string serial = (controller.serial_number ?? String.Empty).Trim();
            if (IsUsableMac(serial.ToUpperInvariant()))
                return serial.ToLowerInvariant();
            if (serial.Length > 0 && serial != "000000000001")
                return "serial-" + EncodeIdentity(serial);

            // Some third-party/USB devices expose no unique serial. The HID path is the best
            // available identity in that case; it is deliberately namespaced so it can never
            // collide with a real MAC or serial-backed controller.
            return "path-" + EncodeIdentity(controller.path ?? String.Empty);
        }

        private static string DeviceSuffix(Controller controller) {
            string mac = AddressString(controller.PadMacAddress);
            if (IsUsableMac(mac))
                return mac.Substring(Math.Max(0, mac.Length - 6)).ToUpperInvariant();

            string serial = (controller.serial_number ?? String.Empty).Trim();
            if (serial.Length > 0 && serial != "000000000001")
                return serial.Substring(Math.Max(0, serial.Length - 6));
            return "Pad " + (controller.PadId + 1);
        }

        private static string DisconnectedDisplayName(string profileId) {
            int separator = profileId == null ? -1 : profileId.IndexOf(':');
            if (separator <= 0 || separator == profileId.Length - 1)
                return (profileId ?? "Controller profile") + " (disconnected)";

            string kind = profileId.Substring(0, separator);
            string identity = profileId.Substring(separator + 1);
            string name;
            switch (kind) {
                case "pair":
                    string[] pair = identity.Split('+');
                    name = pair.Length == 2
                        ? "Joy-Con Pair (L " + IdentitySuffix(pair[0]) + " / R " + IdentitySuffix(pair[1]) + ")"
                        : "Joy-Con Pair (" + IdentitySuffix(identity) + ")";
                    break;
                case "pro":
                    name = "Pro Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "dualsense":
                    name = "DualSense Controller (" + IdentitySuffix(identity) + ")";
                    break;
                // Was missing, so a dualshock4: profile fell through to the generic default and
                // showed as "Controller profile (XXXXXX)" once disconnected, even though it named
                // itself correctly while connected. Every other kind ProfileIdFor can emit has a
                // case here; this one was simply never added when the kind was introduced.
                case "dualshock4":
                    name = "DualShock 4 (" + IdentitySuffix(identity) + ")";
                    break;
                case "snes":
                    name = "SNES Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "n64":
                    name = "N64 Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "solo-left":
                    name = "Left Joy-Con (solo) (" + IdentitySuffix(identity) + ")";
                    break;
                case "solo-right":
                    name = "Right Joy-Con (solo) (" + IdentitySuffix(identity) + ")";
                    break;
                case "vertical-left":
                    name = "Left Joy-Con (vertical) (" + IdentitySuffix(identity) + ")";
                    break;
                case "vertical-right":
                    name = "Right Joy-Con (vertical) (" + IdentitySuffix(identity) + ")";
                    break;
                // Grouped identities (see ProfileGroupId) - one entry covering both orientations,
                // so the name deliberately carries no orientation suffix.
                case "joycon-left":
                    name = "Left Joy-Con (" + IdentitySuffix(identity) + ")";
                    break;
                case "joycon-right":
                    name = "Right Joy-Con (" + IdentitySuffix(identity) + ")";
                    break;
                default:
                    name = "Controller profile (" + IdentitySuffix(identity) + ")";
                    break;
            }

            return name + " (disconnected)";
        }

        public static string NormalizeLightColor(string value) {
            byte red, green, blue;
            if (!TryParseLightColor(value, out red, out green, out blue))
                return DefaultLightColor;
            return String.Format(CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}", red, green, blue);
        }

        public static bool TryParseLightColor(string value,
                                              out byte red, out byte green, out byte blue) {
            red = 0;
            green = 0;
            blue = 255;
            string hex = (value ?? String.Empty).Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal))
                hex = hex.Substring(1);
            uint rgb;
            if (hex.Length != 6 || !UInt32.TryParse(hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out rgb))
                return false;
            red = (byte)(rgb >> 16);
            green = (byte)(rgb >> 8);
            blue = (byte)rgb;
            return true;
        }

        public static void GetLightColor(string profileId,
                                         out byte red, out byte green, out byte blue) {
            if (!TryParseLightColor(OptionValue(profileId, "LightColor"),
                                    out red, out green, out blue)) {
                red = 0;
                green = 0;
                blue = 255;
            }
        }

        // A Joy-Con's orientation is baked into its profile ID (ProfileIdFor), so one physical
        // unit owns two stored rows and the profile list showed both - a connected pair produced
        // five entries (solo L/R, vertical L/R, pair). These fold the two rows onto one identity
        // for display and selection ONLY. Nothing persists a joycon-* ID and nothing reads
        // settings through one: the concrete solo-*/vertical-* row stays the storage key, so
        // per-orientation settings keep working exactly as before.
        private const string JoyconGroupLeftPrefix = "joycon-left:";
        private const string JoyconGroupRightPrefix = "joycon-right:";

        public static string ProfileGroupId(string profileId) {
            if (String.IsNullOrEmpty(profileId))
                return profileId;
            if (profileId.StartsWith("solo-left:", StringComparison.Ordinal))
                return JoyconGroupLeftPrefix + profileId.Substring("solo-left:".Length);
            if (profileId.StartsWith("vertical-left:", StringComparison.Ordinal))
                return JoyconGroupLeftPrefix + profileId.Substring("vertical-left:".Length);
            if (profileId.StartsWith("solo-right:", StringComparison.Ordinal))
                return JoyconGroupRightPrefix + profileId.Substring("solo-right:".Length);
            if (profileId.StartsWith("vertical-right:", StringComparison.Ordinal))
                return JoyconGroupRightPrefix + profileId.Substring("vertical-right:".Length);
            // Every other kind, joined pairs included, is already one entry per device.
            return profileId;
        }

        // The stored rows a grouped identity covers, most-preferred first. Only one may actually
        // exist: EnsureProfileSaved creates the row for whichever orientation was seen on
        // connect, so a Joy-Con that has only ever been used sideways has no vertical row.
        private static string[] ProfileIdsInGroup(string groupId) {
            if (String.IsNullOrEmpty(groupId))
                return new string[0];
            if (groupId.StartsWith(JoyconGroupLeftPrefix, StringComparison.Ordinal)) {
                string identity = groupId.Substring(JoyconGroupLeftPrefix.Length);
                return new[] { "solo-left:" + identity, "vertical-left:" + identity };
            }
            if (groupId.StartsWith(JoyconGroupRightPrefix, StringComparison.Ordinal)) {
                string identity = groupId.Substring(JoyconGroupRightPrefix.Length);
                return new[] { "solo-right:" + identity, "vertical-right:" + identity };
            }
            return new[] { groupId };
        }

        // Which stored row a grouped entry should edit when its controller is NOT connected, so
        // there is no live orientation to follow: the one it will adopt next time it connects.
        // Falls back to horizontal, then to whichever row actually exists.
        public static string ResolveOrientationProfileId(string groupId) {
            string[] candidates = ProfileIdsInGroup(groupId);
            if (candidates.Length < 2)
                return groupId;

            EnsureLoaded();
            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            string horizontal = candidates[0];
            string vertical = candidates[1];

            foreach (string candidate in candidates) {
                Dictionary<string, string> profile;
                string orientation;
                if (snapshot.TryGetValue(candidate, out profile) &&
                        profile.TryGetValue("DefaultOrientation", out orientation) &&
                        !String.IsNullOrEmpty(orientation)) {
                    return orientation == OrientationVertical ? vertical : horizontal;
                }
            }

            if (snapshot.ContainsKey(horizontal))
                return horizontal;
            if (snapshot.ContainsKey(vertical))
                return vertical;
            return horizontal;
        }

        // DefaultOrientation decides whether a lone Joy-Con self-pairs into vertical on connect,
        // but it lived inside one orientation's row - so it had to be set identically in both to
        // take effect, since the row consulted depends on the orientation you are already in.
        // Write it to every row in the group instead. Creating the sibling row is deliberate: it
        // is all-defaults, exactly what EnsureProfileSaved would have written anyway.
        public static void SetGroupedOptionValue(string profileId, string key, string value) {
            if (key != "DefaultOrientation") {
                SetOptionValue(profileId, key, value);
                return;
            }

            foreach (string sibling in ProfileIdsInGroup(ProfileGroupId(profileId)))
                SetOptionValue(sibling, key, value);
        }

        private static ControllerKind? KindFromProfileId(string profileId) {
            if (String.IsNullOrEmpty(profileId))
                return null;
            if (profileId.StartsWith("dualsense:", StringComparison.Ordinal))
                return ControllerKind.DualSense;
            if (profileId.StartsWith("dualshock4:", StringComparison.Ordinal))
                return ControllerKind.DualShock4;
            if (profileId.StartsWith("pro:", StringComparison.Ordinal))
                return ControllerKind.Pro;
            if (profileId.StartsWith("snes:", StringComparison.Ordinal))
                return ControllerKind.Snes;
            if (profileId.StartsWith("n64:", StringComparison.Ordinal))
                return ControllerKind.N64;
            // Grouped ids reach here too, since the list is keyed by ProfileGroupId now.
            if (profileId.StartsWith("solo-left:", StringComparison.Ordinal) ||
                profileId.StartsWith("vertical-left:", StringComparison.Ordinal) ||
                profileId.StartsWith(JoyconGroupLeftPrefix, StringComparison.Ordinal))
                return ControllerKind.Left;
            if (profileId.StartsWith("solo-right:", StringComparison.Ordinal) ||
                profileId.StartsWith("vertical-right:", StringComparison.Ordinal) ||
                profileId.StartsWith(JoyconGroupRightPrefix, StringComparison.Ordinal))
                return ControllerKind.Right;
            return null;
        }

        private static string IdentitySuffix(string identity) {
            if (String.IsNullOrEmpty(identity))
                return "unknown";

            string value = identity;
            if (identity.StartsWith("serial-", StringComparison.Ordinal)) {
                string encoded = identity.Substring("serial-".Length)
                    .Replace('-', '+').Replace('_', '/');
                while (encoded.Length % 4 != 0)
                    encoded += "=";
                try {
                    value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                } catch {
                    value = identity;
                }
            }

            string suffix = value.Substring(Math.Max(0, value.Length - 6));
            return suffix.ToUpperInvariant();
        }

        private static string AddressString(PhysicalAddress address) {
            return address == null ? String.Empty : address.ToString();
        }

        private static bool IsUsableMac(string mac) {
            return mac.Length == 12 && mac != "000000000000" &&
                mac != "000000000001" && mac != "010203040506" &&
                mac.All(Uri.IsHexDigit);
        }

        private static string EncodeIdentity(string value) {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
