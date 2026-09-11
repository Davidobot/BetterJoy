using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Concentus;
using Concentus.Enums;

namespace BetterJoyForCemu {
    // DualSenseController : Controller - step 4 Phase J of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. Everything here was previously Joycon.cs's Tier 2 DualSense-specific code
    // (isDualSense-gated), relocated verbatim into its own real sibling of Joycon under
    // Controller, per the target architecture in the doc. DualSense does not pair (SupportsPairing
    // is always false) and deliberately never gets a DS4-output target - see the doc's "Tier 3"
    // note on MapToDualShock4Input. Gyro/accel support (see ExtractIMUValues/
    // ReadGyroCalibration below): byte offsets, calibration feature report, and scale constants
    // were cross-checked against three independent reference implementations; physical axis
    // sign/handedness (the one item no public reference source documented) was confirmed
    // empirically against real hardware instead - see ExtractIMUValues' own comments for what
    // that investigation found (a Nintendo-only calibration-bias leak in shared CalibrationState
    // code, and gyr_g/acc_g needing matching channel order for AHRS's sensor fusion to agree
    // with itself, not just an axis-sign guess).
    public class DualSenseController : Controller {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => true;
        public override bool HasTouchpad => true;
        protected override int TouchpadMaximumX => 1919;
        protected override int TouchpadMaximumY => 1079;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualSense;
        public override string UsbAudioEndpointNameHint => "Wireless Controller";

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        private const int DualSenseMaxReportLen = 78; // Bluetooth report length; USB (64) fits the same buffer
        private const int DualSenseBluetoothControlFeatureReportLen = 47;
        private const byte DualSenseBluetoothControlFeatureReportId = 0x08;
        private const byte DualSenseBluetoothControlOn = 0x01;
        private const byte DualSenseBluetoothControlOff = 0x02;
        // Restored from df0514e: the USB-side Bluetooth WAKE control. Note it is a different
        // command AND a different report length than the 47-byte 0x08/0x01 connect trigger - a
        // captured PS5 connection sequence uses this exact 17-byte form.
        private const byte DualSenseUsbBluetoothWakeControl = 0x11;
        private const int DualSenseUsbBluetoothWakeReportLen = 17;
        // After waking a fully-powered-off controller, how long to let its radio come up before
        // parking it dormant-paired. Bounded, and only on this recovery path.
        private const int FirmwarePowerOffWakeSettleMs = 2000;
        private const byte DualSensePairingInfoFeatureReportId = 0x09;
        private const int DualSensePairingInfoFeatureReportLen = 20;
        private const int DualSensePairingHostAddressOffset = 10;
        private const byte DualSenseSetPairingFeatureReportId = 0x0A;
        private const int DualSenseSetPairingFeatureReportLen = 27;
        private const int PairingRecordCommitTimeoutMs = 3000;
        private const int PairingRecordPollIntervalMs = 50;
        private const int UsbChargeGlowFrameMs = 80;
        private const double UsbChargeGlowPeriodMs = 10000.0;
        private string chargeOnlyUsbPath;
        private bool monitorChargeOnlyWakeAfterPowerOff;
        // Set when BetterJoy itself powered this controller off, so the firmware-power-off recovery
        // in CleanUp can tell "we did this deliberately, the monitor is already armed" apart from
        // "the controller's own firmware killed it behind our back".
        private volatile bool appInitiatedPowerOff;
        private int chargeOnlyWakeMonitorStarted;
        private bool automaticBluetoothPairingAttempted;
        // Interlocked-exchanged request flag from the scan-timer thread to the Poll thread - see
        // ApplyAutomaticBluetoothPairing/ApplyQueuedAutomaticBluetoothPairingIfAny.
        private int automaticBluetoothPairingPending;
        private volatile bool automaticBluetoothPairingInProgress;
        // A saved link key precedes live authentication; keep fresh pairing awake until input holds.
        private volatile bool freshBluetoothPairingPending;
        // Set by the manager (scan-timer thread) once this Bluetooth pad is confirmed live
        // (IMU_DATA_OK on the 00001124 interface); drained on the Poll thread to run the roaming
        // sleep (PowerOff) - same scan->Poll hand-off shape as automaticBluetoothPairingPending.
        private int roamingSleepPending;
        // Initial USB-connect sleep is separate from long-press/inactivity/app-exit power-off. The
        // critical decision is snapshotted once, while the USB object is born: was this same
        // controller already live over Bluetooth at that moment? If yes, do not let a fresh USB
        // node kick the already-good Bluetooth session into charge-only sleep.
        private int usbSleepOnConnectPending;
        private bool usbSleepOnConnectInitialBluetoothStateKnown;
        private bool usbSleepOnConnectBluetoothWasLive;
        // Stopwatch timestamp of when this pad first reached IMU_DATA_OK, set/read by the manager's
        // confirm bridge to require the Bluetooth link to HOLD (a stable connection = pairing
        // actually finished) before sleeping it - a single brief IMU_DATA_OK is just one lap of the
        // pairing loop, not proof it's done. Resets naturally: a dropped link removes this pad and a
        // reconnect is a fresh object with this back at 0.
        internal long bluetoothImuStableSince;
        private bool lightbarTransportKnown;
        private bool lightbarUpdatePending = true;
        private bool openRgbLightbarUpdatePending;
        private byte lightbarRed;
        private byte lightbarGreen;
        private byte lightbarBlue = 255;
        // The small player-number indicator LEDs below the touchpad - physically separate from
        // the RGB lightbar above but packed into the same output report byte range (see
        // SendDualSenseLightbar/WriteRetainedRumbleAndTriggerState). 0 (all off) by default,
        // matching PlayerLedModes' own Disabled default - see SetLEDByPlayerNum.
        private byte currentPlayerLeds;
        // Every DualSense HID write is serialized here. Bluetooth speaker audio uses the same
        // physical output endpoint as lightbar and rumble state, so those states are folded into
        // the audio carrier while streaming instead of allowing independent reports to collide.
        private readonly object outputReportLock = new object();
        private byte bluetoothOutputSequence;
        private byte currentLeftMotor;
        private byte currentRightMotor;
        // DualSense's two adaptive-trigger blocks are part of the same common output state as
        // rumble, audio, microphone routing, and the lightbar. Keep the encoded hardware state
        // here so every transport writer can compose it instead of competing with a trigger-only
        // HID writer. Each block is the controller-native 11-byte right/left trigger payload.
        private readonly byte[] currentRightTriggerEffect = CreateOffTriggerEffect();
        private readonly byte[] currentLeftTriggerEffect = CreateOffTriggerEffect();
        private bool adaptiveTriggerStateKnown;
        private bool adaptiveTriggerUpdatePending = true;
        private bool bluetoothOutputStateDirty = true;
        // Guards SilenceControllerAudio so the zeroed-volume report is written on the actual
        // transition into audio-off and not once per profile-reconciliation pass. Guarded by
        // outputReportLock like the rest of the outgoing report state.
        private bool audioLevelsSilenced;
        private bool lightbarControlReleased;
        private enum LightbarConnectState {
            WaitingForInput,
            Settling,
            Applied
        }

        // Connect-time lighting handoff (see the ReceiveRaw lighting block): let the firmware own
        // the lightbar until input is actually streaming, then give it a short settle window before
        // applying the profile color directly. No intermediate black/off packet.
        private const double LightbarConnectSettleSeconds = 4.5;
        private LightbarConnectState lightbarConnectState =
            LightbarConnectState.WaitingForInput;
        private long lightbarConnectStreamingSince;
        // DualSense common input status[1] bits 0/1 report headphone/microphone presence. -1
        // means no valid input report has established the physical jack state yet.
        private int headphoneConnectionState = -1;
        public bool HeadphonesConnected => Volatile.Read(ref headphoneConnectionState) == 1;
        private long lastDualSenseRawDumpTimestamp = 0;
        private long lastDualSenseImuLogTimestamp = 0;
        // GyroSubSamplePeriod override support - see GyroMath.cs's field comment. DualSense's real
        // report interval has nothing like Joy-Con's fixed 5ms, so ProcessGyroMouseSample/
        // ProcessGyroStickSample need the actual elapsed time here instead of the Nintendo-tuned
        // hardcoded constant. Measured from the report's own embedded hardware timestamp
        // (r[27+o..30+o], a free-running microsecond counter), not wall-clock arrival time - a
        // real hardware capture showed a fast, sustained wrist-roll motion (gravity trust
        // correctly dropped to ~0.29, but gyro-only integration still drifted ~99deg from the
        // accelerometer's own reading, producing a corkscrew cursor path) traced to USB/BT
        // delivering multiple already-sampled reports in a burst: wall-clock arrival time bunches
        // those together near-zero apart even though the sensor captured them evenly spaced in
        // real time, so Stopwatch-based measurement was under-measuring dt exactly when a fast
        // motion made integration accuracy matter most. The hardware counter reflects the sensor's
        // own sampling clock and is immune to transport-layer buffering/batching.
        private uint? lastImuHardwareTimestampTicks;
        private uint lastLoggedImuDeltaTicks;
        private float measuredGyroSubSamplePeriod = ImuSamplePeriodSeconds;
        // Bounds for the measured interval: floor avoids a near-zero/duplicate-timestamp dt
        // collapsing the gravity-fusion integration to a no-op, ceiling avoids a single stall
        // (BT hiccup, USB re-enumeration) injecting one wildly oversized rotation step.
        private const float MinGyroSubSamplePeriod = 0.0005f;
        private const float MaxGyroSubSamplePeriod = 0.02f;

        // DualSense Bluetooth speaker transport. These values describe the controller protocol;
        // desktop capture and Opus encoding remain generic in BluetoothAudioCapture.cs.
        private const int BtAudioReportLength = 398;
        private const int BtAudioStateOffset = 13;
        private const int BtAudioStateLength = 63;
        private const int BtAudioHapticsOffset = 76;
        private const int BtAudioHapticsLength = 64;
        private const int BtAudioSpeakerOffset = 142;
        private const int BtAudioSpeakerDataOffset = 144;
        private const int BtAudioOpusFrameLength = 200;
        private const int BtMicrophoneOpusFrameOffset = 3;
        private const int BtMicrophoneOpusFrameLength = 71;
        private const int BtMicrophonePcmFrames = 480;
        private const byte BtAudioSpeakerPacketType = 0x93;
        private const byte BtAudioHeadsetPacketType = 0x96;
        private const byte DualSenseValidCompatibleVibration = 0x01;
        private const byte DualSenseValidHapticsSelect = 0x02;
        private const byte DualSenseValidRightTrigger = 0x04;
        private const byte DualSenseValidLeftTrigger = 0x08;
        private const byte DualSenseValidRumbleAndTriggers =
            DualSenseValidCompatibleVibration | DualSenseValidHapticsSelect |
            DualSenseValidRightTrigger | DualSenseValidLeftTrigger;
        // valid_flag1/power_save_control names and bit values match the Linux hid-playstation
        // driver exactly - this is what actually powers the mic capsule down at the hardware
        // level (see WriteRetainedRumbleAndTriggerState), not just the mute LED.
        private const byte DualSensePowerSaveControlEnable = 0x02; // DS_OUTPUT_VALID_FLAG1_POWER_SAVE_CONTROL_ENABLE
        // Lightbar-related validity bits from dualsense_output_report_common. Bluetooth audio
        // transports that structure inside report 0x36, so its lighting controls must be gated
        // just like ordinary USB 0x02 / Bluetooth 0x31 output reports.
        private const byte DualSenseValidLightbarControl = 0x04;
        private const byte DualSenseValidPlayerIndicatorControl = 0x10;
        private const byte DualSenseValidLightingFlag1 =
            DualSenseValidLightbarControl | DualSenseValidPlayerIndicatorControl;
        private const byte DualSenseValidLedBrightnessControl = 0x01;
        private const byte DualSenseValidLightbarSetupControl = 0x02;
        private const byte DualSenseValidLightingFlag2 =
            DualSenseValidLedBrightnessControl | DualSenseValidLightbarSetupControl;
        private const byte DualSensePowerSaveMicMute = 0x10; // DS_OUTPUT_POWER_SAVE_CONTROL_MIC_MUTE
        // Two frames keep startup latency near 21 ms while retaining one frame of capture/IPC
        // cushion. A transient underrun is handled below by withholding the missing speaker report
        // instead of inserting a hard-silence Opus packet into otherwise continuous audio.
        private const int BtAudioPrimeFrameCount = 2;
        // Bound latency as well as memory without allowing a delayed producer burst to rebuild a
        // large stale-audio queue behind the low-latency target.
        private const int BtAudioMaximumQueuedFrames = 4;
        private const double BtAudioFrameCadenceMs = 10.0 + (2.0 / 3.0);
        private static readonly byte[] DefaultBluetoothAudioState = {
            0xFD, 0xF7, 0x00, 0x00, 0x64, 0x64, 0xFF, 0x09,
            0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x07, 0x00,
            0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        private readonly ConcurrentQueue<byte[]> bluetoothAudioFrameQueue =
            new ConcurrentQueue<byte[]>();
        private readonly List<byte[]> bluetoothAudioPending = new List<byte[]>();
        private readonly object bluetoothAudioStateLock = new object();
        private volatile bool bluetoothAudioStreaming;
        private byte bluetoothAudioPacketSequence;
        private int bluetoothAudioVolumePercent = -1;
        private string bluetoothAudioEndpointId = String.Empty;
        private bool bluetoothAudioRouteHeadphones;
        private Stopwatch bluetoothAudioStopwatch;
        private double bluetoothAudioNextSendDeadlineMs;
        // Bluetooth audio owns a second, shareable HID session while streaming. Its bounded
        // OVERLAPPED write pool lets Windows keep reports moving during short scheduler/storage
        // stalls without ever racing hidapi reads on this controller's primary handle.
        private BluetoothAudioWritePool bluetoothAudioWritePool;
        private byte[] bluetoothAudioSilenceFrame;
        // Owned exclusively by Poll. AVRT registration and release must happen on the same thread.
        private IntPtr bluetoothAudioMmcssHandle;
        private bool bluetoothAudioMmcssAttempted;
        // Cross-stage timing telemetry. Capture/IPC arrivals are recorded by the helper-pipe
        // thread; the remaining counters belong to Poll under bluetoothAudioStateLock.
        private long bluetoothAudioLastEnqueueTimestamp;
        private long bluetoothAudioMaximumEnqueueGapTicks;
        private long bluetoothAudioFramesEnqueued;
        private double bluetoothAudioLastSendMs;
        private double bluetoothAudioMaximumSendGapMs;
        private double bluetoothAudioMaximumLatenessMs;
        private double bluetoothAudioMaximumSubmitMs;
        private double bluetoothAudioLastDiagnosticMs;
        private long bluetoothAudioSyntheticSilenceFrames;
        private long bluetoothAudioLastSummarySyntheticSilenceFrames;
        private long bluetoothAudioSpeakerStarvations;
        private long bluetoothAudioLastSummarySpeakerStarvations;
        private bool bluetoothAudioSpeakerStarved;
        private int bluetoothAudioSendsSinceSummary;
        private int bluetoothAudioMinimumPending;
        private int bluetoothAudioMaximumPending;

        // Bluetooth microphone packets share report ID 0x31 with ordinary controller input, but
        // carry a distinct transport tag and a fixed 71-byte, 48 kHz mono Opus frame. Keep the
        // capture/decode worker off the HID poll thread; the controller file owns Sony's framing,
        // while whichever IMicrophoneEndpoint MicrophoneEndpointFactory selects owns only the
        // generic delivery edge (VIIPER's virtual UAC device, or a Virtual Audio Driver render
        // endpoint).
        private readonly ConcurrentQueue<byte[]> bluetoothMicrophoneFrameQueue =
            new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent bluetoothMicrophoneSignal = new AutoResetEvent(false);
        private volatile bool bluetoothMicrophoneRequested;
        private volatile bool bluetoothMicrophoneStreaming;
        // Not Bluetooth-specific despite the neighboring fields - the physical mute button and
        // its LED are the same hardware/report field (mute_button_led, valid_flag1 bit 0 in the
        // Linux hid-playstation driver's dualsense_output_report_common) on both transports, so
        // this tracks the mute toggle regardless of whether Bluetooth mic streaming happens to be
        // active. See WriteRetainedRumbleAndTriggerState.
        private volatile bool microphoneMuted;
        private volatile bool microphoneMuteStatePending;
        // USB has no equivalent to StartBluetoothMicrophone's "genuine fresh start" moment - the
        // mic is a native USB Audio Class endpoint, no BetterJoy-owned capture pipeline to start
        // at all - so ApplyUsbMicrophoneMuteDefault needs its own one-shot latch instead, reset
        // on every fresh Attach so a later reconnect re-applies the Muted default again.
        private volatile bool usbMicrophoneMuteDefaultApplied;
        // Bluetooth's own equivalent latch - StartBluetoothMicrophone only ever runs (and so only
        // ever pushes a mute default) when the Built-in mic mode is Enable or Muted; Disabled
        // never calls it at all, so without this the mute LED/power-save state is never actively
        // pushed to the controller in that mode, leaving whatever state a previous session left it
        // in rather than the Disabled default the UI claims.
        private volatile bool bluetoothMicrophoneMuteDefaultApplied;
        private volatile bool bluetoothMicrophoneDisablePending;
        private volatile bool bluetoothMicrophoneControlPending;
        private Thread bluetoothMicrophoneThread;
        private bool bluetoothSpeakerPrimed;

        private enum AvrtPriority {
            Low = -1,
            Normal = 0,
            High = 1,
            Critical = 2,
        }

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvSetMmThreadPriority(
            IntPtr avrtHandle, AvrtPriority priority);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        protected override float GyroSubSamplePeriod => measuredGyroSubSamplePeriod;
        // See GyroMath.cs's GyroStickBiasCorrection declaration - opts DualSense's gyro-stick into
        // the same stationary-bias correction gyro-mouse already gets, confirmed needed via a real
        // capture (see that comment). Joy-Con never overrides this, so it stays at the class
        // default (Vector3.Zero, a no-op).
        protected override Vector3 GyroStickBiasCorrection => gyroMouseBias;

        // Gyro/accel calibration, read once from DualSense's own hardware calibration feature
        // report (0x05) at Attach() - see ReadGyroCalibration. Nominal sensor-chip scale
        // constants (fixed, not read from hardware): 16 LSB per degree/second, 8192 LSB per g -
        // cross-confirmed against two independent reference implementations (DS4Windows,
        // JoyShockLibrary; DS4Windows's calibration correction below rescales this specific
        // unit's real sensitivity onto these nominal constants before dividing by them).
        private const float GyroLsbPerDegPerSec = 16.0f;
        private const float AccelLsbPerG = 8192.0f;
        private short gyroPitchBias, gyroYawBias, gyroRollBias;
        private short gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus;
        private short gyroSpeedPlus, gyroSpeedMinus;
        private short accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus;
        // False until a real calibration report has been successfully read and CRC-verified (BT)
        // - ExtractIMUValues falls back to the nominal scale with zero bias when this is false,
        // rather than dividing by a degenerate (0-0) Plus/Minus range.
        private bool gyroCalibrationValid;

        public DualSenseController(IntPtr handle_, string path, string serialNum, bool isUsb, int id = 0) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            // Only the amplitude (index 2) is ever read back out for DualSense's simple dual-motor
            // rumble (see SendQueuedRumbleIfAny) - the low/high-frequency values Joy-Con's HD-
            // rumble encoding would use are meaningless here, so this is seeded to all-zero rather
            // than reusing Joy-Con's LowFreqRumble/HighFreqRumble config values.
            rumble_obj = new Rumble(new float[] { 0, 0, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            // Single-unit device, same "primary/solo" convention every non-Joy-Con device uses
            // (see Controller.isLeft's own comment) - CalibrationState.FinishCalibration reads
            // this to correct a real Joy-Con-side gravity-axis sign difference that doesn't apply
            // here, but the flag itself still needs to be true for the gyro calibration wizard.
            isLeft = true;

            PadId = id;
            this.path = path;
            // Transport is the authoritative PnP-bus answer resolved at enumeration
            // (Program.GetControllerTransport: USB vs BTHENUM up the device tree), passed in here so
            // isUSB is correct from packet zero. NOT re-derived from read length in ReceiveRaw:
            // Windows pads reads up to the buffer size, so a wired read can arrive as 78 and be
            // misread as Bluetooth. This is the wire-vs-BT conditional everything else depends on
            // (pairing only runs on USB; a controller on Bluetooth is already paired, so it's never
            // re-paired), so it has to be right the instant the pad exists.
            isUSB = isUsb;

            RefreshGyroOnlyButtonReservations();

            connection = isUSB ? 0x01 : 0x02;

            // Pitch-dominant-motion-leaking-into-yaw correction is opt-in on GyroMousePlayerSpace
            // (see its EnableExtendedAxisCorrection field comment) - confirmed needed on DualSense via
            // a real pure-pitch hardware test, but Joy-Con has no reported version of this problem,
            // so it stays off there and on only here.
            gyroMousePlayerSpace.EnableExtendedAxisCorrection = true;
            gyroStickPlayerSpace.EnableExtendedAxisCorrection = true;
        }

        // No shared shell worth extracting - see Controller.Attach's abstract declaration. This is
        // the exact body of the old Joycon.Attach()'s "if (!UsesNintendoProtocol)" early-return
        // branch, now the whole method since DualSenseController never speaks the Nintendo
        // protocol at all.
        public override int Attach() {
            state = state_.ATTACHED;

            // None of the USB handshake bytes, SPI calibration dump, home-light/player-LED writes,
            // or IMU/rumble/input-mode subcommands a Nintendo device needs apply here - a DualSense
            // doesn't speak that protocol at all. No enable-full-report-mode handshake is known to
            // be required for baseline button/stick/trigger reads; if the first real test shows
            // all-zero/empty reports over Bluetooth, that's the first thing to investigate.
            HIDapi.hid_set_nonblocking(handle, 1);

            // DualSense has no SPI factory calibration to read, so stick_cal/stick2_cal/deadzone
            // would otherwise be left at their class defaults ({0,0,0,0,0,0}/0) - CenterSticks
            // would divide by that zero the moment it's used. Seed an identity calibration matching
            // the DualSense's real raw domain (bytes 0-255, center 128) so stick output is correct
            // out of the box, then let any stored user recalibration (CalibrationState, via the
            // same wizard Joy-Con uses) overlay on top exactly the way it already does for Joy-Con.
            stick_cal[0] = 127; stick_cal[1] = 127;   // max above center (X, Y)
            stick_cal[2] = 128; stick_cal[3] = 128;   // center (X, Y)
            stick_cal[4] = 128; stick_cal[5] = 128;   // min below center (X, Y)
            stick2_cal[0] = 127; stick2_cal[1] = 127;
            stick2_cal[2] = 128; stick2_cal[3] = 128;
            stick2_cal[4] = 128; stick2_cal[5] = 128;
            // A few raw units of headroom over the idle jitter observed on real hardware
            // (~127-133 out of 0-255 at rest) so an uncalibrated DualSense doesn't bleed tiny
            // phantom stick movement before the user ever runs the wizard.
            deadzone = 8;
            deadzone2 = 8;
            getActiveStickData();

            ReadGyroCalibration();

            usbMicrophoneMuteDefaultApplied = false;
            bluetoothMicrophoneMuteDefaultApplied = false;

            form.AppendTextBox("DualSense attached (baseline mode).\r\n");
            return 0;
        }

        private const byte GyroCalibrationFeatureReportId = 0x05;
        private const int GyroCalibrationFeatureReportLen = 41;

        // DualSense's own factory gyro/accel calibration, read via a HID feature report - its
        // equivalent of Joy-Con's SPI-flash calibration read (Joycon.dump_calibration_data).
        // Report ID 0x05, 41 bytes (1 report-ID byte + 36 calibration bytes + trailing CRC32).
        // On Bluetooth the reply is CRC32-verified (seeded 0xA3, via the same Crc32() helper
        // SendDualSenseRumble/SendDualSenseLightbar already use for outgoing reports with a
        // different seed) and retried up to 5 times, matching DS4Windows exactly; USB is trusted
        // on a single unconditional read, also matching DS4Windows. DualSense always uses the
        // same calibration byte grouping on both transports (unlike DualShock 4, which DS4Windows
        // varies by transport) - byte offsets below are DualSense-specific, not shared with any
        // DS4 code path. Falls back to gyroCalibrationValid=false (nominal scale, zero bias) if
        // every attempt fails, rather than leaving stale/degenerate calibration data silently in
        // place.
        private void ReadGyroCalibration() {
            byte[] buf = new byte[GyroCalibrationFeatureReportLen];
            buf[0] = GyroCalibrationFeatureReportId;

            bool verified = false;
            int attempts = isUSB ? 1 : 5;
            for (int attempt = 0; attempt < attempts && !verified; attempt++) {
                int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)GyroCalibrationFeatureReportLen));
                if (ret < GyroCalibrationFeatureReportLen)
                    continue;

                if (isUSB) {
                    verified = true;
                } else {
                    uint received = (uint)buf[37] | ((uint)buf[38] << 8) | ((uint)buf[39] << 16) | ((uint)buf[40] << 24);
                    uint calculated = Crc32(0xA3, buf, GyroCalibrationFeatureReportLen - 4);
                    verified = received == calculated;
                }
            }

            if (!verified) {
                gyroCalibrationValid = false;
                form.AppendTextBox("DualSense gyro calibration read failed - using uncalibrated nominal scale.\r\n");
                LogDualSenseRawDump("Gyro calibration read failed after " + attempts + " attempt(s).");
                return;
            }

            gyroPitchBias = ReadCalibrationInt16(buf, 1);
            gyroYawBias = ReadCalibrationInt16(buf, 3);
            gyroRollBias = ReadCalibrationInt16(buf, 5);
            gyroPitchPlus = ReadCalibrationInt16(buf, 7);
            gyroPitchMinus = ReadCalibrationInt16(buf, 9);
            gyroYawPlus = ReadCalibrationInt16(buf, 11);
            gyroYawMinus = ReadCalibrationInt16(buf, 13);
            gyroRollPlus = ReadCalibrationInt16(buf, 15);
            gyroRollMinus = ReadCalibrationInt16(buf, 17);
            gyroSpeedPlus = ReadCalibrationInt16(buf, 19);
            gyroSpeedMinus = ReadCalibrationInt16(buf, 21);
            accelXPlus = ReadCalibrationInt16(buf, 23);
            accelXMinus = ReadCalibrationInt16(buf, 25);
            accelYPlus = ReadCalibrationInt16(buf, 27);
            accelYMinus = ReadCalibrationInt16(buf, 29);
            accelZPlus = ReadCalibrationInt16(buf, 31);
            accelZMinus = ReadCalibrationInt16(buf, 33);
            gyroCalibrationValid = true;

            LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                "Gyro calibration OK: gyroBias=({0},{1},{2}) gyroPlusMinus=({3}/{4},{5}/{6},{7}/{8}) " +
                "gyroSpeedPlusMinus=({9}/{10}) accelPlusMinus=({11}/{12},{13}/{14},{15}/{16})",
                gyroPitchBias, gyroYawBias, gyroRollBias,
                gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus,
                gyroSpeedPlus, gyroSpeedMinus,
                accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus));
        }

        private static short ReadCalibrationInt16(byte[] buf, int offset) {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }

        // 4-byte LE free-running counter, DualSense's own IMU sample clock - ticks at ~3MHz on
        // real hardware, not the 1MHz a "microsecond timestamp" label would suggest (see the
        // ExtractIMUValues call site for the real-hardware measurement that found this). See
        // measuredGyroSubSamplePeriod's field comment for why this is used over wall-clock timing.
        private static uint ReadTimestampTicks(byte[] buf, int offset) {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) |
                          (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        // Sony's 0x08 command operates on the Bluetooth pairing/connection state; it is not a
        // generic sleep command. Before issuing OFF, preserve the controller's exact current
        // Windows bond through the charge-only USB interface. Report 0x09 supplies the cached
        // host address and Windows supplies the existing 16-byte classic-Bluetooth link key;
        // report 0x0A reasserts those exact values. If any part cannot be verified, never risk the
        // bond: use the host-side radio disconnect instead.
        public override void PowerOff(bool shuttingDown = false) {
            PowerOffCore(shuttingDown, pairingConfirmed: false);
        }

        private void PowerOffCore(bool shuttingDown, bool pairingConfirmed) {
            if (state <= state_.DROPPED)
                return;
            // USB-only: there is no real power-off over a wired handle, so pseudo-sleep instead -
            // park the wired HID, re-enumerate it, and let the charge-only wake monitor wait for
            // the next PS/Home/Capture press. A Bluetooth-connected controller is a separate
            // (non-USB) pad object and still takes the real Bluetooth power-off path below.
            if (isUSB) {
                EnterUsbPseudoSleep(shuttingDown);
                return;
            }
            if (!isUSB) {
                appInitiatedPowerOff = true;
                // Also record it against the MAC in the manager: this pad object is about to be
                // destroyed and rebuilt by the scan, and the flag above dies with it.
                Program.mgr.MarkDeliberatePowerOff(PadMacAddress.GetAddressBytes());
                PrepareChargeOnlyUsbWake();
                // Bond hardening runs BEFORE the audio lanes are torn down, deliberately not
                // between them and the power-off below. Rewriting the 0x0A pairing report is the
                // last thing that should ever touch the controller as it is being parked: doing it
                // immediately before the 0x08/0x02 is confirmed on real hardware to knock the pad
                // back into pairing/low-power broadcast mode, after which it refuses the power-off
                // (featureReportSent=False in every real log) and we silently fell back to the
                // host-side radio disconnect - which only drops the link and leaves the controller
                // AWAKE, so the charge-only wake monitor immediately re-woke it. That is the
                // sleep-then-wake-back-up loop.
                // ...and it is skipped entirely when BetterJoy is the thing shutting down. The
                // reassert exists so the bond survives the controller moving between a PS5 and this
                // PC; a service stop or a machine going to sleep is not a roam, and paying its
                // documented cost there is what produced exactly the failure described above -
                // featureReportSent=False, fallback radio disconnect, controller left awake. It
                // needs the cable to run at all, which is why this is only ever seen with one
                // attached, and never when the pad is already parked in the wake monitor.
                bool pairingStateReasserted =
                    !shuttingDown && !pairingConfirmed && ReassertBluetoothPairingStateOverUsb();
                StopBluetoothMicrophone();
                StopBluetoothAudioStream();
                // Native Bluetooth power-off, exactly as originally implemented in df0514e: send
                // 0x08/0x02 unconditionally, then arm the wake monitor. It is NOT gated on the bond
                // reassert - a failed reassert must never mean the controller is left awake.
                bool sentFeatureReport = SendBluetoothPowerOffFeatureReport();
                if (!sentFeatureReport)
                    BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                DebugLog.Write("DualSense.PowerOff: pad=" + PadId +
                    " pairingConfirmed=" + pairingConfirmed +
                    " pairingStateReasserted=" + pairingStateReasserted +
                    " featureReportSent=" + sentFeatureReport +
                    " monitorChargeOnlyWakeAfterPowerOff=" + monitorChargeOnlyWakeAfterPowerOff);
                state = state_.DROPPED;
                AbandonBluetoothMediaTransport();
                if (monitorChargeOnlyWakeAfterPowerOff)
                    BeginChargeOnlyUsbWakeMonitor(requirePsPress: pairingConfirmed);
            }
        }

        // A Bluetooth pad very often has the cable attached too, and that cable keeps it powered
        // after the link goes: dropping the radio connection on its own leaves it sitting there lit
        // on whatever colour we last wrote, exactly as closing a wired handle did. Same answer -
        // darken it first, while the link is still up and the pad is still streaming (the lightbar
        // write is gated on IMU_DATA_OK), then let the base disconnect it. The wired path does this
        // for itself inside EnterUsbPseudoSleep.
        public override void DisconnectForSuspend() {
            if (!isUSB && state == state_.IMU_DATA_OK) {
                currentPlayerLeds = 0;
                SendDualSenseLightbar(0, 0, 0);
                DebugLog.Write("DualSense: darkened pad=" + PadId + " before suspend disconnect");
            }

            base.DisconnectForSuspend();
        }

        // USB-only pseudo-sleep. Over USB the controller cannot be truly powered off like a live
        // Bluetooth HID pad, but it can be parked as charge-only: BetterJoy closes its HID handle,
        // suppresses re-adoption, nudges Windows to re-enumerate the USB interface, then a wake
        // monitor waits for the next PS/Home report before releasing the USB path back to scanning.
        // shuttingDown=true when BetterJoy is going away (service stop, or the machine suspending).
        // Two things change, both because the process is about to stop running code: the pad is
        // darkened before its handle closes, and no wake monitor is armed.
        private void EnterUsbPseudoSleep(bool shuttingDown = false) {
            if (!isUSB || state <= state_.DROPPED)
                return;

            // Restore an existing Windows bond only if the controller's host differs. A matching
            // host needs no write on plug-in; a missing Windows bond belongs to automatic pairing.
            if (!shuttingDown && BluetoothRadio.TryGetOrCreateClassicPairing(
                    PadMacAddress.GetAddressBytes(), out byte[] pcHostMac, out byte[] pcLinkKey,
                    out bool pairingCreated)) {
                try {
                    if (!pairingCreated) {
                        bool bondVerified = EnsureControllerBondPointsToThisPc(
                            pcHostMac, pcLinkKey, out bool matchesPc);
                        DebugLog.Write("DualSense USB park: hostMatchedPc=" + matchesPc +
                            " bondVerified=" + bondVerified + " pad=" + PadId);
                    }
                } finally {
                    Array.Clear(pcLinkKey, 0, pcLinkKey.Length);
                }
            }

            appInitiatedPowerOff = true;
            Program.mgr.MarkDeliberatePowerOff(PadMacAddress.GetAddressBytes());
            string profileId = ControllerMappings.ProfileIdFor(this);
            chargeOnlyUsbPath = path;
            Program.mgr.MarkChargeOnlyUsbParked(chargeOnlyUsbPath, profileId);
            Program.mgr.PreserveChargeOnlyUsbAfterLongPressPowerOff(profileId);
            monitorChargeOnlyWakeAfterPowerOff = !shuttingDown &&
                Program.mgr.ShouldMonitorChargeOnlyUsbWake(chargeOnlyUsbPath, profileId);

            // Darken the pad before the handle closes. Closing it tells the controller nothing, so
            // it holds the last colour we wrote - that is the lightbar left on across the sleep.
            // A single HID write on an already-open handle costs microseconds and, unlike the
            // re-enumeration below, cannot block while the machine is suspending.
            if (shuttingDown) {
                currentPlayerLeds = 0;
                SendDualSenseLightbar(0, 0, 0);
                DebugLog.Write("DualSense USB pseudo-sleep: darkened pad=" + PadId + " before close");
            }

            ForwardNeutralVirtualInput();
            string parkedPath = chargeOnlyUsbPath;
            DebugLog.Write("DualSense USB pseudo-sleep entered: pad=" + PadId +
                " wakeMonitorArmed=" + monitorChargeOnlyWakeAfterPowerOff);

            state = state_.DROPPED;
            AbandonBluetoothMediaTransport();
            Detach(true);
            Program.mgr.j.Remove(this);

            // Re-enumeration is unusable on the way into a suspend, in both directions: queued, the
            // ThreadPool item is frozen and runs on the far side of the sleep (controller lit all
            // night, going charge-only only after the wake); inline, the SetupAPI device restart
            // itself blocks for the whole suspend, so the stop routine was still inside it when the
            // resume started a second pipeline on top. Neither is survivable here, so a shutting-
            // down pad is darkened by the write above instead, and the interface is simply closed -
            // the machine is taking the USB stack down regardless.
            if (!shuttingDown) {
                ThreadPool.QueueUserWorkItem(_ => {
                    Thread.Sleep(100);
                    bool reenumerated = UsbDeviceReenumerator.TryReenumerateHidInterface(
                        parkedPath, out string detail);
                    DebugLog.Write("DualSense USB pseudo-sleep re-enumeration: path=" +
                        parkedPath + " result=" + reenumerated + " detail=" + detail);
                });
            }

            if (monitorChargeOnlyWakeAfterPowerOff)
                BeginChargeOnlyUsbWakeMonitor();
        }

        // Forward a fully neutral state to whatever virtual controller(s) this pad drives, so games
        // see it present and idle rather than frozen on the last real input. Mirrors ReceiveRaw's
        // own output dispatch; zeroing the parsed fields makes MapTo*Input produce a rest state.
        private void ForwardNeutralVirtualInput() {
            for (int i = 0; i < buttons.Length; i++)
                buttons[i] = false;
            stick[0] = stick[1] = 0f;
            stick2[0] = stick2[1] = 0f;
            triggerVal[0] = triggerVal[1] = 0;
            if (out_xbox != null) {
                try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
            }
            if (out_ds4 != null || out_dualsense != null) {
                var ds4State = MapToDualShock4Input(this);
                if (out_ds4 != null) {
                    try { out_ds4.UpdateInput(ds4State); } catch (Exception) { }
                }
                if (out_dualsense != null) {
                    try { out_dualsense.UpdateInput(ds4State); } catch (Exception) { }
                }
            }
        }

        // Called by the same periodic profile reconciliation that applies lighting, audio, and
        // trigger options - but that reconciliation runs on the manager's scan-timer thread, not
        // this controller's own Poll thread, and the actual pairing sequence below issues many
        // hid_get/send_feature_report calls (plus the Bluetooth authentication-window setup)
        // against the same handle Poll's ReceiveRaw/output calls use continuously. Calling that
        // sequence directly from here would be exactly the concurrent-hid-call hazard
        // ApplyQueuedAutomaticBluetoothPairingIfAny's comment describes - confirmed on real
        // hardware earlier this session. So this only ever records a request;
        // PerformAutomaticBluetoothPairing below does the actual work, only from the Poll thread.
        // One attempt per USB attachment/toggle still applies - set immediately here, not after
        // the Poll thread gets to it - so a failed radio/registry operation or slow Poll
        // iteration can't cause repeat requests every scan.
        public void ApplyAutomaticBluetoothPairing() {
            bool enabled = ControllerMappings.AutomaticBluetoothPairingEnabled(
                ControllerMappings.ProfileIdFor(this));
            if (!enabled) {
                automaticBluetoothPairingAttempted = false;
                freshBluetoothPairingPending = false;
                return;
            }
            if (!isUSB || state <= state_.DROPPED || automaticBluetoothPairingAttempted)
                return;

            // Bond repair needs no wake/connect output and can run during the firmware glow.
            // Fresh pairing and its retries still wait before issuing their connect sequence.
            if (!CanRepairExistingBluetoothBond() &&
                    !Program.mgr.IsDualSenseFirmwareConnectSettled(path))
                return;
            CaptureUSBSleepOnConnectInitialBluetoothState();
            // Same guard as the before-attach path: never queue the ceremony for a controller that
            // is already live over Bluetooth. This is the reconciliation entry point, so without it
            // a cable plugged into a connected pad re-queues the connect routine on every scan.
            if (Program.mgr.HasLiveBluetoothDualSense(PadMacAddress?.GetAddressBytes()))
                return;
            automaticBluetoothPairingAttempted = true;
            Interlocked.Exchange(ref automaticBluetoothPairingPending, 1);
        }

        internal bool TryRunAutomaticBluetoothPairingBeforeAttach() {
            string profileId = ControllerMappings.ProfileIdFor(this);
            if (!isUSB || state != state_.NOT_ATTACHED ||
                    !ControllerMappings.AutomaticBluetoothPairingEnabled(profileId))
                return false;

            CaptureUSBSleepOnConnectInitialBluetoothState();
            // Plugging the cable into a controller that is ALREADY up and streaming over Bluetooth
            // has nothing to pair and nothing to connect. Running the ceremony here fires a connect
            // trigger at a live link and interrupts it. Fall through instead and let the ordinary
            // duplicate resolution park this wired interface as charge-only, which is what happens
            // for a cable plugged into an already-connected pad. Deliberately does NOT set
            // automaticBluetoothPairingAttempted, so if Bluetooth later drops the ceremony can
            // still run for this pad.
            if (Program.mgr.HasLiveBluetoothDualSense(PadMacAddress?.GetAddressBytes()))
                return false;
            // Restore an existing host/key before Attach can engage lighting, audio or input.
            // Only a fresh pairing ceremony needs the firmware-connect settle delay.
            if (!CanRepairExistingBluetoothBond() &&
                    !Program.mgr.IsDualSenseFirmwareConnectSettled(path))
                return false;
            // Bluetooth-preferred automatic pairing owns the controller before the normal pad
            // lifecycle does: no Attach(), no virtual output, no lighting/audio/adaptive-trigger
            // writes. USB-preferred automatic pairing is only bond maintenance; after the feature
            // reports/connect trigger are sent, fall through to the ordinary USB attach path.
            automaticBluetoothPairingAttempted = true;
            bool preferBluetooth = ControllerMappings.UsablePreferredTransport(profileId) ==
                ControllerMappings.PreferredTransportBluetooth;
            PerformAutomaticBluetoothPairing();
            if (monitorChargeOnlyWakeAfterPowerOff)
                return true;
            if (!preferBluetooth)
                return false;
            byte[] mac = PadMacAddress?.GetAddressBytes();
            return automaticBluetoothPairingInProgress ||
                Program.mgr.HasPendingBluetoothPairingAttempt(mac) ||
                Program.mgr.ShouldMonitorChargeOnlyUsbWake(path, profileId);
        }

        private bool CanRepairExistingBluetoothBond() {
            byte[] mac = PadMacAddress?.GetAddressBytes();
            return !freshBluetoothPairingPending &&
                !Program.mgr.HasPendingBluetoothPairingAttempt(mac) &&
                BluetoothRadio.HasClassicPairing(mac);
        }

        protected override void ApplyQueuedAutomaticBluetoothPairingIfAny() {
            if (Interlocked.Exchange(ref automaticBluetoothPairingPending, 0) != 0)
                PerformAutomaticBluetoothPairing();
            // A confirmed pairing has already proved the key. Rewriting it before OFF can disturb
            // the new connection; run only the sleep tail, on this pad's Poll thread.
            if (Interlocked.Exchange(ref roamingSleepPending, 0) != 0 &&
                    ControllerMappings.USBSleepOnConnectMode(ControllerMappings.ProfileIdFor(this)) !=
                        ControllerMappings.USBSleepOnConnectDisabled)
                PowerOffCore(shuttingDown: false, pairingConfirmed: true);
            // Initial USB-only sleep also runs on the Poll thread for the same reason. This is not
            // driven by Hold Home/Capture or inactivity; it is only the first-attach USB policy.
            if (Interlocked.Exchange(ref usbSleepOnConnectPending, 0) != 0)
                PowerOff();
        }

        // Called by the manager reconciliation once this Bluetooth pad reaches IMU_DATA_OK and its
        // MAC had a pending pairing attempt. Records the request; the Poll thread runs PowerOff.
        public void RequestRoamingSleepAfterBluetoothConfirmation() {
            Interlocked.Exchange(ref roamingSleepPending, 1);
        }

        public bool ApplyUSBSleepOnConnectAfterAttach() {
            if (!isUSB || state <= state_.DROPPED)
                return false;
            CaptureUSBSleepOnConnectInitialBluetoothState();
            string profileId = ControllerMappings.ProfileIdFor(this);
            if (usbSleepOnConnectBluetoothWasLive)
                return false;
            if (ControllerMappings.USBSleepOnConnectMode(profileId) !=
                    ControllerMappings.USBSleepOnConnectEnabled)
                return false;
            if (ControllerMappings.AutomaticBluetoothPairingEnabled(profileId)) {
                if (!BluetoothRadio.HasClassicPairing(PadMacAddress?.GetAddressBytes()))
                    freshBluetoothPairingPending = true;
                if (freshBluetoothPairingPending ||
                        Program.mgr.HasPendingBluetoothPairingAttempt(
                            PadMacAddress?.GetAddressBytes())) {
                    DebugLog.Write("DualSense USB sleep-on-connect deferred: pad=" + PadId +
                        " waiting for fresh Bluetooth pairing confirmation");
                    return false;
                }
            }
            // INITIAL connection only. This pad object is destroyed and rebuilt on every wake, so the
            // per-object snapshot flag above resets and cannot enforce "once" - without this the
            // controller gets put straight back to sleep the moment it is woken. The manager holds
            // the mark against the wired path and releases it only on a real unplug.
            if (!Program.mgr.TryMarkUsbSleepOnConnectApplied(path)) {
                DebugLog.Write("DualSense USB sleep-on-connect skipped: pad=" + PadId +
                    " already applied for this connection (not an initial connect)");
                return false;
            }
            QueueUSBSleepOnConnect("initial-usb-attach-enabled");
            return true;
        }

        internal bool ConfirmFreshBluetoothPairing() {
            if (!freshBluetoothPairingPending)
                return false;
            freshBluetoothPairingPending = false;
            // Reapply the transport-specific sleep policy only after fresh pairing is confirmed.
            if (!ShouldUSBSleepOnConnectAfterBluetoothEstablished() ||
                    !Program.mgr.TryMarkUsbSleepOnConnectApplied(path))
                return false;
            QueueUSBSleepOnConnect("fresh-bluetooth-pairing-confirmed");
            return true;
        }

        private void CaptureUSBSleepOnConnectInitialBluetoothState() {
            if (usbSleepOnConnectInitialBluetoothStateKnown || !isUSB)
                return;
            byte[] mac = PadMacAddress?.GetAddressBytes();
            usbSleepOnConnectBluetoothWasLive =
                Program.mgr.HasLiveBluetoothDualSense(mac);
            usbSleepOnConnectInitialBluetoothStateKnown = true;
            DebugLog.Write("DualSense USB sleep-on-connect snapshot: pad=" + PadId +
                " bluetoothWasLive=" + usbSleepOnConnectBluetoothWasLive);
        }

        private bool ShouldUSBSleepOnConnectAfterBluetoothEstablished() {
            // Reject only a pad that is actually gone. NOT_ATTACHED is 0 and DROPPED is 1, so the
            // obvious "state <= DROPPED" also rejects NOT_ATTACHED - which is precisely the state
            // this runs in: the automatic-pairing ceremony owns the controller BEFORE the normal
            // pad lifecycle (TryRunAutomaticBluetoothPairingBeforeAttach requires
            // state == NOT_ATTACHED). That made this return false on every single connect, before
            // the USB-sleep mode was even read, so "USB Sleep = Bluetooth" never armed the sleep
            // that follows a confirmed Bluetooth connection. Manual power-off was unaffected
            // because that pad is IMU_DATA_OK.
            if (!isUSB || state == state_.DROPPED)
                return false;
            CaptureUSBSleepOnConnectInitialBluetoothState();
            if (usbSleepOnConnectBluetoothWasLive)
                return false;
            string mode = ControllerMappings.USBSleepOnConnectMode(
                ControllerMappings.ProfileIdFor(this));
            return mode == ControllerMappings.USBSleepOnConnectEnabled ||
                (mode == ControllerMappings.USBSleepOnConnectBluetooth && PrefersBluetoothTransport());
        }

        private void QueueUSBSleepOnConnect(string reason) {
            Interlocked.Exchange(ref usbSleepOnConnectPending, 1);
            DebugLog.Write("DualSense USB sleep-on-connect queued: pad=" + PadId +
                " reason=" + reason);
        }

        private void PerformAutomaticBluetoothPairing() {
            byte[] controllerMac = PadMacAddress.GetAddressBytes();
            BluetoothRadio.BeginClassicPairingRegistryTrace(controllerMac);
            byte[] hostMacLittleEndian = null;
            byte[] linkKey = null;
            byte[] emptyHost = new byte[6];
            byte[] emptyKey = new byte[16];
            bool created = false;
            try {
                if (!BluetoothRadio.TryGetOrCreateClassicPairing(controllerMac,
                        out hostMacLittleEndian, out linkKey, out created)) {
                    form.AppendTextBox("Automatic DualSense Bluetooth pairing could not access " +
                        "a local Bluetooth radio or its Windows bond store.\r\n");
                    return;
                }
                if (created)
                    freshBluetoothPairingPending = true;

                // An established Windows bond needs only controller-side repair in either mode.
                // A key committed by an unfinished fresh ceremony still needs connect/confirmation.
                if (!created && !freshBluetoothPairingPending &&
                        !Program.mgr.HasPendingBluetoothPairingAttempt(controllerMac)) {
                    PerformRepairModePairing(controllerMac, hostMacLittleEndian, linkKey);
                    return;
                }

                // A Windows key which already exists must never be cleared just because the live
                // Bluetooth link failed to hold. Registry-state testing proved the persistent bond
                // is complete in round one; later rounds were only repeating the live transition.
                // Reuse that bond and retry only the controller-native low-power handoff below.
                if (!created) {
                    PerformEnabledConnectAndConfirm(controllerMac, hostMacLittleEndian, linkKey);
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    created ? "windows-bond-missing" : "windows-bond-reused");

                bool previousPairingCleared = SendBluetoothPairingFeatureReport(
                    handle, emptyHost, emptyKey);
                if (previousPairingCleared)
                    previousPairingCleared = WaitForBluetoothPairingHost(handle,
                        emptyHost, PairingRecordCommitTimeoutMs);
                if (!previousPairingCleared) {
                    form.AppendTextBox("Automatic DualSense Bluetooth pairing could not verify " +
                        "that the controller cleared its previous bond.\r\n");
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "controller-bond-cleared");

                bool controllerUpdated = SendBluetoothPairingFeatureReport(
                    handle, hostMacLittleEndian, linkKey);
                if (controllerUpdated)
                    controllerUpdated = WaitForBluetoothPairingHost(handle,
                        hostMacLittleEndian, PairingRecordCommitTimeoutMs);
                if (!controllerUpdated) {
                    form.AppendTextBox("Automatic DualSense Bluetooth pairing was rejected by " +
                        "the controller; the Windows bond was left unchanged.\r\n");
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "controller-bond-written");

                bool pairingStateReasserted = SendBluetoothPairingFeatureReport(
                    handle, hostMacLittleEndian, linkKey);
                if (pairingStateReasserted)
                    pairingStateReasserted = WaitForBluetoothPairingHost(handle,
                        hostMacLittleEndian, PairingRecordCommitTimeoutMs);
                if (!pairingStateReasserted) {
                    form.AppendTextBox("DualSense Bluetooth bond was saved, but its pairing " +
                        "state could not be reasserted and verified; reconnect USB and try again.\r\n");
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "controller-bond-reasserted");

                // Authentication starts as soon as 0x08/ON brings up the incoming link. BthPort
                // must already have the same controller-verified key at that instant; waiting for
                // device.connected is circular because that flag requires authentication first.
                if (!BluetoothRadio.TryCommitClassicLinkKey(
                        hostMacLittleEndian, controllerMac, linkKey)) {
                    form.AppendTextBox("DualSense accepted its Bluetooth bond, but BetterJoy " +
                        "could not commit the matching key to Windows.\r\n");
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "windows-link-key-committed");

                // Reached ONLY when created == true - no Windows bond existed, so this controller
                // genuinely needs to PAIR. Never send the 0x08/0x02 low-power/assert edge here:
                // parking a controller that is still establishing its bond works against the very
                // pairing it is in the middle of. It was also treated as fatal, so a rejected edge
                // aborted the whole fresh pair before the connect trigger was ever issued.
                // The bond has been written, verified and committed to Windows above - go straight
                // to the connect. Sleeping belongs only to a controller whose bond ALREADY exists
                // (PerformRepairModePairing, or the roaming sleep after a confirmed Bluetooth link).
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "fresh-bond-low-power-skipped-needs-pairing");
                DebugLog.Write("DualSense automatic Bluetooth pairing: pad=" + PadId +
                    " createdWindowsBond=" + created +
                    " previousPairingCleared=" + previousPairingCleared +
                    " pairingStateReasserted=" + pairingStateReasserted +
                    " freshBondLowPowerSkipped=True (needs actual pairing)");
                BeginEnabledConnectAndConfirm(controllerMac, hostMacLittleEndian,
                    created, "fresh bond");
            } finally {
                if (linkKey != null)
                    Array.Clear(linkKey, 0, linkKey.Length);
                Array.Clear(emptyHost, 0, emptyHost.Length);
                Array.Clear(emptyKey, 0, emptyKey.Length);
            }
        }

        // Ensures the controller's onboard bond points at THIS PC. Reads the current host (report
        // 0x09, readable unlike the write-only link key); if it already matches, does nothing and
        // returns true with matchedAlready=true (no clobber). If it's foreign/empty, repoints it
        // (0x0A) and verifies via WaitForBluetoothPairingHost. Returns false if the host cannot be
        // read or the repair cannot be verified. Shared by parking, Repair and Enabled pairing.
        private bool EnsureControllerBondPointsToThisPc(byte[] hostMacLittleEndian,
                byte[] linkKey, out bool matchedAlready) {
            byte[] currentHost = ReadControllerPairedHost();
            matchedAlready = currentHost != null &&
                ByteArraysEqual(currentHost, hostMacLittleEndian);
            if (currentHost == null)
                return false;
            if (matchedAlready)
                return true;
            bool reasserted = SendBluetoothPairingFeatureReport(
                handle, hostMacLittleEndian, linkKey);
            if (reasserted)
                reasserted = WaitForBluetoothPairingHost(handle,
                    hostMacLittleEndian, PairingRecordCommitTimeoutMs);
            return reasserted;
        }

        // Existing-bond path: confirm the controller points at this PC, then retry only the live
        // connect trigger. Never clear or rewrite an already-correct bond to solve a link problem.
        private void PerformEnabledConnectAndConfirm(byte[] controllerMac,
                byte[] hostMacLittleEndian, byte[] linkKey) {
            if (!EnsureControllerBondPointsToThisPc(hostMacLittleEndian, linkKey,
                    out bool matchesPc)) {
                form.AppendTextBox("DualSense bond could not be reasserted over USB; reconnect " +
                    "and try again.\r\n");
                DebugLog.Write("DualSense enabled connect: pad=" + PadId +
                    " hostMatchedPc=False reasserted=False (aborted)");
                return;
            }

            DebugLog.Write("DualSense enabled connect: pad=" + PadId +
                " hostMatchedPc=" + matchesPc + " reusingWindowsBond=True");
            BeginEnabledConnectAndConfirm(controllerMac, hostMacLittleEndian,
                false, matchesPc ? "existing bond" : "repaired bond");
        }

        // Trigger the controller's incoming link, register the already-existing production attempt,
        // then release USB immediately. A fresh caller has already committed the controller-verified
        // key to BthPort immediately before reaching this method.
        private void BeginEnabledConnectAndConfirm(byte[] controllerMac,
                byte[] hostMacLittleEndian, bool createdWindowsBond, string bondState) {
            string profileId = ControllerMappings.ProfileIdFor(this);
            string usbPath = path;
            bool preferBluetooth =
                ControllerMappings.UsablePreferredTransport(profileId) ==
                ControllerMappings.PreferredTransportBluetooth;
            bool sleepOnConnectAfterBluetoothEstablished =
                ShouldUSBSleepOnConnectAfterBluetoothEstablished();

            // Only tell the controller to connect when Bluetooth can actually be used AND is wanted.
            // All the bond work has already run by this point, so a USB-preferred controller keeps a
            // valid bond for a later roam - it is simply never told to connect, which is what used to
            // produce the blinking player LED on a plain USB plug-in. A first-time bond
            // (createdWindowsBond) is exempt: that pairing ceremony needs the connect to complete, so
            // a first-ever pair still works. With no usable host adapter nothing is ever sent - a
            // soft-toggled-off radio still enumerates via BluetoothFindFirstRadio, which is exactly
            // how the connect leaked through before. Returning here also registers no pairing
            // attempt, so the retry/give-up cycle never starts and the pad falls straight through to
            // the ordinary USB attach.
            // Deliberately gated HERE and NOT inside SendBluetoothControlFeatureReport: that
            // chokepoint is shared with the charge-only wake monitor's PS-press wake, which must keep
            // working regardless of host adapter state.
            bool radioAvailable = BluetoothRadio.IsLocalRadioAvailable();
            if (!radioAvailable || (!preferBluetooth && !createdWindowsBond)) {
                DebugLog.Write("DualSense pairing handoff: pad=" + PadId +
                    " bond=" + bondState + " connect suppressed (preferBluetooth=" +
                    preferBluetooth + " createdWindowsBond=" + createdWindowsBond +
                    " radioAvailable=" + radioAvailable + ")");
                return;
            }

            bool connectRequested = SendBluetoothControlFeatureReport(
                handle, false, DualSenseBluetoothControlOn);
            BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                connectRequested ? "bluetooth-on-sent" : "bluetooth-on-rejected");
            if (!connectRequested) {
                form.AppendTextBox("DualSense bond was saved, but its Bluetooth connection " +
                    "trigger was rejected.\r\n");
                DebugLog.Write("DualSense pairing handoff: pad=" + PadId +
                    " bond=" + bondState + " connectRequested=False (aborted)");
                return;
            }

            if (!preferBluetooth) {
                bool queuedUsbMaintenance = QueueAutomaticBluetoothPairingFinalization(
                    hostMacLittleEndian, controllerMac, connectRequested, createdWindowsBond,
                    0, sleepOnConnectAfterBluetoothEstablished);
                if (!queuedUsbMaintenance) {
                    form.AppendTextBox("DualSense bond was saved, but Windows device setup could " +
                        "not be scheduled.\r\n");
                    return;
                }

                form.AppendTextBox("DualSense " + bondState +
                    " saved; keeping USB active while Bluetooth setup completes.\r\n");
                DebugLog.Write("DualSense Bluetooth maintenance: pad=" + PadId +
                    " bond=" + bondState +
                    " preferredTransport=USB connectRequested=True" +
                    " sleepOnConnectAfterBluetooth=" +
                    sleepOnConnectAfterBluetoothEstablished +
                    " finalizerQueued=True windowsKeyCommitted=" + createdWindowsBond);
                return;
            }

            automaticBluetoothPairingInProgress = true;
            Program.mgr.SuppressUsbControllerForBluetoothPreference(usbPath, profileId);
            int attemptNumber = Program.mgr.RecordBluetoothPairingAttempt(
                controllerMac, profileId, usbPath,
                sleepOnConnectAfterBluetoothEstablished);
            bool queued = QueueAutomaticBluetoothPairingFinalization(
                hostMacLittleEndian, controllerMac, connectRequested, createdWindowsBond,
                attemptNumber, sleepOnConnectAfterBluetoothEstablished);
            if (!queued) {
                automaticBluetoothPairingInProgress = false;
                form.AppendTextBox("DualSense bond was saved, but Windows device setup could " +
                    "not be scheduled.\r\n");
                return;
            }

            form.AppendTextBox("DualSense " + bondState +
                " saved; waiting for its incoming Bluetooth connection.\r\n");
            DebugLog.Write("DualSense pairing handoff: pad=" + PadId +
                " attempt=" + attemptNumber +
                " bond=" + bondState + " connectRequested=True finalizerQueued=True" +
                " sleepOnConnectAfterBluetooth=" +
                sleepOnConnectAfterBluetoothEstablished +
                " windowsKeyCommitted=" + createdWindowsBond);

            state = state_.DROPPED;
            Detach(true);
            Program.mgr.j.Remove(this);
        }

        // Restore an established bond before normal initialization, or on the Poll thread when
        // automatic pairing is toggled on. Parked pads wait for PS; disabling USB sleep starts
        // the preferred transport immediately after the bond is verified.
        private void PerformRepairModePairing(byte[] controllerMac,
                byte[] hostMacLittleEndian, byte[] linkKey) {
            string profileId = ControllerMappings.ProfileIdFor(this);
            string usbPath = path;

            if (!EnsureControllerBondPointsToThisPc(hostMacLittleEndian, linkKey,
                    out bool matchesPc)) {
                // Couldn't confirm the controller points at this PC - do NOT sleep/wake (avoid
                // waking whatever host it still holds, e.g. a nearby PS5).
                form.AppendTextBox("DualSense bond could not be repaired over USB; reconnect " +
                    "and try again.\r\n");
                DebugLog.Write("DualSense repair: pad=" + PadId +
                    " hostMatchedPc=False reasserted=False (aborted, not woken)");
                return;
            }

            string sleepMode = ControllerMappings.USBSleepOnConnectMode(profileId);
            if (sleepMode == ControllerMappings.USBSleepOnConnectDisabled) {
                // USB falls through to normal Attach; Bluetooth uses the confirmed connection
                // flow with sleep-after-confirmation disabled by the same profile setting.
                if (PrefersBluetoothTransport())
                    BeginEnabledConnectAndConfirm(controllerMac, hostMacLittleEndian,
                        false, matchesPc ? "existing bond" : "repaired bond");
                return;
            }
            bool shouldPark = sleepMode == ControllerMappings.USBSleepOnConnectEnabled ||
                (PrefersBluetoothTransport() &&
                    sleepMode == ControllerMappings.USBSleepOnConnectBluetooth);
            if (!shouldPark || !Program.mgr.TryMarkUsbSleepOnConnectApplied(usbPath)) {
                DebugLog.Write("DualSense repair: pad=" + PadId +
                    " hostMatchedPc=" + matchesPc +
                    " bondVerified=True connectRequested=False parked=False");
                return;
            }

            // Host verification precedes low power and parking. Only the PS wake monitor may
            // request a connection after this repair.
            bool sentLowPower = SendBluetoothControlFeatureReport(
                handle, false, DualSenseBluetoothControlOff);

            chargeOnlyUsbPath = usbPath;
            appInitiatedPowerOff = true;
            Program.mgr.MarkDeliberatePowerOff(controllerMac);
            Program.mgr.MarkChargeOnlyUsbParked(usbPath, profileId);
            // Grace window so a brief USB re-enumeration while the controller drops to low power
            // doesn't release the suppression out from under the wake monitor - same call PowerOff
            // makes via PrepareChargeOnlyUsbWake.
            Program.mgr.PreserveChargeOnlyUsbAfterLongPressPowerOff(profileId);
            monitorChargeOnlyWakeAfterPowerOff =
                Program.mgr.ShouldMonitorChargeOnlyUsbWake(chargeOnlyUsbPath, profileId);

            form.AppendTextBox(matchesPc
                ? "DualSense already bonded to this PC; parked until PS is pressed.\r\n"
                : "DualSense bond repaired; parked until PS is pressed.\r\n");
            DebugLog.Write("DualSense repair: pad=" + PadId +
                " hostMatchedPc=" + matchesPc +
                " bondVerified=True" +
                " sentLowPower=" + sentLowPower +
                " wakeMonitorArmed=" + monitorChargeOnlyWakeAfterPowerOff);

            // Same step-off order PowerOff uses: DROPPED (which stops this controller's own Poll
            // thread) before BeginChargeOnlyUsbWakeMonitor opens its own handle to the same USB
            // path - never leave both touching the device at once.
            state = state_.DROPPED;
            Detach(true);
            Program.mgr.j.Remove(this);
            if (monitorChargeOnlyWakeAfterPowerOff)
                BeginChargeOnlyUsbWakeMonitor(requirePsPress: true);
        }

        // Reads the host address the controller is currently bonded to (report 0x09, offset 10),
        // little-endian to match hostMacLittleEndian. Returns null if it can't be read. The host is
        // readable unlike the write-only link key, which is what lets Repair skip rewriting a bond
        // that already points at this PC.
        private byte[] ReadControllerPairedHost() {
            byte[] pairingInfo = new byte[DualSensePairingInfoFeatureReportLen];
            pairingInfo[0] = DualSensePairingInfoFeatureReportId;
            int received = HIDapi.hid_get_feature_report(handle, pairingInfo,
                new UIntPtr((uint)pairingInfo.Length));
            if (received < DualSensePairingHostAddressOffset + 6)
                return null;
            byte[] host = new byte[6];
            Buffer.BlockCopy(pairingInfo, DualSensePairingHostAddressOffset, host, 0, 6);
            return host;
        }

        private bool QueueAutomaticBluetoothPairingFinalization(
                byte[] hostMacLittleEndian, byte[] controllerMac,
                bool connectRequested, bool createdWindowsBond, int attemptNumber,
                bool sleepOnConnectAfterBluetoothEstablished) {
            byte[] hostCopy = (byte[])hostMacLittleEndian.Clone();
            byte[] controllerCopy = (byte[])controllerMac.Clone();
            bool queued = ThreadPool.QueueUserWorkItem(_ => {
                try {
                    bool bluetoothHandoff = attemptNumber > 0;
                    bool completed = BluetoothRadio.TryFinalizeClassicHidPairing(
                        hostCopy, controllerCopy, BluetoothPairingFallbackName(),
                        createdWindowsBond, 10000);
                    // Previously also reopened a fresh USB handle here and rewrote the pairing-info
                    // feature report (0x0A) once more as a "final key reassert" once Windows
                    // finished authenticating - confirmed on real hardware to knock the controller
                    // back into pairing/low-power broadcast mode right as the connection was
                    // settling, so Windows kept showing Connected while the controller itself
                    // stopped actually being a working HID endpoint. A successful return below only
                    // means Windows accepted the live device's HID-service setup request. The
                    // manager's existing sustained IMU dwell remains the real success criterion.
                    form.AppendTextBox(completed
                        ? (bluetoothHandoff
                            ? "DualSense Bluetooth HID setup requested; confirming sustained input.\r\n"
                            : "DualSense Bluetooth HID setup requested; keeping USB active.\r\n")
                        : (bluetoothHandoff
                            ? "DualSense Bluetooth bond was saved, but its live incoming connection " +
                                "did not reach Windows HID setup. Press PS once and try again.\r\n"
                            : "DualSense Bluetooth bond was saved, but its USB-preferred " +
                                "maintenance connection did not reach Windows HID setup.\r\n"));
                    DebugLog.Write("DualSense automatic Bluetooth registration: pad=" + PadId +
                        " attempt=" + attemptNumber +
                        " createdWindowsBond=" + createdWindowsBond +
                        " connectRequested=" + connectRequested +
                        " sleepOnConnectAfterBluetooth=" +
                        sleepOnConnectAfterBluetoothEstablished +
                        " liveHidSetupRequested=" + completed);
                    BluetoothRadio.MarkClassicPairingRegistryTrace(controllerCopy,
                        completed ? "windows-hid-setup-requested" :
                            "windows-hid-setup-not-reached");
                    // Deliberately NO sleep here. This is the maintenance path - bluetoothHandoff is
                    // false, meaning attemptNumber was 0 and no Bluetooth pad was ever confirmed - so
                    // sleeping from here parked the controller BEFORE any Bluetooth connection
                    // existed, and did it on the USB pad (pseudo-sleep) instead of as a roaming sleep
                    // on the Bluetooth pad. "USB Sleep = Bluetooth" means park only AFTER Bluetooth
                    // is actually established; that ordering matters because this mode's wake monitor
                    // depends on the controller having made a brief Bluetooth connection first.
                    // Each mode has exactly one trigger, as it did before this branch existed:
                    // Bluetooth sleeps from the confirmed-BT path (RecordBluetoothPairingAttempt ->
                    // TryConfirmBluetoothPairing -> RequestRoamingSleepAfterBluetoothConfirmation),
                    // and Enabled sleeps on the initial attach via
                    // ApplyUSBSleepOnConnectAfterAttach. If Bluetooth is never established, Bluetooth
                    // mode simply performs no sleep on connect - that is the USB path.
                } finally {
                    automaticBluetoothPairingInProgress = false;
                    Array.Clear(hostCopy, 0, hostCopy.Length);
                    Array.Clear(controllerCopy, 0, controllerCopy.Length);
                }
            });
            if (!queued) {
                Array.Clear(hostCopy, 0, hostCopy.Length);
                Array.Clear(controllerCopy, 0, controllerCopy.Length);
                form.AppendTextBox("DualSense Bluetooth bond was saved, but Windows device " +
                    "registration could not be started.\r\n");
            }
            return queued;
        }

        private string BluetoothPairingFallbackName() {
            return path != null &&
                path.IndexOf("PID_0DF2", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "DualSense Edge Wireless Controller"
                    : "DualSense Wireless Controller";
        }

        private bool ReassertBluetoothPairingStateOverUsb() {
            if (String.IsNullOrEmpty(chargeOnlyUsbPath))
                return false;

            IntPtr usbHandle = HIDapi.hid_open_path(chargeOnlyUsbPath);
            if (usbHandle == IntPtr.Zero)
                return false;

            try {
                return ReassertBluetoothPairingStateOverUsb(usbHandle,
                    PadMacAddress.GetAddressBytes());
            } finally {
                HIDapi.hid_close(usbHandle);
            }
        }

        private static bool ReassertBluetoothPairingStateOverUsb(IntPtr usbHandle,
                byte[] expectedControllerMac) {
            if (usbHandle == IntPtr.Zero)
                return false;

            byte[] pairingInfo = new byte[DualSensePairingInfoFeatureReportLen];
            byte[] linkKey = null;
            try {
                pairingInfo[0] = DualSensePairingInfoFeatureReportId;
                int received = HIDapi.hid_get_feature_report(usbHandle, pairingInfo,
                    new UIntPtr((uint)pairingInfo.Length));
                if (received < DualSensePairingHostAddressOffset + 6)
                    return false;

                byte[] controllerMac = new byte[6];
                for (int i = 0; i < controllerMac.Length; i++)
                    controllerMac[i] = pairingInfo[1 + (5 - i)];
                if (expectedControllerMac != null &&
                        !ByteArraysEqual(controllerMac, expectedControllerMac))
                    return false;

                byte[] hostMacLittleEndian = new byte[6];
                Buffer.BlockCopy(pairingInfo, DualSensePairingHostAddressOffset,
                    hostMacLittleEndian, 0, hostMacLittleEndian.Length);
                bool emptyHost = true;
                for (int i = 0; i < hostMacLittleEndian.Length; i++)
                    emptyHost &= hostMacLittleEndian[i] == 0;
                if (emptyHost || !BluetoothRadio.TryGetClassicLinkKey(
                        hostMacLittleEndian, controllerMac, out linkKey))
                    return false;

                bool pairingStateReasserted = SendBluetoothPairingFeatureReport(
                    usbHandle, hostMacLittleEndian, linkKey);
                return pairingStateReasserted && WaitForBluetoothPairingHost(
                    usbHandle, hostMacLittleEndian, PairingRecordCommitTimeoutMs);
            } finally {
                if (linkKey != null)
                    Array.Clear(linkKey, 0, linkKey.Length);
                Array.Clear(pairingInfo, 0, pairingInfo.Length);
            }
        }

        private static bool SendBluetoothPairingFeatureReport(IntPtr targetHandle,
                byte[] hostMacLittleEndian, byte[] linkKey) {
            if (targetHandle == IntPtr.Zero || hostMacLittleEndian == null ||
                    hostMacLittleEndian.Length != 6 || linkKey == null ||
                    linkKey.Length != 16)
                return false;

            byte[] report = new byte[DualSenseSetPairingFeatureReportLen];
            try {
                report[0] = DualSenseSetPairingFeatureReportId;
                Buffer.BlockCopy(hostMacLittleEndian, 0, report, 1,
                    hostMacLittleEndian.Length);
                Buffer.BlockCopy(linkKey, 0, report, 7, linkKey.Length);
                // Bytes 23..26 are the optional CRC field. USB uses zero, matching the other
                // USB feature reports and the controller descriptor's 26-byte payload.
                bool sent = HIDapi.hid_send_feature_report(targetHandle, report,
                    new UIntPtr((uint)report.Length)) == report.Length;
                return sent;
            } finally {
                Array.Clear(report, 0, report.Length);
            }
        }

        private static bool WaitForBluetoothPairingHost(IntPtr targetHandle,
                byte[] expectedHostMacLittleEndian, int timeoutMs) {
            if (targetHandle == IntPtr.Zero || expectedHostMacLittleEndian == null ||
                    expectedHostMacLittleEndian.Length != 6)
                return false;

            Stopwatch timer = Stopwatch.StartNew();
            byte[] pairingInfo = new byte[DualSensePairingInfoFeatureReportLen];
            try {
                while (true) {
                    Array.Clear(pairingInfo, 0, pairingInfo.Length);
                    pairingInfo[0] = DualSensePairingInfoFeatureReportId;
                    int received = HIDapi.hid_get_feature_report(targetHandle, pairingInfo,
                        new UIntPtr((uint)pairingInfo.Length));
                    if (received >= DualSensePairingHostAddressOffset + 6) {
                        bool matches = true;
                        for (int i = 0; i < expectedHostMacLittleEndian.Length; i++) {
                            if (pairingInfo[DualSensePairingHostAddressOffset + i] !=
                                    expectedHostMacLittleEndian[i]) {
                                matches = false;
                                break;
                            }
                        }
                        if (matches)
                            return true;
                    }

                    if (timer.ElapsedMilliseconds >= timeoutMs)
                        return false;
                    Thread.Sleep(PairingRecordPollIntervalMs);
                }
            } finally {
                Array.Clear(pairingInfo, 0, pairingInfo.Length);
                timer.Stop();
            }
        }

        // Restored verbatim from df0514e. The USB-side wake that brings a dormant/off controller's
        // Bluetooth radio back - 17 bytes, not the 47-byte control report, and repeated: "A
        // captured PS5 connection sequence repeats this exact 17-byte feature report a few times.
        // Match that behavior so one transient USB control transfer cannot lose the wake request
        // before the controller's Bluetooth radio begins reconnecting."
        private static bool SendUsbBluetoothWakeControl(IntPtr wakeHandle) {
            return SendUsbBluetoothControlFeatureReport(wakeHandle,
                DualSenseUsbBluetoothWakeControl);
        }

        private static bool SendUsbBluetoothControlFeatureReport(
                IntPtr wakeHandle, byte command) {
            byte[] report = new byte[DualSenseUsbBluetoothWakeReportLen];
            report[0] = DualSenseBluetoothControlFeatureReportId;
            report[1] = command;

            bool sent = false;
            for (int attempt = 0; attempt < 3; attempt++) {
                sent |= HIDapi.hid_send_feature_report(wakeHandle, report,
                    new UIntPtr((uint)report.Length)) == report.Length;
                if (attempt < 2)
                    Thread.Sleep(20);
            }
            return sent;
        }

        // A firmware-initiated power off - PS held past the controller's OWN hardware timeout -
        // runs none of BetterJoy's power-off code. The controller simply goes completely dark,
        // radio included. That is NOT the dormant-paired state PowerOff() leaves behind: there is
        // no live radio for the charge-only wake monitor to watch, so a later PS press has nothing
        // on our side listening and the controller is effectively stranded while still cabled.
        //
        // Recover it deliberately, in the order the hardware needs: wake the radio back over the
        // still-present wired interface (0x08/0x11), let it come up, park it dormant-paired
        // (0x08/0x02), then arm the same wake monitor PowerOff() would have armed.
        //
        // Called from the manager's CleanUp pass for every dropped DualSense. Self-limiting: it
        // opens its own handle (this pad's is already closed), and a genuine unplug simply fails
        // that open and no-ops. Runs on a worker so the HID round-trip and settle never block the
        // scan pass.
        public void RecoverFromFirmwarePowerOff() {
            // NEVER wake a controller BetterJoy itself shut down. This is deliberately checked
            // against the manager's MAC-keyed record, not just this object's own flag: the pad a
            // power-off ran on is destroyed and rebuilt by the scan, so the per-object flag reads
            // false on every pad after the first and would let the wake fire on a controller we
            // put to sleep on purpose. The record clears only once the controller streams again.
            // One and done. This pad never received a single input report, so it isn't a working
            // controller that disappeared - it's the pad a previous wake produced, which came up
            // and died without ever streaming. Waking it again just loops (drop -> wake -> connect
            // -> drop, every ~4s on real hardware). Only a pad that actually worked gets a wake.
            if (!lightbarTransportKnown)
                return;
            byte[] mac = PadMacAddress.GetAddressBytes();
            if (appInitiatedPowerOff || Program.mgr.WasDeliberatelyPoweredOff(mac))
                return;
            // A pairing attempt in flight churns the Bluetooth pad up and down by design - that is
            // the attempt working, not a controller that died. Checked in the manager for the same
            // object-lifetime reason as above.
            if (automaticBluetoothPairingInProgress ||
                    Program.mgr.HasPendingBluetoothPairingAttempt(mac))
                return;
            // The wake is a BLUETOOTH wake; it only makes sense for a Bluetooth-preferred profile.
            string profileId = ControllerMappings.ProfileIdFor(this);
            if (ControllerMappings.UsablePreferredTransport(profileId) !=
                    ControllerMappings.PreferredTransportBluetooth)
                return;
            // A Bluetooth pad the SCAN created (rather than one our own power-off path built) has
            // never had chargeOnlyUsbPath populated - and that is precisely the pad that drops when
            // the controller dies on its own. Recover the wired interface by profile, exactly as
            // PrepareChargeOnlyUsbWake does; without this the whole thing silently no-ops on the
            // one case it exists for.
            string wiredPath = isUSB ? path : chargeOnlyUsbPath;
            if (String.IsNullOrEmpty(wiredPath))
                Program.mgr.TryGetChargeOnlyUsbPath(profileId, out wiredPath);
            if (String.IsNullOrEmpty(wiredPath)) {
                // Never fail silently here again - a missing wired path is a real answer.
                DebugLog.Write("DualSense disappeared - wake skipped: pad=" + PadId +
                    " no wired interface known for profile " + profileId);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => {
                // Give the wired interface a moment: the controller going dark can briefly take the
                // path with it before it settles back as charge-only. Retry the open rather than
                // deciding "gone" on one attempt.
                IntPtr wakeHandle = IntPtr.Zero;
                for (int attempt = 0; attempt < 8 && wakeHandle == IntPtr.Zero; attempt++) {
                    if (attempt > 0)
                        Thread.Sleep(250);
                    wakeHandle = HIDapi.hid_open_path(wiredPath);
                }
                bool wokeOn = false;
                bool wokeUsb = false;
                if (wakeHandle != IntPtr.Zero) {
                    try {
                        // 0x08/0x01 first: this is the SAME trigger the service sends over the
                        // wired interface the moment it adopts a DualSense at startup, and the same
                        // one MonitorChargeOnlyUsbWake sends on a PS press. Restarting the service
                        // waking a dark controller is exactly this report landing - so it is the
                        // one wake with real evidence behind it on this hardware.
                        wokeOn = SendBluetoothControlFeatureReport(
                            wakeHandle, false, DualSenseBluetoothControlOn);
                        // Then df0514e's 17-byte USB wake (0x08/0x11, x3) as a second chance -
                        // taken from a captured PS5 connection sequence, but never confirmed on
                        // current hardware. Harmless if the controller is already coming up.
                        wokeUsb = SendUsbBluetoothWakeControl(wakeHandle);
                    } finally {
                        HIDapi.hid_close(wakeHandle);
                    }
                }
                DebugLog.Write("DualSense disappeared - wake attempt: pad=" + PadId +
                    " wiredPath=" + (wakeHandle != IntPtr.Zero ? "open" : "unavailable") +
                    " sentConnectTrigger=" + wokeOn + " sentUsbWake=" + wokeUsb);
                // Deliberately nothing further: no low-power park, no wake monitor. If the
                // controller comes back, the normal scan adopts it like any other connection.
            });
        }

        // NATIVE Bluetooth power-off - restored verbatim from df0514e. Sony's own low-power command
        // (0x08 / 0x02) sent over the BLUETOOTH handle with the CRC32 (seed 0x53) that Bluetooth
        // feature reports require, and taken under outputReportLock so it cannot interleave with
        // this pad's other output traffic on the same handle. Deliberately an instance method, not
        // the static generic sender, for exactly that lock.
        //
        // Why this exists rather than a Windows-side disconnect: an OS-level disconnect bypasses
        // the controller's own shutdown/reconnect lifecycle and leaves a cable-powered controller
        // unable to reconnect on a PS press. DisconnectDevice is the fallback for when the
        // controller actually rejects this command - never the normal path.
        private bool SendBluetoothPowerOffFeatureReport() {
            if (handle == IntPtr.Zero || isUSB)
                return false;

            byte[] report = new byte[DualSenseBluetoothControlFeatureReportLen];
            report[0] = DualSenseBluetoothControlFeatureReportId;
            report[1] = DualSenseBluetoothControlOff;
            uint crc = Crc32(0x53, report, report.Length - 4);
            report[report.Length - 4] = (byte)crc;
            report[report.Length - 3] = (byte)(crc >> 8);
            report[report.Length - 2] = (byte)(crc >> 16);
            report[report.Length - 1] = (byte)(crc >> 24);

            lock (outputReportLock) {
                return HIDapi.hid_send_feature_report(handle, report,
                    new UIntPtr((uint)report.Length)) == report.Length;
            }
        }

        private static bool SendBluetoothControlFeatureReport(IntPtr targetHandle,
                bool bluetoothTransport, byte command) {
            if (targetHandle == IntPtr.Zero)
                return false;

            byte[] report = new byte[DualSenseBluetoothControlFeatureReportLen];
            report[0] = DualSenseBluetoothControlFeatureReportId;
            report[1] = command;
            if (bluetoothTransport) {
                uint crc = Crc32(0x53, report, report.Length - 4);
                report[report.Length - 4] = (byte)crc;
                report[report.Length - 3] = (byte)(crc >> 8);
                report[report.Length - 2] = (byte)(crc >> 16);
                report[report.Length - 1] = (byte)(crc >> 24);
            }

            return HIDapi.hid_send_feature_report(targetHandle, report,
                new UIntPtr((uint)report.Length)) == report.Length;
        }

        public override void PrepareLongPressPowerOff() {
            // A USB-side firmware power-off can clear the controller's volatile pairing state just
            // like the Bluetooth-side low-power command. Assert the exact Windows bond first while
            // this live wired handle is still available; PowerOff() is intentionally a no-op for
            // USB because the controller's own long-press timeout performs the actual shutdown.
            if (state > state_.DROPPED && isUSB) {
                bool pairingStateReasserted = ReassertBluetoothPairingStateOverUsb(handle,
                    PadMacAddress.GetAddressBytes());
                DebugLog.Write("DualSense USB power-off preparation: pairingStateReasserted=" +
                    pairingStateReasserted);
            }
            PrepareChargeOnlyUsbWake();
        }

        private void PrepareChargeOnlyUsbWake() {
            monitorChargeOnlyWakeAfterPowerOff = false;
            if (state > state_.DROPPED && !isUSB && PrefersBluetoothTransport()) {
                string profileId = ControllerMappings.ProfileIdFor(this);
                if (String.IsNullOrEmpty(chargeOnlyUsbPath))
                    Program.mgr.TryGetChargeOnlyUsbPath(profileId,
                        out chargeOnlyUsbPath);
                Program.mgr.PreserveChargeOnlyUsbAfterLongPressPowerOff(profileId);
                monitorChargeOnlyWakeAfterPowerOff =
                    !String.IsNullOrEmpty(chargeOnlyUsbPath) &&
                    Program.mgr.ShouldMonitorChargeOnlyUsbWake(
                        chargeOnlyUsbPath, profileId);
            }
        }

        private void BeginChargeOnlyUsbWakeMonitor(bool requirePsPress = false) {
            if (Interlocked.Exchange(ref chargeOnlyWakeMonitorStarted, 1) != 0)
                return;
            if (!Program.mgr.TryBeginChargeOnlyUsbWakeMonitor()) {
                Interlocked.Exchange(ref chargeOnlyWakeMonitorStarted, 0);
                return;
            }

            string devicePath = chargeOnlyUsbPath;
            string profileId = ControllerMappings.ProfileIdFor(this);
            // Seed the battery gradient from what this controller last reported, so a Battery
            // indicator shows the right colour immediately instead of waiting for the parked pad
            // to volunteer a report. The monitor refreshes it from any report that does arrive.
            int seedBatteryPercent = batteryPercent;
            bool queued = ThreadPool.QueueUserWorkItem(_ => {
                try {
                    MonitorChargeOnlyUsbWake(devicePath, profileId, seedBatteryPercent,
                        requirePsPress);
                } finally {
                    Interlocked.Exchange(ref chargeOnlyWakeMonitorStarted, 0);
                    Program.mgr.EndChargeOnlyUsbWakeMonitor();
                }
            });
            if (!queued) {
                Interlocked.Exchange(ref chargeOnlyWakeMonitorStarted, 0);
                Program.mgr.EndChargeOnlyUsbWakeMonitor();
            }
        }

        private static void MonitorChargeOnlyUsbWake(string devicePath, string profileId,
                int seedBatteryPercent, bool requirePsPress) {
            // Opening the USB HID interface during teardown prevents the firmware from reaching
            // its native orange charging state. Let that transition finish first; only then own
            // the input endpoint while waiting for the PS wake edge.
            Thread.Sleep(FirmwarePowerOffWakeSettleMs);
            bool fakeUsbChargeGlow = ControllerMappings.ChargeGlowEnabled(profileId);
            bool glowFollowsBattery = ControllerMappings.ChargeGlowUsesBattery(profileId);
            byte glowRed, glowGreen, glowBlue;
            ControllerMappings.TryParseLightColor(
                ControllerMappings.ChargeGlowColor(profileId),
                out glowRed, out glowGreen, out glowBlue);
            int glowBatteryPercent = seedBatteryPercent;
            if (glowFollowsBattery)
                BatteryGlowColor(glowBatteryPercent,
                    out glowRed, out glowGreen, out glowBlue);
            double glowPeriodMs =
                ControllerMappings.ChargeGlowPeriodSeconds(profileId) * 1000.0;
            DebugLog.Write("ChargeOnlyWake: monitor started, path=" + devicePath +
                " fakeUsbChargeGlow=" + fakeUsbChargeGlow);
            while (Program.mgr.ShouldMonitorChargeOnlyUsbWake(devicePath, profileId)) {
                IntPtr wakeHandle = HIDapi.hid_open_path(devicePath);
                if (wakeHandle == IntPtr.Zero) {
                    Thread.Sleep(250);
                    continue;
                }

                try {
                    // Entering the wake monitor means the controller is parked, so it must stop
                    // showing the profile colour. It does not truly power off here (the 0x08/0x02
                    // is not always accepted), so the firmware never clears the lightbar itself -
                    // without this the user's colour stayed lit for the whole park. Blank it once
                    // as this monitor takes ownership of the handle; when the fake charge glow is
                    // enabled its frames immediately take over from this.
                    WriteUsbChargeGlowOff(wakeHandle);
                    bool sawPsReleased = false;
                    bool everQuiet = false;
                    long quietSince = Stopwatch.GetTimestamp();
                    long nextGlowAt = 0;
                    Stopwatch glowClock = fakeUsbChargeGlow ? Stopwatch.StartNew() : null;
                    byte[] report = new byte[64];
                    while (Program.mgr.ShouldMonitorChargeOnlyUsbWake(
                            devicePath, profileId)) {
                        if (fakeUsbChargeGlow) {
                            long nowTicks = Stopwatch.GetTimestamp();
                            if (nowTicks >= nextGlowAt) {
                                WriteUsbChargeGlowFrame(wakeHandle,
                                    glowClock.Elapsed.TotalMilliseconds, glowPeriodMs,
                                    glowRed, glowGreen, glowBlue);
                                nextGlowAt = nowTicks +
                                    Stopwatch.Frequency * UsbChargeGlowFrameMs / 1000;
                            }
                        }
                        int received = HIDapi.hid_read_timeout(wakeHandle, report,
                            new UIntPtr((uint)report.Length),
                            fakeUsbChargeGlow ? 40 : 100);
                        if (received < 0) {
                            DebugLog.Write("ChargeOnlyWake: read failed (handle invalid), reopening.");
                            break;
                        }

                        // Refresh the gradient from whatever the parked pad does send, so the
                        // colour tracks the charge climbing rather than freezing at the level it
                        // had when it was parked. Same byte ReceiveRaw reads (r[52 + o], o=1 for
                        // this USB-framed report) and the same decoder.
                        if (glowFollowsBattery && received > 53 && report[0] == 0x01) {
                            int reportedPercent;
                            ControllerBatteryStatus reportedStatus;
                            DecodeBatteryStatus(report[53], out reportedPercent,
                                out reportedStatus);
                            if (reportedPercent != glowBatteryPercent) {
                                glowBatteryPercent = reportedPercent;
                                BatteryGlowColor(glowBatteryPercent,
                                    out glowRed, out glowGreen, out glowBlue);
                            }
                        }

                        long now = Stopwatch.GetTimestamp();
                        if (received == 0) {
                            if (!everQuiet &&
                                    (now - quietSince) >= Stopwatch.Frequency * 3L / 4L) {
                                everQuiet = true;
                                DebugLog.Write("ChargeOnlyWake: interface went dormant; " +
                                    "any input now counts as the wake.");
                            }
                            continue;
                        }

                        // Once the interface has gone quiet (low power / charge-only), ANY report
                        // is the user waking it - do not require the normal 0x01 input report or a
                        // specific PS bit, because the low-power/charge report format is not that
                        // report (this is exactly why the old 0x01-only check missed the press).
                        // While input is still continuous (never went quiet), fall back to a
                        // released-to-pressed PS edge on the 0x01 report so ordinary play input
                        // doesn't wake it.
                        // A repaired bond must wait for PS; charge/status reports alone cannot
                        // turn the bond restore into an automatic connection.
                        bool wakeRequested = IsChargeOnlyUsbWakeRequested(report, received,
                            requirePsPress, everQuiet, ref sawPsReleased);
                        quietSince = now;
                        if (wakeRequested) {
                            // Always clear the charge-light lane before handing the controller back
                            // to the normal wake/connect path. Otherwise the last orange/red glow
                            // frame can bleed into the next awake session before profile lighting
                            // or firmware lighting takes ownership again.
                            WriteUsbChargeGlowOff(wakeHandle);
                            bool preferBluetooth =
                                ControllerMappings.UsablePreferredTransport(profileId) ==
                                ControllerMappings.PreferredTransportBluetooth;
                            if (!preferBluetooth) {
                                DebugLog.Write("ChargeOnlyWake: wake detected, releasing USB " +
                                    "pseudo-sleep park (everQuiet=" + everQuiet +
                                    ", reportId=0x" + report[0].ToString("X2") + ")");
                                Program.mgr.ReleaseChargeOnlyUsbPark(devicePath);
                                return;
                            }

                            bool sent = SendBluetoothControlFeatureReport(
                                wakeHandle, false, DualSenseBluetoothControlOn);
                            DebugLog.Write("ChargeOnlyWake: wake detected (everQuiet=" +
                                everQuiet + ", reportId=0x" + report[0].ToString("X2") +
                                "), connect trigger sent=" + sent);
                            if (sent) {
                                Program.mgr.ReleaseChargeOnlyUsbPark(devicePath);
                                return;
                            }
                        }
                    }
                } finally {
                    // The glow is written straight to the lightbar on THIS handle, outside every
                    // normal lighting gate, so it has to be cleared on EVERY exit - not just the
                    // wake branch above. The other ways out (ShouldMonitor going false because the
                    // park was released elsewhere, or a read failure forcing a reopen) otherwise
                    // leave the last red/amber frame lit, and the reconnecting pad cannot repaint
                    // it until IMU_DATA_OK plus the 4.5s connect settle - seen as a red/pink flash
                    // during the connection sequence. Unconditional because this monitor now blanks
                    // the lightbar as it takes the handle, so it owns that lane for the whole park
                    // whether or not the glow is running - and must leave it dark on the way out.
                    WriteUsbChargeGlowOff(wakeHandle);
                    HIDapi.hid_close(wakeHandle);
                }
            }
            DebugLog.Write("ChargeOnlyWake: monitor ended (ShouldMonitor went false).");
        }

        private static bool IsChargeOnlyUsbWakeRequested(byte[] report, int received,
                bool requirePsPress, bool everQuiet, ref bool sawPsReleased) {
            if (received <= 0)
                return false;
            if (!requirePsPress && everQuiet)
                return true;
            if (received != 64 || report == null || report.Length < 64 || report[0] != 0x01)
                return false;
            if ((report[10] & 0x01) == 0) {
                sawPsReleased = true;
                return false;
            }
            return sawPsReleased || (requirePsPress && everQuiet);
        }

        // Red at empty through to green at full, via yellow at the midpoint. A continuous ramp
        // rather than LightingModeBattery's three fixed bands: that one quantises to avoid
        // chattering at the controller every time the charge twitches, which does not apply here
        // because the pulse is already rewriting the lightbar every frame anyway. An unknown
        // level (-1, never reported) falls back to the midpoint instead of showing empty-red.
        private static void BatteryGlowColor(int batteryPercent,
                out byte red, out byte green, out byte blue) {
            double level = batteryPercent < 0
                ? 0.5
                : Math.Max(0.0, Math.Min(100.0, batteryPercent)) / 100.0;
            red = (byte)Math.Round(255.0 * (1.0 - level));
            green = (byte)Math.Round(255.0 * level);
            blue = 0;
        }

        // The profile's colour is the PEAK of the breath - the raised cosine scales it from off up
        // to that colour and back, so a picked colour reads as "the colour it glows", not a floor.
        private static void WriteUsbChargeGlowFrame(IntPtr wakeHandle, double elapsedMs,
                double periodMs, byte peakRed, byte peakGreen, byte peakBlue) {
            if (wakeHandle == IntPtr.Zero)
                return;

            if (periodMs <= 0.0)
                periodMs = UsbChargeGlowPeriodMs;
            double phase = (elapsedMs % periodMs) / periodMs;
            double wave = 0.5 - 0.5 * Math.Cos(phase * Math.PI * 2.0);
            WriteUsbChargeGlowColor(wakeHandle,
                (byte)Math.Round(wave * peakRed),
                (byte)Math.Round(wave * peakGreen),
                (byte)Math.Round(wave * peakBlue));
        }

        private static void WriteUsbChargeGlowOff(IntPtr wakeHandle) {
            WriteUsbChargeGlowColor(wakeHandle, 0, 0, 0);
        }

        private static void WriteUsbChargeGlowColor(IntPtr wakeHandle,
                byte red, byte green, byte blue) {
            byte[] buf = new byte[64];
            buf[0] = 0x02;
            // USB-only charge glow is deliberately outside normal profile lighting. Claim only
            // lightbar RGB validity: no player LEDs, no profile color, no OpenRGB/default mode.
            buf[2] = DualSenseValidLightbarControl;
            buf[45] = red;
            buf[46] = green;
            buf[47] = blue;
            HIDapi.hid_write(wakeHandle, buf, new UIntPtr((uint)buf.Length));
        }

        public override void SetLightColor(byte red, byte green, byte blue) {
            SetTrackedLightColor(red, green, blue, false);
        }

        public override void SetOpenRgbLightColor(byte red, byte green, byte blue) {
            SetTrackedLightColor(red, green, blue, true);
        }

        private void SetTrackedLightColor(byte red, byte green, byte blue,
                                          bool fromOpenRgbServer) {
            lock (outputReportLock) {
                // OpenRGB is allowed to bypass the ordinary hands-off gate only while this exact
                // profile is still assigned to OpenRGB. A queued SDK animation frame can race a
                // live profile switch back to Default; accepting it there would let the Bluetooth
                // audio carrier reclaim the lightbar on its next 0x36 report, undoing the strict
                // Default-mode guarantee from 69b94e9.
                if (fromOpenRgbServer && !LightingModeIsOpenRgb())
                    return;

                // Profile reconciliation reapplies controller options on every scan pass. Once
                // this exact color has reached an initialized transport, another 0x31 report is
                // redundant and needlessly consumes Bluetooth output bandwidth. Preserve the
                // pending path during attach so the first real color write is never suppressed.
                if (lightbarTransportKnown && !lightbarUpdatePending &&
                    lightbarRed == red && lightbarGreen == green &&
                    lightbarBlue == blue)
                    return;

                lightbarRed = red;
                lightbarGreen = green;
                lightbarBlue = blue;
                lightbarUpdatePending = true;
                // This flag describes the source of the currently pending color, not whether an
                // OpenRGB color has ever been queued. A later managed-profile update must revoke a
                // stale OpenRGB bypass before any standalone lighting report is built.
                openRgbLightbarUpdatePending = fromOpenRgbServer;
                if (lightbarTransportKnown && !LightingSuppressedForUsbHandoff() &&
                        LightbarConnectReady()) {
                    // Lighting remains a standalone controller-output request even while the
                    // Bluetooth media lane is active. Audio carriers never need RGB state
                    // interleaved into them; the controller retains the last LED command itself.
                    SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue,
                        fromOpenRgbServer);
                    lightbarUpdatePending = false;
                    openRgbLightbarUpdatePending = false;
                    MarkLightbarConnectApplied();
                }
            }
        }

        public override (byte Red, byte Green, byte Blue) GetLightColor() {
            return (lightbarRed, lightbarGreen, lightbarBlue);
        }

        // Sony's own player-LED convention (the player_ids table in the Linux kernel's
        // hid-playstation.c dualsense_set_player_leds) - one of 5 patterns for the 5 small LEDs
        // below the touchpad, growing from a single center LED for player 1 up to all five lit
        // for player 5+. Same role as NintendoController.SetLEDByPlayerNum (called from the same
        // RequestLEDUpdate plumbing whenever PadId changes), just Sony's own bit layout instead
        // of Joy-Con's.
        private static readonly byte[] PlayerLedPatterns = {
            0x04, // player 1: center LED only               - BIT(2)
            0x0A, // player 2: two LEDs either side of center - BIT(3)|BIT(1)
            0x15, // player 3: three LEDs                     - BIT(4)|BIT(2)|BIT(0)
            0x1B, // player 4: four LEDs                      - BIT(4)|BIT(3)|BIT(1)|BIT(0)
            0x1F, // player 5+: all five LEDs
        };

        public override void SetLEDByPlayerNum(int id) {
            // false: before this dropdown existed, DualSense's player LEDs were always silently
            // off (see PlayerLedEnabled's own comment) - unlike Joy-Con/Pro, an unset profile
            // should not suddenly start lighting them.
            byte desired = ControllerMappings.PlayerLedEnabled(
                    ControllerMappings.ProfileIdFor(this), false)
                ? PlayerLedPatterns[Math.Max(0, Math.Min(PlayerLedPatterns.Length - 1, id))]
                : (byte)0;

            lock (outputReportLock) {
                if (currentPlayerLeds == desired)
                    return;

                currentPlayerLeds = desired;
                // Reuses the exact same "not yet known, retry once ReceiveRaw confirms transport"
                // path SetLightColor already relies on - SendDualSenseLightbar publishes both the
                // lightbar color and currentPlayerLeds together in one report either way.
                // LightbarConnectReady is required here for the same reason SetTrackedLightColor
                // requires it: this publishes a full lightbar report, so without it a queued
                // player-LED update wrote to the controller as soon as state hit IMU_DATA_OK -
                // during the connect sequence, before the settle window had elapsed. That was the
                // one lighting path still leaking output into power-on. Defer instead; the pending
                // flag is flushed by ReceiveRaw once the pad is genuinely settled.
                if (lightbarTransportKnown && !LightingSuppressedForUsbHandoff() &&
                        LightbarConnectReady())
                    SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
                else
                    lightbarUpdatePending = true;
            }
        }

        private bool PrefersBluetoothTransport() {
            return ControllerMappings.UsablePreferredTransport(
                    ControllerMappings.ProfileIdFor(this)) ==
                ControllerMappings.PreferredTransportBluetooth;
        }

        // During automatic Bluetooth pairing/repair, the wired HID interface is only the
        // controller's charge/configuration lane. Do not send lightbar or player-LED output while
        // that USB object is being handed off; the firmware's startup lighting must remain in charge
        // until the Bluetooth HID transport is fully established.
        private bool LightingSuppressedForUsbHandoff() {
            if (!isUSB)
                return false;
            string profileId = ControllerMappings.ProfileIdFor(this);
            return ControllerMappings.UsablePreferredTransport(profileId) ==
                    ControllerMappings.PreferredTransportBluetooth &&
                ControllerMappings.AutomaticBluetoothPairingEnabled(profileId);
        }

        // Whichever transport proves itself alive second normally wins shared MAC deduplication.
        // For DualSense the profile chooses instead: Bluetooth keeps the wireless HID/output/audio
        // lanes and quarantines the matching USB HID path as charge-only; USB preserves the prior
        // behavior and tears down the radio connection so it cannot churn through rediscovery.
        protected override bool CanResolveDuplicate(Controller other) {
            DualSenseController dualSense = other as DualSenseController;
            return dualSense == null ||
                (!automaticBluetoothPairingInProgress &&
                    !dualSense.automaticBluetoothPairingInProgress &&
                    !freshBluetoothPairingPending &&
                    !dualSense.freshBluetoothPairingPending &&
                    dualSense.lightbarTransportKnown);
        }

        protected override bool PreferExistingDuplicate(Controller other) {
            if (!(other is DualSenseController) || isUSB == other.isUSB)
                return false;

            return PrefersBluetoothTransport()
                ? isUSB && !other.isUSB
                : !isUSB && other.isUSB;
        }

        protected override void OnRetiredAsDuplicate(Controller other) {
            DualSenseController dualSense = other as DualSenseController;
            if (dualSense == null)
                return;

            if (isUSB && !other.isUSB) {
                dualSense.chargeOnlyUsbPath = path;
                Program.mgr.SuppressUsbControllerForBluetoothPreference(
                    path, ControllerMappings.ProfileIdFor(this));
            } else if (!isUSB && other.isUSB) {
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
            }
        }

        protected override void OnDuplicateRetired(Controller other) {
            if (!(other is DualSenseController))
                return;

            if (!isUSB && other.isUSB) {
                chargeOnlyUsbPath = other.path;
                Program.mgr.SuppressUsbControllerForBluetoothPreference(
                    other.path, ControllerMappings.ProfileIdFor(this));
            } else if (isUSB && !other.isUSB) {
                // The profile lightbar color is applied on the first transport-confirmed read,
                // covering this handoff as well as a fresh USB or Bluetooth connection.
                bool disconnected = BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                form.AppendTextBox(disconnected
                    ? "Disconnected DualSense's Bluetooth link now that USB has taken over.\r\n"
                    : "Could not disconnect DualSense's Bluetooth link - it may keep reappearing.\r\n");
            }
        }

        // TEMPORARY diagnostic: user reports a DualSense still acting on click/gyro-mouse binds
        // after disabling them in the profile UI - log the actual resolved profile ID and value
        // (per key, own throttle each) so this can be confirmed against controller_mappings.xml
        // directly instead of guessed at.
        private readonly System.Collections.Generic.Dictionary<string, long> lastMappingValueDumpTimestamp =
            new System.Collections.Generic.Dictionary<string, long>();

        protected override void OnMappingValueResolved(string key, string value) {
            if (key == "left_click" || key == "right_click" || key == "active_gyro_mouse") {
                long nowTicks = Stopwatch.GetTimestamp();
                long last;
                if (!lastMappingValueDumpTimestamp.TryGetValue(key, out last) ||
                    (nowTicks - last) / (double)Stopwatch.Frequency >= 1.0) {
                    lastMappingValueDumpTimestamp[key] = nowTicks;
                    LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                        "MappingValue: profileId={0} key={1} value={2}", mappingProfileId, key, value));
                }
            }
        }

        private static readonly ConcurrentQueue<string> dualSenseRawDumpQueue = new ConcurrentQueue<string>();
        private static int dualSenseRawDumpWriterStarted;

        // Same async queue + background-writer pattern as autocal_debug.log, so this can't block a
        // controller's own Poll thread on file I/O. Gated behind DualSenseDebugLogging (default
        // off) - this writes continuously while a DualSense is connected, so it shouldn't run
        // unconditionally for every user, only when actually troubleshooting something.
        internal void LogDualSenseRawDump(string message) {
            if (!Boolean.Parse(ConfigurationManager.AppSettings["DualSenseDebugLogging"]))
                return;

            if (Interlocked.CompareExchange(ref dualSenseRawDumpWriterStarted, 1, 0) == 0) {
                new Thread(DualSenseRawDumpWriterLoop) {
                    IsBackground = true,
                    Name = "DualSenseRawDumpWriter"
                }.Start();
            }
            dualSenseRawDumpQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
        }

        private static void DualSenseRawDumpWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "dualsense_raw_debug.log");
            while (true) {
                Thread.Sleep(250);
                if (dualSenseRawDumpQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (dualSenseRawDumpQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // Diagnostic only: never let an unavailable log path affect controller I/O.
                }
            }
        }

        protected override int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;

            byte[] dsBuf = new byte[DualSenseMaxReportLen];
            int dsRet = HIDapi.hid_read_timeout(handle, dsBuf, new UIntPtr((uint)DualSenseMaxReportLen), 5);

            // Process only real DualSense input reports (64 bytes wired, 78 bytes Bluetooth). Do
            // NOT re-derive isUSB from this length: Windows pads reads up to the buffer size, so a
            // wired read can arrive as 78 and be misread as Bluetooth. isUSB is set once from the
            // authoritative PnP-bus transport in the constructor and stays fixed for this handle's
            // single interface (one interface = one transport; a real transport switch is a new
            // device path and therefore a new controller object). connection tracks it in lockstep.
            if (dsRet == 64 || dsRet == 78) {
                connection = isUSB ? 0x01 : 0x02;

                // Mic-duplex frames are media, not controller state. They intentionally use the
                // same 0x31 report ID and length as Bluetooth gamepad input, so they must be
                // separated before any stick/button/IMU parsing. Byte 1 bit 1 identifies the mic
                // lane; byte 2 is its sequence and bytes 3..73 are one fixed Opus frame.
                if (!isUSB && IsBluetoothMicrophoneFrame(dsBuf)) {
                    EnqueueBluetoothMicrophoneFrame(dsBuf);
                    return dsRet;
                }

                lightbarTransportKnown = true;
                if (adaptiveTriggerUpdatePending) {
                    lock (outputReportLock) {
                        if (adaptiveTriggerUpdatePending && !bluetoothAudioStreaming &&
                            !bluetoothMicrophoneStreaming &&
                            !bluetoothMicrophoneDisablePending &&
                            !bluetoothMicrophoneControlPending) {
                            SendAdaptiveTriggerStateLocked();
                            adaptiveTriggerUpdatePending = false;
                        }
                    }
                }
                // Lighting Mode: Default means never touch the LED, not even to confirm a fresh
                // connection - relying on SendDualSenseLightbar's own internal bit-masking to make
                // this call a no-op isn't good enough (today's BT-audio investigation showed that
                // kind of masking can silently fail to gate what it's assumed to), so just never
                // issue it here at all while Default is active. lightbarUpdatePending is
                // deliberately left set (not cleared) so switching the profile away from Default
                // later, even without a reconnect, still applies its assigned color once.
                // Discard an OpenRGB frame that was queued immediately before a profile switch.
                // In particular, never let it fall through as a normal profile color in Default.
                if (openRgbLightbarUpdatePending && !LightingModeIsOpenRgb()) {
                    openRgbLightbarUpdatePending = false;
                    lightbarUpdatePending = false;
                }
                bool lightingSuppressedForUsbHandoff = LightingSuppressedForUsbHandoff();
                // The DualSense firmware runs its own lighting routine as it connects, including a
                // blue flash. Do not send our own lighting until the pad is fully connected,
                // streaming input (state IMU_DATA_OK, set only after a good HID report), and past a
                // short settle window; then apply the profile color directly.
                if (LightbarConnectReady()) {
                    if (lightbarUpdatePending && openRgbLightbarUpdatePending &&
                            !lightingSuppressedForUsbHandoff) {
                        SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue, true);
                        lightbarUpdatePending = false;
                        openRgbLightbarUpdatePending = false;
                        MarkLightbarConnectApplied();
                    } else if (lightbarUpdatePending && !lightingSuppressedForUsbHandoff &&
                            !LightingModeIsHandsOff()) {
                        SendDualSenseLightbar(lightbarRed, lightbarGreen, lightbarBlue);
                        lightbarUpdatePending = false;
                        MarkLightbarConnectApplied();
                    }
                }
                // hid_read_timeout does NOT strip the leading report-ID byte for either transport -
                // byte 0 is a constant 0x01 (USB) or 0x31 (BT) report ID. USB has no further
                // padding, so real data starts at byte 1. BT has one more padding/tag byte after
                // the report ID before real data starts at byte 2. Confirmed two independent ways:
                // (1) decoding a real idle BT capture at offset 2 gives sane values (sticks
                // dead-center, triggers at 0, button byte reading the DualSense's documented
                // dpad-neutral encoding 0x08) while offset 1 does not; (2) DS4Windows's own
                // DualSenseDevice.cs (a shipped Windows implementation) uses reportOffset = BT ? 1
                // : 0 relative to a buffer that, like ours, still includes the report-ID byte -
                // i.e. absolute offset 2 (BT) / 1 (USB), matching (1).
                int reportOffset = isUSB ? 1 : 2;

                // TEMPORARY diagnostic: the offsets guessed from a secondhand reference are
                // demonstrably wrong (confirmed on real hardware - trigger/button bytes don't line
                // up), so dump real bytes to a file instead of guessing a third time - the
                // on-screen console has not been a reliable way to actually see this. Throttled to
                // ~4/sec so it's readable while still catching real changes as controls are pressed
                // one at a time. Remove once ParseDualSenseReport's offsets are confirmed correct
                // against real data.
                long nowTicks = Stopwatch.GetTimestamp();
                if ((nowTicks - lastDualSenseRawDumpTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                    lastDualSenseRawDumpTimestamp = nowTicks;
                    var hex = new StringBuilder();
                    for (int i = 0; i < dsRet; i++)
                        hex.Append(dsBuf[i].ToString("X2")).Append(' ');
                    LogDualSenseRawDump("DS raw[" + dsRet + "]: " + hex.ToString());
                }

                ParseDualSenseReport(dsBuf, reportOffset);
                // BeginGyroStickDiagnosticReport/AccumulateGyroStickDiagnosticSample bracket every
                // ExtractIMUValues call so gyro_stick_debug.csv actually gets rows for DualSense -
                // previously only NintendoController.ReceiveRaw wired these up, so this csv was
                // silently empty for every DualSense session, gyro-stick issues included.
                BeginGyroStickDiagnosticReport();
                ExtractIMUValues(dsBuf, reportOffset);
                AccumulateGyroStickDiagnosticSample();
                DoThingsWithButtons();

                // The actual acc_g/gyr_g -> mouse/stick conversion - ExtractIMUValues only
                // computes calibrated sensor values and feeds the AHRS filter, it doesn't itself
                // produce any output (see NintendoController.ReceiveRaw's identical call shape).
                // flush=true unconditionally: DualSense's report carries one IMU sample, not
                // Joy-Con's three sub-samples per report, so there's no partial-accumulation case
                // to gate on.
                ProcessGyroMouseSample(true);
                ProcessGyroStickSample(true);
                // r[6+o] is DualSense's free-running sequence/status counter - the nearest
                // equivalent to Joy-Con's per-report device timer byte NintendoController passes
                // here (see ParseDualSenseReport's comment on that same byte).
                RecordGyroStickDiagnosticReport(dsBuf[6 + reportOffset], Stopwatch.GetTimestamp());

                if (out_xbox != null) {
                    try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
                }
                // Previously never fed for a physical DualSense/DualShock4 (see DualShock4.cs's
                // identical block) - the "DualShock 4 controller"/new DualSense output options
                // silently did nothing for a PlayStation-family physical controller until now.
                if (out_ds4 != null || out_dualsense != null) {
                    var ds4State = MapToDualShock4Input(this);
                    if (out_ds4 != null) {
                        try { out_ds4.UpdateInput(ds4State); } catch (Exception) { }
                    }
                    if (out_dualsense != null) {
                        try { out_dualsense.UpdateInput(ds4State); } catch (Exception) { }
                    }
                }
                return dsRet;
            }

            // An unexpected length means the report stream is no longer what this parser expects -
            // possibly a transient glitch, but also possibly a connection that's genuinely gone bad
            // (confirmed on real hardware: report framing can shift after something puts the
            // controller in a bad state). Treating this as harmless previously meant such a
            // connection could never reach DROPPED and would sit in joy.cpl as a stale, frozen
            // "connected" entry forever - count it as a real error instead so a truly broken
            // connection gets cleaned up like any other.
            if (dsRet > 0)
                return -1;
            return dsRet; // 0 = timeout, <0 = read error - Poll()'s state machine already handles both
        }

        // Called from Controller.Poll()'s shared shell whenever rumble_obj's queue has data.
        // DualSense's simple dual-motor rumble has no equivalent to the low/high-frequency split
        // Joy-Con's HD-rumble Rumble.GetData() encodes - just take the queued amplitude directly
        // and drive both motors the same. Was disabled after real hardware went into continuous,
        // non-stopping rumble the first time this ran - root cause found: outputReport[2] (USB) /
        // [3] (BT) is a required feature-flags byte (0x55: mic LED, audio mute, touchpad strips,
        // player lights, motor power) that was left at 0x00 by omission, not an intentional "leave
        // alone" zero. Re-enabled with that byte now set.
        protected override void SendQueuedRumbleIfAny() {
            if (rumble_obj.queue.Count > 0) {
                float amp = rumble_obj.queue.Dequeue()[2];
                byte motor = (byte)(Math.Max(0f, Math.Min(1f, amp)) * 255f);
                SendDualSenseRumble(motor, motor);
            }
        }

        // DualSense baseline report parsing - buttons/sticks/triggers only (no gyro/touchpad/
        // adaptive-trigger reads yet). Offsets and layout from the standard DualSense USB/BT HID
        // report; o is 1 on Bluetooth (a leading byte USB doesn't have), 0 on USB. Populates the
        // exact same buttons[]/stick[]/stick2[]/triggerVal[] fields Joy-Con parsing does, so every
        // downstream consumer (MapToXbox360Input, profiles, UI) needs no DualSense-specific code
        // beyond the analog-trigger branch in MapToXbox360Input.
        private void ParseDualSenseReport(byte[] r, int o) {
            // Offsets below are from a direct hardware capture (raw hex dump, dualsense_raw_debug.log),
            // not a secondhand reference - both references checked (DS4Windows, a community wire-
            // format doc) agreed with each other on field order but disagreed with real hardware,
            // not just by a constant byte shift: the actual order is sticks, buttons1, buttons2, a
            // free-running sequence counter, THEN L2/R2 analog - references had triggers before the
            // counter and buttons after. Confirmed from real data: byte 4 reads a constant 0x08 at
            // rest (dpad nibble 8 = neutral, matching the real PS dpad convention, face-button
            // nibble 0 = nothing pressed); byte 5 toggles exactly 0x04/0x08 in sync with L2/R2's
            // digital end-of-travel click; byte 6 free-runs 0x00-0x3C regardless of input (the
            // counter); bytes 7/8 ramp with L2/R2 squeeze depth precisely when byte 5's matching
            // click bit is set. o is the genuine Bluetooth-vs-USB protocol byte (1/0).
            //
            // Raw 0-255, center ~128, run through the same CenterSticks/CalibrationState pipeline
            // Joy-Con uses (stick_cal/stick2_cal seeded with an identity default in Attach() since
            // there's no SPI factory data to read) - a DualSense can now be recalibrated with the
            // existing double-click wizard exactly like a Pro controller's sticks, now including a
            // gyro step too (see HeadlessJoyconHost.StartCalibration and ExtractIMUValues below).
            // AddStickSample is a no-op unless this controller is the one currently claimed by
            // that wizard. Y is inverted
            // after CenterSticks (not before, unlike the old fixed linear map) since CenterSticks'
            // raw subtraction/division doesn't know about BetterJoy's own "up is positive" stick
            // convention - only the sign needs flipping, not the calibration math.
            UInt16[] stickRaw = { r[0 + o], r[1 + o] };
            // Gyro auto-calibration's stick-center pass samples stick_precal/stick2_precal, not the
            // locals below (see GyroMath.PublishAutoCalStickCenter). Those were only ever written by
            // NintendoController, so on a DualSense they stayed at their {0,0} initializer forever -
            // auto-cal then published a center of (0,0) over the 128,128 seeded in Attach(), and
            // CenterSticks read every resting sample as a large positive deflection: both sticks
            // pinned down and to the right, persisted to disk. Populate them here so the existing
            // pass sees real values.
            stick_precal[0] = stickRaw[0];
            stick_precal[1] = stickRaw[1];
            CalibrationState.AddStickSample(this, false, stickRaw[0], stickRaw[1]);
            float[] stickResult = CenterSticks(stickRaw, stick_cal, deadzone,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]));
            stick[0] = stickResult[0];
            stick[1] = -stickResult[1];

            UInt16[] stick2Raw = { r[2 + o], r[3 + o] };
            stick2_precal[0] = stick2Raw[0];
            stick2_precal[1] = stick2Raw[1];
            CalibrationState.AddStickSample(this, true, stick2Raw[0], stick2Raw[1]);
            float[] stick2Result = CenterSticks(stick2Raw, stick2_cal, deadzone2,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]));
            stick2[0] = stick2Result[0];
            stick2[1] = -stick2Result[1];

            // USB and BT reports use the identical field order once o has skipped each transport's
            // own report-ID(+padding) prefix (see the o assignment in ReceiveRaw) - no further
            // per-transport swap needed here. Order after the sticks: L2, R2, a free-running
            // sequence/status counter (field index 6, skipped), then the two button bytes.
            // Cross-checked against DS4Windows's DualSenseDevice.cs (inputReport[5/6+ro] for
            // triggers, [8/9+ro] for the button bytes) and against a real idle BT capture, which
            // only decodes to sane values (dead-center sticks, zeroed triggers, neutral dpad) at
            // these positions.
            int triggerFieldBase = 4;
            int buttonFieldBase = 7;

            triggerVal[0] = r[triggerFieldBase + o];
            triggerVal[1] = r[triggerFieldBase + 1 + o];

            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i)
                        down_[i] = buttons[i];
                }
                bool[] b = new bool[ButtonCount];

                byte btn1 = r[buttonFieldBase + o];
                b[(int)Button.X] = (btn1 & 0x80) != 0; // Triangle
                b[(int)Button.A] = (btn1 & 0x40) != 0; // Circle
                b[(int)Button.B] = (btn1 & 0x20) != 0; // Cross
                b[(int)Button.Y] = (btn1 & 0x10) != 0; // Square

                int dpad = btn1 & 0x0F;
                b[(int)Button.DPAD_UP] = dpad == 0 || dpad == 1 || dpad == 7;
                b[(int)Button.DPAD_RIGHT] = dpad == 1 || dpad == 2 || dpad == 3;
                b[(int)Button.DPAD_DOWN] = dpad == 3 || dpad == 4 || dpad == 5;
                b[(int)Button.DPAD_LEFT] = dpad == 5 || dpad == 6 || dpad == 7;

                byte btn2 = r[buttonFieldBase + 1 + o];
                b[(int)Button.STICK2] = (btn2 & 0x80) != 0;      // R3
                b[(int)Button.STICK] = (btn2 & 0x40) != 0;       // L3
                b[(int)Button.PLUS] = (btn2 & 0x20) != 0;        // Options
                b[(int)Button.MINUS] = (btn2 & 0x10) != 0;       // Share
                b[(int)Button.SHOULDER2_2] = (btn2 & 0x08) != 0; // R2 (digital click)
                b[(int)Button.SHOULDER_2] = (btn2 & 0x04) != 0;  // L2 (digital click)
                b[(int)Button.SHOULDER2_1] = (btn2 & 0x02) != 0; // R1
                b[(int)Button.SHOULDER_1] = (btn2 & 0x01) != 0;  // L1

                // byte 6 is the sequence counter (skipped). PS button confirmed via DS4Windows's
                // DualSenseDevice.cs (inputReport[10+ro], bit 0).
                byte btn3 = r[9 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                b[(int)Button.TOUCHPAD] = (btn3 & 0x02) != 0;
                b[(int)Button.MIC_MUTE] = (btn3 & 0x04) != 0;
                // The actual mute toggle used to live here as a hardcoded check against this one
                // physical button - now a real binding (toggle_built_in_mic, defaulting to this
                // same button), dispatched from DoDeviceSpecificButtonActions once buttons is
                // committed below and combo-matching against it is valid. Still populating the
                // raw state here regardless, since IsComboHeld needs it live either way.

                // DualSense Edge function buttons, from a direct Bluetooth capture on real
                // hardware (dualsense_raw_debug.log, 2026-09-09) rather than any secondhand
                // reference - same reason the offsets above were captured instead of looked up.
                // Protocol: six alternating presses FN1/FN2/FN1/FN2/FN1/FN2 produced exactly
                // 0x10/0x20/0x10/0x20/0x10 in this byte (the sixth fell between samples - the raw
                // dump is throttled to 4/sec, the controller reports every press). Both bits also
                // appeared together as 0x30, so they are independent momentary buttons, not a
                // shared encoding. L2/R3/L3 were untouched for the whole capture (button byte 2
                // stayed 0x00 across all 72 samples), which rules out a stick-click artifact.
                // A plain DualSense never sets either bit, so this costs it nothing.
                //
                // Bluetooth only so far - this is absolute index 11 with o=2. The USB 0x01 report
                // puts the same field at absolute index 10 (o=1) and has NOT been captured; the
                // bit meanings are assumed to carry over but are unverified there, exactly the
                // assumption that broke the DualSense Bluetooth audio work. Capture USB before
                // trusting FN over the cable.
                b[(int)Button.FN1] = (btn3 & 0x10) != 0;
                b[(int)Button.FN2] = (btn3 & 0x20) != 0;
                // Edge paddles remain unmapped - btn3 bits 0x08/0x40/0x80 stayed clear throughout
                // the capture, but the paddles were never pressed, so that is untested rather
                // than evidence they go unreported. SL/SR have no DualSense equivalent.

                buttons = b;
                CommitButtonState();
            }

            // DualSense contact status bytes are common-report offsets 32 and 36 (absolute
            // Bluetooth offsets 34 and 38). Live touch movement changes the first packed contact
            // at absolute 34 while the second remains the inactive 0x80 record at absolute 38.
            // Everything after these device-specific offsets is shared with the DS4 path.
            SubmitTouchpadReport(ReadPackedTouchContact(r, 32 + o),
                                 ReadPackedTouchContact(r, 36 + o));

            // DualSense packs both capacity and charge state into status[0]: the low nibble is a
            // 10-percent capacity bucket and the high nibble distinguishes discharging, charging,
            // full, thermal/voltage lockout, and charge errors. status[1] is jack/mic detection;
            // using its 0x08 bit as the charge flag made wired controllers appear to discharge.
            byte batteryByte = r[52 + o];
            byte powerStateByte = r[53 + o];
            int nextHeadphoneState = (powerStateByte & 0x03) != 0 ? 1 : 0;
            int previousHeadphoneState = Interlocked.Exchange(
                ref headphoneConnectionState, nextHeadphoneState);
            if (previousHeadphoneState != nextHeadphoneState) {
                // Profile reconciliation owns the existing "Route Bluetooth audio to headphones"
                // policy. Keep capture/pipe work off this HID poll thread; when enabled, insertion
                // starts the 0x96 headset lane and removal stops it immediately.
                ThreadPool.QueueUserWorkItem(_ => Program.mgr?.ApplyControllerProfileOptions());
            }
            int batteryPercent;
            ControllerBatteryStatus batteryState;
            DecodeBatteryStatus(batteryByte, out batteryPercent, out batteryState);
            SetBatteryStatus(batteryPercent, batteryState);
        }

        internal static void DecodeBatteryStatus(byte batteryValue, out int percent,
                                                 out ControllerBatteryStatus status) {
            int capacityBucket = batteryValue & 0x0F;
            int chargingState = (batteryValue >> 4) & 0x0F;

            // Sony reports 0 as 0-9%, 1 as 10-19%, and so on. Use each bucket's midpoint just as
            // the Linux hid-playstation driver and dualsensectl do, except that full has an exact
            // state of its own. A lockout/error state must remain visible instead of masquerading
            // as an ordinary discharge.
            percent = Math.Min(capacityBucket * 10 + 5, 100);
            switch (chargingState) {
                case 0x0:
                    status = ControllerBatteryStatus.Discharging;
                    break;
                case 0x1:
                    status = ControllerBatteryStatus.Charging;
                    break;
                case 0x2:
                    percent = 100;
                    status = ControllerBatteryStatus.Full;
                    break;
                case 0xA: // voltage or temperature outside the charging range
                case 0xB: // temperature error
                    status = ControllerBatteryStatus.NotCharging;
                    break;
                default:  // includes 0xF, the controller's charging-error state
                    status = ControllerBatteryStatus.Unknown;
                    break;
            }
        }

        // Gyro/accel byte offsets, cross-checked against three independent reference
        // implementations (DS4Windows, nondebug/dualsense, JoyShockLibrary) - all three agree
        // exactly. Wire order is gyroPitch, gyroYaw, gyroRoll (raw sensor channels 0/1/2, DS4
        // Windows's own field names - not a claim about which is physically pitch/yaw/roll on
        // the real controller), then accelX/Y/Z, then a 4-byte hardware timestamp used to measure
        // real per-report elapsed time (see ReadTimestampTicks - despite the name/reference
        // sources calling it a microsecond counter, a real hardware capture shows it increments at
        // ~3 ticks/us, not 1; ReadTimestampTicks's caller compensates). One IMU sample per report
        // (unlike Joy-Con's three sub-samples per report), so this runs once per ReceiveRaw call,
        // not in a sub-sample loop.
        private void ExtractIMUValues(byte[] r, int o) {
            EnsureGyroOrientationBasis();

            uint hardwareTimestampTicks = ReadTimestampTicks(r, 27 + o);
            if (lastImuHardwareTimestampTicks.HasValue) {
                // Unsigned subtraction wraps correctly modulo 2^32 across the counter's rollover,
                // as long as the true elapsed time between consecutive reports is well under half
                // that range - always true for a per-report delta.
                uint deltaTicks = unchecked(hardwareTimestampTicks -
                                            lastImuHardwareTimestampTicks.Value);
                // Decoded directly from real raw hex dumps: consecutive samples known to be 250ms
                // apart (a throttled debug log's own wall-clock interval) showed this counter
                // advancing by ~750,000 ticks, not ~250,000 - a consistent ~3.0x ratio across five
                // separate sample pairs (750,418 average / 250,000 = 3.0017). The field name/
                // reference sources call it a microsecond counter, but on real hardware it's
                // ticking at ~3MHz, not 1MHz. Dividing by 1,000,000 (treating it as literal
                // microseconds) was silently feeding gravity integration a dt ~3x too large on
                // every single report - confirmed as the actual cause of a real corkscrew/spiral
                // cursor path during sustained wrist roll, even at full gyro trust.
                float measuredDt = deltaTicks / 3000000.0f;
                measuredGyroSubSamplePeriod = Math.Max(MinGyroSubSamplePeriod,
                    Math.Min(MaxGyroSubSamplePeriod, measuredDt));
                lastLoggedImuDeltaTicks = deltaTicks;
            }
            lastImuHardwareTimestampTicks = hardwareTimestampTicks;

            gyr_r[0] = ReadCalibrationInt16(r, 15 + o); // gyroPitch (raw channel 0)
            gyr_r[1] = ReadCalibrationInt16(r, 17 + o); // gyroYaw (raw channel 1)
            gyr_r[2] = ReadCalibrationInt16(r, 19 + o); // gyroRoll (raw channel 2)
            acc_r[0] = ReadCalibrationInt16(r, 21 + o);
            acc_r[1] = ReadCalibrationInt16(r, 23 + o);
            acc_r[2] = ReadCalibrationInt16(r, 25 + o);

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                // Mirrors NintendoController.ExtractIMUValues's live-calibration branch exactly -
                // the manual calibration wizard's gyro step (HeadlessJoyconHost.StartCalibration,
                // now reachable for DualSense since HasGyro is true) and auto-calibration both
                // depend on samples being collected here; CalibrationState later publishes them
                // into activeData (already refreshed generically for every Controller, including
                // this one - see Program.cs's post-connect getActiveData() call). Same shared
                // mechanism Joy-Con already uses, not a DualSense-specific one - only the nominal
                // scale below (16/8192, not Joy-Con's 18642/816 and 16384/4) is DualSense-specific.
                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[0], gyr_r[0]);
                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[1], gyr_r[1]);
                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[2], gyr_r[2]);
            }

            float gyroPitchDegPerSec, gyroYawDegPerSec, gyroRollDegPerSec;

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]) && activeData != null) {
                // activeData[0-2] = gyro offsets, per-axis in wire-channel order - same indexing
                // NintendoController.ExtractIMUValues uses, just against DualSense's own fixed
                // nominal scale instead of Joy-Con's gyr_sen. Gyro's "zero" is orientation-
                // independent (angular velocity genuinely is ~0 at rest regardless of how the
                // controller is held), so re-deriving it from wherever the wizard's "hold still"
                // step happened to run is valid - unlike accel below.
                gyroPitchDegPerSec = (gyr_r[0] - activeData[0]) / GyroLsbPerDegPerSec;
                gyroYawDegPerSec = (gyr_r[1] - activeData[1]) / GyroLsbPerDegPerSec;
                gyroRollDegPerSec = (gyr_r[2] - activeData[2]) / GyroLsbPerDegPerSec;
            } else {
                gyroPitchDegPerSec = CorrectGyroSample(gyr_r[0], gyroPitchBias, gyroPitchPlus, gyroPitchMinus);
                gyroYawDegPerSec = CorrectGyroSample(gyr_r[1], gyroYawBias, gyroYawPlus, gyroYawMinus);
                gyroRollDegPerSec = CorrectGyroSample(gyr_r[2], gyroRollBias, gyroRollPlus, gyroRollMinus);
            }

            // Accelerometer deliberately NEVER uses activeData, unlike gyro above - the wizard's
            // "hold still" step zero-references whatever raw value it captured at THAT pose,
            // which is correct for gyro (rate is genuinely ~0 at rest in any orientation) but
            // wrong for accel (gravity is NOT ~0 in any orientation - zero-referencing it there
            // wipes out the real gravity vector UpdateCanonicalGyroMouseImu/Player Space need to
            // determine "which way is down"). Confirmed on real hardware: with activeData driving
            // accel, resting |acc_g| read ~0 instead of ~1g, and yaw was misprojected into a
            // diagonal/vertical blend instead of horizontal cursor movement as a direct result.
            // Always use the factory-calibrated (or nominal-fallback) formula instead.
            float accelXG = CorrectAccelSample(acc_r[0], accelXPlus, accelXMinus);
            float accelYG = CorrectAccelSample(acc_r[1], accelYPlus, accelYMinus);
            float accelZG = CorrectAccelSample(acc_r[2], accelZPlus, accelZMinus);

            // Keep all three DualSense gyro channels in the same handed sensor frame as its
            // accelerometer before UpdateCanonicalGyroMouseImu applies the shared proper rotation.
            // A long, flat yaw capture makes this invariant directly observable: the physical
            // rotation axis is gravity, so the calibrated gyro vector must be collinear with the
            // accelerometer vector. Pitch and yaw already were; negating only roll made that one
            // component point the opposite way, creating a fake ~10%-of-yaw roll rate. Player
            // Space integrated it into an alternating tilt even while raw accelerometer roll
            // stayed fixed. The device's factory-corrected roll sign is therefore retained here.
            gyr_g.X = gyroRollDegPerSec;
            gyr_g.Y = gyroYawDegPerSec;
            // Sign confirmed on real hardware (was -gyroPitchDegPerSec, produced inverted
            // up/down - tilting up moved the cursor down and vice versa).
            gyr_g.Z = gyroPitchDegPerSec;
            // acc_g's channel order must match gyr_g's above, index-for-index - AHRS.Update below
            // fuses gyro integration with gravity-based correction, and if the two sensors don't
            // agree on which index is which physical axis, the fused orientation estimate gets
            // internally confused (confirmed on real hardware: AHRS's "roll" output was tracking
            // pitch motion, not actual roll, because acc_g was still in the old unswapped channel
            // order after gyr_g's X/Z swap above - GyroMouseRollCompensation then misapplied that
            // wrong roll estimate as a curve on straight vertical pitch motion). Same X<->Z swap
            // as gyr_g, accelY (already index 1, already the confirmed gravity-dominant channel)
            // untouched.
            acc_g.X = accelZG;
            acc_g.Y = -accelYG;
            acc_g.Z = -accelXG;

            UpdateCanonicalGyroMouseImu();

            // AHRS is a shared field on Controller (Controller.cs:249), constructed once with a
            // hardcoded 0.005f "5ms sampling rate" - correct for Nintendo's genuinely fixed 3x5ms
            // report cadence, but MadgwickAHRS.Update integrates its quaternion using this
            // SamplePeriod internally regardless of how much real time actually elapsed between
            // calls. This is a second, independent instance of the same class of bug
            // GyroSubSamplePeriod fixed for GyroMousePlayerSpace: DualSense's real report interval
            // (measured above into measuredGyroSubSamplePeriod) is nowhere near 5ms, so AHRS's own
            // tracked orientation - which GyroMouseRollCompensation's wrist-roll correction reads
            // directly via AHRS.GetEulerAngles() - was drifting/rotating at the wrong rate on every
            // single DualSense report, independent of whether GyroMousePlayerSpace's own timing was
            // already fixed. Sync it to the same measured value every report.
            AHRS.SamplePeriod = measuredGyroSubSamplePeriod;

            float deg_to_rad = 0.0174533f;
            AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);

            // Throttled the same ~4/sec as the raw hex dump (this runs every report, far more
            // often) - lets axis-sign verification be read directly (rest flat -> which acc_g
            // axis reads ~1g; rotate around one axis -> which gyr_g axis responds) instead of
            // hand-decoding the raw hex dump for every sample.
            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - lastDualSenseImuLogTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                lastDualSenseImuLogTimestamp = nowTicks;
                LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                    "IMU: raw gyro=({0},{1},{2}) raw accel=({3},{4},{5}) gyr_g=({6:F1},{7:F1},{8:F1})deg/s acc_g=({9:F2},{10:F2},{11:F2})g dt={12:F3}ms rawTicksDelta={13}",
                    gyr_r[0], gyr_r[1], gyr_r[2], acc_r[0], acc_r[1], acc_r[2],
                    gyr_g.X, gyr_g.Y, gyr_g.Z, acc_g.X, acc_g.Y, acc_g.Z,
                    measuredGyroSubSamplePeriod * 1000.0f, lastLoggedImuDeltaTicks));
            }
        }

        // Applies this specific unit's factory calibration (bias + real sensitivity rescaled onto
        // the nominal GyroLsbPerDegPerSec scale) to one raw gyro sample - see ReadGyroCalibration.
        // Falls back to the nominal scale with zero bias if calibration wasn't read successfully,
        // or if a Plus/Minus pair is degenerate (would otherwise divide by zero).
        private float CorrectGyroSample(Int16 raw, short bias, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / GyroLsbPerDegPerSec;

            float sensNumer = (gyroSpeedPlus + gyroSpeedMinus) * GyroLsbPerDegPerSec;
            float sensDenom = plus - minus;
            float corrected = (raw - bias) * (sensNumer / sensDenom);
            return corrected / GyroLsbPerDegPerSec;
        }

        private float CorrectAccelSample(Int16 raw, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / AccelLsbPerG;

            float range = plus - minus;
            float bias = plus - range / 2.0f;
            float sensNumer = 2.0f * AccelLsbPerG;
            float corrected = (raw - bias) * (sensNumer / range);
            return corrected / AccelLsbPerG;
        }

        private static bool IsBluetoothMicrophoneFrame(byte[] report) {
            return report != null && report.Length == DualSenseMaxReportLen &&
                report[0] == 0x31 && (report[1] & 0x02) != 0;
        }

        private void EnqueueBluetoothMicrophoneFrame(byte[] report) {
            if (!bluetoothMicrophoneStreaming || report == null ||
                report.Length < BtMicrophoneOpusFrameOffset + BtMicrophoneOpusFrameLength)
                return;

            byte[] frame = new byte[BtMicrophoneOpusFrameLength];
            Buffer.BlockCopy(report, BtMicrophoneOpusFrameOffset, frame, 0, frame.Length);
            bluetoothMicrophoneFrameQueue.Enqueue(frame);
            while (bluetoothMicrophoneFrameQueue.Count > 16)
                bluetoothMicrophoneFrameQueue.TryDequeue(out _);
            bluetoothMicrophoneSignal.Set();
        }

        // startMuted applies microphoneMuted only on a genuine fresh start (the worker thread
        // wasn't already running) - Program.cs's reconciliation loop calls this every ~2 seconds
        // regardless of whether anything changed, and forcing this on every one of those calls
        // would make the physical unmute button unusable, re-muting moments after every press.
        // Always calls SetMicrophoneMuted explicitly, even to unmute - the real hardware mute
        // state can otherwise be left muted from an earlier Disabled/Muted session (or the
        // ApplyBluetoothMicrophoneMuteDefault path below) with nothing to correct it here.
        public void StartBluetoothMicrophone(bool startMuted) {
            if (isUSB || state <= state_.DROPPED)
                return;

            bluetoothMicrophoneRequested = true;
            Thread worker = bluetoothMicrophoneThread;
            if (worker != null && worker.IsAlive)
                return;

            // Deliberately NOT SetMicrophoneMuted(startMuted) - that publishes through the
            // Bluetooth media carrier (EnsureBluetoothMediaTransport), which puts the controller
            // into the mode where it interleaves mic-duplex frames into the same 0x31 report ID
            // as ordinary input (see ReceiveRaw's IsBluetoothMicrophoneFrame comment) - for every
            // reader of the raw device, not just BetterJoy. Doing that here meant simply choosing
            // Muted put the controller in that mode for the entire session even if nothing was
            // ever actually recorded from it. The hardware mute/power-save state still gets
            // published correctly below via the ordinary (non-media-carrier) rumble/lightbar
            // report - WriteRetainedRumbleAndTriggerState writes it regardless. Only
            // BluetoothMicrophoneWorker's own interfaceActive transition (a real recording app
            // actually opening the endpoint) should ever call EnsureBluetoothMediaTransport - and
            // already does, further down.
            microphoneMuted = startMuted;
            microphoneMuteStatePending = true;
            SendDualSenseRumble(currentLeftMotor, currentRightMotor);

            bluetoothMicrophoneThread = new Thread(BluetoothMicrophoneWorker) {
                IsBackground = true,
                Name = "BetterJoy2DualSenseMicrophone"
            };
            bluetoothMicrophoneThread.Start();
        }

        // USB's built-in mic is a native USB Audio Class endpoint with no BetterJoy-owned capture
        // pipeline to start - there's no equivalent "genuine fresh start" moment to hang the
        // startMuted default on the way StartBluetoothMicrophone does, so this is applied once
        // per physical connection instead (the usbMicrophoneMuteDefaultApplied latch, reset in
        // Attach). Program.cs's reconciliation loop calls this every ~2 seconds for every USB
        // DualSense regardless of the Built-in mic setting; the latch is what keeps this a true
        // one-shot "on connect" default rather than fighting the physical mute button afterward.
        // Always calls SetMicrophoneMuted explicitly, even to unmute - the real hardware endpoint's
        // mute state is a persistent Windows setting that survives reconnects, so a fresh
        // connection with startMuted=false still has to actively correct a mic left muted from an
        // earlier session, not just skip touching it.
        public void ApplyUsbMicrophoneMuteDefault(bool startMuted) {
            if (!isUSB || usbMicrophoneMuteDefaultApplied)
                return;

            usbMicrophoneMuteDefaultApplied = true;
            SetMicrophoneMuted(startMuted);
        }

        // Bluetooth's equivalent of the above, for the specific case where StartBluetoothMicrophone
        // never runs at all - Built-in mic: Disabled (or Controller audio itself off) never starts
        // the worker, so without this the mute LED/power-save hardware mute is never actively
        // pushed to the controller in that case, leaving whatever state a previous session left it
        // in rather than the Disabled default the UI claims. Program.cs only calls this from the
        // branch where StartBluetoothMicrophone is NOT also being called, so the two never race to
        // set different values on the same connection.
        public void ApplyBluetoothMicrophoneMuteDefault() {
            if (isUSB || bluetoothMicrophoneMuteDefaultApplied)
                return;

            bluetoothMicrophoneMuteDefaultApplied = true;
            SetMicrophoneMuted(true);
        }

        public void StopBluetoothMicrophone() {
            bluetoothMicrophoneRequested = false;
            bluetoothMicrophoneSignal.Set();
            Thread worker = bluetoothMicrophoneThread;
            if (worker != null && worker.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != worker.ManagedThreadId)
                worker.Join(1500);
            if (worker == null || !worker.IsAlive)
                bluetoothMicrophoneThread = null;
        }

        private void BluetoothMicrophoneWorker() {
            bool failureReported = false;
            try {
                while (bluetoothMicrophoneRequested && state > state_.DROPPED) {
                    // Muted means fully inert, not just muted PCM - no endpoint, no media
                    // transport, nothing that could put the controller into its mic-duplex report
                    // mode (see StartBluetoothMicrophone's comment) - until the user actually
                    // unmutes. SetMicrophoneMuted signals bluetoothMicrophoneSignal on every
                    // toggle, so this wakes promptly in both directions rather than polling.
                    if (microphoneMuted) {
                        bluetoothMicrophoneSignal.WaitOne(250);
                        continue;
                    }

                    IMicrophoneEndpoint endpoint = null;
                    try {
                        endpoint = MicrophoneEndpointFactory.Open();
                        if (!bluetoothMicrophoneRequested || microphoneMuted)
                            continue;

                        form.AppendTextBox(failureReported
                            ? "DualSense Bluetooth microphone backend recovered.\r\n"
                            : "DualSense Bluetooth microphone is available as a Windows recording device.\r\n");
                        failureReported = false;

                        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
                        short[] monoPcm = new short[BtMicrophonePcmFrames];
                        byte[] stereoPcm = new byte[BtMicrophonePcmFrames * 2 * sizeof(short)];
                        while (bluetoothMicrophoneRequested && state > state_.DROPPED &&
                                !microphoneMuted) {
                            bluetoothMicrophoneSignal.WaitOne(250);
                            bool interfaceActive = endpoint.IsMicrophoneInterfaceActive();
                            if (interfaceActive != bluetoothMicrophoneStreaming) {
                                lock (bluetoothAudioStateLock) {
                                    if (interfaceActive)
                                        EnsureBluetoothMediaTransport();
                                    bluetoothMicrophoneStreaming = interfaceActive;
                                    if (interfaceActive)
                                        bluetoothMicrophoneDisablePending = false;
                                    else
                                        bluetoothMicrophoneDisablePending = true;
                                    lock (outputReportLock)
                                        bluetoothOutputStateDirty = true;
                                    if (!interfaceActive)
                                        StopBluetoothMediaTransportIfIdle();
                                }
                            }

                            byte[] opusFrame;
                            while (bluetoothMicrophoneStreaming &&
                                bluetoothMicrophoneRequested &&
                                bluetoothMicrophoneFrameQueue.TryDequeue(out opusFrame)) {
                                int decoded = decoder.Decode(new ReadOnlySpan<byte>(opusFrame),
                                    new Span<short>(monoPcm), BtMicrophonePcmFrames, false);
                                if (decoded <= 0)
                                    continue;

                                bool muted = microphoneMuted;
                                Array.Clear(stereoPcm, 0, stereoPcm.Length);
                                int frames = Math.Min(decoded, BtMicrophonePcmFrames);
                                if (!muted) {
                                    for (int frame = 0; frame < frames; frame++) {
                                        short sample = monoPcm[frame];
                                        int offset = frame * 4;
                                        stereoPcm[offset] = (byte)sample;
                                        stereoPcm[offset + 1] = (byte)(sample >> 8);
                                        stereoPcm[offset + 2] = (byte)sample;
                                        stereoPcm[offset + 3] = (byte)(sample >> 8);
                                    }
                                }
                                endpoint.WriteMicrophonePcm(stereoPcm);
                            }
                        }
                    } catch (Exception ex) {
                        if (bluetoothMicrophoneRequested && !failureReported) {
                            form.AppendTextBox("DualSense Bluetooth microphone unavailable; " +
                                "retrying automatically: " + ex.Message + "\r\n");
                            failureReported = true;
                        }
                    } finally {
                        CleanupBluetoothMicrophoneAttempt(endpoint);
                    }

                    if (bluetoothMicrophoneRequested && state > state_.DROPPED)
                        bluetoothMicrophoneSignal.WaitOne(10000);
                }
            } finally {
                bluetoothMicrophoneThread = null;
            }
        }

        private void CleanupBluetoothMicrophoneAttempt(
            IMicrophoneEndpoint endpoint) {
            while (bluetoothMicrophoneFrameQueue.TryDequeue(out _)) { }
            endpoint?.Dispose();
            lock (bluetoothAudioStateLock) {
                if (bluetoothMicrophoneStreaming)
                    bluetoothMicrophoneDisablePending = true;
                bluetoothMicrophoneStreaming = false;
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
                StopBluetoothMediaTransportIfIdle();
            }
        }

        // toggle_built_in_mic defaults to the physical mute button alone (see
        // ControllerMappings.LegacyValue) but is a real binding like volume_up/lt_haptics, so it
        // can be reassigned to a different chord instead. Same discrete-per-press model; the
        // combo is checked against buttons here rather than in the raw report parser, since that
        // runs before buttons is committed for this report and combo-matching needs it live.
        protected override void DoDeviceSpecificButtonActions() {
            bool held = UpdateDesktopActionComboHeld("toggle_built_in_mic", true, out bool wasHeld);
            if (held && !wasHeld)
                SetMicrophoneMuted(!microphoneMuted);
        }

        private void SetMicrophoneMuted(bool muted) {
            microphoneMuted = muted;
            // Wakes BluetoothMicrophoneWorker immediately in both directions instead of leaving it
            // to its own poll interval - unmuting lets it actually open the endpoint (and, only
            // once something genuinely captures from it, the media transport); muting lets it tear
            // both back down right away. Deliberately no longer eagerly calling
            // EnsureBluetoothMediaTransport/setting bluetoothMicrophoneControlPending here the way
            // this used to (to publish the LED before any app opened the endpoint) - that kept the
            // controller in its mic-duplex report mode (see StartBluetoothMicrophone's comment) on
            // every mute-button press, not just genuine capture. The mute LED still updates
            // immediately below via the ordinary rumble/lightbar report instead.
            bluetoothMicrophoneSignal.Set();
            lock (bluetoothAudioStateLock) {
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
            }

            // USB (and Bluetooth outside the media-carrier path above) has no continuously-polled
            // report loop to piggyback the new mute state on - the ordinary rumble/lightbar report
            // is the only channel, so push it out immediately rather than waiting for it to
            // happen to be resent for an unrelated reason. SendDualSenseRumble already skips
            // sending on its own while the Bluetooth media carrier is authoritative instead
            // (bluetoothAudioStreaming/bluetoothMicrophoneStreaming/...), so this is a no-op there.
            microphoneMuteStatePending = true;
            SendDualSenseRumble(currentLeftMotor, currentRightMotor);
        }

        public bool IsStreamingBluetoothAudio => bluetoothAudioStreaming;

        // The 0x36 media clock is shared by speaker output and microphone duplex. Either lane may
        // keep it alive independently; in microphone-only mode valid encoded silence supplies the
        // carrier without starting desktop loopback capture or producing audible output.
        private void EnsureBluetoothMediaTransport() {
            if (bluetoothAudioStopwatch != null)
                return;

            DisposeBluetoothAudioWritePool();
            bluetoothAudioWritePool = BluetoothAudioWritePool.TryOpen(path,
                out int audioHandleError);
            if (bluetoothAudioWritePool == null)
                AudioDebugLog.Write("DualSenseSend",
                    "Dedicated audio handle unavailable error=" + audioHandleError +
                    "; using primary HID handle fallback");
            else
                AudioDebugLog.Write("DualSenseSend",
                    "Dedicated overlapped audio handle opened");

            while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
            bluetoothAudioPending.Clear();
            bluetoothAudioPacketSequence = 0;
            bluetoothSpeakerPrimed = false;
            bluetoothAudioStopwatch = Stopwatch.StartNew();
            bluetoothAudioNextSendDeadlineMs = 0;
            if (bluetoothAudioSilenceFrame == null)
                bluetoothAudioSilenceFrame = CreateBluetoothAudioSilenceFrame();
            Interlocked.Exchange(ref bluetoothAudioLastEnqueueTimestamp, 0);
            Interlocked.Exchange(ref bluetoothAudioMaximumEnqueueGapTicks, 0);
            Interlocked.Exchange(ref bluetoothAudioFramesEnqueued, 0);
            bluetoothAudioLastSendMs = 0;
            bluetoothAudioMaximumSendGapMs = 0;
            bluetoothAudioMaximumLatenessMs = 0;
            bluetoothAudioMaximumSubmitMs = 0;
            bluetoothAudioLastDiagnosticMs = 0;
            bluetoothAudioSyntheticSilenceFrames = 0;
            bluetoothAudioLastSummarySyntheticSilenceFrames = 0;
            bluetoothAudioSpeakerStarvations = 0;
            bluetoothAudioLastSummarySpeakerStarvations = 0;
            bluetoothAudioSpeakerStarved = false;
            bluetoothAudioSendsSinceSummary = 0;
            bluetoothAudioMinimumPending = Int32.MaxValue;
            bluetoothAudioMaximumPending = 0;
        }

        private void StopBluetoothMediaTransportIfIdle() {
            if (bluetoothAudioStreaming || bluetoothMicrophoneStreaming ||
                bluetoothMicrophoneDisablePending ||
                bluetoothMicrophoneControlPending)
                return;
            bluetoothAudioPending.Clear();
            while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
            DisposeBluetoothAudioWritePool();
            bluetoothAudioStopwatch = null;
            bluetoothSpeakerPrimed = false;
        }

        private void AbandonBluetoothMediaTransport() {
            lock (bluetoothAudioStateLock) {
                // Once the physical link is leaving there is no controller left to acknowledge a
                // final FE carrier. Drop the pending barrier so the dedicated HID handles are not
                // retained until process exit.
                bluetoothMicrophoneDisablePending = false;
                bluetoothMicrophoneControlPending = false;
                bluetoothMicrophoneStreaming = false;
                bluetoothAudioStreaming = false;
                StopBluetoothMediaTransportIfIdle();
            }
        }

        public void StartBluetoothAudioStream(int volumePercent, string endpointId,
            bool routeToHeadphones) {
            lock (bluetoothAudioStateLock) {
                if (isUSB || state <= state_.DROPPED)
                    return;

                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                endpointId = endpointId ?? String.Empty;
                if (bluetoothAudioStreaming) {
                    bool endpointMatches = String.Equals(bluetoothAudioEndpointId, endpointId,
                        StringComparison.Ordinal);
                    if (endpointMatches) {
                        // Volume and route are state bytes inside the same 0x36 carrier. Apply
                        // them on its next packet instead of tearing down the active capture.
                        bluetoothAudioVolumePercent = volumePercent;
                        bluetoothAudioRouteHeadphones = routeToHeadphones;
                        lock (outputReportLock)
                            bluetoothOutputStateDirty = true;
                        return;
                    }

                    StopBluetoothAudioStream();
                }

                if (!form.StartBluetoothAudioCapture(PadId, endpointId,
                    BluetoothAudioCodec.DualSenseOpus)) {
                    return;
                }

                EnsureBluetoothMediaTransport();
                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                bluetoothAudioPending.Clear();
                bluetoothSpeakerPrimed = false;
                bluetoothAudioVolumePercent = volumePercent;
                bluetoothAudioEndpointId = endpointId;
                bluetoothAudioRouteHeadphones = routeToHeadphones;
                lock (outputReportLock)
                    bluetoothOutputStateDirty = true;
                bluetoothAudioStreaming = true;
                AudioDebugLog.Write("DualSenseSend", "Start pad=" + PadId +
                    " volume=" + volumePercent + " endpoint=" +
                    (String.IsNullOrEmpty(endpointId) ? "(default)" : endpointId) +
                    " headphones=" + routeToHeadphones);
            }
        }

        public void StopBluetoothAudioStream() {
            bool restoreControllerOutput = false;
            lock (bluetoothAudioStateLock) {
                // Sent unconditionally, before the bluetoothAudioStreaming check below: Start's
                // live-settings-change restart path stops the old stream and starts a new one as
                // two separate fire-and-forget pipe messages with no delivery confirmation, so
                // this flag can end up false while the helper is still actually capturing.
                // OnDetachingWhileAttached is the one guaranteed last chance to clean that up
                // before the handle closes - a stop sent to an already-idle helper is a harmless
                // no-op (BluetoothAudioCapture.Stop is idempotent), but skipping it here when it
                // turns out to be needed orphans the capture with nothing left to ever stop it.
                form.StopBluetoothAudioCapture(PadId);

                if (!bluetoothAudioStreaming)
                    return;

                bluetoothAudioStreaming = false;
                bluetoothAudioVolumePercent = -1;
                bluetoothAudioEndpointId = String.Empty;
                bluetoothAudioRouteHeadphones = false;
                bluetoothSpeakerPrimed = false;
                bluetoothAudioPending.Clear();
                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                StopBluetoothMediaTransportIfIdle();
                restoreControllerOutput = !bluetoothMicrophoneStreaming &&
                    state > state_.DROPPED;
                AudioDebugLog.Write("DualSenseSend", "Stop pad=" + PadId);
            }

            // Reassert rumble after the media carrier stops. Lighting is intentionally absent here:
            // it was already delivered by standalone reports while media was active, and the
            // controller retains that LED state without an audio-lifecycle resend.
            if (restoreControllerOutput) {
                SendDualSenseRumble(currentLeftMotor, currentRightMotor);
            }
        }

        public void EnqueueBluetoothAudioFrame(byte[] frame) {
            if (frame == null || frame.Length != BtAudioOpusFrameLength)
                return;

            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(
                ref bluetoothAudioLastEnqueueTimestamp, now);
            if (previous != 0)
                InterlockedMaximum(ref bluetoothAudioMaximumEnqueueGapTicks,
                    now - previous);
            Interlocked.Increment(ref bluetoothAudioFramesEnqueued);
            bluetoothAudioFrameQueue.Enqueue(frame);
            while (bluetoothAudioFrameQueue.Count > BtAudioMaximumQueuedFrames)
                bluetoothAudioFrameQueue.TryDequeue(out _);
        }

        private void DisposeBluetoothAudioWritePool() {
            BluetoothAudioWritePool pool = bluetoothAudioWritePool;
            bluetoothAudioWritePool = null;
            pool?.Dispose();
        }

        private static void InterlockedMaximum(ref long target, long candidate) {
            long observed = Volatile.Read(ref target);
            while (candidate > observed) {
                long replaced = Interlocked.CompareExchange(ref target,
                    candidate, observed);
                if (replaced == observed)
                    return;
                observed = replaced;
            }
        }

        protected override void SendQueuedBluetoothAudioIfAny() {
            lock (bluetoothAudioStateLock) {
                bool speakerActive = bluetoothAudioStreaming;
                bool microphoneActive = bluetoothMicrophoneStreaming;
                bool microphoneDisablePending = bluetoothMicrophoneDisablePending;
                bool microphoneControlPending = bluetoothMicrophoneControlPending;
                if (!speakerActive && !microphoneActive &&
                    !microphoneDisablePending && !microphoneControlPending) {
                    ReleaseBluetoothAudioPollScheduling();
                    return;
                }

                EnsureBluetoothAudioPollScheduling();

                int dequeued = 0;
                while (bluetoothAudioFrameQueue.TryDequeue(out byte[] queuedFrame)) {
                    bluetoothAudioPending.Add(queuedFrame);
                    dequeued++;
                }
                if (bluetoothAudioPending.Count > BtAudioMaximumQueuedFrames)
                    bluetoothAudioPending.RemoveRange(0,
                        bluetoothAudioPending.Count - BtAudioMaximumQueuedFrames);

                if (speakerActive && !bluetoothSpeakerPrimed) {
                    if (bluetoothAudioPending.Count < BtAudioPrimeFrameCount &&
                        !microphoneActive)
                        return;
                    if (bluetoothAudioPending.Count >= BtAudioPrimeFrameCount) {
                        bluetoothSpeakerPrimed = true;
                        AudioDebugLog.Write("DualSenseSend", "Primed pending=" +
                            bluetoothAudioPending.Count);
                    }
                }

                double nowMs = bluetoothAudioStopwatch.Elapsed.TotalMilliseconds;
                if (nowMs < bluetoothAudioNextSendDeadlineMs)
                    return;

                // The original low-latency transport simply withheld a speaker report when a
                // real frame was momentarily unavailable. Replacing that missing report with an
                // encoded all-zero Opus frame creates an abrupt live-audio -> digital-silence ->
                // live-audio transition, heard as a short blip on real hardware. Let the
                // controller bridge the missing presentation interval and send the next real frame
                // immediately when it arrives. Mic-only mode still needs encoded silence because
                // its inbound microphone lane shares and depends on this outbound media clock.
                bool speakerStarved = speakerActive && bluetoothSpeakerPrimed &&
                    bluetoothAudioPending.Count == 0;
                if (speakerStarved) {
                    if (!bluetoothAudioSpeakerStarved) {
                        bluetoothAudioSpeakerStarved = true;
                        bluetoothAudioSpeakerStarvations++;
                    }
                    return;
                }
                bluetoothAudioSpeakerStarved = false;

                bool syntheticSilence = !speakerActive || !bluetoothSpeakerPrimed;
                byte[] frame = syntheticSilence
                    ? bluetoothAudioSilenceFrame
                    : bluetoothAudioPending[0];
                if (frame == null)
                    return;

                double latenessMs = nowMs - bluetoothAudioNextSendDeadlineMs;
                double sendGapMs = bluetoothAudioLastSendMs <= 0
                    ? 0
                    : nowMs - bluetoothAudioLastSendMs;
                long submitStartTicks = Stopwatch.GetTimestamp();
                bool submitted;
                lock (outputReportLock) {
                    byte outputSequenceBefore = bluetoothOutputSequence;
                    byte packetSequenceBefore = bluetoothAudioPacketSequence;
                    byte[] report = BuildBluetoothSpeakerReport(frame);
                    bool hardFailure = false;
                    submitted = bluetoothAudioWritePool != null
                        ? bluetoothAudioWritePool.TrySend(report, out hardFailure)
                        : HIDapi.hid_write(handle, report,
                            new UIntPtr((uint)report.Length)) >= 0;
                    if (!submitted && hardFailure) {
                        DisposeBluetoothAudioWritePool();
                        AudioDebugLog.Write("DualSenseSend",
                            "Dedicated audio write failed; using primary HID handle fallback");
                        submitted = HIDapi.hid_write(handle, report,
                            new UIntPtr((uint)report.Length)) >= 0;
                    }

                    if (submitted) {
                        bluetoothOutputStateDirty = false;
                        adaptiveTriggerUpdatePending = false;
                    } else {
                        // Building a carrier reserves both protocol sequence values. A saturated
                        // native pool did not publish it, so preserve contiguous wire sequences for
                        // the retry rather than creating a phantom lost packet ourselves.
                        bluetoothOutputSequence = outputSequenceBefore;
                        bluetoothAudioPacketSequence = packetSequenceBefore;
                    }
                }
                double submitMs = (Stopwatch.GetTimestamp() - submitStartTicks) *
                    1000.0 / Stopwatch.Frequency;

                if (!submitted)
                    return;
                if (syntheticSilence)
                    bluetoothAudioSyntheticSilenceFrames++;
                else
                    bluetoothAudioPending.RemoveAt(0);

                bluetoothAudioLastSendMs = nowMs;
                bluetoothAudioMaximumSendGapMs = Math.Max(
                    bluetoothAudioMaximumSendGapMs, sendGapMs);
                bluetoothAudioMaximumLatenessMs = Math.Max(
                    bluetoothAudioMaximumLatenessMs, latenessMs);
                bluetoothAudioMaximumSubmitMs = Math.Max(
                    bluetoothAudioMaximumSubmitMs, submitMs);
                bluetoothAudioSendsSinceSummary++;
                bluetoothAudioMinimumPending = Math.Min(
                    bluetoothAudioMinimumPending, bluetoothAudioPending.Count);
                bluetoothAudioMaximumPending = Math.Max(
                    bluetoothAudioMaximumPending, bluetoothAudioPending.Count);

                if (nowMs - bluetoothAudioLastDiagnosticMs >= 1000.0) {
                    BluetoothAudioWriteStatus hidStatus = bluetoothAudioWritePool != null
                        ? bluetoothAudioWritePool.GetStatus()
                        : default(BluetoothAudioWriteStatus);
                    long enqueueGapTicks = Interlocked.Exchange(
                        ref bluetoothAudioMaximumEnqueueGapTicks, 0);
                    long enqueued = Interlocked.Exchange(
                        ref bluetoothAudioFramesEnqueued, 0);
                    long intervalSilence = bluetoothAudioSyntheticSilenceFrames -
                        bluetoothAudioLastSummarySyntheticSilenceFrames;
                    long intervalStarvations = bluetoothAudioSpeakerStarvations -
                        bluetoothAudioLastSummarySpeakerStarvations;
                    AudioDebugLog.Write("DualSenseSend", "sends=" +
                        bluetoothAudioSendsSinceSummary +
                        " maxGapMs=" + bluetoothAudioMaximumSendGapMs.ToString("F2") +
                        " maxLateMs=" + bluetoothAudioMaximumLatenessMs.ToString("F2") +
                        " maxSubmitMs=" + bluetoothAudioMaximumSubmitMs.ToString("F2") +
                        " pendingMinMax=" +
                        (bluetoothAudioMinimumPending == Int32.MaxValue ? 0 :
                            bluetoothAudioMinimumPending) + "/" +
                        bluetoothAudioMaximumPending +
                        " dequeuedLast=" + dequeued +
                        " enqueued=" + enqueued +
                        " maxEnqueueGapMs=" +
                        (enqueueGapTicks * 1000.0 / Stopwatch.Frequency).ToString("F2") +
                        " silence=" + intervalSilence + "/" +
                        bluetoothAudioSyntheticSilenceFrames +
                        " speakerStarved=" + intervalStarvations + "/" +
                        bluetoothAudioSpeakerStarvations +
                        (bluetoothAudioWritePool != null
                            ? " hidPending=" + hidStatus.PendingWrites +
                              " hidOldestMs=" + hidStatus.OldestPendingMs.ToString("F2") +
                              " hidMaxCompleteMs=" +
                                  hidStatus.MaximumIntervalCompletionMs.ToString("F2") +
                              " hidSaturated=" + hidStatus.IntervalSaturations +
                              " hidFailures=" + hidStatus.CompletionFailures +
                              " hidShort=" + hidStatus.ShortTransfers
                            : " hid=primary-sync"));
                    bluetoothAudioLastDiagnosticMs = nowMs;
                    bluetoothAudioLastSummarySyntheticSilenceFrames =
                        bluetoothAudioSyntheticSilenceFrames;
                    bluetoothAudioLastSummarySpeakerStarvations =
                        bluetoothAudioSpeakerStarvations;
                    bluetoothAudioMaximumSendGapMs = 0;
                    bluetoothAudioMaximumLatenessMs = 0;
                    bluetoothAudioMaximumSubmitMs = 0;
                    bluetoothAudioSendsSinceSummary = 0;
                    bluetoothAudioMinimumPending = Int32.MaxValue;
                    bluetoothAudioMaximumPending = 0;
                }

                bluetoothAudioNextSendDeadlineMs += BtAudioFrameCadenceMs;
                if (nowMs - bluetoothAudioNextSendDeadlineMs > BtAudioFrameCadenceMs * 4)
                    bluetoothAudioNextSendDeadlineMs = nowMs + BtAudioFrameCadenceMs;

                // Publish one in-order FE carrier before releasing a microphone-only media
                // clock. Without this barrier the controller can continue transmitting mic input
                // after Windows closes the endpoint because it never observes the disable state.
                if (microphoneDisablePending || microphoneControlPending) {
                    bluetoothMicrophoneDisablePending = false;
                    bluetoothMicrophoneControlPending = false;
                    StopBluetoothMediaTransportIfIdle();
                }
            }
        }

        private void EnsureBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero || bluetoothAudioMmcssAttempted)
                return;

            bluetoothAudioMmcssAttempted = true;
            try {
                uint taskIndex = 0;
                bluetoothAudioMmcssHandle = AvSetMmThreadCharacteristics(
                    "Pro Audio", ref taskIndex);
                if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                    AvSetMmThreadPriority(bluetoothAudioMmcssHandle,
                        AvrtPriority.Critical);
                    AudioDebugLog.Write("DualSenseSend",
                        "Poll thread registered with MMCSS Pro Audio");
                }
            } catch (DllNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            } catch (EntryPointNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
        }

        private void ReleaseBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                try {
                    AvRevertMmThreadCharacteristics(bluetoothAudioMmcssHandle);
                } catch { }
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
            bluetoothAudioMmcssAttempted = false;
        }

        private static byte[] CreateBluetoothAudioSilenceFrame() {
            try {
                IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 2,
                    OpusApplication.OPUS_APPLICATION_AUDIO);
                encoder.Bitrate = 160000;
                encoder.UseVBR = false;
                encoder.Complexity = 0;
                var samples = new float[480 * 2];
                var frame = new byte[BtAudioOpusFrameLength];
                int encoded = encoder.Encode(new ReadOnlySpan<float>(samples), 480,
                    new Span<byte>(frame), frame.Length);
                return encoded == BtAudioOpusFrameLength ? frame : null;
            } catch {
                return null;
            }
        }

        private byte[] BuildBluetoothSpeakerReport(byte[] opusFrame) {
            byte[] report = new byte[BtAudioReportLength];
            report[0] = 0x36;
            report[1] = (byte)((bluetoothOutputSequence & 0x0F) << 4);
            bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
            report[2] = 0x91;
            report[3] = 0x07;
            // 0xFF enables the controller-to-host microphone lane; 0xFE leaves only host-to-
            // controller audio active. A valid encoded-silence speaker frame remains present in
            // mic-only mode because both directions share this media clock.
            report[4] = bluetoothMicrophoneStreaming ? (byte)0xFF : (byte)0xFE;
            for (int i = 5; i <= 9; i++)
                report[i] = 0x80;
            report[10] = bluetoothAudioPacketSequence++;
            report[11] = 0x90;
            report[12] = BtAudioStateLength;
            Buffer.BlockCopy(DefaultBluetoothAudioState, 0, report, BtAudioStateOffset,
                DefaultBluetoothAudioState.Length);

            // Trigger and LED validity bits are one-shot state strobes. Compatible rumble is the
            // exception: both main-motor bits must remain asserted while either motor is active.
            // Dropping from F3 to F1 immediately after the transition switches the controller
            // back to its audio-haptics lane before ordinary rumble can be felt. A zero-motor
            // transition is still published once through bluetoothOutputStateDirty, after which
            // steady media carriers return to F1.
            if (!bluetoothOutputStateDirty) {
                report[BtAudioStateOffset] &= 0xF0;
                report[BtAudioStateOffset] |= DualSenseValidCompatibleVibration;
                report[BtAudioStateOffset + 1] &= 0x83;
                report[BtAudioStateOffset + 38] = 0;
            }

            // A 0x36 media carrier contains the complete DualSense output-state structure, but
            // audio does not require it to repeatedly claim or resend any lighting state. Strip
            // RGB, player-indicator, lightbar-setup, and LED-brightness validity on every carrier,
            // in every lighting mode. Standalone 0x31/0x02 requests own LED transitions instead;
            // the controller retains their result while subsequent media packets carry audio,
            // microphone, rumble, and trigger state only. Compatible vibration2 (flag2 bit 2)
            // remains intact.
            report[BtAudioStateOffset + 1] &=
                unchecked((byte)~DualSenseValidLightingFlag1);
            report[BtAudioStateOffset + 38] &=
                unchecked((byte)~DualSenseValidLightingFlag2);

            if (bluetoothOutputStateDirty || currentLeftMotor != 0 || currentRightMotor != 0) {
                report[BtAudioStateOffset] |=
                    DualSenseValidCompatibleVibration | DualSenseValidHapticsSelect;
            }

            report[BtAudioStateOffset + 2] = currentRightMotor;
            report[BtAudioStateOffset + 3] = currentLeftMotor;
            byte speakerVolume = MapBluetoothSpeakerVolume(bluetoothAudioVolumePercent);
            report[BtAudioStateOffset + 4] = bluetoothAudioRouteHeadphones
                ? MapBluetoothHeadphoneVolume(bluetoothAudioVolumePercent)
                : speakerVolume;
            report[BtAudioStateOffset + 5] = bluetoothAudioRouteHeadphones
                ? (byte)0
                : speakerVolume;
            // DualSense's physical microphone gain is 0x00..0x40. Muting is intentionally
            // represented both here and in the mute/power-save state below so the hardware LED,
            // captured PCM, and Windows endpoint agree.
            report[BtAudioStateOffset + 6] = bluetoothMicrophoneStreaming
                ? (byte)0x40
                : (byte)0x00;
            report[BtAudioStateOffset + 7] = bluetoothAudioRouteHeadphones
                ? (byte)0x00
                : (byte)0x09;
            WriteMicrophoneMuteState(report, BtAudioStateOffset, microphoneMuted);
            WriteAdaptiveTriggerState(report, BtAudioStateOffset,
                bluetoothOutputStateDirty);
            report[BtAudioStateOffset + 37] = 0x0A;
            report[BtAudioStateOffset + 43] = currentPlayerLeds;
            report[BtAudioStateOffset + 44] = lightbarRed;
            report[BtAudioStateOffset + 45] = lightbarGreen;
            report[BtAudioStateOffset + 46] = lightbarBlue;

            report[BtAudioHapticsOffset] = 0x92;
            report[BtAudioHapticsOffset + 1] = BtAudioHapticsLength;
            report[BtAudioSpeakerOffset] = bluetoothAudioRouteHeadphones
                ? BtAudioHeadsetPacketType
                : BtAudioSpeakerPacketType;
            report[BtAudioSpeakerOffset + 1] = BtAudioOpusFrameLength;
            Buffer.BlockCopy(opusFrame, 0, report, BtAudioSpeakerDataOffset,
                BtAudioOpusFrameLength);

            uint crc = Crc32(0xA2, report, report.Length - 4);
            report[report.Length - 4] = (byte)crc;
            report[report.Length - 3] = (byte)(crc >> 8);
            report[report.Length - 2] = (byte)(crc >> 16);
            report[report.Length - 1] = (byte)(crc >> 24);
            return report;
        }

        private static byte MapBluetoothSpeakerVolume(int volumePercent) {
            if (volumePercent <= 0)
                return 0;
            volumePercent = Math.Min(100, volumePercent);
            return (byte)(0x3D + (volumePercent * (0x64 - 0x3D) + 50) / 100);
        }

        private static byte MapBluetoothHeadphoneVolume(int volumePercent) {
            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            return (byte)(volumePercent * 0x64 / 100);
        }

        // Applies profile-owned, persistent DualSense trigger effects. These are real hardware
        // adaptive-trigger effects even when the virtual output is XInput/DS4; "pseudo" only
        // describes the fact that a profile supplies the effect instead of native game feedback.
        // The three modes use the public zone packing documented by Nielk1's MIT-licensed
        // TriggerEffectGenerator and cross-checked against hbashton/DS4Windows's Trigger Lab:
        // resistance, weapon wall/break, and vibration.
        public void SetAdaptiveTriggerProfile(
            string leftMode, int leftStartPercent, int leftSecondaryPercent,
            int leftStrengthPercent, string rightMode, int rightStartPercent,
            int rightSecondaryPercent, int rightStrengthPercent) {
            byte[] nextLeft = EncodeAdaptiveTriggerEffect(leftMode, leftStartPercent,
                leftSecondaryPercent, leftStrengthPercent);
            byte[] nextRight = EncodeAdaptiveTriggerEffect(rightMode, rightStartPercent,
                rightSecondaryPercent, rightStrengthPercent);

            lock (outputReportLock) {
                if (adaptiveTriggerStateKnown &&
                    ByteArraysEqual(currentLeftTriggerEffect, nextLeft) &&
                    ByteArraysEqual(currentRightTriggerEffect, nextRight))
                    return;

                Buffer.BlockCopy(nextLeft, 0, currentLeftTriggerEffect, 0,
                    currentLeftTriggerEffect.Length);
                Buffer.BlockCopy(nextRight, 0, currentRightTriggerEffect, 0,
                    currentRightTriggerEffect.Length);
                adaptiveTriggerStateKnown = true;
                adaptiveTriggerUpdatePending = true;
                bluetoothOutputStateDirty = true;

                // Until the first full input report arrives, isUSB is only a constructor-time
                // guess. Defer exactly like the lightbar so the effect gets the correct USB/BT
                // framing after ReceiveRaw establishes the physical transport.
                if (!lightbarTransportKnown)
                    return;

                // A streaming Bluetooth media report owns this HID lane and will publish the
                // dirty state on its next frame. USB and idle Bluetooth can apply immediately.
                if (!isUSB && (bluetoothAudioStreaming || bluetoothMicrophoneStreaming ||
                    bluetoothMicrophoneDisablePending || bluetoothMicrophoneControlPending))
                    return;

                SendAdaptiveTriggerStateLocked();
                adaptiveTriggerUpdatePending = false;
            }
        }

        private void SendAdaptiveTriggerStateLocked() {
            bool bt = !isUSB;
            int len = bt ? DualSenseMaxReportLen : 64;
            byte[] report = new byte[len];
            int commonOffset;
            if (bt) {
                report[0] = 0x31;
                report[1] = (byte)(bluetoothOutputSequence << 4);
                bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
                report[2] = 0x10;
                commonOffset = 3;
            } else {
                report[0] = 0x02;
                commonOffset = 1;
            }

            WriteAdaptiveTriggerState(report, commonOffset, true);
            if (bt) {
                uint crc = Crc32(0xA2, report, report.Length - 4);
                report[report.Length - 4] = (byte)crc;
                report[report.Length - 3] = (byte)(crc >> 8);
                report[report.Length - 2] = (byte)(crc >> 16);
                report[report.Length - 1] = (byte)(crc >> 24);
            }
            HIDapi.hid_write(handle, report, new UIntPtr((uint)report.Length));
        }

        private void WriteAdaptiveTriggerState(byte[] report, int commonOffset,
                                               bool enableEffects) {
            // valid_flag0 bits 2/3 select the right/left trigger blocks. The common structure
            // starts at USB byte 1, ordinary BT byte 3, and BtAudioStateOffset in report 0x36.
            if (enableEffects)
                report[commonOffset] |= 0x0C;
            Buffer.BlockCopy(currentRightTriggerEffect, 0, report, commonOffset + 10,
                currentRightTriggerEffect.Length);
            Buffer.BlockCopy(currentLeftTriggerEffect, 0, report, commonOffset + 21,
                currentLeftTriggerEffect.Length);
        }

        // Single source of truth for the mute LED and the real hardware mic-mute bit: both bytes
        // only ever come from here, from the same muted bool, so no other code path can write one
        // without the other and let the LED drift from what the mic hardware is actually doing.
        // There's no hardware-readback status to double check this against instead (checked
        // against Sony's own Linux driver source - it doesn't exist), so keeping these two writes
        // structurally inseparable is the strongest guarantee the protocol allows. The LED's own
        // on/off meaning is separately customizable (MicIndicatorLedByte, the "Mic indicator"
        // profile option) - but power_save_control here always reflects the real muted state
        // exactly, regardless of that setting, since the actual hardware mute must never be
        // allowed to drift from what SetMicrophoneMuted's caller asked for.
        private void WriteMicrophoneMuteState(byte[] report, int offset, bool muted) {
            report[offset + 8] = MicIndicatorLedByte(muted); // mute_button_led
            report[offset + 9] = muted ? DualSensePowerSaveMicMute : (byte)0; // power_save_control
        }

        // "Mic indicator" profile option: what the mute-button LED actually shows, independent of
        // the real hardware mute state it's paired with above. Enabled matches Sony's own default
        // behavior (mute_button_led = mic_muted); Inverted flips it (lit = active, not muted);
        // Disabled never lights it; EnabledWhileDisabled only lights it when Built-in mic itself
        // is set to Disabled, ignoring the runtime mute toggle entirely otherwise.
        private byte MicIndicatorLedByte(bool muted) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            switch (ControllerMappings.MicIndicatorMode(mappingProfileId)) {
                case ControllerMappings.MicIndicatorModeDisabled:
                    return 0;
                case ControllerMappings.MicIndicatorModeInverted:
                    return muted ? (byte)0 : (byte)1;
                case ControllerMappings.MicIndicatorModeEnabledWhileDisabled:
                    return ControllerMappings.BluetoothMicrophoneMode(mappingProfileId) ==
                        ControllerMappings.ModeDisable ? (byte)1 : (byte)0;
                default: // MicIndicatorModeEnabled
                    return muted ? (byte)1 : (byte)0;
            }
        }

        // Read once per report rather than cached on the instance - LightingMode can change at
        // any time via a live profile edit, and this needs to reflect whatever's current on every
        // single write, not a stale snapshot from Attach. Default and OpenRGB share the same
        // hands-off behavior here - see ControllerMappings.LightingModeIsHandsOff's own comment.
        private string CurrentLightingMode() {
            return ControllerMappings.LightingMode(ControllerMappings.ProfileIdFor(this));
        }

        private bool LightingModeIsHandsOff() {
            string mode = CurrentLightingMode();
            return mode == ControllerMappings.LightingModeDefault ||
                mode == ControllerMappings.LightingModeOpenRgb;
        }

        private bool LightingModeIsOpenRgb() {
            return CurrentLightingMode() == ControllerMappings.LightingModeOpenRgb;
        }

        private bool LightbarConnectReady() {
            if (lightbarConnectState == LightbarConnectState.Applied)
                return true;
            if (state != state_.IMU_DATA_OK)
                return false;

            long now = Stopwatch.GetTimestamp();
            if (lightbarConnectState == LightbarConnectState.WaitingForInput) {
                lightbarConnectStreamingSince = now;
                lightbarConnectState = LightbarConnectState.Settling;
                return false;
            }

            return (now - lightbarConnectStreamingSince) >=
                Stopwatch.Frequency * LightbarConnectSettleSeconds;
        }

        private void MarkLightbarConnectApplied() {
            lightbarConnectState = LightbarConnectState.Applied;
        }

        private void WriteRetainedRumbleAndTriggerState(byte[] report, int commonOffset,
                                                        byte leftMotor, byte rightMotor) {
            // A rumble publication enables both compatibility-rumble bits and both trigger
            // blocks. The trigger payloads must therefore accompany it; advertising 0x0C while
            // leaving those bytes zero silently replaces the profile's adaptive-trigger state.
            report[commonOffset] = DualSenseValidRumbleAndTriggers;
            // 0x55 already sets valid_flag1 bit 0 (DS_OUTPUT_VALID_FLAG1_MIC_MUTE_LED_CONTROL_
            // ENABLE in the Linux hid-playstation driver), claiming authority over the mute LED
            // byte below on every single report - previously that byte was just left at its
            // zero-initialized default, meaning every rumble/lightbar report silently forced the
            // controller back to unmuted regardless of what the physical button had set.
            // Also OR in bit 1 (DS_OUTPUT_VALID_FLAG1_POWER_SAVE_CONTROL_ENABLE) so
            // power_save_control below actually takes effect - mute_button_led only ever
            // controlled the LED (confirmed against the same Linux driver's naming; the physical
            // button's own mute state has never affected the mic hardware, on real hardware or in
            // any OS), power_save_control's DS_OUTPUT_POWER_SAVE_CONTROL_MIC_MUTE bit (BIT 4) is
            // the real thing - it's what actually powers the mic capsule down at the hardware
            // level, the same control Sony's own driver uses. DualSenseValidLightbarControl is
            // conditionally dropped so Lighting Mode: Default leaves the physical lightbar alone -
            // see that constant's own comment.
            byte validFlag1 = (byte)(0x55 | DualSensePowerSaveControlEnable);
            if (LightingModeIsHandsOff() || LightingSuppressedForUsbHandoff())
                validFlag1 &= unchecked((byte)~DualSenseValidLightingFlag1);
            if (LightingSuppressedForUsbHandoff())
                report[commonOffset + 38] &= unchecked((byte)~DualSenseValidLightingFlag2);
            report[commonOffset + 1] = validFlag1;
            report[commonOffset + 2] = rightMotor;
            report[commonOffset + 3] = leftMotor;
            WriteMicrophoneMuteState(report, commonOffset, microphoneMuted);
            WriteAdaptiveTriggerState(report, commonOffset, true);
            // 0x55 above already includes bit 4 (DS_OUTPUT_VALID_FLAG1_PLAYER_INDICATOR_CONTROL_
            // ENABLE), claiming authority over player_leds on every report through this shared
            // path (rumble, adaptive triggers) the same way it already does for mute_button_led -
            // omitting this write would silently blank the player-number LEDs on the next rumble
            // or trigger update, the same class of bug that motivated writing mute_button_led here.
            report[commonOffset + 43] = currentPlayerLeds;
            report[commonOffset + 44] = lightbarRed;
            report[commonOffset + 45] = lightbarGreen;
            report[commonOffset + 46] = lightbarBlue;
        }

        private static byte[] EncodeAdaptiveTriggerEffect(string mode, int startPercent,
                                                           int secondaryPercent,
                                                           int strengthPercent) {
            mode = (mode ?? "off").Trim().ToLowerInvariant();
            int strength = PercentToTriggerStrength(strengthPercent);
            if ((mode != "resistance" && mode != "weapon" && mode != "vibration") ||
                strength == 0)
                return CreateOffTriggerEffect();

            int start = PercentToTriggerPosition(startPercent);
            if (mode == "weapon") {
                start = Math.Max(2, Math.Min(7, start));
                int wall = Math.Max(start + 1,
                    Math.Min(8, PercentToTriggerPosition(secondaryPercent)));
                int zones = (1 << start) | (1 << wall);
                byte[] effect = new byte[11];
                effect[0] = 0x25;
                effect[1] = (byte)zones;
                effect[2] = (byte)(zones >> 8);
                effect[3] = (byte)((strength - 1) & 0x07);
                return effect;
            }

            byte effectMode = mode == "vibration" ? (byte)0x26 : (byte)0x21;
            int activeZones = 0;
            uint packedStrength = 0;
            uint value = (uint)((strength - 1) & 0x07);
            for (int zone = start; zone < 10; zone++) {
                activeZones |= 1 << zone;
                packedStrength |= value << (3 * zone);
            }

            byte[] zoneEffect = new byte[11];
            zoneEffect[0] = effectMode;
            zoneEffect[1] = (byte)activeZones;
            zoneEffect[2] = (byte)(activeZones >> 8);
            zoneEffect[3] = (byte)packedStrength;
            zoneEffect[4] = (byte)(packedStrength >> 8);
            zoneEffect[5] = (byte)(packedStrength >> 16);
            zoneEffect[6] = (byte)(packedStrength >> 24);
            if (effectMode == 0x26)
                zoneEffect[9] = (byte)Math.Max(1,
                    (ClampPercent(secondaryPercent) * 28 + 50) / 100);
            return zoneEffect;
        }

        private static byte[] CreateOffTriggerEffect() {
            byte[] effect = new byte[11];
            effect[0] = 0x05;
            return effect;
        }

        private static int PercentToTriggerStrength(int percent) {
            percent = ClampPercent(percent);
            return percent == 0 ? 0 : Math.Max(1, (percent * 8 + 99) / 100);
        }

        private static int PercentToTriggerPosition(int percent) {
            return Math.Min(9, (ClampPercent(percent) + 5) / 10);
        }

        private static int ClampPercent(int percent) {
            return Math.Max(0, Math.Min(100, percent));
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right) {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++) {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        protected override void OnDetachingWhileAttached() {
            StopBluetoothMicrophone();
            StopBluetoothAudioStream();
            AbandonBluetoothMediaTransport();
        }

        // DualSense baseline rumble - both motors driven by the same single amplitude value
        // dequeued from rumble_obj (see the Poll() call site), since DualSense's simple dual-motor
        // rumble has no equivalent to Joy-Con's HD-rumble low/high-frequency split Rumble.GetData()
        // encodes. Report layout (motor byte offsets, enable-rumble flags, Bluetooth
        // CRC32-with-0xA2-seed) from DS4Windows's DualSense output-report code.
        private void SendDualSenseRumble(byte leftMotor, byte rightMotor) {
            lock (outputReportLock) {
                // Disabling rumble calls StopRumble during every profile reconciliation. Do not
                // emit a fresh zero-motor 0x31 report when the physical state is already stopped:
                // on Bluetooth that report shares state with the lightbar and alternated with the
                // profile color report, visibly strobing whenever headphone-gated audio was idle.
                // A real nonzero -> zero transition still falls through and sends the stop. A
                // pending mic-mute toggle also forces this through even at rest - it's the only
                // report that carries mute_button_led outside the Bluetooth media carrier, so
                // skipping it here would silently drop the mute press whenever rumble was idle.
                if (leftMotor == 0 && rightMotor == 0 &&
                    currentLeftMotor == 0 && currentRightMotor == 0 &&
                    !microphoneMuteStatePending)
                    return;

                currentLeftMotor = leftMotor;
                currentRightMotor = rightMotor;
                bluetoothOutputStateDirty = true;
                if (!isUSB && (bluetoothAudioStreaming ||
                    bluetoothMicrophoneStreaming || bluetoothMicrophoneDisablePending ||
                    bluetoothMicrophoneControlPending)) {
                    // The Bluetooth media carrier (bluetoothMicrophoneControlPending) is the
                    // authoritative channel for mute state while it's active, not this report.
                    microphoneMuteStatePending = false;
                    return;
                }
                microphoneMuteStatePending = false;

                bool bt = !isUSB;
                int len = bt ? DualSenseMaxReportLen : 64;
                byte[] buf = new byte[len];
                int commonOffset;
                if (bt) {
                    buf[0] = 0x31;
                    buf[1] = (byte)(bluetoothOutputSequence << 4);
                    bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
                    buf[2] = 0x10;
                    commonOffset = 3;
                    WriteRetainedRumbleAndTriggerState(
                        buf, commonOffset, leftMotor, rightMotor);
                    uint crc = Crc32(0xA2, buf, len - 4);
                    buf[len - 4] = (byte)crc;
                    buf[len - 3] = (byte)(crc >> 8);
                    buf[len - 2] = (byte)(crc >> 16);
                    buf[len - 1] = (byte)(crc >> 24);
                } else {
                    buf[0] = 0x02;
                    commonOffset = 1;
                    WriteRetainedRumbleAndTriggerState(
                        buf, commonOffset, leftMotor, rightMotor);
                }
                HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
            }
        }

        // The USB audio endpoint's first pair is ordinary audio; its second pair drives the
        // voice-coil actuators. ControllerAudio keeps the test tone off that actuator pair. This
        // report sends the right audio channel to the built-in mono speaker and sets its volume -
        // or, when the aux jack is occupied, both channels to the headphones instead.
        public override void PrepareUsbAudio(int volumePercent) {
            // Re-arm SilenceControllerAudio's latch whenever audio comes back, before the USB
            // check below - on Bluetooth this method sends nothing (the 0x36 media carrier owns
            // the volume bytes and restores them itself), but the latch still has to clear so a
            // later transition back to audio-off writes its report again.
            lock (outputReportLock)
                audioLevelsSilenced = false;

            if (!isUSB || state <= state_.DROPPED)
                return;

            lock (outputReportLock) {
                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                byte[] buf = new byte[64];
                buf[0] = 0x02;
                buf[1] = 0xB0; // headphone volume + speaker volume + audio routing are valid
                buf[5] = (byte)(volumePercent * 0x7F / 100);
                buf[6] = (byte)(volumePercent * 0x64 / 100);
                // byte 8 bits 4-5 (DS_OUTPUT_AUDIO_FLAGS_OUTPUT_PATH_SEL in the Linux
                // hid-playstation driver, cross-checked against this codebase's own pre-existing
                // 0x30 speaker-only value): 0x30 mutes headphones and routes the right channel to
                // the internal speaker, 0x00 mutes the speaker and routes both channels to
                // headphones. This never read HeadphonesConnected at all before, so plugging in
                // headphones over USB never routed audio to them.
                buf[8] = HeadphonesConnected ? (byte)0x00 : (byte)0x30;
                HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
            }
        }

        // Zeroes the controller's own output levels when the profile turns controller audio off.
        // Previously that state was simply never written: PrepareUsbAudio has one call site,
        // gated on audio being enabled, with no else branch - so disabling audio only stopped
        // sending audio data and left headphone/speaker volume at whatever the controller last
        // had, from an earlier session or a PS5. Ordinary rumble/lightbar reports don't correct
        // that either: they set valid_flag0 to DualSenseValidRumbleAndTriggers, which omits the
        // audio-volume validity bits, so their zeroed volume bytes are ignored by the controller.
        //
        // There is no DAC or amp power bit to use instead. power_save_control's only defined bit
        // is MIC_MUTE (BIT 4) - confirmed against both the upstream Linux hid-playstation defines
        // and DS4Windows's own DualSense implementation - so zeroed volume is as close to "output
        // off" as this protocol goes.
        //
        // Deliberately transition-only. Profile reconciliation calls this every scan pass, and
        // re-sending an identical report would cost battery for nothing: output reports on this
        // controller are otherwise event-driven (see SendDualSenseRumble's at-rest early return).
        // Restoring is left to the paths that already own volume - PrepareUsbAudio over USB, the
        // 0x36 media carrier over Bluetooth - which is why only the two volume bytes are touched
        // here. valid_flag1 stays 0 so this cannot disturb mic mute or the mute LED.
        public override void SilenceControllerAudio() {
            if (state <= state_.DROPPED)
                return;

            lock (outputReportLock) {
                if (audioLevelsSilenced)
                    return;
                // While the Bluetooth media carrier is running it owns these bytes on every 0x36
                // frame; an ordinary 0x31 here would fight it. Return without latching so this
                // still applies once the stream has actually stopped.
                if (!isUSB && (bluetoothAudioStreaming || bluetoothMicrophoneStreaming))
                    return;

                bool bt = !isUSB;
                int len = bt ? DualSenseMaxReportLen : 64;
                byte[] buf = new byte[len];
                int commonOffset;
                if (bt) {
                    buf[0] = 0x31;
                    buf[1] = (byte)(bluetoothOutputSequence << 4);
                    bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
                    buf[2] = 0x10;
                    commonOffset = 3;
                } else {
                    buf[0] = 0x02;
                    commonOffset = 1;
                }

                // Same validity byte PrepareUsbAudio uses (headphone volume + speaker volume +
                // audio routing), so this claims nothing new. Both volume bytes zeroed; routing
                // still follows the jack so re-enabling lands on the right path.
                buf[commonOffset] = 0xB0;
                buf[commonOffset + 4] = 0; // headphone_volume
                buf[commonOffset + 5] = 0; // speaker_volume
                buf[commonOffset + 7] = HeadphonesConnected ? (byte)0x00 : (byte)0x30;

                if (bt) {
                    uint crc = Crc32(0xA2, buf, len - 4);
                    buf[len - 4] = (byte)crc;
                    buf[len - 3] = (byte)(crc >> 8);
                    buf[len - 2] = (byte)(crc >> 16);
                    buf[len - 1] = (byte)(crc >> 24);
                }

                HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
                audioLevelsSilenced = true;
            }
        }

        // Sets the DualSense's lightbar to the profile's solid RGB color, plus the current player-
        // number indicator LEDs (currentPlayerLeds - see SetLEDByPlayerNum), via one output
        // report. Layout matches SendDualSenseRumble; rumble flags are left at "not in use" since
        // this report isn't rumble-related. RGB offsets (45/46/47 USB, 46/47/48 BT) and the fact
        // that no separate "enable lightbar" bit is needed beyond the same 0x55 feature-flags byte
        // the rumble report already sets - both confirmed via DS4Windows's DualSenseDevice.cs.
        // player_leds sits at USB offset 44 / BT commonOffset+43 (one byte before lightbar red) -
        // confirmed against the Linux kernel's hid-playstation.c dualsense_output_report_common
        // struct, cross-checked against this same function's own already-working RGB offsets.
        private void SendDualSenseLightbar(byte red, byte green, byte blue,
                                           bool fromOpenRgbServer = false) {
            lock (outputReportLock) {
                bool bt = !isUSB;
                // Hard gate: never write lighting to the hardware until the pad is fully connected
                // and streaming input (IMU_DATA_OK). During connect the firmware owns the lightbar
                // (its own routine, incl. a blue flash); our writes there collide with it. Defer the
                // request as pending so the ReceiveRaw lighting block applies it once streaming.
                if (state != state_.IMU_DATA_OK) {
                    lightbarUpdatePending = true;
                    return;
                }
                // TEMPORARY lighting-timing trace (gated on DualSenseDebugLogging): every actual
                // color that reaches the pad funnels through here, now only at IMU_DATA_OK.
                LogDualSenseRawDump(String.Format(CultureInfo.InvariantCulture,
                    "LIGHT SendLightbar rgb={0:X2}{1:X2}{2:X2} isUSB={3} mode={4} fromOpenRgb={5} " +
                    "pending={6} state={7}",
                    red, green, blue, isUSB, CurrentLightingMode(), fromOpenRgbServer,
                    lightbarUpdatePending, state));
                // This helper carries both RGB and player-indicator state. Default delegates all
                // lighting, even when the separate Player LEDs option is enabled. Retain the
                // desired state as pending so leaving Default can apply it without reconnecting,
                // but do not publish any LED command while Default is active.
                string lightingMode = CurrentLightingMode();
                bool lightingHandsOff =
                    lightingMode == ControllerMappings.LightingModeDefault ||
                    lightingMode == ControllerMappings.LightingModeOpenRgb;
                bool openRgbAuthorized = fromOpenRgbServer &&
                    lightingMode == ControllerMappings.LightingModeOpenRgb;
                if ((fromOpenRgbServer && !openRgbAuthorized) ||
                    (lightingHandsOff && !openRgbAuthorized)) {
                    if (fromOpenRgbServer) {
                        openRgbLightbarUpdatePending = false;
                        lightbarUpdatePending = false;
                    } else {
                        lightbarUpdatePending = true;
                    }
                    return;
                }
                if (!bt) {
                    const int len = 64;
                    byte[] buf = new byte[len];
                    buf[0] = 0x02;
                    buf[1] = DualSenseValidRightTrigger | DualSenseValidLeftTrigger;
                    buf[2] = fromOpenRgbServer
                        ? (byte)(0x55 & ~DualSenseValidPlayerIndicatorControl)
                        : (byte)0x55;
                    WriteAdaptiveTriggerState(buf, 1, true);
                    buf[44] = currentPlayerLeds;
                    buf[45] = red;
                    buf[46] = green;
                    buf[47] = blue;
                    HIDapi.hid_write(handle, buf, new UIntPtr((uint)len));
                    return;
                }

                if (!lightbarControlReleased) {
                    byte[] setup = CreateDualSenseBluetoothLightbarReport();
                    const int commonOffset = 3;
                    setup[commonOffset + 38] =
                        DualSenseValidLightbarSetupControl;
                    setup[commonOffset + 41] = 0x02; // release startup animation ownership
                    WriteDualSenseBluetoothLightbarReport(setup);
                    lightbarControlReleased = true;
                }

                byte[] color = CreateDualSenseBluetoothLightbarReport();
                const int colorCommonOffset = 3;
                // 0x04 lightbar control enable | 0x10 player-indicator control enable
                // (DS_OUTPUT_VALID_FLAG1_PLAYER_INDICATOR_CONTROL_ENABLE) - without the latter the
                // controller ignores player_leds below on this particular report.
                color[colorCommonOffset + 1] = fromOpenRgbServer
                    ? DualSenseValidLightbarControl
                    : (byte)(DualSenseValidLightbarControl |
                             DualSenseValidPlayerIndicatorControl);
                color[colorCommonOffset + 43] = currentPlayerLeds;
                color[colorCommonOffset + 44] = red;
                color[colorCommonOffset + 45] = green;
                color[colorCommonOffset + 46] = blue;
                WriteDualSenseBluetoothLightbarReport(color);
            }
        }

        private byte[] CreateDualSenseBluetoothLightbarReport() {
            byte[] buf = new byte[DualSenseMaxReportLen];
            buf[0] = 0x31;
            buf[1] = (byte)(bluetoothOutputSequence << 4);
            bluetoothOutputSequence = (byte)((bluetoothOutputSequence + 1) & 0x0F);
            buf[2] = 0x10;
            return buf;
        }

        private void WriteDualSenseBluetoothLightbarReport(byte[] buf) {
            uint crc = Crc32(0xA2, buf, buf.Length - 4);
            buf[buf.Length - 4] = (byte)crc;
            buf[buf.Length - 3] = (byte)(crc >> 8);
            buf[buf.Length - 2] = (byte)(crc >> 16);
            buf[buf.Length - 1] = (byte)(crc >> 24);
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
        }

        private struct BluetoothAudioWriteStatus {
            public readonly int PendingWrites;
            public readonly long CompletionFailures;
            public readonly long ShortTransfers;
            public readonly long IntervalSaturations;
            public readonly double OldestPendingMs;
            public readonly double MaximumIntervalCompletionMs;

            public BluetoothAudioWriteStatus(int pendingWrites,
                long completionFailures, long shortTransfers,
                long intervalSaturations, double oldestPendingMs,
                double maximumIntervalCompletionMs) {
                PendingWrites = pendingWrites;
                CompletionFailures = completionFailures;
                ShortTransfers = shortTransfers;
                IntervalSaturations = intervalSaturations;
                OldestPendingMs = oldestPendingMs;
                MaximumIntervalCompletionMs = maximumIntervalCompletionMs;
            }
        }

        // DualSense 0x36 media carriers are ordinary HID output reports. Keep a bounded number of
        // native OVERLAPPED writes in flight on a second shared file session so a transient Windows
        // Bluetooth/HIDCLASS completion stall does not block the controller's input Poll thread.
        // The primary hidapi handle remains the sole input owner.
        private sealed class BluetoothAudioWritePool : IDisposable {
            private const int SlotCount = 32;
            private const int NativeBackingBufferLength = 640;
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 3;
            private const uint FileFlagOverlapped = 0x40000000;
            private const uint WaitObject0 = 0;
            private const int ErrorIoPending = 997;
            private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeOverlappedState {
                public IntPtr Internal;
                public IntPtr InternalHigh;
                public uint Offset;
                public uint OffsetHigh;
                public IntPtr EventHandle;
            }

            private readonly object gate = new object();
            private readonly IntPtr nativeHandle;
            private readonly byte[][] buffers = new byte[SlotCount][];
            private readonly GCHandle[] pins = new GCHandle[SlotCount];
            private readonly IntPtr[] events = new IntPtr[SlotCount];
            private readonly IntPtr[] overlapped = new IntPtr[SlotCount];
            private readonly bool[] outstanding = new bool[SlotCount];
            private readonly int[] expectedLengths = new int[SlotCount];
            private readonly long[] submittedTimestamps = new long[SlotCount];
            private int nextSlot;
            private bool disposed;
            private long completionFailures;
            private long shortTransfers;
            private long maximumIntervalCompletionTicks;
            private long intervalSaturations;

            private BluetoothAudioWritePool(IntPtr nativeHandle) {
                this.nativeHandle = nativeHandle;
                try {
                    int overlappedSize = Marshal.SizeOf(typeof(NativeOverlappedState));
                    for (int slot = 0; slot < SlotCount; slot++) {
                        buffers[slot] = new byte[NativeBackingBufferLength];
                        pins[slot] = GCHandle.Alloc(buffers[slot],
                            GCHandleType.Pinned);
                        events[slot] = CreateEventW(IntPtr.Zero, true, true, null);
                        if (events[slot] == IntPtr.Zero)
                            throw new IOException(
                                "Could not create a DualSense audio completion event.");
                        overlapped[slot] = Marshal.AllocHGlobal(overlappedSize);
                        ResetOverlapped(slot);
                    }
                } catch {
                    ReleaseAllocatedSlots(false);
                    throw;
                }
            }

            public static BluetoothAudioWritePool TryOpen(string devicePath,
                out int error) {
                error = 0;
                if (String.IsNullOrEmpty(devicePath)) {
                    error = 87;
                    return null;
                }

                // Write-only: this pool only ever calls WriteFile. Requesting GENERIC_READ too
                // used to leave a second read-capable handle sitting on the same Bluetooth HID
                // device for as long as the media transport was active (on top of the primary
                // handle and whatever else - a game - has it open), which correlated with
                // duplicate input reports reaching other readers. Unverified as the actual
                // mechanism, but this handle never reads, so the access it requests should match.
                IntPtr nativeHandle = CreateFileW(devicePath,
                    GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (nativeHandle == IntPtr.Zero ||
                    nativeHandle == InvalidHandleValue) {
                    error = Marshal.GetLastWin32Error();
                    return null;
                }

                try {
                    return new BluetoothAudioWritePool(nativeHandle);
                } catch {
                    error = Marshal.GetLastWin32Error();
                    CloseHandle(nativeHandle);
                    return null;
                }
            }

            public bool TrySend(byte[] report, out bool hardFailure) {
                hardFailure = false;
                if (report == null || report.Length == 0 ||
                    report.Length > NativeBackingBufferLength) {
                    hardFailure = true;
                    return false;
                }

                lock (gate) {
                    if (disposed) {
                        hardFailure = true;
                        return false;
                    }

                    if (!ReapCompletedNoLock()) {
                        hardFailure = true;
                        return false;
                    }

                    int slot = FindFreeSlotNoLock();
                    if (slot < 0) {
                        intervalSaturations++;
                        return false;
                    }

                    if (!SubmitNoLock(slot, report)) {
                        completionFailures++;
                        hardFailure = true;
                        return false;
                    }
                    return true;
                }
            }

            private int FindFreeSlotNoLock() {
                for (int offset = 0; offset < SlotCount; offset++) {
                    int candidate = (nextSlot + offset) % SlotCount;
                    if (!outstanding[candidate])
                        return candidate;
                }
                return -1;
            }

            private bool SubmitNoLock(int slot, byte[] report) {
                Array.Clear(buffers[slot], 0, buffers[slot].Length);
                Buffer.BlockCopy(report, 0, buffers[slot], 0, report.Length);
                ResetEvent(events[slot]);
                ResetOverlapped(slot);
                bool completedSynchronously = WriteFile(nativeHandle,
                    pins[slot].AddrOfPinnedObject(), (uint)report.Length,
                    IntPtr.Zero, overlapped[slot]);
                int error = completedSynchronously ? 0 : Marshal.GetLastWin32Error();
                if (!completedSynchronously && error != ErrorIoPending) {
                    SetEvent(events[slot]);
                    AudioDebugLog.Write("DualSenseSend",
                        "Overlapped audio submit failed error=" + error);
                    return false;
                }

                outstanding[slot] = true;
                expectedLengths[slot] = report.Length;
                submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                nextSlot = (slot + 1) % SlotCount;
                return true;
            }

            private bool ReapCompletedNoLock() {
                bool success = true;
                for (int slot = 0; slot < SlotCount; slot++) {
                    if (!outstanding[slot] ||
                        WaitForSingleObject(events[slot], 0) != WaitObject0)
                        continue;

                    bool completed = GetOverlappedResult(nativeHandle,
                        overlapped[slot], out uint transferred, false);
                    long completionTicks = Stopwatch.GetTimestamp() -
                        submittedTimestamps[slot];
                    maximumIntervalCompletionTicks = Math.Max(
                        maximumIntervalCompletionTicks, completionTicks);
                    outstanding[slot] = false;
                    if (!completed) {
                        completionFailures++;
                        success = false;
                    } else if (transferred != 0 &&
                        transferred < expectedLengths[slot]) {
                        shortTransfers++;
                    }
                }
                return success;
            }

            public BluetoothAudioWriteStatus GetStatus() {
                lock (gate) {
                    if (disposed)
                        return default(BluetoothAudioWriteStatus);

                    ReapCompletedNoLock();
                    int pending = 0;
                    long oldestTicks = 0;
                    long now = Stopwatch.GetTimestamp();
                    for (int slot = 0; slot < SlotCount; slot++) {
                        if (!outstanding[slot])
                            continue;
                        pending++;
                        oldestTicks = Math.Max(oldestTicks,
                            now - submittedTimestamps[slot]);
                    }

                    long intervalMaximum = maximumIntervalCompletionTicks;
                    long saturations = intervalSaturations;
                    maximumIntervalCompletionTicks = 0;
                    intervalSaturations = 0;
                    return new BluetoothAudioWriteStatus(pending,
                        completionFailures, shortTransfers, saturations,
                        oldestTicks * 1000.0 / Stopwatch.Frequency,
                        intervalMaximum * 1000.0 / Stopwatch.Frequency);
                }
            }

            private void ResetOverlapped(int slot) {
                var value = new NativeOverlappedState {
                    EventHandle = events[slot]
                };
                Marshal.StructureToPtr(value, overlapped[slot], false);
            }

            public void Dispose() {
                lock (gate) {
                    if (disposed)
                        return;
                    disposed = true;
                    ReleaseAllocatedSlots(true);
                    CloseHandle(nativeHandle);
                }
            }

            private void ReleaseAllocatedSlots(bool cancelOutstanding) {
                for (int slot = 0; slot < SlotCount; slot++) {
                    bool safeToFree = true;
                    if (events[slot] != IntPtr.Zero && cancelOutstanding &&
                        outstanding[slot] &&
                        WaitForSingleObject(events[slot], 0) != WaitObject0) {
                        CancelIoEx(nativeHandle, overlapped[slot]);
                        safeToFree = WaitForSingleObject(events[slot], 250) ==
                            WaitObject0;
                    }
                    if (!safeToFree) {
                        // Kernel I/O can still own this memory. A bounded teardown leak is safer
                        // than freeing a live OVERLAPPED structure or pinned report buffer.
                        events[slot] = IntPtr.Zero;
                        overlapped[slot] = IntPtr.Zero;
                        pins[slot] = default(GCHandle);
                        continue;
                    }

                    if (events[slot] != IntPtr.Zero) {
                        CloseHandle(events[slot]);
                        events[slot] = IntPtr.Zero;
                    }
                    if (overlapped[slot] != IntPtr.Zero) {
                        Marshal.FreeHGlobal(overlapped[slot]);
                        overlapped[slot] = IntPtr.Zero;
                    }
                    if (pins[slot].IsAllocated)
                        pins[slot].Free();
                }
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW",
                CharSet = CharSet.Unicode, ExactSpelling = true,
                SetLastError = true)]
            private static extern IntPtr CreateFileW(string fileName,
                uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                uint creationDisposition, uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool WriteFile(IntPtr file, IntPtr buffer,
                uint bytesToWrite, IntPtr bytesWritten, IntPtr nativeOverlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetOverlappedResult(IntPtr file,
                IntPtr nativeOverlapped, out uint bytesTransferred,
                [MarshalAs(UnmanagedType.Bool)] bool wait);

            [DllImport("kernel32.dll", EntryPoint = "CreateEventW",
                CharSet = CharSet.Unicode, ExactSpelling = true,
                SetLastError = true)]
            private static extern IntPtr CreateEventW(IntPtr eventAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool manualReset,
                [MarshalAs(UnmanagedType.Bool)] bool initialState,
                string name);

            [DllImport("kernel32.dll")]
            private static extern uint WaitForSingleObject(IntPtr handle,
                uint milliseconds);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ResetEvent(IntPtr handle);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetEvent(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CancelIoEx(IntPtr handle,
                IntPtr nativeOverlapped);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
