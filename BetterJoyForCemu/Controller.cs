using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterJoyForCemu.VirtualOutput;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    // Base class for every physical controller type BetterJoy talks to - see
    // DOCS/CONTROLLERS-REFACTOR.md for the full plan this is step 2 of. Content moves in
    // incrementally, sub-step by sub-step, not all at once - see the plan's "Suggested migration
    // approach" for why, and its "What must not regress" section for the three highest-stakes
    // surfaces (PadId/auto-join, XInput mapping, gyro/IMU) that live substantially in what will
    // eventually move here.
    //
    // Content moves in as either pure data (nested type declarations, simple fields with no
    // property getter/setter, no constructor-time computation reaching into not-yet-moved
    // subsystems) or pure/self-contained methods already written as shared helpers with no
    // per-device branching (CenterSticks/CommitButtonState below) - never entangled state like
    // the other/mapping-profile-engine/gyro-pipeline, which stays on Joycon until their own,
    // deliberately later sub-steps. Everything here is exactly as safe to read/write/call from
    // Joycon as it was before the move - C# doesn't distinguish "declared in the base class"
    // from "declared in the subclass" for field/property/method access from subclass code.
    public abstract partial class Controller {
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;

        // The canonical wire contract every subclass's report parser must populate - every
        // downstream consumer (mapping profiles, MapToXbox360Input/MapToDualShock4Input, the UDP
        // server) is built on this shared shape, not on any device-specific button layout.
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
            TOUCHPAD = 20,
            // A completed one-finger tap is a gesture-derived input rather than a physical
            // report bit. Keeping it in the canonical button space lets it participate in the
            // same bindings and chords as every physical control (for example PS + Tap), while
            // TOUCHPAD above remains the pad's mechanical click.
            TOUCHPAD_TAP = 21,
            TOUCHPAD_TWO_FINGER_TAP = 22,
            TOUCHPAD_TWO_FINGER_SCROLL_UP = 23,
            TOUCHPAD_TWO_FINGER_SCROLL_DOWN = 24,
            // Physical microphone-mute button on DualSense. Keep this in the canonical input
            // space instead of consuming it inside DualSense.cs so it can participate in every
            // normal binding/chord (including future audio toggle and volume actions).
            MIC_MUTE = 25,
            // The two function buttons below the sticks on a DualSense Edge. Same reasoning as
            // MIC_MUTE above: kept in the canonical input space so they bind and chord like any
            // other physical control rather than being consumed inside DualSense.cs. Appended at
            // the end deliberately - existing binds are stored as "joy_<enum value>", so inserting
            // anywhere earlier would silently repoint every saved mapping.
            FN1 = 26,
            FN2 = 27,
        };
        protected const int ButtonCount = (int)Button.FN2 + 1;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int batteryPercent = -1;
        public ControllerBatteryStatus batteryStatus = ControllerBatteryStatus.Unknown;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        protected void BatteryChanged() { // battery changed level
            form.UpdateBatteryColor(this);

            if (battery <= 1 && !isUSB) {
                form.NotifyLowBattery(this);
            }
        }

        // Sony controllers report both a real 0-100 capacity and a charging state. Keep the
        // legacy 0-4 battery field in sync for DSU clients and tile colors, while retaining the
        // richer values for the service snapshot/UI.
        protected void SetBatteryStatus(int percent, ControllerBatteryStatus status) {
            percent = Math.Max(0, Math.Min(100, percent));
            int oldBattery = battery;
            bool changed = batteryPercent != percent || batteryStatus != status;

            batteryPercent = percent;
            batteryStatus = status;
            battery = BatteryLevelFromPercent(percent);

            if (!changed && oldBattery == battery)
                return;

            form.UpdateBatteryColor(this);
            if (oldBattery != battery && battery <= 1 && !isUSB &&
                status == ControllerBatteryStatus.Discharging)
                form.NotifyLowBattery(this);
        }

        public static int BatteryLevelFromPercent(int percent) {
            if (percent <= 9) return 0;
            if (percent <= 29) return 1;
            if (percent <= 54) return 2;
            if (percent <= 79) return 3;
            return 4;
        }

        // Queues low/high-frequency + amplitude rumble requests, one FIFO of at most 15 pending
        // entries - fully generic (both Joy-Con's HD-rumble byte encoding in GetData() and
        // DualSense's simpler dual-motor approach just dequeue from the same queue, see
        // SendQueuedRumbleIfAny), even though GetData()'s actual encoding math is Nintendo-only
        // (harmless if a future non-Nintendo subclass never calls it, same as MapToDualShock4Input
        // having branches DualSense never exercises).
        protected struct Rumble {
            public Queue<float[]> queue;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                if (queue.Count > 15) {
                    queue.Dequeue();
                }
                queue.Enqueue(rumbleQueue);
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                float[] queued_data = queue.Dequeue();

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        protected Rumble rumble_obj;

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= state_.ATTACHED) return;
            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        // Nintendo HD-rumble's frequency-split encoding is Joy-Con-only (GetData()'s actual math),
        // but every controller kind's simple amplitude-based rumble reads through the same
        // queue/SetRumble contract - see SendQueuedRumbleIfAny's per-subclass overrides. Meaningless
        // to a device whose rumble ignores frequency (DualSense just reads back the amplitude), but
        // harmless to have seeded regardless.
        protected int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        protected int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        public bool RumbleEnabled {
            get {
                string mode = ControllerMappings.RumbleMode(
                    ControllerMappings.ProfileIdFor(this));
                if (mode == ControllerMappings.ModeDisable)
                    return false;
                return mode != ControllerMappings.RumbleModeDisableWithGyro ||
                    !PairHasActiveGyroOutput();
            }
        }

        private bool HasActiveGyroOutput() {
            return gyroMouseEnabledThisReport || gyroLeftStickActiveThisReport ||
                gyroRightStickActiveThisReport;
        }

        private bool PairHasActiveGyroOutput() {
            return HasActiveGyroOutput() ||
                (other != null && other != this && other.HasActiveGyroOutput());
        }

        // A game can leave its last nonzero rumble command running while gyro becomes active.
        // Stop both halves of a joined pair once at that transition; repeatedly sending zero
        // output reports can interfere with shared Sony light/audio state.
        private void UpdateGyroRumbleSuppression() {
            bool suppress = ControllerMappings.RumbleMode(
                ControllerMappings.ProfileIdFor(this)) ==
                ControllerMappings.RumbleModeDisableWithGyro &&
                PairHasActiveGyroOutput();
            bool wasSuppressed = rumbleSuppressedByGyro ||
                (other != null && other != this && other.rumbleSuppressedByGyro);

            rumbleSuppressedByGyro = suppress;
            if (other != null && other != this)
                other.rumbleSuppressedByGyro = suppress;

            if (!suppress || wasSuppressed)
                return;

            StopRumble();
            if (other != null && other != this)
                other.StopRumble();
        }

        // Profile changes are applied while a controller may already be vibrating. Queue an
        // explicit stop when rumble is disabled so removing the virtual feedback subscription
        // alone cannot strand the motors at their last nonzero command.
        public void StopRumble() {
            SetRumble(lowFreq, highFreq, 0.0f);
        }

        // ViGEmBus feedback (rumble commanded by a game through the virtual controller) - generic
        // across every device kind, since it just forwards into the same SetRumble/rumble_obj queue
        // every subclass's own SendQueuedRumbleIfAny already reads from. A joined pair's passive
        // half also gets the rumble (other != this), matching the active half physically being felt
        // as one unit by the player. DS4-output feedback is included for completeness even though
        // DualSenseController never gets a DS4 output target by design (see DOCS/
        // CONTROLLERS-REFACTOR.md's Tier-3 note) - harmless no-op for anything that never wires it.
        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            if (!RumbleEnabled)
                return;

            DebugPrint("Rumble data Recived: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            if (!RumbleEnabled)
                return;

            DebugPrint("Rumble data Recived: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        // Device identity - every controller type has an OS-assigned HID path and (usually) a
        // real serial number, used by ControllerMappings' DeviceId/DeviceSuffix to derive a
        // stable profile identity independent of PadMacAddress (which isn't known until Attach()
        // completes for some device types).
        public string path = String.Empty;
        public string serial_number;

        // Handedness/orientation convention, not literal physical handedness for every device
        // kind - Program.cs constructs a Pro controller with isLeft=true too (see Kind's default
        // fallthrough), and single-unit devices in general default to true (matches the existing
        // "primary/solo" convention). CalibrationState.FinishCalibration reads this directly to
        // correct a real Joy-Con-side gravity-axis sign difference - see its own comment for why.
        public bool isLeft;

        // Set once at connect time for Joy-Con (placeholder-serial heuristic), re-derived every
        // packet for DualSense based on observed report length - same field, two different "who
        // updates it and when" contracts (see DOCS/CONTROLLERS-REFACTOR.md's known-issues list).
        internal bool isUSB = false;
        public bool IsUsbConnection => isUSB;

        // Windows owns the USB Audio Class stream; each physical controller definition owns the
        // HID command which selects and unmutes its speaker.
        public virtual string UsbAudioEndpointNameHint => null;
        public virtual void PrepareUsbAudio(int volumePercent) { }

        // Counterpart to PrepareUsbAudio, for the transition into "no controller audio" -
        // either Controller audio: Disabled, or Require headphones with an empty jack.
        // Controllers that can zero their own output levels override this; the default is a
        // no-op so a type without that control simply keeps today's behavior.
        public virtual void SilenceControllerAudio() { }

        // Bluetooth exposes no audio-class endpoint at all, so there is nothing for Windows/WASAPI
        // to open - DualShock4Controller instead streams an SBC-encoded live capture directly
        // inside HID output reports (see its StartBluetoothAudioStream/StopBluetoothAudioStream
        // and Program.cs's connect/disconnect hook). Not promoted to a virtual member here since
        // DS4 is currently the only controller with a Bluetooth audio implementation - Program.cs
        // reaches it via a plain type check, matching how narrowly-applicable behavior is handled
        // elsewhere in this class hierarchy (e.g. SupportsPairing-gated Joy-Con-only members).

        // Calibrated stick position, gyro/accel readings, and filtered orientation - written by
        // each subclass's own report-parsing code (Joycon.ExtractIMUValues stays Nintendo-report-
        // format-specific and isn't promoted here, it just writes into these inherited fields the
        // same way it always did), read by the gyro-mouse/gyro-stick pipeline below.
        protected float[] stick = { 0, 0 };
        protected float[] stick2 = { 0, 0 };
        protected Int16[] acc_r = { 0, 0, 0 };
        protected Vector3 acc_g;
        protected Int16[] gyr_r = { 0, 0, 0 };
        protected Vector3 gyr_g;
        protected float[] cur_rotation; // Filtered IMU data

        static float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        protected MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        // Stick calibration - three init strategies feed this one consuming contract (Joy-Con/Pro
        // from SPI flash via Joycon.dump_calibration_data, SNES/N64 from App.config, DualSense
        // from a hardcoded identity default in its own Attach()), all still device-specific and
        // not promoted here - only the fields themselves move, since getActiveStickData/
        // CenterSticks/the auto-cal stick-center pass below all need to read and write them.
        protected UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        protected UInt16 deadzone;
        protected UInt16[] stick_precal = { 0, 0 };
        protected UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        protected UInt16 deadzone2;
        protected UInt16[] stick2_precal = { 0, 0 };

        // Gyro/accel neutral-offset calibration data, empirically republished by
        // CalibrationState (manual wizard or auto-cal) - see ActiveCaliData. gyr_neutral is read
        // by both this and Joycon.ExtractIMUValues's own factory/manual fallback path.
        protected float[] activeData;
        protected Int16[] gyr_neutral = { 0, 0, 0 };

        public void getActiveData() {
            this.activeData = CalibrationState.ActiveCaliData(serial_number);
        }

        // Applies any empirically-recalibrated stick data on top of whatever dump_calibration_data
        // already loaded from SPI - called both there (so a controller with prior stick
        // recalibration gets it immediately on every future connect, not just the session it was
        // captured in) and right after CalibrationState.FinishStickCalibration (so it takes effect
        // without needing a reconnect). No-op per stick when ActiveStickCal returns null - i.e.
        // this controller/stick has never been recalibrated, so the SPI-read values stand.
        public void getActiveStickData() {
            ushort[] primary = CalibrationState.ActiveStickCal(serial_number, false);
            if (primary != null) {
                Array.Copy(primary, stick_cal, 6);
                PrintArray(stick_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick data: {0:S}");
            }

            if (HasDualSticks) {
                ushort[] secondary = CalibrationState.ActiveStickCal(serial_number, true);
                if (secondary != null) {
                    Array.Copy(secondary, stick2_cal, 6);
                    PrintArray(stick2_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick2 data: {0:S}");
                }
            }
        }

        protected void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }

        // Standard IEEE 802.3 CRC32 with a virtual leading seed byte. Sony Bluetooth feature and
        // output reports use this same checksum with 0xA3 for controller-to-host feature data and
        // 0xA2 for host-to-controller output data. Kept in the shared controller base so Sony
        // device definitions can describe their own reports without duplicating checksum code.
        private static readonly uint[] crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table() {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        protected static uint Crc32(byte seed, byte[] data, int length) {
            uint crc = 0xFFFFFFFF;
            crc = crc32Table[(crc ^ seed) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++)
                crc = crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }

        public IOutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;
        public OutputControllerDualSenseViiper out_dualsense;

        // Raw HID handle every subclass's Attach/Poll/Detach/report-parsing code reads/writes -
        // device-agnostic (every controller type talks over one), even though what gets written
        // through it is not.
        protected IntPtr handle;
        protected volatile bool stop_polling = true;

        // Host callback for UI/status updates (AssignSlot, AppendTextBox, etc.) - every
        // controller type needs one, regardless of device kind.
        public IJoyconHost form;

        private Thread PollThreadObj;

        // Starts this controller's own read/report thread pointed at Poll() - not itself moved
        // here (see Joycon.Poll's comment: real device-branching inside, stays put until that's
        // worth splitting on its own), just referenced by name. Fully device-agnostic otherwise.
        public void Begin() {
            if (PollThreadObj == null) {
                stop_polling = false;
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                form.AppendTextBox("Starting poll thread.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
            STICK, // appended, not inserted - existing numeric values are persisted in App.config/settings
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox(s + "\r\n");
            }
        }

        // Guards RetireDuplicateConnections() below so it only ever runs once per controller, the
        // first time it actually proves itself alive (not merely that Attach() didn't throw,
        // which happens before the connection is known to be stable/receiving real data).
        protected bool retiredDuplicates = false;

        // How long a connection can go without a single successful read before being forced to
        // DROPPED even though nothing ever came back as a hard read error - see the staleness
        // check in Poll()'s loop below for why this exists.
        protected const double StaleConnectionSeconds = 3.0;

        // Device-agnostic read-loop shell: LED update dispatch, queued-rumble send, ReceiveRaw
        // dispatch with a crash guard, drop/stale-connection detection, and the final gyro-mouse-
        // release backstop. The actual per-device work happens in the hooks below - ReceiveRaw is
        // the only one with no meaningful shared default (every device's report format is
        // unrelated), so it stays abstract; the rest default to a no-op and Joycon overrides them
        // with its existing bodies unchanged.
        public void Poll() {
            int attempts = 0;
            long lastSuccessTimestamp = Stopwatch.GetTimestamp();
            while (!stop_polling & state > state_.NO_JOYCONS) {
                int requestedLed = Interlocked.Exchange(ref pendingLedPlayerNum, -1);
                if (requestedLed >= 0) {
                    SetLEDByPlayerNum(requestedLed);
                }
                int requestedHomeLight = Interlocked.Exchange(ref pendingHomeLightOn, -1);
                if (requestedHomeLight >= 0) {
                    SetHomeLight(requestedHomeLight != 0);
                }
                SendQueuedRumbleIfAny();
                SendQueuedBluetoothAudioIfAny();
                ApplyQueuedAutomaticBluetoothPairingIfAny();
                // The pairing hook above can itself Detach() this controller mid-iteration
                // (closing handle) once its Bluetooth-preferred hand-off is armed - stop_polling
                // alone doesn't take effect until the next loop condition check, so without this,
                // ReceiveRaw() right below would still run against the now-closed handle.
                if (stop_polling)
                    break;

                int a;
                try {
                    a = ReceiveRaw();
                } catch (Exception ex) {
                    // ReceiveRaw covers report parsing, gyro/stick processing, and ViGEm report
                    // building for every packet - an unhandled exception anywhere in that chain
                    // reaches here uncaught, and this runs on a bare Thread (not a UI/task
                    // context with its own handler), so .NET terminates the whole process by
                    // default. One malformed packet or transient edge case shouldn't take down
                    // every connected controller - treat it the same as a read error below
                    // (brief pause, count toward the drop threshold) instead.
                    DebugPrint("Unhandled exception in ReceiveRaw: " + ex, DebugType.ALL);
                    a = -1;
                }

                if (a > 0 && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;
                    lastSuccessTimestamp = Stopwatch.GetTimestamp();

                    if (!retiredDuplicates) {
                        retiredDuplicates = true;
                        RetireDuplicateConnections();
                    }
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                    break;
                } else if (a < 0) {
                    // An error on read.
                    //form.AppendTextBox("Pause 5ms");
                    Thread.Sleep((Int32)5);
                    if (!AllowsSilentHidIdle)
                        ++attempts;
                } else if (a == 0) {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }

                // Belt-and-suspenders on top of the attempts>240 hard-error threshold above: a
                // connection whose transport just goes quiet (e.g. a Bluetooth radio link
                // dropping) rather than the HID handle itself becoming invalid can have
                // hid_read_timeout return plain timeouts (a==0) forever, never a hard error -
                // confirmed on real hardware with a Bluetooth DualSense, which the attempts
                // counter above never penalizes, so it never reached DROPPED and sat as a stale,
                // frozen "connected" entry (and virtual controller) indefinitely. This is a
                // second, independent detector using elapsed wall-clock time since the last
                // genuinely successful read, regardless of why reads have been failing.
                if (state > state_.DROPPED && !AllowsSilentHidIdle &&
                    (Stopwatch.GetTimestamp() - lastSuccessTimestamp) / (double)Stopwatch.Frequency > StaleConnectionSeconds) {
                    DebugLog.Write("Poll stale drop: pad=" + PadId +
                        " kind=" + Kind +
                        " path=" + path +
                        " state=" + state +
                        " attempts=" + attempts.ToString(CultureInfo.InvariantCulture) +
                        " staleSeconds=" + StaleConnectionSeconds.ToString(CultureInfo.InvariantCulture));
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped (connection went silent).\r\n");
                    DebugPrint("Connection lost - no successful read in " + StaleConnectionSeconds + "s.", DebugType.ALL);
                    break;
                }
            }

            // A disconnect or detach may prevent another input report from arriving. Release
            // stateful desktop inputs here as the final backstop instead of leaving Windows with
            // a button-down whose corresponding physical controller can no longer report up.
            ReleaseGyroMouseActions();
            ReleaseTouchpadMouseActions();
            FinishTouchpadColorWheel();
            if (HasTouchpad)
                ReleaseMappedHold(MappingValue("touchpad_click"));
            ResetTouchpadGestureState();
        }

        // Every report's raw HID read + parse + downstream processing - no shared default
        // possible (every device's report format is unrelated), so this is the one truly abstract
        // hook. See Joycon.ReceiveRaw (Nintendo family) and DualSenseController.ReceiveRaw.
        //
        // If your override picks a report's byte offset (USB vs Bluetooth prefix length) from the
        // read's return LENGTH: don't. On Windows, hidapi can pad a read to the full requested
        // buffer size regardless of the device's actual native report length - confirmed on real
        // hardware for DualShock4Controller, which returned 78 bytes carrying the USB-style
        // single-byte report ID (0x01), not Bluetooth's extended 0x11, because the OS padded the
        // read rather than the device actually using the BT format. DualSenseController's own
        // report-offset bug (d755fae) was a close cousin of this same failure shape. Branch on the
        // report-ID byte (buf[0]) instead; length is fine only as a sanity/validity check.
        protected abstract int ReceiveRaw();

        // Some HID gamepads only send input on state changes rather than continuously streaming
        // neutral reports. Those should still be owned by BetterJoy while idle; real read errors
        // continue to use the attempts-based drop path above.
        protected virtual bool AllowsSilentHidIdle => false;

        // No-op by default; Joycon overrides this to send whatever HD-rumble data is queued in
        // rumble_obj (DualSenseController overrides it too, for its own simpler dual-motor rumble)
        // - kept as a hook since the actual encoding/wire format is device-specific.
        protected virtual void SendQueuedRumbleIfAny() { }

        // Same reasoning as SendQueuedRumbleIfAny: hidapi's device functions are documented as
        // thread-unsafe when called concurrently on the same handle, and every other write in this
        // codebase already only ever happens from this controller's own Poll thread, interleaved
        // sequentially with its own reads - never from a second thread. DualShock4Controller's
        // Bluetooth audio stream originally ran its own background thread calling hid_write
        // concurrently with Poll's hid_read_timeout here; on real hardware that corrupted both the
        // audio stream and unrelated input parsing (battery status went bad too) - a live
        // demonstration of exactly the hazard hidapi's docs warn about. Draining one batch per
        // Poll iteration instead keeps everything on this single thread, matching every other
        // output path, and needs no artificial pacing/sleep - Poll's own natural iteration rate
        // (driven by real HID read timing) already provides smooth-enough cadence.
        protected virtual void SendQueuedBluetoothAudioIfAny() { }

        // Same reasoning again: DualSenseController's automatic-pairing feature-report sequence
        // (clear/write/verify/reassert, plus the Bluetooth authentication-window setup) must run
        // on this Poll thread, not the manager's scan-timer thread that requests it - a real race
        // confirmed on hardware earlier this session, where the timer thread's writes interleaved
        // with this thread's own concurrent hid_read_timeout/output traffic on the same handle.
        // Request-then-drain on this thread, matching pendingLedPlayerNum's pattern below.
        protected virtual void ApplyQueuedAutomaticBluetoothPairingIfAny() { }

        // Generic MAC-based duplicate-connection dedup, shared by every device type - if another
        // already-connected entry has the same PadMacAddress as this one, it's the same physical
        // controller reachable twice (e.g. wireless + wired at once) and gets dropped. Was
        // previously a no-op-by-default hook with this exact logic duplicated into Joycon's
        // override, DualSense's Bluetooth-auto-disconnect tail spliced directly into the middle of
        // it - see DOCS/CONTROLLERS-REFACTOR.md's Tier-3 "danger zone" note on this method, and
        // OnDuplicateRetired below for where that tail lives now.
        protected virtual void RetireDuplicateConnections() {
            foreach (Controller other in Program.mgr.j) {
                if (other != this && other.state != state_.DROPPED && other.PadMacAddress.Equals(PadMacAddress)) {
                    // Some devices identify their transport only after their first actual input
                    // report. Let both sides reach that point before choosing a winner; otherwise
                    // a just-created USB instance can still carry its constructor's wireless
                    // placeholder and make a transport preference depend on thread timing.
                    if (!CanResolveDuplicate(other))
                        continue;

                    // Most controllers retain the historical "latest live connection wins"
                    // behavior. Device definitions that can deliberately choose between two
                    // simultaneous transports may instead keep the already-connected instance
                    // and retire this one. The losing instance owns its transport-specific
                    // cleanup in OnRetiredAsDuplicate; the winner owns cleanup of the other one
                    // in OnDuplicateRetired.
                    if (PreferExistingDuplicate(other)) {
                        state = state_.DROPPED;
                        form.AppendTextBox("Retiring non-preferred connection for the same controller.\r\n");
                        OnRetiredAsDuplicate(other);
                        return;
                    }

                    other.state = state_.DROPPED;
                    form.AppendTextBox("Retiring duplicate connection for the same controller.\r\n");
                    OnDuplicateRetired(other);
                }
            }
        }

        protected virtual bool CanResolveDuplicate(Controller other) { return true; }

        protected virtual bool PreferExistingDuplicate(Controller other) { return false; }

        protected virtual void OnRetiredAsDuplicate(Controller other) { }

        // No-op by default; DualSenseController overrides this to attempt a Bluetooth-level
        // disconnect of the stale entry once USB has taken over for the same physical controller -
        // see DualSense.cs's OnDuplicateRetired. Called from RetireDuplicateConnections right after
        // a duplicate is marked DROPPED.
        protected virtual void OnDuplicateRetired(Controller other) { }

        // No-op by default; Joycon overrides this with the actual Nintendo power-off subcommand
        // sequence (SetHCIState/Subcommand) - kept as a hook since those are Nintendo-protocol-
        // only, not promoted to Controller. Called from the (now-shared) DoThingsWithButtons on
        // HOME-long-press and power-off-on-inactivity.
        // shuttingDown=true means BetterJoy itself is going away (service stop / app exit) rather
        // than the user asking this controller to switch off. The controller still powers off; what
        // changes is that no transport-roaming maintenance runs on the way out, because nothing is
        // roaming anywhere - see DualSenseController's override.
        public virtual void PowerOff(bool shuttingDown = false) { }

        // Plain host-side disconnect for a machine going to sleep - drop the Bluetooth link and
        // nothing else. Deliberately NOT PowerOff: that is the "the user asked for this controller
        // to go off" path, and for DualSense it carries the roaming ceremony (rewriting the 0x0A
        // pairing report over the cable so the bond survives moving between a PS5 and this PC).
        // Nothing is roaming anywhere when the machine suspends, and running it there is actively
        // harmful - the reassert knocks the pad into pairing/low-power broadcast mode, the 0x08/0x02
        // that follows is then refused (featureReportSent=False), and the fallback radio disconnect
        // leaves the controller awake and re-announcing. This is that fallback on its own, which is
        // all a suspend ever wanted: the same primitive DualShock4 and the Nintendo controllers
        // already use as their entire PowerOff.
        public virtual void DisconnectForSuspend() {
            if (state <= state_.DROPPED)
                return;

            // Wired, so there is no link to drop - and closing our handle tells the controller
            // nothing at all, leaving it awake on the cable still showing whatever lightbar we last
            // wrote. Kick it into the charge-only park instead: that is PowerOff's own wired path
            // (EnterUsbPseudoSleep), and it is the case already confirmed to go dark on suspend,
            // because releasing and re-enumerating the interface is what lets the firmware reach
            // its native charge-only state. The wake monitor it arms only has to live long enough
            // for that transition - the stop routine right behind this ends it.
            if (isUSB) {
                PowerOff(shuttingDown: true);
                return;
            }

            BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
            state = state_.DROPPED;
        }

        // Long-press shutdown can require preparation which must not run for inactivity or
        // application-exit power-off. Sony controllers use this boundary to preserve a
        // Bluetooth-preferred, charge-only USB quarantine while their firmware changes state.
        public virtual void PrepareLongPressPowerOff() { }

        // No-op by default; Joycon overrides this with Nintendo's actual LED-set subcommand -
        // already self-guards on UsesNintendoProtocol today, kept as a hook (rather than promoted
        // outright) so that guard stays exactly where the rest of Joycon's Nintendo-only output
        // wiring lives.
        public virtual void SetLEDByPlayerNum(int id) { }

        // No-op by default; Joycon overrides this with Nintendo's actual home-LED subcommand -
        // already self-guards on UsesNintendoProtocol today, same reasoning as SetLEDByPlayerNum
        // above. Called from ApplyControllerProfileOptions (Program.cs) on every profile-option
        // pass, generically, regardless of device kind.
        public virtual void SetHomeLight(bool on) { }

        // No-op for controllers without configurable RGB lighting. Sony controller definitions
        // override this with their transport-specific lightbar output reports; callers can apply
        // one profile color generically without importing device protocol details.
        public virtual void SetLightColor(byte red, byte green, byte blue) { }

        // Explicit color received through BetterJoy's OpenRGB SDK server. Most controllers can
        // use their normal lightbar path; DualSense overrides this because its OpenRGB profile is
        // otherwise intentionally hands-off and its Bluetooth media carrier owns output state.
        public virtual void SetOpenRgbLightColor(byte red, byte green, byte blue) {
            SetLightColor(red, green, blue);
        }

        // Paired getter for SetLightColor - lets a caller (OpenRgbServer's device descriptor)
        // report the actual current color generically, without needing each controller type's
        // own private lightbarRed/Green/Blue tracking fields. Black for controllers without
        // configurable RGB lighting, matching SetLightColor's own no-op default.
        public virtual (byte Red, byte Green, byte Blue) GetLightColor() {
            return (0, 0, 0);
        }


        // The canonical per-report button state every subclass's report parser populates (see
        // the Button enum above) - protected, not public, since nothing outside a Controller
        // subclass's own report-parsing/mapping code reads these directly today (verified: no
        // external file referenced them before this move either, they were private on Joycon).
        // down_ is the pre-update snapshot CommitButtonState diffs the freshly-parsed buttons[]
        // against to derive buttons_down/buttons_up (rising/falling edges), and
        // buttons_down_timestamp records when each button last went down, for press-and-hold/
        // double-click detection.
        protected bool[] buttons_down = new bool[ButtonCount];
        protected bool[] buttons_up = new bool[ButtonCount];
        protected bool[] buttons = new bool[ButtonCount];
        protected bool[] down_ = new bool[ButtonCount];
        protected long[] buttons_down_timestamp = new long[ButtonCount];

        // Public read accessors for the arrays above - used by Reassign.cs/HeadlessJoyconHost.cs's
        // button-mapping auto-detect (press a button to bind it) for any connected controller, not
        // just Joy-Con family. Trivial wrappers, generic since the backing arrays are.
        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }

        // Last time any button's down/up edge changed - read by auto-power-off (HomeLongPowerOff-
        // style idle checks) and gyro-mouse idle detection, both device-generic.
        protected long inactivity = Stopwatch.GetTimestamp();

        // volatile: written by other's setter (join/split thread) and read by NintendoController's
        // MappingValue/ProfileBoolOption/etc (poll thread) - see OnOtherChanging's override there
        // for the race this guards against.
        protected volatile string mappingProfileId;

        // Program.cs's MAC resolution (DualSense's feature-report read, Joy-Con's BT-address
        // parse in Attach()) runs slightly after this object starts existing - if anything reads a
        // mapping profile bind before that lands, mappingProfileId's lazy cache would otherwise
        // lock onto the placeholder MAC's fallback identity for the rest of the connection. Call
        // this right after PadMacAddress is actually assigned the real value, exactly like the
        // "other" (join/split) setter already does for that case. Deliberately NOT hooked into
        // PadMacAddress's own assignment generically (e.g. via a property) - Joy-Con's own
        // Attach() also reassigns PadMacAddress internally (its BT-address parse), and
        // invalidating on every such write broke Joy-Con auto-join (two Joycons showing joined in
        // the UI but each keeping its own virtual controller instead of the loser's being torn
        // down) in a way never fully root-caused; narrowing this to an explicit call at the one
        // call site that actually needs it avoids touching that path at all.
        public void InvalidateMappingProfileCache() {
            mappingProfileId = null;
        }

        private JoyconController _other = null;

        // Pairing contract: null = solo, == this = self-paired ("vertical"), == <other instance>
        // = a real two-unit pair. Only JoyconController currently ever sets this away from null
        // (see SupportsPairing) - the mechanism itself is device-agnostic (a device that never
        // pairs just never touches it), which is why it lives here rather than only on
        // JoyconController itself. Typed JoyconController (step 5 sub-step D2b), not the wider
        // NintendoController - no ProController/SnesController/N64Controller ever actually pairs,
        // and every real assignment site is already gated behind SupportsPairing (true only for
        // JoyconController), so this makes that invariant impossible to violate by accident
        // rather than just true in practice today.
        public JoyconController other {
            get {
                return _other;
            }
            set {
                if (_other != value)
                    OnOtherChanging();
                _other = value;
                mappingProfileId = null;

                // Queued (RequestLEDUpdate), not written directly - this setter runs on
                // whatever thread is doing the join/split (scan thread for auto-join, UI/pipe
                // thread for a manual one), which by this point always races this controller's own
                // already-running Poll() thread for the HID handle. See RequestLEDUpdate's
                // comment.
                if (_other == null || _other == this) {
                    // Solo (_other == null, held sideways) and self-paired ("vertical",
                    // _other == this, held upright) both use this controller's own PadId for its
                    // LED - neither has a partner controller to share a pair's LED value with.
                    RequestLEDUpdate(PadId);
                } else {
                    // Set LED to current pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    RequestLEDUpdate(lowestPadId);
                }
            }
        }

        // Called just before other actually changes (join/split), before the new value takes
        // effect - lets a subclass release any state tied to its old pairing/profile identity.
        // No-op by default; Joycon overrides this to invalidate synthetic input holds under the
        // old profile (see Joycon.PrepareForMappingProfileChange) - kept as a hook rather than
        // moving that method here, since it reaches into gyro-mouse/mapping-engine state that
        // isn't shared yet.
        protected virtual void OnOtherChanging() { }

        // Requested LED player-number update, applied by this controller's own Poll() thread
        // rather than the caller's - SetLEDByPlayerNum/Subcommand does a blocking HID write+read
        // on the same handle Poll() is concurrently reading from, so calling it directly from a
        // foreign thread (the scan thread doing a mass re-rank after a drop, or other's setter
        // during a join/split) on an already-Begin()'d controller risked the response getting
        // interleaved with normal packet reads and the LED update silently timing out - matching
        // the existing rumble_obj queue pattern in Joycon, just for a single latest-wins value
        // instead of a FIFO, since only the most recent requested LED value matters. -1 means "no
        // update pending" - Interlocked.Exchange (not volatile, which int? can't be) makes the
        // read-and-clear in Poll() atomic against a concurrent RequestLEDUpdate call.
        protected int pendingLedPlayerNum = -1;

        public void RequestLEDUpdate(int playerNum) {
            Interlocked.Exchange(ref pendingLedPlayerNum, playerNum);
        }

        protected int pendingHomeLightOn = -1;
        private int lastRequestedHomeLightOn = -1;

        public void RequestHomeLightUpdate(bool on) {
            int value = on ? 1 : 0;
            if (Interlocked.Exchange(ref lastRequestedHomeLightOn, value) != value)
                Interlocked.Exchange(ref pendingHomeLightOn, value);
        }

        // Establishes the connection - part of the Controller contract (Program.cs's connect
        // loop calls this on anything it opens), but with no shared shell worth extracting:
        // every implementation is either wholly one device-specific handshake or another (see
        // Joycon.Attach for Nintendo's SPI/subcommand sequence vs. DualSense's early-return
        // baseline path), unlike Detach below where most of the method really is shared.
        public abstract int Attach();

        // Device-agnostic disconnect shell: stop the poll loop, tear down whatever virtual
        // output exists, and release the HID handle. OnDetachingWhileAttached is the one point a
        // subclass gets to send its own "give the connection back" bytes before the handle
        // closes (see Joycon's override) - everything else here is identical for every device
        // type.
        public void Detach(bool close = false) {
            stop_polling = true;

            // A target that was created but never actually got plugged in (or was already
            // unplugged) throws on Disconnect() - the same "wasn't connected in the first place"
            // case OnApplicationQuit already guards, missed here. Left unhandled it escapes all
            // the way out of the scan pass, which is how a dropped controller ends up stuck in
            // the list holding its virtual pad forever (see CleanUp).
            if (out_xbox != null) {
                try { out_xbox.Disconnect(); } catch { }
            }

            if (out_ds4 != null) {
                try { out_ds4.Disconnect(); } catch { }
            }

            if (out_dualsense != null) {
                try { out_dualsense.Disconnect(); } catch { }
            }

            if (state > state_.NO_JOYCONS && handle != IntPtr.Zero) {
                HIDapi.hid_set_nonblocking(handle, 0);
                OnDetachingWhileAttached();
            }
            if (close || state > state_.DROPPED) {
                if (handle != IntPtr.Zero) {
                    HIDapi.hid_close(handle);
                    handle = IntPtr.Zero;
                }
            }
            state = state_.NOT_ATTACHED;
        }

        internal void RequestStopPollingForSuspend() {
            stop_polling = true;
        }

        internal bool StopPollingForSuspend() {
            RequestStopPollingForSuspend();
            Thread worker = PollThreadObj;
            return worker == null || worker == Thread.CurrentThread || worker.Join(1500);
        }

        // No-op by default; subclasses can release transport-specific resources before the HID
        // handle closes. Avoid transport handoff commands here unless they are tied to an
        // explicit user action, because cleanup also reaches this path after transient read/init
        // failures.
        protected virtual void OnDetachingWhileAttached() { }

        // Shared by ProcessButtonsAndStick (Joy-Con/Pro) and ParseDualSenseReport - diffs the
        // freshly-populated buttons[] against down_[] (the pre-update snapshot the caller must
        // already have taken under lock(down_), matching ProcessButtonsAndStick's own pattern)
        // into buttons_up/buttons_down/buttons_down_timestamp, and updates inactivity. Report
        // parsing itself is not shareable (the two devices' byte layouts are unrelated), just
        // this bookkeeping tail.
        protected void CommitButtonState() {
            long timestamp = Stopwatch.GetTimestamp();

            // Device parsers replace buttons[] with a freshly decoded physical snapshot every
            // report. Restore the gesture-derived Tap pulse before diffing so it produces normal
            // down/up edges and can be consumed by IsComboHeld and remote binding capture.
            buttons[(int)Button.TOUCHPAD_TAP] = HasTouchpad &&
                timestamp < touchpadTapInputUntilTimestamp;
            buttons[(int)Button.TOUCHPAD_TWO_FINGER_TAP] = HasTouchpad &&
                timestamp < touchpadTwoFingerTapInputUntilTimestamp;
            buttons[(int)Button.TOUCHPAD_TWO_FINGER_SCROLL_UP] = HasTouchpad &&
                timestamp < touchpadTwoFingerScrollUpInputUntilTimestamp;
            buttons[(int)Button.TOUCHPAD_TWO_FINGER_SCROLL_DOWN] = HasTouchpad &&
                timestamp < touchpadTwoFingerScrollDownInputUntilTimestamp;

            lock (buttons_up) {
                lock (buttons_down) {
                    bool changed = false;
                    for (int i = 0; i < buttons.Length; ++i) {
                        buttons_up[i] = (down_[i] & !buttons[i]);
                        buttons_down[i] = (!down_[i] & buttons[i]);
                        if (down_[i] != buttons[i])
                            buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                        if (buttons_up[i] || buttons_down[i])
                            changed = true;
                    }

                    inactivity = (changed) ? timestamp : inactivity;
                }
            }
        }

        // Should really be called calculating stick data. Pure function of its parameters - no
        // per-device branching, so it's shared as-is rather than duplicated (Joy-Con/Pro read
        // stick_cal from SPI flash, SNES/N64 from App.config, DualSense from a hardcoded
        // identity default - three different init strategies feeding this one consuming
        // contract, per DOCS/CONTROLLERS-REFACTOR.md).
        protected float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz, float scaling_factor) {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);

            if (scaling_factor != 1.0f) {
                s[0] *= scaling_factor;
                s[1] *= scaling_factor;

                s[0] = Math.Max(Math.Min(s[0], 1.0f), -1.0f);
                s[1] = Math.Max(Math.Min(s[1], 1.0f), -1.0f);
            }

            return s;
        }

        // Capability contract every subclass must answer explicitly - see Joycon's overrides for
        // the original isPro/isSnes/isDualSense-flag-derived logic this replaces, and
        // DualSenseController's overrides (step 4) for how trivially these reduce to hardcoded
        // constants once a device isn't sharing a class with others. Abstract, not a defaulted
        // virtual, since there's no meaningful generic answer to any of these - every concrete
        // controller type must decide.
        public abstract bool SupportsPairing { get; }      // can combine with another unit into one logical controller
        public abstract bool HasDualSticks { get; }        // has two physical sticks/thumb-stick-click buttons on one unit
        public abstract bool HasGyro { get; }               // currently populates real gyr_g/acc_g data
        public virtual bool HasTouchpad => false;
        // Native coordinate extents belong to each controller definition even though gesture and
        // pointer behavior is shared. They are only consumed when HasTouchpad is true.
        protected virtual int TouchpadMaximumX => 0;
        protected virtual int TouchpadMaximumY => 0;
        public abstract bool HasAnalogTriggers { get; }     // triggers report a real analog value, not just a digital button bit
        public abstract bool UsesNintendoProtocol { get; }  // speaks the Joy-Con SPI/subcommand protocol (LED, rumble encoding, handshake)

        // Single source of truth for device-kind identity - see ServiceControlProtocol.cs for
        // the ControllerKind enum this returns (used by the remote-mode snapshot protocol).
        public abstract ControllerKind Kind { get; }

        // Device-agnostic battery%->Color mapping - promoted off Joycon (step 5) since nothing in
        // its body is Joy-Con-specific. Used by MainForm's remote-snapshot rendering.
        public static System.Drawing.Color GetBatteryColor(int battery) {
            switch (battery) {
                case 4:
                case 3:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                case 2:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.GreenYellow);
                case 1:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Orange);
                default:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
            }
        }

        // Monotonic creation order, assigned once in the constructor - used to decide which half
        // of a pair gets disconnected on join: whichever connected (and got its virtual
        // controller created) FIRST is the one most likely to already be the controller a
        // running game has locked onto, so joining always disconnects the newer one and keeps
        // the older one active, regardless of which physical Joycon you click to initiate the
        // join or which one a scan pass happens to enumerate first. Confirmed by testing:
        // disconnecting/suppressing the wrong half left
        // a game's already-locked-on slot silent while input went to a different slot it wasn't
        // watching.
        private static long nextVirtualControllerSequence = 0;
        public readonly long virtualControllerSequence = System.Threading.Interlocked.Increment(ref nextVirtualControllerSequence);

        // Kept public for compatibility with existing callers; this is now specifically the
        // activation latch for gyro-to-mouse. Stick outputs have independent latches below.
        public bool active_gyro = false;
        protected bool activeGyroLeftStick = false;
        protected bool activeGyroRightStick = false;
        private bool rumbleSuppressedByGyro = false;
        protected bool activeTouchpadMouse = false;
        protected bool activeTouchpadLeftStick = false;
        protected bool activeTouchpadRightStick = false;

        // Real elapsed time since the last DoThingsWithButtons call, used to scale raw angular
        // velocity (gyr_g) into a per-packet rotation amount - previously a hardcoded 0.015f
        // (assumed 15ms/~66Hz) regardless of how much time had actually passed. Report timing
        // isn't perfectly metronomic, especially over Bluetooth, so a fixed assumption scales
        // every frame's motion by however wrong that assumption happened to be that frame -
        // read as jittery/inconsistent speed rather than smooth motion, independent of anything
        // else about the connection or IMU filtering settings. -1 the first call so a long gap
        // before the very first packet (e.g. right after connecting) can't produce a huge dt.
        protected long lastDoThingsTimestamp = -1;

        // Each output tracks its own combo edge and toggle state. This lets the same profile use
        // gyro mouse and either virtual stick independently (or at the same time).
        protected bool prevActiveGyroMouseComboHeld = false;
        protected bool prevActiveGyroLeftStickComboHeld = false;
        protected bool prevActiveGyroRightStickComboHeld = false;
        protected bool gyroMouseEnabledThisReport = false;
        protected bool prevActiveTouchpadMouseComboHeld = false;
        protected bool touchpadMouseEnabledThisReport = false;
        protected bool prevActiveTouchpadLeftStickComboHeld = false;
        protected bool prevActiveTouchpadRightStickComboHeld = false;
        protected bool touchpadLeftStickEnabledThisReport = false;
        protected bool touchpadRightStickEnabledThisReport = false;

        // Same idea for reset_mouse - a one-shot action needs the rising edge only, or it would
        // keep re-centering every packet for as long as the bind stays held.
        protected bool prevResetMouseComboHeld = false;

        // Updated once per controller report in DoThingsWithButtons, then consumed by all three
        // IMU sub-samples from that report. Clenching suppresses pointer output without changing
        // active_gyro, so clicks and the rest of the active gyro-mouse state remain intact.
        protected bool gyroMouseClenched = false;

        // Gyro-only actions reserve their assigned physical controller buttons while gyro-mouse
        // is active. Keep this mask separate from buttons[]: special actions and UDP still need
        // the real input; only the virtual Xbox/DS4 report should consume it. The snapshot is
        // reused to avoid adding per-report garbage collection to the latency-sensitive path.
        protected static readonly string[] GyroOnlyBindKeys = {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro"
        };
        // One extra slot holds reset_mouse, which is also gyro-mouse-only under the same active
        // gate. All seven values come from the current logical controller profile.
        protected readonly string[] lastGyroOnlyBindValues =
            new string[GyroOnlyBindKeys.Length + 1];
        protected readonly bool[] gyroOnlyReservedButtons = new bool[ButtonCount];
        protected static readonly string[] TouchpadOnlyBindKeys = {
            "touchpad_left_click", "touchpad_right_click", "touchpad_center_click",
            "touchpad_scroll_up", "touchpad_scroll_down", "touchpad_pointer_lock",
        };
        protected readonly string[] lastTouchpadOnlyBindValues =
            new string[TouchpadOnlyBindKeys.Length];
        protected readonly bool[] touchpadOnlyReservedButtons = new bool[ButtonCount];
        protected readonly bool[] vigemButtons = new bool[ButtonCount];
        // SimulateContinous's joy_-to-joy_ remap output (e.g. "Touchpad click also acts as
        // PLUS") - deliberately never written into buttons[] itself, which combo/chord capture
        // and IsComboHeld/IsModifierHeld read as ground truth for what's actually physically
        // held. A button remapped to another button was leaking into chord capture as an extra,
        // never-pressed member (e.g. capturing HOME+TOUCHPAD as HOME+TOUCHPAD+PLUS when Touchpad
        // was mapped to PLUS) - the same bug class f398640 fixed for key_/mse_ output, just for
        // joy_-to-joy_ remaps, which never went through that fix's SimulateKeyClick/
        // SimulateButtonClick suppression at all. Reset once per report at the top of
        // DoThingsWithButtons, accumulated by every SimulateContinous call within that same
        // report, then folded into vigemButtons (virtual-controller-output only) by
        // GetButtonsForVigem - buttons[] itself is never touched.
        protected readonly bool[] continuousRemapButtons = new bool[ButtonCount];

        // Controller definitions decode their own report offsets, then submit this canonical
        // contact shape to the shared activation/actions/pointer pipeline below. Sony touchpads
        // use the same four-byte packed contact representation but place it at different offsets.
        protected struct TouchContact {
            internal bool Active;
            internal byte Id;
            internal int X;
            internal int Y;
        }

        protected TouchContact touchpadFirstContact;
        protected TouchContact touchpadSecondContact;
        protected bool touchpadContactActive;
        protected byte touchpadContactId;
        protected int touchpadLastX;
        protected int touchpadLastY;
        protected float touchpadMovementRemainderX;
        protected float touchpadMovementRemainderY;
        protected bool touchpadStickContactActive;
        protected byte touchpadStickContactId;
        protected int touchpadStickOriginX;
        protected int touchpadStickOriginY;
        protected bool touchpadTapTracking;
        protected bool touchpadTapRejected;
        protected byte touchpadTapContactId;
        protected int touchpadTapStartX;
        protected int touchpadTapStartY;
        protected long touchpadTapStartTimestamp;
        protected int touchpadPreviousContactCount;
        protected long touchpadLastTapTimestamp;
        protected int touchpadLastTapX;
        protected int touchpadLastTapY;
        protected bool touchpadTapHoldActive;
        protected byte touchpadTapHoldContactId;
        protected string touchpadTapHoldMapping;
        protected bool touchpadTwoFingerTapTracking;
        protected bool touchpadTwoFingerTapRejected;
        protected byte touchpadTwoFingerTapFirstId;
        protected byte touchpadTwoFingerTapSecondId;
        protected int touchpadTwoFingerTapFirstStartX;
        protected int touchpadTwoFingerTapFirstStartY;
        protected int touchpadTwoFingerTapSecondStartX;
        protected int touchpadTwoFingerTapSecondStartY;
        protected long touchpadTwoFingerTapStartTimestamp;
        protected bool touchpadTwoFingerScrolling;
        protected int touchpadTwoFingerScrollStartXSum;
        protected int touchpadTwoFingerScrollStartYSum;
        protected int touchpadTwoFingerScrollLastYSum;
        protected float touchpadTwoFingerScrollRemainder;
        // Gesture recognition happens after the raw report's ordinary button-edge commit. Hold
        // the resulting synthetic input across subsequent reports long enough for both runtime
        // chord evaluation and the service's 30 ms binding-capture poll to observe it.
        protected long touchpadTapInputUntilTimestamp;
        protected long touchpadTwoFingerTapInputUntilTimestamp;
        protected long touchpadTwoFingerScrollUpInputUntilTimestamp;
        protected long touchpadTwoFingerScrollDownInputUntilTimestamp;
        protected volatile bool touchpadColorWheelActiveThisReport;
        public bool TouchpadColorWheelActive => touchpadColorWheelActiveThisReport;
        private bool touchpadColorWheelToggled;
        private bool touchpadColorWheelExclusiveThisReport;
        private bool touchpadColorWheelAwaitingContactRelease;
        private bool touchpadColorWheelHasSelection;
        private bool touchpadColorWheelPublishPending;
        private byte touchpadColorWheelRed;
        private byte touchpadColorWheelGreen;
        private byte touchpadColorWheelBlue;
        private long touchpadColorWheelLastPublishTimestamp;

        // Both Sony pads use roughly 1,920 horizontal coordinate units. This permits normal
        // fingertip jitter while rejecting a deliberate drag before it can become a tap action.
        protected const int TouchpadTapMaxTravel = 48;
        protected const double TouchpadTapMaxSeconds = 0.25;
        protected const double TouchpadTapHoldDelaySeconds = 0.25;
        protected const int TouchpadDoubleTapMaxDistance = 96;
        protected const double TouchpadDoubleTapWindowSeconds = 0.35;
        protected const double TouchpadTapInputPulseSeconds = 0.10;
        // Sony touch coordinates span roughly 0-942 vertically. One conventional wheel detent
        // per 72 average contact units gives a controllable full-pad swipe without flooding the
        // desktop-input pipe with a wheel event for every HID report.
        protected const float TouchpadScrollUnitsPerTick = 72.0f;
        // A floating stick reaches full deflection after roughly one third of the pad's short
        // axis. The landing point is always neutral, so this is travel from that point rather
        // than a fixed absolute region on the controller.
        protected const float TouchpadStickFullDeflectionUnits = 320.0f;
        protected const float TouchpadStickDeadzoneUnits = 16.0f;
        // A lightbar update is real controller output, not just local pointer math. Thirty visual
        // updates per second stays smooth while avoiding one HID lighting write for every 250 Hz
        // touch report (especially important when Bluetooth audio is sharing the controller).
        protected const double TouchpadColorWheelUpdatesPerSecond = 30.0;
        // A fingertip's reported centroid cannot reliably reach the touch sensor's mathematical
        // perimeter. Pull full saturation inward so pure red/green/blue are comfortably
        // selectable while keeping the center-to-edge progression and hue angles unchanged.
        protected const double TouchpadColorWheelSaturationScale = 1.35;

        protected static TouchContact ReadPackedTouchContact(byte[] report, int offset) {
            byte status = report[offset];
            return new TouchContact {
                Active = (status & 0x80) == 0,
                Id = (byte)(status & 0x7F),
                X = report[offset + 1] | ((report[offset + 2] & 0x0F) << 8),
                Y = ((report[offset + 2] & 0xF0) >> 4) | (report[offset + 3] << 4),
            };
        }

        protected void SubmitTouchpadReport(TouchContact first, TouchContact second) {
            touchpadFirstContact = first;
            touchpadSecondContact = second;
        }

        protected void ResetTouchpadGestureState() {
            ReleaseTouchpadTapHold();
            touchpadTapTracking = false;
            touchpadTapRejected = false;
            touchpadPreviousContactCount = 0;
            touchpadLastTapTimestamp = 0;
            touchpadTapInputUntilTimestamp = 0;
            touchpadTwoFingerTapInputUntilTimestamp = 0;
            touchpadTwoFingerScrollUpInputUntilTimestamp = 0;
            touchpadTwoFingerScrollDownInputUntilTimestamp = 0;
            touchpadTwoFingerTapTracking = false;
            touchpadTwoFingerTapRejected = false;
            touchpadTwoFingerScrolling = false;
            touchpadTwoFingerScrollRemainder = 0.0f;
            touchpadStickContactActive = false;
            touchpadMovementRemainderX = 0.0f;
            touchpadMovementRemainderY = 0.0f;
        }

        // Wheel modes are deliberately split into a saved lighting mode and one binding. Wheel
        // owns the pad only while that binding is held. Wheel (toggle) latches the color-wheel
        // overlay on each rising edge but stays non-exclusive: every touch still reaches normal
        // mouse, gesture, click, and stick processing while also choosing a color. Angle chooses
        // hue, distance from center chooses saturation, and value stays at full brightness.
        private void UpdateTouchpadColorWheel() {
            bool supported = HasTouchpad &&
                TouchpadMaximumX > 0 && TouchpadMaximumY > 0 &&
                (Kind == ControllerKind.DualSense || Kind == ControllerKind.DualShock4);
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            string lightingMode = ControllerMappings.LightingMode(mappingProfileId);
            bool holdMode = supported && lightingMode == ControllerMappings.LightingModeWheel;
            bool toggleMode = supported &&
                lightingMode == ControllerMappings.LightingModeWheelToggle;
            bool held = UpdateDesktopActionComboHeld("color_wheel", holdMode || toggleMode,
                out bool wasHeld);

            if (toggleMode && held && !wasHeld)
                touchpadColorWheelToggled = !touchpadColorWheelToggled;
            else if (!toggleMode)
                touchpadColorWheelToggled = false;

            bool active = holdMode ? held : toggleMode && touchpadColorWheelToggled;
            bool wasActive = touchpadColorWheelActiveThisReport;

            if (!active) {
                if (wasActive)
                    FinishTouchpadColorWheel();
                else
                    touchpadColorWheelActiveThisReport = false;

                // A finger that selected a color still belongs to that wheel gesture until it
                // lifts. Without this drain state, releasing the activation binding first makes
                // the same still-down contact look like a brand-new tap/drag to normal touchpad
                // mouse handling.
                if (touchpadColorWheelAwaitingContactRelease &&
                        !touchpadFirstContact.Active && !touchpadSecondContact.Active) {
                    touchpadColorWheelAwaitingContactRelease = false;
                    ResetTouchpadGestureState();
                }
                return;
            }
            touchpadColorWheelActiveThisReport = true;
            touchpadColorWheelExclusiveThisReport = holdMode;
            touchpadColorWheelAwaitingContactRelease = false;

            if (!wasActive) {
                touchpadColorWheelHasSelection = false;
                touchpadColorWheelPublishPending = false;
                touchpadColorWheelLastPublishTimestamp = 0;
                if (holdMode) {
                    // Exclusive held mode consumes its activation chord and any in-progress
                    // gesture. Toggle mode deliberately skips all of this so color selection can
                    // run alongside ordinary touchpad mouse/click/gesture behavior.
                    if (buttons[(int)Button.TOUCHPAD])
                        ReleaseMappedHold(MappingValue("touchpad_click"));
                    ResetTouchpadGestureState();
                    ReleaseTouchpadMouseActions();
                }
            }

            TouchContact contact = touchpadFirstContact.Active
                ? touchpadFirstContact : touchpadSecondContact;
            if (!contact.Active)
                return;

            double halfWidth = TouchpadMaximumX / 2.0;
            double halfHeight = TouchpadMaximumY / 2.0;
            double x = (Math.Max(0, Math.Min(TouchpadMaximumX, contact.X)) - halfWidth) /
                halfWidth;
            double y = (halfHeight - Math.Max(0, Math.Min(TouchpadMaximumY, contact.Y))) /
                halfHeight;
            double hue = Math.Atan2(y, x) * 180.0 / Math.PI;
            if (hue < 0.0)
                hue += 360.0;
            double saturation = Math.Min(1.0,
                Math.Sqrt(x * x + y * y) * TouchpadColorWheelSaturationScale);
            HsvToRgb(hue, saturation, out byte red, out byte green, out byte blue);

            if (!touchpadColorWheelHasSelection || red != touchpadColorWheelRed ||
                    green != touchpadColorWheelGreen || blue != touchpadColorWheelBlue) {
                touchpadColorWheelRed = red;
                touchpadColorWheelGreen = green;
                touchpadColorWheelBlue = blue;
                touchpadColorWheelHasSelection = true;
                touchpadColorWheelPublishPending = true;
            }

            long timestamp = Stopwatch.GetTimestamp();
            if (touchpadColorWheelPublishPending &&
                    (touchpadColorWheelLastPublishTimestamp == 0 ||
                     (timestamp - touchpadColorWheelLastPublishTimestamp) /
                         (double)Stopwatch.Frequency >=
                         1.0 / TouchpadColorWheelUpdatesPerSecond)) {
                SetTouchpadColorWheelLight();
                touchpadColorWheelPublishPending = false;
                touchpadColorWheelLastPublishTimestamp = timestamp;
            }
        }

        private static void HsvToRgb(double hue, double saturation,
                out byte red, out byte green, out byte blue) {
            double chroma = saturation;
            double hueSection = hue / 60.0;
            double secondary = chroma * (1.0 - Math.Abs(hueSection % 2.0 - 1.0));
            double redBase = 0.0;
            double greenBase = 0.0;
            double blueBase = 0.0;
            if (hueSection < 1.0) {
                redBase = chroma; greenBase = secondary;
            } else if (hueSection < 2.0) {
                redBase = secondary; greenBase = chroma;
            } else if (hueSection < 3.0) {
                greenBase = chroma; blueBase = secondary;
            } else if (hueSection < 4.0) {
                greenBase = secondary; blueBase = chroma;
            } else if (hueSection < 5.0) {
                redBase = secondary; blueBase = chroma;
            } else {
                redBase = chroma; blueBase = secondary;
            }

            double match = 1.0 - chroma;
            red = (byte)Math.Round((redBase + match) * Byte.MaxValue);
            green = (byte)Math.Round((greenBase + match) * Byte.MaxValue);
            blue = (byte)Math.Round((blueBase + match) * Byte.MaxValue);
        }

        private void SetTouchpadColorWheelLight() {
            (byte red, byte green, byte blue) = ControllerMappings.ApplyLightBrightness(
                mappingProfileId, touchpadColorWheelRed, touchpadColorWheelGreen,
                touchpadColorWheelBlue);
            SetLightColor(red, green, blue);
        }

        // The latest preview is flushed and persisted exactly once at the end of the hold. This
        // keeps controller_mappings.xml off the high-rate touch-report path while ensuring the
        // color survives reconnects and application restarts.
        private void FinishTouchpadColorWheel() {
            touchpadColorWheelToggled = false;
            if (!touchpadColorWheelHasSelection) {
                touchpadColorWheelActiveThisReport = false;
                touchpadColorWheelExclusiveThisReport = false;
                return;
            }

            SetTouchpadColorWheelLight();
            string color = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                touchpadColorWheelRed, touchpadColorWheelGreen, touchpadColorWheelBlue);
            ControllerMappings.SetOptionValue(mappingProfileId, "LightColor", color);
            try {
                ControllerMappings.Save();
            } catch (IOException) {
                form?.AppendTextBox("Could not save the touchpad color-wheel selection.\r\n");
            } catch (UnauthorizedAccessException) {
                form?.AppendTextBox("Could not save the touchpad color-wheel selection.\r\n");
            }
            touchpadColorWheelHasSelection = false;
            touchpadColorWheelPublishPending = false;
            touchpadColorWheelLastPublishTimestamp = 0;
            touchpadColorWheelAwaitingContactRelease =
                touchpadColorWheelExclusiveThisReport &&
                (touchpadFirstContact.Active || touchpadSecondContact.Active);
            touchpadColorWheelActiveThisReport = false;
            touchpadColorWheelExclusiveThisReport = false;
        }

        private bool TouchpadColorWheelConsumesTouchpad =>
            touchpadColorWheelExclusiveThisReport || touchpadColorWheelAwaitingContactRelease;

        // A mouse-button binding (mse_N, alone or as part of a chord) on Tap fires on every
        // accidental brush of a touchpad the user has said is "so sensitive it's easy to fire
        // off the left click in games" - gating it on touchpadMouseEnabledThisReport reuses
        // exactly the check the "Mouse actions" section's own gyro mouse-button bindings already
        // use (SimulateMouseActionButton's enabled parameter, gyroMouseActionsEnabled), rather
        // than inventing a separate inhibit mechanism. Key/joystick targets are unaffected -
        // only mouse-button actions are sensitive to this, and only those were asked to be
        // inhibited outside mouse mode.
        private static bool MappingIncludesMouseAction(string mapping) {
            foreach (string part in mapping.Split('+')) {
                if (part.StartsWith("mse_"))
                    return true;
            }
            return false;
        }

        protected void TriggerTouchpadTap() {
            // Publish Tap as an input even when its output mapping is Disabled. Bind capture and
            // activation chords are input-side consumers and must not depend on what Tap itself
            // happens to be mapped to.
            touchpadTapInputUntilTimestamp = Stopwatch.GetTimestamp() +
                (long)(TouchpadTapInputPulseSeconds * Stopwatch.Frequency);

            string mapping = MappingValue("touchpad_tap");
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return;
            if (!touchpadMouseEnabledThisReport && MappingIncludesMouseAction(mapping))
                return;

            // Key/mouse targets are discrete clicks. Controller targets become a one-report
            // virtual-button pulse, using the same joy_N output convention as physical remaps.
            Simulate(mapping);
            foreach (string part in mapping.Split('+')) {
                if (!part.StartsWith("joy_"))
                    continue;

                int buttonIndex;
                if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                    buttonIndex >= 0 && buttonIndex < buttons.Length)
                    buttons[buttonIndex] = true;
            }
        }

        protected void TriggerTouchpadTwoFingerTap() {
            touchpadTwoFingerTapInputUntilTimestamp = Stopwatch.GetTimestamp() +
                (long)(TouchpadTapInputPulseSeconds * Stopwatch.Frequency);

            string mapping = MappingValue("touchpad_two_finger_tap");
            if (mapping == "default") {
                if (touchpadMouseEnabledThisReport && form != null)
                    form.SimulateButtonClick((int)WindowsInput.Events.ButtonCode.Right);
                return;
            }
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return;

            Simulate(mapping);
            foreach (string part in mapping.Split('+')) {
                if (!part.StartsWith("joy_"))
                    continue;

                int buttonIndex;
                if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                    buttonIndex >= 0 && buttonIndex < buttons.Length)
                    buttons[buttonIndex] = true;
            }
        }

        protected void TriggerTouchpadTwoFingerScroll(bool up) {
            long pulseUntil = Stopwatch.GetTimestamp() +
                (long)(TouchpadTapInputPulseSeconds * Stopwatch.Frequency);
            if (up)
                touchpadTwoFingerScrollUpInputUntilTimestamp = pulseUntil;
            else
                touchpadTwoFingerScrollDownInputUntilTimestamp = pulseUntil;

            string mappingKey = up
                ? "touchpad_two_finger_scroll_up"
                : "touchpad_two_finger_scroll_down";
            string mapping = MappingValue(mappingKey);
            if (mapping == "default") {
                if (touchpadMouseEnabledThisReport && form != null)
                    form.SimulateScroll(up);
                return;
            }
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return;

            // Like Tap, each wheel detent is also a first-class physical gesture. A custom
            // mapping replaces the default wheel output without hiding that gesture from bind
            // capture or preventing it from participating in a larger controller chord.
            Simulate(mapping);
            foreach (string part in mapping.Split('+')) {
                if (!part.StartsWith("joy_"))
                    continue;

                int buttonIndex;
                if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                    buttonIndex >= 0 && buttonIndex < buttons.Length)
                    buttons[buttonIndex] = true;
            }
        }

        protected bool BeginTouchpadTapHold(TouchContact contact) {
            string mapping = MappingValue("touchpad_tap");
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return false;
            if (!touchpadMouseEnabledThisReport && MappingIncludesMouseAction(mapping))
                return false;

            touchpadTapHoldActive = true;
            touchpadTapHoldContactId = contact.Id;
            touchpadTapHoldMapping = mapping;
            foreach (string part in mapping.Split('+')) {
                int code;
                if (part.StartsWith("key_") && Int32.TryParse(part.Substring(4), out code))
                    form.SimulateKeyHold(code);
                else if (part.StartsWith("mse_") && Int32.TryParse(part.Substring(4), out code))
                    form.SimulateButtonHold(code);
            }
            ApplyTouchpadTapHoldControllerButtons();
            return true;
        }

        protected void ApplyTouchpadTapHoldControllerButtons() {
            if (!touchpadTapHoldActive || String.IsNullOrEmpty(touchpadTapHoldMapping))
                return;

            foreach (string part in touchpadTapHoldMapping.Split('+')) {
                int buttonIndex;
                if (part.StartsWith("joy_") &&
                    Int32.TryParse(part.Substring(4), out buttonIndex) &&
                    buttonIndex >= 0 && buttonIndex < buttons.Length)
                    buttons[buttonIndex] = true;
            }
        }

        protected void ReleaseTouchpadTapHold() {
            if (!touchpadTapHoldActive)
                return;

            foreach (string part in (touchpadTapHoldMapping ?? String.Empty).Split('+')) {
                int code;
                if (part.StartsWith("key_") && Int32.TryParse(part.Substring(4), out code))
                    form.SimulateKeyRelease(code);
                else if (part.StartsWith("mse_") && Int32.TryParse(part.Substring(4), out code))
                    form.SimulateButtonRelease(code);
            }
            touchpadTapHoldActive = false;
            touchpadTapHoldMapping = null;
        }

        protected bool TryGetActiveTouchContact(byte id, out TouchContact contact) {
            if (touchpadFirstContact.Active && touchpadFirstContact.Id == id) {
                contact = touchpadFirstContact;
                return true;
            }
            if (touchpadSecondContact.Active && touchpadSecondContact.Id == id) {
                contact = touchpadSecondContact;
                return true;
            }

            contact = new TouchContact();
            return false;
        }

        protected void StartTwoFingerTap(long timestamp) {
            touchpadTwoFingerTapTracking = true;
            // A second finger must not turn an existing one-finger drag into a click when the
            // drag is released. It may still end that drag below, but this contact sequence is
            // permanently ineligible for the two-finger action.
            touchpadTwoFingerTapRejected = buttons[(int)Button.TOUCHPAD] ||
                touchpadTapHoldActive;
            touchpadTwoFingerTapFirstId = touchpadFirstContact.Id;
            touchpadTwoFingerTapSecondId = touchpadSecondContact.Id;

            // If the first finger arrived one report earlier, retain its real touchdown point
            // and time. That keeps a one-finger drag followed by a second finger from being
            // mistaken for a stationary two-finger tap merely because finger two arrived late.
            bool firstWasTracked = touchpadTapTracking &&
                touchpadTapContactId == touchpadFirstContact.Id;
            bool secondWasTracked = touchpadTapTracking &&
                touchpadTapContactId == touchpadSecondContact.Id;
            touchpadTwoFingerTapFirstStartX = firstWasTracked
                ? touchpadTapStartX : touchpadFirstContact.X;
            touchpadTwoFingerTapFirstStartY = firstWasTracked
                ? touchpadTapStartY : touchpadFirstContact.Y;
            touchpadTwoFingerTapSecondStartX = secondWasTracked
                ? touchpadTapStartX : touchpadSecondContact.X;
            touchpadTwoFingerTapSecondStartY = secondWasTracked
                ? touchpadTapStartY : touchpadSecondContact.Y;
            touchpadTwoFingerTapStartTimestamp = (firstWasTracked || secondWasTracked)
                ? touchpadTapStartTimestamp : timestamp;
            touchpadTwoFingerScrolling = false;
            touchpadTwoFingerScrollStartXSum =
                touchpadFirstContact.X + touchpadSecondContact.X;
            touchpadTwoFingerScrollStartYSum =
                touchpadFirstContact.Y + touchpadSecondContact.Y;
            touchpadTwoFingerScrollLastYSum = touchpadTwoFingerScrollStartYSum;
            touchpadTwoFingerScrollRemainder = 0.0f;

            if ((timestamp - touchpadTwoFingerTapStartTimestamp) /
                    (double)Stopwatch.Frequency > TouchpadTapMaxSeconds)
                touchpadTwoFingerTapRejected = true;

            // This contact sequence now belongs to the two-finger recognizer. The one-finger
            // recognizer stays alive only long enough to observe both contacts lifting, but may
            // no longer emit Tap or become a double-tap drag.
            touchpadTapRejected = true;
            touchpadLastTapTimestamp = 0;
        }

        protected void UpdateTwoFingerTap(long timestamp, int contactCount) {
            if (!touchpadTwoFingerTapTracking)
                return;

            TouchContact first;
            TouchContact second;
            bool firstActive = TryGetActiveTouchContact(
                touchpadTwoFingerTapFirstId, out first);
            bool secondActive = TryGetActiveTouchContact(
                touchpadTwoFingerTapSecondId, out second);

            // An active contact with neither original ID is a finger handoff/new gesture, not
            // the staggered lift of the same two fingers.
            int originalContactsActive = (firstActive ? 1 : 0) + (secondActive ? 1 : 0);
            if (contactCount > originalContactsActive || buttons[(int)Button.TOUCHPAD])
                touchpadTwoFingerTapRejected = true;

            if (firstActive) {
                int dx = first.X - touchpadTwoFingerTapFirstStartX;
                int dy = first.Y - touchpadTwoFingerTapFirstStartY;
                if (dx * dx + dy * dy > TouchpadTapMaxTravel * TouchpadTapMaxTravel)
                    touchpadTwoFingerTapRejected = true;
            }
            if (secondActive) {
                int dx = second.X - touchpadTwoFingerTapSecondStartX;
                int dy = second.Y - touchpadTwoFingerTapSecondStartY;
                if (dx * dx + dy * dy > TouchpadTapMaxTravel * TouchpadTapMaxTravel)
                    touchpadTwoFingerTapRejected = true;
            }

            bool twoFingerScrollEnabled =
                ProfileBoolOption("TouchpadTwoFingerScroll");
            if (!twoFingerScrollEnabled) {
                touchpadTwoFingerScrolling = false;
                touchpadTwoFingerScrollRemainder = 0.0f;
            } else if (firstActive && secondActive && !touchpadTwoFingerScrolling) {
                // Track the center of both contacts rather than either finger independently.
                // This recognizes parallel vertical travel while rejecting a pinch/spread as
                // scroll. Values are kept as sums to avoid throwing away half-unit movement.
                int centerDxSum = first.X + second.X - touchpadTwoFingerScrollStartXSum;
                int centerDySum = first.Y + second.Y - touchpadTwoFingerScrollStartYSum;
                int scrollStartThresholdSum = TouchpadTapMaxTravel * 2;
                if (Math.Abs(centerDySum) > scrollStartThresholdSum &&
                    Math.Abs(centerDySum) > Math.Abs(centerDxSum)) {
                    touchpadTwoFingerScrolling = true;
                    touchpadTwoFingerTapRejected = true;
                }
            }
            if (twoFingerScrollEnabled && firstActive && secondActive &&
                touchpadTwoFingerScrolling)
                UpdateTouchpadTwoFingerScroll(first, second);

            double elapsed = (timestamp - touchpadTwoFingerTapStartTimestamp) /
                             (double)Stopwatch.Frequency;
            if (elapsed > TouchpadTapMaxSeconds)
                touchpadTwoFingerTapRejected = true;

            // Fingers rarely leave on the exact same HID report. Wait until both original IDs
            // are up, accepting a brief one-finger tail, then emit one canonical gesture pulse.
            if (!firstActive && !secondActive) {
                if (!touchpadTwoFingerTapRejected)
                    TriggerTouchpadTwoFingerTap();

                touchpadTwoFingerTapTracking = false;
                touchpadTwoFingerTapRejected = false;
                touchpadTwoFingerScrolling = false;
                touchpadTwoFingerScrollRemainder = 0.0f;
            }
        }

        protected void ProcessTouchpadGestures() {
            int contactCount = (touchpadFirstContact.Active ? 1 : 0) +
                               (touchpadSecondContact.Active ? 1 : 0);
            long timestamp = Stopwatch.GetTimestamp();
            string tapHoldMode = ControllerMappings.OptionValue(
                mappingProfileId, "TouchpadTapAndHold");
            // Profiles written by the earlier Enabled/Disabled toggle remain valid.
            if (String.Equals(tapHoldMode, "true", StringComparison.OrdinalIgnoreCase))
                tapHoldMode = "hold";
            else if (String.Equals(tapHoldMode, "false", StringComparison.OrdinalIgnoreCase))
                tapHoldMode = "disabled";
            bool holdToDrag = tapHoldMode == "hold";
            bool doubleTapToDrag = tapHoldMode == "double_tap";

            if (!touchpadTwoFingerTapTracking &&
                touchpadPreviousContactCount < 2 && contactCount == 2)
                StartTwoFingerTap(timestamp);
            UpdateTwoFingerTap(timestamp, contactCount);

            if (!holdToDrag && !doubleTapToDrag) {
                ReleaseTouchpadTapHold();
                touchpadLastTapTimestamp = 0;
            }

            if (touchpadTapHoldActive) {
                bool heldContactActive =
                    (touchpadFirstContact.Active &&
                     touchpadFirstContact.Id == touchpadTapHoldContactId) ||
                    (touchpadSecondContact.Active &&
                     touchpadSecondContact.Id == touchpadTapHoldContactId);
                if (!heldContactActive || contactCount != 1 ||
                    buttons[(int)Button.TOUCHPAD]) {
                    ReleaseTouchpadTapHold();
                } else {
                    ApplyTouchpadTapHoldControllerButtons();
                    touchpadPreviousContactCount = contactCount;
                    return;
                }
            }

            if (!doubleTapToDrag) {
                touchpadLastTapTimestamp = 0;
            } else if (touchpadLastTapTimestamp != 0 &&
                (timestamp - touchpadLastTapTimestamp) / (double)Stopwatch.Frequency >
                    TouchpadDoubleTapWindowSeconds) {
                touchpadLastTapTimestamp = 0;
            }

            // A candidate begins only on a clean zero-to-one-finger transition. This prevents
            // lifting one finger after a two-finger gesture from turning the remaining finger
            // into a fresh tap candidate halfway through its contact.
            if (!touchpadTapTracking && touchpadPreviousContactCount == 0 && contactCount == 1) {
                TouchContact contact = touchpadFirstContact.Active
                    ? touchpadFirstContact : touchpadSecondContact;
                int doubleTapDx = contact.X - touchpadLastTapX;
                int doubleTapDy = contact.Y - touchpadLastTapY;
                bool beginDoubleTapHold = doubleTapToDrag && touchpadLastTapTimestamp != 0 &&
                    doubleTapDx * doubleTapDx + doubleTapDy * doubleTapDy <=
                        TouchpadDoubleTapMaxDistance * TouchpadDoubleTapMaxDistance &&
                    BeginTouchpadTapHold(contact);
                touchpadLastTapTimestamp = 0;
                if (beginDoubleTapHold) {
                    touchpadTapTracking = false;
                    touchpadPreviousContactCount = contactCount;
                    return;
                }

                touchpadTapTracking = true;
                touchpadTapRejected = false;
                touchpadTapContactId = contact.Id;
                touchpadTapStartX = contact.X;
                touchpadTapStartY = contact.Y;
                touchpadTapStartTimestamp = timestamp;
            }

            if (touchpadTapTracking) {
                TouchContact tracked = touchpadFirstContact;
                bool trackedActive = touchpadFirstContact.Active &&
                                     touchpadFirstContact.Id == touchpadTapContactId;
                if (!trackedActive && touchpadSecondContact.Active &&
                    touchpadSecondContact.Id == touchpadTapContactId) {
                    tracked = touchpadSecondContact;
                    trackedActive = true;
                }

                double elapsed = (timestamp - touchpadTapStartTimestamp) /
                                 (double)Stopwatch.Frequency;
                if (contactCount > 1 || buttons[(int)Button.TOUCHPAD])
                    touchpadTapRejected = true;

                if (trackedActive) {
                    int dx = tracked.X - touchpadTapStartX;
                    int dy = tracked.Y - touchpadTapStartY;
                    if (dx * dx + dy * dy > TouchpadTapMaxTravel * TouchpadTapMaxTravel)
                        touchpadTapRejected = true;

                    // A short contact still becomes the ordinary Tap action on lift. Keeping the
                    // same finger down past the threshold turns that pending click into a real
                    // held action, so subsequent pointer motion naturally becomes a drag.
                    if (holdToDrag && !touchpadTapRejected &&
                        elapsed >= TouchpadTapHoldDelaySeconds &&
                        BeginTouchpadTapHold(tracked)) {
                        touchpadTapTracking = false;
                        touchpadPreviousContactCount = contactCount;
                        return;
                    }
                } else if (contactCount > 0) {
                    // Contact ID replacement without the surface becoming empty is a handoff,
                    // not the end of a one-finger tap.
                    touchpadTapRejected = true;
                }

                if (contactCount == 0) {
                    if (!touchpadTapRejected && elapsed <= TouchpadTapMaxSeconds) {
                        TriggerTouchpadTap();
                        if (doubleTapToDrag) {
                            touchpadLastTapTimestamp = timestamp;
                            touchpadLastTapX = touchpadTapStartX;
                            touchpadLastTapY = touchpadTapStartY;
                        }
                    }
                    touchpadTapTracking = false;
                    touchpadTapRejected = false;
                }
            }

            touchpadPreviousContactCount = contactCount;
        }

        protected string MappingValue(string key) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            string value = ControllerMappings.Value(mappingProfileId, key);

            OnMappingValueResolved(key, value);

            return value;
        }

        // No-op by default; DualSenseController overrides this with a TEMPORARY diagnostic dump
        // (see DualSense.cs) - kept as a hook since LogDualSenseRawDump is DualSense-only.
        protected virtual void OnMappingValueResolved(string key, string value) { }

        protected bool swapAB => ProfileBoolOption("SwapAB");
        protected bool swapXY => ProfileBoolOption("SwapXY");

        protected bool ProfileBoolOption(string key) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            return ControllerMappings.BoolOption(mappingProfileId, key);
        }

        protected int ProfileIntOption(string key, int fallback = -1) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            return ControllerMappings.IntOption(mappingProfileId, key, fallback);
        }

        protected string ProfileStringOption(string key, string fallback) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);
            string value = ControllerMappings.OptionValue(mappingProfileId, key);
            return String.IsNullOrEmpty(value) ? fallback : value;
        }

        // Join/split changes which mapping profile this physical half belongs to. Release any
        // synthetic holds under the old profile before changing topology; otherwise pressing an
        // SL/SR mouse/key bind while joined and releasing it after a split would look up the new
        // solo bind for the release and could leave the old profile's key stuck down forever.
        protected void PrepareForMappingProfileChange() {
            if (form == null)
                return;

            ReleaseGyroMouseActions();
            ReleaseTouchpadMouseActions();
            FinishTouchpadColorWheel();
            if (HasTouchpad)
                ReleaseMappedHold(MappingValue("touchpad_click"));
            ReleaseMappedHold(MappingValue(isLeft ? "sl_l" : "sl_r"));
            ReleaseMappedHold(MappingValue(isLeft ? "sr_l" : "sr_r"));
            if (hasShaked)
                ReleaseMappedHold(MappingValue("shake"));

            hasShaked = false;
            mouse_toggle_btn.Clear();
            active_gyro = false;
            activeGyroLeftStick = false;
            activeGyroRightStick = false;
            activeTouchpadMouse = false;
            activeTouchpadLeftStick = false;
            activeTouchpadRightStick = false;
            prevActiveGyroMouseComboHeld = false;
            prevActiveGyroLeftStickComboHeld = false;
            prevActiveGyroRightStickComboHeld = false;
            prevActiveTouchpadMouseComboHeld = false;
            prevActiveTouchpadLeftStickComboHeld = false;
            prevActiveTouchpadRightStickComboHeld = false;
            gyroMouseEnabledThisReport = false;
            touchpadMouseEnabledThisReport = false;
            touchpadLeftStickEnabledThisReport = false;
            touchpadRightStickEnabledThisReport = false;
            touchpadColorWheelActiveThisReport = false;
            touchpadColorWheelExclusiveThisReport = false;
            touchpadColorWheelAwaitingContactRelease = false;
            touchpadColorWheelToggled = false;
            ResetTouchpadGestureState();
            gyroLeftStickActiveThisReport = false;
            gyroRightStickActiveThisReport = false;
            rumbleSuppressedByGyro = false;
            prevResetMouseComboHeld = false;
            gyroMouseClenched = false;
            gyroStickRatcheted = false;
        }

        protected void ReleaseMappedHold(string mapping) {
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return;

            // Simulate(mapping, click:false, up:true) already releases key_ parts
            // unconditionally, and mse_ parts too whenever DragToggle is off. The one gap is
            // mse_ under DragToggle: its hold branch only acts when !up (see Simulate's
            // dragToggle block above), so a toggled-on mouse hold needs forcing below regardless
            // of toggle state - mouse_toggle_btn itself doesn't need resetting here, the caller
            // (PrepareForMappingProfileChange) clears it right after this returns.
            Simulate(mapping, click: false, up: true);

            if (ProfileBoolOption("DragToggle")) {
                foreach (string part in mapping.Split('+')) {
                    int code;
                    if (part.StartsWith("mse_") && Int32.TryParse(part.Substring(4), out code))
                        form.SimulateButtonRelease(code);
                }
            }
        }

        // A bind is one or more "joy_N"/"key_N"/"mse_N" parts joined with "+" (a single part is
        // just a combo of one) - true only when every part is currently held, in the same
        // left-to-right press order they're written in for the joy_ parts, AND no other
        // controller button beyond those parts is also held.
        //
        // The exact-match half matters: without it, a two-button chord like SHOULDER2_1+HOME
        // reads as "held" even while the user is actually holding the three-button
        // HOME+SHOULDER_1+SHOULDER2_1 (a real superset) meant for a completely different binding,
        // since every part of the shorter combo is technically still down too.
        //
        // The order half matters too: HOME+A and A+HOME are meant to be different bindings, not
        // the same combo written twice. Order is derived from buttons_down_timestamp (already
        // tracked per button for the long-press/power-off logic above) rather than a separately
        // maintained press-order list - simultaneous presses (equal timestamps) are treated as
        // satisfying either order, rather than rejected outright.
        //
        // Controller parts check this Joycon's own buttons (and its pair partner's, if joined,
        // matching how every other joy_ bind here already treats a pair as one logical
        // controller); keyboard/mouse parts check InputState, fed from Program.OnKeyDown/OnKeyUp/
        // OnMouseButtonDown/OnMouseButtonUp - the same unified entry points that already work in
        // both GUI and service mode. Both the exact-match and ordering requirements are scoped to
        // controller buttons only (ButtonCount comfortably fits a ulong bitmask, so this costs
        // nothing extra per poll) - held keyboard/mouse input alongside a controller chord
        // doesn't disqualify it or need to slot into the same order, since those are a different
        // input domain with no equivalent per-key press-order tracking here.
        //
        // A mapping can also be several ","-separated alternative combos (Reassign.cs's
        // right-click-to-add-an-alternative capture) - true if ANY of them is currently held,
        // each checked independently by the same exact-match rules above (including "no other
        // controller button held" - a two-alternative bind doesn't get to treat the other
        // alternative's buttons as an allowed extra).
        protected bool IsComboHeld(string mapping) {
            foreach (string alternative in mapping.Split(',')) {
                if (IsSingleComboHeld(alternative))
                    return true;
            }
            return false;
        }

        private bool IsSingleComboHeld(string combo) {
            ulong comboButtonMask = 0;
            long previousDownTimestamp = long.MinValue;
            foreach (string part in combo.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int i = Int32.Parse(part.Substring(4));
                    bool heldHere = buttons[i];
                    bool heldByPartner = other != null && other != this && other.buttons[i];
                    if (!(heldHere || heldByPartner))
                        return false;

                    long downTimestamp = heldHere
                        ? buttons_down_timestamp[i]
                        : other.buttons_down_timestamp[i];
                    if (downTimestamp < previousDownTimestamp)
                        return false;
                    previousDownTimestamp = downTimestamp;

                    comboButtonMask |= 1UL << i;
                } else if (part.StartsWith("key_")) {
                    if (!InputState.IsKeyHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else if (part.StartsWith("mse_")) {
                    if (!InputState.IsMouseButtonHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else {
                    return false; // malformed/unknown part - fail closed rather than ignore it
                }
            }

            for (int i = 0; i < ButtonCount; i++) {
                bool heldHere = buttons[i] || (other != null && other != this && other.buttons[i]);
                if (heldHere && (comboButtonMask & (1UL << i)) == 0)
                    return false;
            }
            return true;
        }

        // "Modifier" binding (Reassign.cs's Controller functions section) - held to use this
        // button/combo purely as a chord for other bindings without also leaking button/stick
        // state to the virtual controller or moving the gyro mouse cursor while it's down. See
        // MapToXbox360Input/MapToDualShock4Input and MoveGyroMouseBy for where this is actually
        // enforced. Unbound ("0") by default, matching every other new Keys entry with no
        // LegacyValue migration.
        //
        // Deliberately NOT IsComboHeld(MappingValue("modifier")) - that now requires an exact
        // match (nothing else held), which is backwards for a modifier: the entire point is
        // staying "held" while other buttons join it for their own chords, so an exact-match
        // check would stop counting it as held the instant it's actually being used as one. What
        // it needs instead is a subset check (every modifier button down, extras allowed) plus
        // one extra rule the user asked for explicitly: it only counts if the modifier's own
        // buttons were ALL already down before anything else currently held - joining an
        // in-progress press doesn't retroactively turn it into "the modifier led this chord".
        // Reuses buttons_down_timestamp the same way IsComboHeld's own ordering does. Like every
        // other bind captured by Reassign, "," separates alternatives; each alternative must be
        // evaluated independently so adding a second Modifier bind cannot turn the entire value
        // into one malformed joy_ index and break gyro-mouse movement after activation.
        protected bool IsModifierHeld() {
            string mapping = MappingValue("modifier");
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return false;

            foreach (string alternative in mapping.Split(',')) {
                if (IsSingleModifierHeld(alternative))
                    return true;
            }
            return false;
        }

        private bool IsSingleModifierHeld(string combo) {
            ulong comboButtonMask = 0;
            long latestModifierDownTimestamp = long.MinValue;
            foreach (string part in combo.Split('+')) {
                if (!part.StartsWith("joy_"))
                    return false; // Modifier is controller-button-only - see Reassign.cs's menu.
                int i;
                if (!Int32.TryParse(part.Substring(4), out i) ||
                        i < 0 || i >= ButtonCount)
                    return false;
                bool heldHere = buttons[i];
                bool heldByPartner = other != null && other != this && other.buttons[i];
                if (!(heldHere || heldByPartner))
                    return false;

                long downTimestamp = heldHere ? buttons_down_timestamp[i] : other.buttons_down_timestamp[i];
                if (downTimestamp > latestModifierDownTimestamp)
                    latestModifierDownTimestamp = downTimestamp;
                comboButtonMask |= 1UL << i;
            }

            for (int i = 0; i < ButtonCount; i++) {
                if ((comboButtonMask & (1UL << i)) != 0)
                    continue; // one of the modifier's own buttons, not "something else"

                bool otherHeldHere = buttons[i];
                bool otherHeldByPartner = other != null && other != this && other.buttons[i];
                if (!(otherHeldHere || otherHeldByPartner))
                    continue;

                long otherDownTimestamp = otherHeldHere
                    ? buttons_down_timestamp[i]
                    : other.buttons_down_timestamp[i];
                if (otherDownTimestamp < latestModifierDownTimestamp)
                    return false; // this button was already down before the modifier finished
            }
            return true;
        }

        // Pointer/output activation mappings have three explicit states:
        //   always  - active without a bind
        //   0       - disabled
        //   combo   - controlled by the profile's hold/toggle preference
        // The old unbound value is migrated to "always" by ControllerMappings, so 0 can safely
        // mean disabled for every new output without unexpectedly enabling every output.
        protected bool UpdateOutputActivation(string key, ref bool toggledActive,
                                              ref bool previousComboHeld,
                                              out bool justEnabled) {
            string mapping = MappingValue(key);
            if (mapping == "always") {
                toggledActive = false;
                previousComboHeld = false;
                justEnabled = false; // always-on has no activation edge to recenter on
                return true;
            }

            if (String.IsNullOrEmpty(mapping) || mapping == "0") {
                toggledActive = false;
                previousComboHeld = false;
                justEnabled = false;
                return false;
            }

            bool wasEnabled = toggledActive;
            bool comboHeld = IsComboHeld(mapping);
            if (ProfileBoolOption("GyroHoldToggle")) {
                toggledActive = comboHeld;
            } else if (comboHeld && !previousComboHeld) {
                toggledActive = !toggledActive;
            }
            previousComboHeld = comboHeld;
            justEnabled = !wasEnabled && toggledActive;
            return toggledActive;
        }

        // Rebuild only when a bind actually changes. ControllerMappings is live-reloaded by the
        // service watcher, so this also picks up remote edits without reconnecting the controller.
        protected void RefreshGyroOnlyButtonReservations() {
            bool changed = false;
            for (int i = 0; i < GyroOnlyBindKeys.Length; i++) {
                string value = MappingValue(GyroOnlyBindKeys[i]);
                if (lastGyroOnlyBindValues[i] != value) {
                    lastGyroOnlyBindValues[i] = value;
                    changed = true;
                }
            }

            int resetMouseSlot = GyroOnlyBindKeys.Length;
            string resetMouseValue = MappingValue("reset_mouse");
            if (String.IsNullOrEmpty(resetMouseValue))
                resetMouseValue = "0";
            if (lastGyroOnlyBindValues[resetMouseSlot] != resetMouseValue) {
                lastGyroOnlyBindValues[resetMouseSlot] = resetMouseValue;
                changed = true;
            }

            if (!changed)
                return;

            Array.Clear(gyroOnlyReservedButtons, 0, gyroOnlyReservedButtons.Length);
            foreach (string value in lastGyroOnlyBindValues) {
                if (value == "0")
                    continue;

                // "," separates alternative combos (see IsComboHeld's own comment) - every button
                // from every alternative gets reserved here regardless of which one is actually
                // currently held, matching how a single combo's buttons are already reserved
                // whether-or-not that combo happens to be held on any given report.
                foreach (string part in value.Split('+', ',')) {
                    if (!part.StartsWith("joy_"))
                        continue; // keyboard/mouse combo members never enter ViGEm

                    int buttonIndex;
                    if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                        buttonIndex >= 0 && buttonIndex < gyroOnlyReservedButtons.Length)
                        gyroOnlyReservedButtons[buttonIndex] = true;
                }
            }
        }

        protected void RefreshTouchpadOnlyButtonReservations() {
            bool changed = false;
            for (int i = 0; i < TouchpadOnlyBindKeys.Length; i++) {
                string value = MappingValue(TouchpadOnlyBindKeys[i]);
                if (lastTouchpadOnlyBindValues[i] != value) {
                    lastTouchpadOnlyBindValues[i] = value;
                    changed = true;
                }
            }

            if (!changed)
                return;

            Array.Clear(touchpadOnlyReservedButtons, 0, touchpadOnlyReservedButtons.Length);
            foreach (string value in lastTouchpadOnlyBindValues) {
                if (String.IsNullOrEmpty(value) || value == "0")
                    continue;

                // See RefreshGyroOnlyButtonReservations' own comment on the "," alternatives split.
                foreach (string part in value.Split('+', ',')) {
                    if (!part.StartsWith("joy_"))
                        continue;

                    int buttonIndex;
                    if (Int32.TryParse(part.Substring(4), out buttonIndex) &&
                        buttonIndex >= 0 && buttonIndex < touchpadOnlyReservedButtons.Length)
                        touchpadOnlyReservedButtons[buttonIndex] = true;
                }
            }
        }

        protected bool IsTouchpadMovementLocked() {
            string pointerLock = MappingValue("touchpad_pointer_lock");
            bool bindingLocked = !String.IsNullOrEmpty(pointerLock) &&
                pointerLock != "0" && IsComboHeld(pointerLock);
            bool clickLocked = ProfileBoolOption("TouchpadClickMovementLockout") &&
                buttons[(int)Button.TOUCHPAD];
            return bindingLocked || clickLocked;
        }

        protected void UpdateTouchpadTwoFingerScroll(TouchContact first, TouchContact second) {
            int currentYSum = first.Y + second.Y;
            int deltaYSum = currentYSum - touchpadTwoFingerScrollLastYSum;
            touchpadTwoFingerScrollLastYSum = currentYSum;

            if (touchpadMouseEnabledThisReport && IsTouchpadMovementLocked()) {
                touchpadTwoFingerScrollRemainder = 0.0f;
                return;
            }

            // Divide the summed contact delta by two to get centroid travel. Moving fingers up
            // emits wheel-up; moving them down emits wheel-down, matching Windows touchpads.
            touchpadTwoFingerScrollRemainder += deltaYSum * 0.5f;
            while (touchpadTwoFingerScrollRemainder <= -TouchpadScrollUnitsPerTick) {
                TriggerTouchpadTwoFingerScroll(true);
                touchpadTwoFingerScrollRemainder += TouchpadScrollUnitsPerTick;
            }
            while (touchpadTwoFingerScrollRemainder >= TouchpadScrollUnitsPerTick) {
                TriggerTouchpadTwoFingerScroll(false);
                touchpadTwoFingerScrollRemainder -= TouchpadScrollUnitsPerTick;
            }
        }

        protected void ProcessTouchpadStick() {
            bool leftEnabled = HasTouchpad && touchpadLeftStickEnabledThisReport;
            bool rightEnabled = HasTouchpad && touchpadRightStickEnabledThisReport;
            int contactCount = (touchpadFirstContact.Active ? 1 : 0) +
                               (touchpadSecondContact.Active ? 1 : 0);
            if ((!leftEnabled && !rightEnabled) || contactCount != 1) {
                touchpadStickContactActive = false;
                return;
            }

            TouchContact current = touchpadFirstContact.Active
                ? touchpadFirstContact : touchpadSecondContact;
            if (!touchpadStickContactActive || current.Id != touchpadStickContactId) {
                // Floating origin: the first report for every new contact is always neutral,
                // regardless of where the finger landed on the physical pad.
                touchpadStickContactActive = true;
                touchpadStickContactId = current.Id;
                touchpadStickOriginX = current.X;
                touchpadStickOriginY = current.Y;
                return;
            }

            if (IsTouchpadMovementLocked()) {
                // Locking is a ratchet, not a pause: resume from the finger's current location
                // so releasing the lock cannot replay all movement made while it was clenched.
                touchpadStickOriginX = current.X;
                touchpadStickOriginY = current.Y;
                return;
            }

            float rawX = current.X - touchpadStickOriginX;
            float rawY = current.Y - touchpadStickOriginY;
            float distance = (float)Math.Sqrt(rawX * rawX + rawY * rawY);
            float outputX = 0.0f;
            float outputY = 0.0f;
            if (distance > TouchpadStickDeadzoneUnits) {
                float sensitivity = Math.Max(10, Math.Min(400,
                    ProfileIntOption("TouchpadStickSensitivity", 100))) / 100.0f;
                // Stick sensitivity controls how much physical travel is required, not the
                // maximum virtual deflection. Apply it before the clamp so (for example) 75%
                // can still reach a full stick after a longer swipe; the per-axis scales below
                // are the controls which intentionally cap deflection.
                float magnitude = Math.Min(1.0f,
                    (distance - TouchpadStickDeadzoneUnits) /
                    (TouchpadStickFullDeflectionUnits - TouchpadStickDeadzoneUnits) *
                    sensitivity);
                float normalizedX = rawX / distance * magnitude;
                // Touch coordinates grow downward; BetterJoy's canonical stick Y grows upward.
                float normalizedY = -rawY / distance * magnitude;
                int horizontalScale = Math.Max(0, Math.Min(100,
                    ProfileIntOption("TouchpadHorizontalScale", 100)));
                int verticalScale = Math.Max(0, Math.Min(100,
                    ProfileIntOption("TouchpadVerticalScale", 100)));
                outputX = Math.Max(-1.0f, Math.Min(1.0f, normalizedX)) *
                    horizontalScale / 100.0f;
                outputY = Math.Max(-1.0f, Math.Min(1.0f, normalizedY)) *
                    verticalScale / 100.0f;
            }

            // Preserve the physical stick and layer the floating touch stick onto it, matching
            // gyro-to-stick's additive behavior. The next HID report reparses the physical
            // values first, so lifting the finger removes this contribution immediately.
            if (leftEnabled) {
                stick[0] = Math.Max(-1.0f, Math.Min(1.0f, stick[0] + outputX));
                stick[1] = Math.Max(-1.0f, Math.Min(1.0f, stick[1] + outputY));
            }
            if (rightEnabled) {
                stick2[0] = Math.Max(-1.0f, Math.Min(1.0f, stick2[0] + outputX));
                stick2[1] = Math.Max(-1.0f, Math.Min(1.0f, stick2[1] + outputY));
            }
        }

        protected void ProcessTouchpadMouse() {
            bool enabled = HasTouchpad && touchpadMouseEnabledThisReport;
            if (!enabled) {
                ReleaseTouchpadMouseActions();
                return;
            }

            SimulateMouseActionButton("touchpad_left_click",
                (int)WindowsInput.Events.ButtonCode.Left, true);
            SimulateMouseActionButton("touchpad_right_click",
                (int)WindowsInput.Events.ButtonCode.Right, true);
            SimulateMouseActionButton("touchpad_center_click",
                (int)WindowsInput.Events.ButtonCode.Middle, true);
            SimulateMouseActionScroll("touchpad_scroll_up", true, true);
            SimulateMouseActionScroll("touchpad_scroll_down", false, true);

            // Until its centroid moves far enough, this remains a stationary tap candidate. If
            // it crosses the threshold, the recognizer permanently converts it to scrolling.
            // Either way, two contacts own the surface and must never also move the pointer.
            if (touchpadTwoFingerTapTracking) {
                touchpadContactActive = false;
                touchpadMovementRemainderX = 0.0f;
                touchpadMovementRemainderY = 0.0f;
                return;
            }

            TouchContact current = touchpadFirstContact;
            bool hasContact = touchpadFirstContact.Active;
            if (touchpadContactActive) {
                if (touchpadFirstContact.Active && touchpadFirstContact.Id == touchpadContactId) {
                    current = touchpadFirstContact;
                    hasContact = true;
                } else if (touchpadSecondContact.Active &&
                           touchpadSecondContact.Id == touchpadContactId) {
                    current = touchpadSecondContact;
                    hasContact = true;
                } else {
                    // The tracked finger lifted. A remaining finger becomes a new baseline in
                    // this report; never turn the absolute distance between fingers into motion.
                    hasContact = touchpadFirstContact.Active || touchpadSecondContact.Active;
                    current = touchpadFirstContact.Active
                        ? touchpadFirstContact : touchpadSecondContact;
                    touchpadContactActive = false;
                }
            } else if (!hasContact && touchpadSecondContact.Active) {
                current = touchpadSecondContact;
                hasContact = true;
            }

            if (!hasContact) {
                touchpadContactActive = false;
                touchpadMovementRemainderX = 0.0f;
                touchpadMovementRemainderY = 0.0f;
                return;
            }

            if (!touchpadContactActive || current.Id != touchpadContactId) {
                touchpadContactActive = true;
                touchpadContactId = current.Id;
                touchpadLastX = current.X;
                touchpadLastY = current.Y;
                touchpadMovementRemainderX = 0.0f;
                touchpadMovementRemainderY = 0.0f;
                return;
            }

            int dx = current.X - touchpadLastX;
            int dy = current.Y - touchpadLastY;
            touchpadLastX = current.X;
            touchpadLastY = current.Y;

            if (IsTouchpadMovementLocked()) {
                touchpadMovementRemainderX = 0.0f;
                touchpadMovementRemainderY = 0.0f;
                return;
            }

            int sensitivity = Math.Max(10, Math.Min(400,
                ProfileIntOption("TouchpadSensitivity", 100)));
            int horizontalScale = Math.Max(0, Math.Min(100,
                ProfileIntOption("TouchpadHorizontalScale", 100)));
            int verticalScale = Math.Max(0, Math.Min(100,
                ProfileIntOption("TouchpadVerticalScale", 100)));
            float scaledX = horizontalScale == 0
                ? 0.0f
                : dx * sensitivity * horizontalScale / 10000.0f +
                    touchpadMovementRemainderX;
            float scaledY = verticalScale == 0
                ? 0.0f
                : dy * sensitivity * verticalScale / 10000.0f +
                    touchpadMovementRemainderY;
            int moveX = (int)scaledX;
            int moveY = (int)scaledY;
            touchpadMovementRemainderX = scaledX - moveX;
            touchpadMovementRemainderY = scaledY - moveY;
            if ((moveX != 0 || moveY != 0) && form != null)
                form.SimulateMoveBy(moveX, moveY);
        }

        protected void ReleaseTouchpadMouseActions() {
            touchpadContactActive = false;
            touchpadMovementRemainderX = 0.0f;
            touchpadMovementRemainderY = 0.0f;
            SimulateMouseActionButton("touchpad_left_click",
                (int)WindowsInput.Events.ButtonCode.Left, false);
            SimulateMouseActionButton("touchpad_right_click",
                (int)WindowsInput.Events.ButtonCode.Right, false);
            SimulateMouseActionButton("touchpad_center_click",
                (int)WindowsInput.Events.ButtonCode.Middle, false);
            SimulateMouseActionScroll("touchpad_scroll_up", true, false);
            SimulateMouseActionScroll("touchpad_scroll_down", false, false);
        }

        protected bool OwnsGyroMouse() {
            return !SupportsPairing || other == null || other == this ||
                 (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"])
                    ? isLeft : !isLeft);
        }

        protected bool IsGyroMouseActive() {
            return OwnsGyroMouse() && gyroMouseEnabledThisReport;
        }

        // A joined pair's ViGEm target stays on whichever half connected first, while gyro-mouse
        // ownership is selected independently by handedness. Query both halves so consumption
        // follows the actual gyro owner instead of whichever object happens to emit the report.
        protected bool PairHasActiveGyroMouse() {
            return IsGyroMouseActive() ||
                (other != null && other != this && other.IsGyroMouseActive());
        }

        // Guide/PS is an output action, not a physical-controller button identity. "default"
        // retains the established per-controller mapping below (including the solo Joy-Con
        // layouts); any explicit bind can instead use one physical button, a controller chord,
        // or a keyboard/mouse combination. The old physical Home-button remap remains separate.
        protected bool ResolveVirtualGuideState(bool defaultState) {
            string mapping = MappingValue("guide");
            if (mapping == "default") {
                // Preserve the old rule: assigning the physical Home/PS button to something else
                // also suppressed its hardcoded Guide/PS output. Once Guide has an explicit bind,
                // the two mappings are intentionally independent.
                return MappingValue("home") == "0" && defaultState;
            }
            if (String.IsNullOrEmpty(mapping) || mapping == "0")
                return false;
            return IsComboHeld(mapping);
        }

        private bool TryGetHeldCustomGuideMapping(out string mapping) {
            mapping = MappingValue("guide");
            return !String.IsNullOrEmpty(mapping) && mapping != "0" && mapping != "default" &&
                   IsComboHeld(mapping);
        }

        // Bind capture deliberately uses the left Joycon as a joined pair's canonical Pro-style
        // view. If the ViGEm target survived on the right Joycon, that object's local button array
        // stores the same physical controls under the opposite-half indices. Translate the
        // canonical reserved index before filtering so join order cannot make us consume the
        // wrong physical control.
        protected int CanonicalButtonToLocalVigemIndex(int canonicalIndex) {
            if (!SupportsPairing || other == null || other == this || isLeft)
                return canonicalIndex;

            switch ((Button)canonicalIndex) {
                case Button.DPAD_DOWN: return (int)Button.B;
                case Button.DPAD_RIGHT: return (int)Button.A;
                case Button.DPAD_LEFT: return (int)Button.Y;
                case Button.DPAD_UP: return (int)Button.X;
                case Button.B: return (int)Button.DPAD_DOWN;
                case Button.A: return (int)Button.DPAD_RIGHT;
                case Button.Y: return (int)Button.DPAD_LEFT;
                case Button.X: return (int)Button.DPAD_UP;
                case Button.STICK: return (int)Button.STICK2;
                case Button.STICK2: return (int)Button.STICK;
                case Button.SHOULDER_1: return (int)Button.SHOULDER2_1;
                case Button.SHOULDER2_1: return (int)Button.SHOULDER_1;
                case Button.SHOULDER_2: return (int)Button.SHOULDER2_2;
                case Button.SHOULDER2_2: return (int)Button.SHOULDER_2;
                default: return canonicalIndex;
            }
        }

        protected bool[] GetButtonsForVigem() {
            bool gyroMouseConsumesButtons = PairHasActiveGyroMouse() &&
                ProfileBoolOption("GyroMouseInhibitButtons");
            bool touchpadMouseConsumesButtons = touchpadMouseEnabledThisReport &&
                ProfileBoolOption("TouchpadMouseInhibitButtons");
            bool customGuideHeld = TryGetHeldCustomGuideMapping(out string guideMapping);
            bool hasContinuousRemap = false;
            for (int i = 0; i < continuousRemapButtons.Length; i++) {
                if (continuousRemapButtons[i]) {
                    hasContinuousRemap = true;
                    break;
                }
            }
            if (!gyroMouseConsumesButtons && !touchpadMouseConsumesButtons &&
                    !TouchpadColorWheelConsumesTouchpad && !customGuideHeld && !hasContinuousRemap)
                return buttons;

            Array.Copy(buttons, vigemButtons, buttons.Length);
            if (hasContinuousRemap) {
                for (int i = 0; i < continuousRemapButtons.Length; i++) {
                    if (continuousRemapButtons[i])
                        vigemButtons[i] = true;
                }
            }
            if (gyroMouseConsumesButtons) {
                for (int canonicalIndex = 0;
                     canonicalIndex < gyroOnlyReservedButtons.Length;
                     canonicalIndex++) {
                    if (gyroOnlyReservedButtons[canonicalIndex])
                        vigemButtons[CanonicalButtonToLocalVigemIndex(canonicalIndex)] = false;
                }
            }
            if (touchpadMouseConsumesButtons) {
                for (int buttonIndex = 0;
                     buttonIndex < touchpadOnlyReservedButtons.Length;
                     buttonIndex++) {
                    if (touchpadOnlyReservedButtons[buttonIndex])
                        vigemButtons[buttonIndex] = false;
                }
            }
            if (TouchpadColorWheelConsumesTouchpad) {
                vigemButtons[(int)Button.TOUCHPAD] = false;
                vigemButtons[(int)Button.TOUCHPAD_TAP] = false;
                vigemButtons[(int)Button.TOUCHPAD_TWO_FINGER_TAP] = false;
                vigemButtons[(int)Button.TOUCHPAD_TWO_FINGER_SCROLL_UP] = false;
                vigemButtons[(int)Button.TOUCHPAD_TWO_FINGER_SCROLL_DOWN] = false;
            }

            // A controller button assigned to Guide/PS becomes Guide/PS instead of also leaking
            // its native virtual button. For chords, consume members only while the full chord is
            // held; pressing one member alone keeps its ordinary output. Every button from every
            // "," alternative (see IsComboHeld's own comment) is a suppression candidate here,
            // not just whichever one is actually satisfying customGuideHeld right now - harmless
            // for the others since a button that isn't actually held has nothing to suppress.
            if (customGuideHeld) {
                foreach (string part in guideMapping.Split('+', ',')) {
                    if (!part.StartsWith("joy_"))
                        continue;

                    int canonicalIndex;
                    if (Int32.TryParse(part.Substring(4), out canonicalIndex) &&
                        canonicalIndex >= 0 && canonicalIndex < vigemButtons.Length)
                        vigemButtons[CanonicalButtonToLocalVigemIndex(canonicalIndex)] = false;
                }
            }
            return vigemButtons;
        }

        protected readonly Stopwatch shakeTimer = Stopwatch.StartNew(); //Setup a timer for measuring shake in milliseconds
        protected long shakedTime = 0;
        protected bool hasShaked;
        protected bool shakeInputEnabled = Boolean.Parse(ConfigurationManager.AppSettings["EnableShakeInput"]);
        protected float shakeSensitivity = float.Parse(ConfigurationManager.AppSettings["ShakeInputSensitivity"]);
        protected float shakeDelay = float.Parse(ConfigurationManager.AppSettings["ShakeInputDelay"]);
        protected void DetectShake() {
            if (shakeInputEnabled) {
                long currentShakeTime = shakeTimer.ElapsedMilliseconds;

                // Shake detection logic
                bool isShaking = GetAccel().LengthSquared() >= shakeSensitivity;
                if (isShaking && currentShakeTime >= shakedTime + shakeDelay || isShaking && shakedTime == 0) {
                    shakedTime = currentShakeTime;
                    hasShaked = true;

                    // Mapped shake key down
                    Simulate(MappingValue("shake"), false, false);
                    DebugPrint("Shaked at time: " + shakedTime.ToString(), DebugType.SHAKE);
                }

                // If controller was shaked then release mapped key after a small delay to simulate a button press, then reset hasShaked
                if (hasShaked && currentShakeTime >= shakedTime + 10) {
                    // Mapped shake key up
                    Simulate(MappingValue("shake"), false, true);
                    DebugPrint("Shake completed", DebugType.SHAKE);
                    hasShaked = false;
                }

            } else {
                shakeTimer.Stop();
                return;
            }
        }

        // ConcurrentDictionary, not plain Dictionary: PrepareForMappingProfileChange (join/split
        // thread) calls Clear() on this while the poll thread concurrently reads/writes it via
        // Simulate() - a plain Dictionary under that access pattern can throw or corrupt its
        // bucket state.
        ConcurrentDictionary<int, bool> mouse_toggle_btn = new ConcurrentDictionary<int, bool>();

        // s can be a "+"-joined combo (see Reassign's combo capture) - here that means "simulate
        // all of these together", e.g. a capture bind of "key_17+key_67" presses Ctrl+C. Any
        // joy_ part is silently skipped - there's no "press another virtual controller button"
        // output, that's what SimulateContinous below is for instead.
        protected void Simulate(string s, bool click = true, bool up = false) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("key_")) {
                    int key = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateKeyClick(key);
                    } else {
                        if (up) {
                            form.SimulateKeyRelease(key);
                        } else {
                            form.SimulateKeyHold(key);
                        }
                    }
                } else if (part.StartsWith("mse_")) {
                    int button = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateButtonClick(button);
                    } else {
                        if (ProfileBoolOption("DragToggle")) {
                            if (!up) {
                                bool release;
                                mouse_toggle_btn.TryGetValue(button, out release);
                                if (release)
                                    form.SimulateButtonRelease(button);
                                else
                                    form.SimulateButtonHold(button);
                                mouse_toggle_btn[button] = !release;
                            }
                        } else {
                            if (up) {
                                form.SimulateButtonRelease(button);
                            } else {
                                form.SimulateButtonHold(button);
                            }
                        }
                    }
                }
            }
        }

        // For Joystick->Joystick inputs - s can likewise be a "+"-joined combo; every joy_ part
        // gets OR'd in (mse_/key_ parts are Simulate's job above, not this one's).
        // Writes to continuousRemapButtons, not buttons[] - see that field's own comment for why:
        // this is virtual-controller-output-only, not a real physical press.
        protected void SimulateContinous(int origin, string s) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int button = Int32.Parse(part.Substring(4));
                    continuousRemapButtons[button] |= buttons[origin];
                }
            }
        }

        protected bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        protected long lastDoubleClick = -1;

        protected int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        protected byte[] sliderVal = new byte[] { 0, 0 };

        // A/B/X/Y only ever reflect THIS device's own buttons on a Pro controller. A solo
        // Joycon's 4 primary buttons live at the DPAD_* indices instead (labeled a d-pad on the
        // left one, the same 4 buttons Nintendo prints as A/B/X/Y on the right) - and critically,
        // that's still true when joined: ProcessButtonsAndStick's buttons[A/B/X/Y] cross-
        // reference on a joined pair pulls from the OTHER Joycon's DPAD_* to build one merged
        // Pro-style layout for output, so it does NOT represent this specific physical device's
        // own buttons. Checking DPAD_* here instead, unconditionally for every non-Pro case,
        // is what actually stays correct for whichever single physical Joycon the caller means.
        protected bool CalibrationConfirmPressed() {
            if (!SupportsPairing)
                return buttons_down[(int)Button.A] || buttons_down[(int)Button.B] || buttons_down[(int)Button.X] || buttons_down[(int)Button.Y];
            return buttons_down[(int)Button.DPAD_UP] || buttons_down[(int)Button.DPAD_DOWN] || buttons_down[(int)Button.DPAD_LEFT] || buttons_down[(int)Button.DPAD_RIGHT];
        }

        // Discrete per-press step, matching SimulateMouseActionScroll's own "one tick per press"
        // model (GyroMath.cs) - a rising edge on the assigned combo nudges the profile's saved
        // Controller audio volume by deltaPercent, clamped to 0-100. Program.cs's existing ~2s
        // reconciliation loop is what actually applies the new value to the live audio stream,
        // the same as every other way of changing this same setting (the Volume dropdown, editing
        // another profile's copy of it, etc.) - no separate live-apply path needed here.
        private void AdjustControllerAudioVolumeOnPress(string configKey, int deltaPercent) {
            bool held = UpdateDesktopActionComboHeld(configKey, true, out bool wasHeld);
            if (!held || wasHeld)
                return;

            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            int current = ControllerMappings.IntOption(mappingProfileId, "ControllerAudioVolume", 75);
            int updated = Math.Max(0, Math.Min(100, current + deltaPercent));
            ControllerMappings.SetOptionValue(mappingProfileId, "ControllerAudioVolume", updated.ToString());
        }

        // One discrete 10% step per press. Default and OpenRGB are hard exclusions rather than
        // merely delayed updates: BetterJoy does not own lighting in either mode, so these binds
        // cannot alter their output or quietly queue a brightness change behind their back.
        private void AdjustLightBrightnessOnPress(string configKey, int deltaPercent) {
            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            string lightingMode = ControllerMappings.LightingMode(mappingProfileId);
            bool enabled = (Kind == ControllerKind.DualSense ||
                            Kind == ControllerKind.DualShock4) &&
                lightingMode != ControllerMappings.LightingModeDefault &&
                lightingMode != ControllerMappings.LightingModeOpenRgb;
            bool held = UpdateDesktopActionComboHeld(configKey, enabled, out bool wasHeld);
            if (!held || wasHeld)
                return;

            int current = ControllerMappings.LightBrightness(mappingProfileId);
            int updated = Math.Max(0, Math.Min(100, current + deltaPercent));
            if (updated == current)
                return;

            ControllerMappings.SetOptionValue(
                mappingProfileId, "LightBrightness", updated.ToString());
            JoyconManager.ApplyControllerProfileLighting(this, mappingProfileId);
            try {
                ControllerMappings.Save();
            } catch (IOException) {
                form?.AppendTextBox("Could not save the controller lighting brightness.\r\n");
            } catch (UnauthorizedAccessException) {
                form?.AppendTextBox("Could not save the controller lighting brightness.\r\n");
            }
        }

        // Advances optionKey to the next entry in modes (ControllerMappings.AdaptiveTriggerModes/
        // RumbleModes - the same list its own dropdown populates .Items from, so a mode added
        // there is automatically part of the cycle here too, nothing to keep in sync by hand).
        // Same discrete-per-press model as AdjustControllerAudioVolumeOnPress above. The dropdown
        // and this binding both just write the same option, so whichever one the user touched
        // last wins - no separate state of its own to track. currentValue reads through whatever
        // validating accessor the option already has (e.g. ControllerMappings.RumbleMode, which
        // still needs to migrate old plain-bool EnableRumble values) rather than a raw OptionValue
        // read, and is only called once an actual press is confirmed.
        private bool CycleOptionModeOnPress(string configKey, string optionKey,
                Func<string, string> currentValue, (string Value, string Label)[] modes) {
            bool held = UpdateDesktopActionComboHeld(configKey, true, out bool wasHeld);
            if (!held || wasHeld)
                return false;

            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            string next = ControllerMappings.NextCycleValue(modes, currentValue(mappingProfileId));
            ControllerMappings.SetOptionValue(mappingProfileId, optionKey, next);
            return true;
        }

        // Plain on/off flip, for bindings that toggle a bool option rather than cycle through a
        // list of modes - same discrete-per-press model as the two above.
        private void ToggleBoolOptionOnPress(string configKey, string optionKey) {
            bool held = UpdateDesktopActionComboHeld(configKey, true, out bool wasHeld);
            if (!held || wasHeld)
                return;

            if (mappingProfileId == null)
                mappingProfileId = ControllerMappings.ProfileIdFor(this);

            bool current = ControllerMappings.BoolOption(mappingProfileId, optionKey);
            ControllerMappings.SetOptionValue(mappingProfileId, optionKey,
                (!current).ToString().ToLowerInvariant());
        }

        // Hook for a subclass's own binding-driven button actions that need a private/internal
        // member only that subclass has (DualSenseController's built-in-mic mute toggle needs
        // SetMicrophoneMuted, which nothing outside that class should call directly) - the
        // combo-bound actions above don't need this since they only ever touch ControllerMappings
        // and this base class's own fields.
        protected virtual void DoDeviceSpecificButtonActions() { }

        protected void DoThingsWithButtons() {
            // Fresh per report - every SimulateContinous call below accumulates into this same
            // array for this one report, then GetButtonsForVigem folds it into vigemButtons.
            Array.Clear(continuousRemapButtons, 0, continuousRemapButtons.Length);

            // Checked first and returns early like the other button-driven side effects below -
            // a face button doubling as "confirm" only ever matters while a calibration prompt
            // is actually showing (PendingConfirmController names this exact controller only
            // then), so there's no real conflict with its normal mapped behavior the rest of the
            // time.
            if (CalibrationState.PendingConfirmController == this && CalibrationConfirmPressed()) {
                ReleaseGyroMouseActions();
                form.HandleCalibrationConfirm(this);
                return;
            }

            int powerOffButton = (int)((!SupportsPairing || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (ProfileBoolOption("HomeLongPowerOff") && buttons[powerOffButton]) {
                // Configurable rather than a fixed 2 seconds - too short for a profile that also
                // uses this same button as a modifier key, where any combo held a little long
                // would otherwise power the controller off. DualSense/DualShock4 have their own
                // ~5-second hardware timeout that powers them off regardless of this setting -
                // not something BetterJoy can override, so values past that are effectively
                // moot for those controllers specifically.
                int holdSeconds = Math.Max(1, Math.Min(10, ProfileIntOption("HomeLongPowerOffHoldSeconds", 2)));
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > holdSeconds * 1000.0) {
                    if (other != null) {
                        other.PrepareLongPressPowerOff();
                        other.PowerOff();
                    }

                    ReleaseGyroMouseActions();
                    PrepareLongPressPowerOff();
                    PowerOff();
                    return;
                }
            }

            if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1 && SupportsPairing) {
                if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                    ReleaseGyroMouseActions();
                    // is-check, not a bare cast: JoinOrSplitJoycon is JoyconController-typed
                    // (pairing is Joy-Con-only, see Controller.other's comment) - SupportsPairing
                    // being true above already guarantees this is a JoyconController today, but
                    // this stays correct (a silent no-op) rather than throwing if that ever changes.
                    if (this is JoyconController joyconForDoubleClick)
                        form.JoinOrSplitJoycon(joyconForDoubleClick); // trigger connection button click

                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                    return;
                }
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && SupportsPairing) {
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            }

            int powerOffInactivityMins = ProfileIntOption("PowerOffInactivity", -1);
            if (powerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > powerOffInactivityMins * 60 * 1000) {
                    if (other != null)
                        other.PowerOff();

                    ReleaseGyroMouseActions();
                    PowerOff();
                    return;
                }
            }

            TryAutoCalibrate();

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE])
                Simulate(MappingValue("capture"));
            if (buttons_down[(int)Button.HOME])
                Simulate(MappingValue("home"));
            if (buttons_down[(int)Button.MIC_MUTE])
                Simulate(MappingValue("mic_mute"));
            SimulateContinous((int)Button.CAPTURE, MappingValue("capture"));
            SimulateContinous((int)Button.HOME, MappingValue("home"));
            SimulateContinous((int)Button.MIC_MUTE, MappingValue("mic_mute"));

            // No controller has a dedicated volume button - these bind an arbitrary combo instead
            // of remapping a fixed one, the same "any combo, one discrete step per press" model
            // GyroMath.cs's SimulateMouseActionScroll uses for scroll_up/scroll_down.
            if (Kind == ControllerKind.DualSense || Kind == ControllerKind.DualShock4) {
                AdjustControllerAudioVolumeOnPress("volume_up", 10);
                AdjustControllerAudioVolumeOnPress("volume_down", -10);
            }
            if (Kind == ControllerKind.DualSense) {
                CycleOptionModeOnPress("lt_haptics", "AdaptiveTriggerModeLeft",
                    id => ControllerMappings.OptionValue(id, "AdaptiveTriggerModeLeft"),
                    ControllerMappings.AdaptiveTriggerModes);
                CycleOptionModeOnPress("rt_haptics", "AdaptiveTriggerModeRight",
                    id => ControllerMappings.OptionValue(id, "AdaptiveTriggerModeRight"),
                    ControllerMappings.AdaptiveTriggerModes);
            }
            // Rumble applies to every controller type, unlike the DualSense/DualShock4-only
            // bindings above.
            CycleOptionModeOnPress("toggle_haptics", "EnableRumble",
                ControllerMappings.RumbleMode, ControllerMappings.RumbleModes);
            // RGB lightbar - DualSense/DualShock4 only, matching Reassign.cs's own
            // hasConfigurableLight check. Cycle lighting reads the exact same LightingModes array
            // as the Device behavior dropdown, then immediately applies the new selection. If it
            // enters OpenRGB, resync both supported ownership paths: the built-in SDK server's
            // cached state and the existing raw-HID rescan nudge. LightingOff never touches the
            // user's actual LightColor setting (see ApplyControllerProfileLighting) - just a flag
            // applied on top, so there's nothing to save/restore, only to flip.
            if (Kind == ControllerKind.DualSense || Kind == ControllerKind.DualShock4) {
                if (CycleOptionModeOnPress("cycle_lighting", "LightingMode",
                        ControllerMappings.LightingMode, ControllerMappings.LightingModes)) {
                    // This binding changes the profile's actual lighting mode, not a temporary
                    // effect override. Persist the discrete press before the GUI/service config
                    // watcher can reload the old saved mode and let User color take over again.
                    try {
                        ControllerMappings.Save();
                    } catch (IOException) {
                        form?.AppendTextBox("Could not save the controller lighting mode.\r\n");
                    } catch (UnauthorizedAccessException) {
                        form?.AppendTextBox("Could not save the controller lighting mode.\r\n");
                    }
                    JoyconManager.ApplyControllerProfileLighting(this, mappingProfileId);
                    if (ControllerMappings.LightingMode(mappingProfileId) ==
                            ControllerMappings.LightingModeOpenRgb) {
                        OpenRgbServer.ApplyCachedColorToEligibleControllers();
                        OpenRgbRescan.RequestRescan();
                    }
                }
                ToggleBoolOptionOnPress("toggle_lighting", "LightingOff");
            }
            AdjustLightBrightnessOnPress("brightness_up", 10);
            AdjustLightBrightnessOnPress("brightness_down", -10);
            UpdateTouchpadColorWheel();
            DoDeviceSpecificButtonActions();

            if (HasTouchpad && !TouchpadColorWheelConsumesTouchpad) {
                if (buttons_down[(int)Button.TOUCHPAD])
                    Simulate(MappingValue("touchpad_click"), false, false);
                if (buttons_up[(int)Button.TOUCHPAD])
                    Simulate(MappingValue("touchpad_click"), false, true);
                SimulateContinous((int)Button.TOUCHPAD, MappingValue("touchpad_click"));
            }

            if (isLeft) {
                if (buttons_down[(int)Button.SL])
                    Simulate(MappingValue("sl_l"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(MappingValue("sl_l"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(MappingValue("sr_l"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(MappingValue("sr_l"), false, true);

                SimulateContinous((int)Button.SL, MappingValue("sl_l"));
                SimulateContinous((int)Button.SR, MappingValue("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL])
                    Simulate(MappingValue("sl_r"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(MappingValue("sl_r"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(MappingValue("sr_r"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(MappingValue("sr_r"), false, true);

                SimulateContinous((int)Button.SL, MappingValue("sl_r"));
                SimulateContinous((int)Button.SR, MappingValue("sr_r"));
            }

            // Filtered IMU data
            this.cur_rotation = AHRS.GetEulerAngles();

            long nowTimestamp = Stopwatch.GetTimestamp();
            float dt = lastDoThingsTimestamp < 0
                ? 0.015f // no prior packet to measure from yet - same assumption this always used
                : (float)((nowTimestamp - lastDoThingsTimestamp) / (double)Stopwatch.Frequency);
            lastDoThingsTimestamp = nowTimestamp;

            // Evaluate the gyro and touchpad profile outputs independently. A real gyro-mouse edge
            // recenters; Always Active intentionally has no edge, matching the old unbound
            // activation behavior.
            bool gyroMouseJustEnabled;
            gyroMouseEnabledThisReport = UpdateOutputActivation(
                "active_gyro_mouse", ref active_gyro,
                ref prevActiveGyroMouseComboHeld, out gyroMouseJustEnabled);
            bool gyroLeftStickJustEnabled;
            gyroLeftStickActiveThisReport = UpdateOutputActivation(
                "active_gyro_left_stick", ref activeGyroLeftStick,
                ref prevActiveGyroLeftStickComboHeld, out gyroLeftStickJustEnabled);
            bool gyroRightStickJustEnabled;
            gyroRightStickActiveThisReport = UpdateOutputActivation(
                "active_gyro_right_stick", ref activeGyroRightStick,
                ref prevActiveGyroRightStickComboHeld, out gyroRightStickJustEnabled);
            UpdateGyroRumbleSuppression();
            bool touchpadMouseJustEnabled = false;
            touchpadMouseEnabledThisReport = HasTouchpad && UpdateOutputActivation(
                "active_touchpad_mouse", ref activeTouchpadMouse,
                ref prevActiveTouchpadMouseComboHeld, out touchpadMouseJustEnabled);
            bool touchpadLeftStickJustEnabled;
            touchpadLeftStickEnabledThisReport = HasTouchpad && UpdateOutputActivation(
                "active_touchpad_left_stick", ref activeTouchpadLeftStick,
                ref prevActiveTouchpadLeftStickComboHeld, out touchpadLeftStickJustEnabled);
            bool touchpadRightStickJustEnabled;
            touchpadRightStickEnabledThisReport = HasTouchpad && UpdateOutputActivation(
                "active_touchpad_right_stick", ref activeTouchpadRightStick,
                ref prevActiveTouchpadRightStickComboHeld, out touchpadRightStickJustEnabled);
            gyroStickReportDt = dt;

            RefreshGyroOnlyButtonReservations();
            if (HasTouchpad)
                RefreshTouchpadOnlyButtonReservations();

            if (HasTouchpad && !TouchpadColorWheelConsumesTouchpad)
                ProcessTouchpadGestures();

            bool ownsGyroMouse = OwnsGyroMouse();
            bool gyroMouseActionsEnabled = ownsGyroMouse && gyroMouseEnabledThisReport;
            gyroMouseJustEnabled = ownsGyroMouse && gyroMouseJustEnabled;

            string clenchGyroVal = MappingValue("clench_gyro");
            gyroMouseClenched = gyroMouseActionsEnabled && clenchGyroVal != "0" &&
                IsComboHeld(clenchGyroVal);

            string ratchetGyroVal = MappingValue("ratchet_gyro");
            gyroStickRatcheted = (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport) &&
                ratchetGyroVal != "0" && IsComboHeld(ratchetGyroVal);

            // "Re-Centre Gyro" is a one-shot orientation operation, not merely a request to move
            // the Windows pointer. Apply it before sliders/stick/mouse consume this packet so the
            // pose held at the rising edge is neutral immediately. The legacy config key remains
            // reset_mouse for compatibility. Keep tracking the bind edge while gyro-mouse is
            // inactive, but do not let it reset orientation or move the pointer unless this
            // controller currently owns active gyro-mouse output.
            string resetMouseVal = MappingValue("reset_mouse");
            bool manualRecenterRequested = false;
            bool gyroStickManualRecenterRequested = false;
            if (resetMouseVal != "0") {
                bool resetMouseHeld = IsComboHeld(resetMouseVal);
                bool resetMouseRisingEdge = resetMouseHeld && !prevResetMouseComboHeld;
                manualRecenterRequested = gyroMouseActionsEnabled && resetMouseRisingEdge;
                // Same bind, independently gated: lets a controller using gyro-stick only (no
                // gyro-mouse output at all) still have a way to declare its current pose neutral,
                // without requiring a second bind just for Absolute/Hybrid stick modes. Mode is
                // per-stick, so each side only requests a recenter under its own mode/active state.
                gyroStickManualRecenterRequested = UseFilteredIMU && resetMouseRisingEdge &&
                    ((gyroLeftStickActiveThisReport && IsAbsoluteOrHybridGyroStickMode(GyroStickModeLeft)) ||
                     (gyroRightStickActiveThisReport && IsAbsoluteOrHybridGyroStickMode(GyroStickModeRight)));
                prevResetMouseComboHeld = resetMouseHeld;
            } else {
                prevResetMouseComboHeld = false;
            }

            // Gyro-stick's own activation edge also recenters when Absolute/Hybrid mode is
            // configured, mirroring gyro-mouse's auto-recenter-on-activation below - Absolute
            // tilt output is only meaningful relative to a neutral pose, so establishing one
            // automatically on activation (rather than requiring "Re-center gyro" to be bound)
            // matches how gyro-mouse already behaves. Checked independently per stick since mode
            // is now per-stick.
            bool gyroStickActivationRecenter = UseFilteredIMU &&
                ((gyroLeftStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeLeft)) ||
                 (gyroRightStickJustEnabled && IsAbsoluteOrHybridGyroStickMode(GyroStickModeRight)));

            // Enabling gyro-mouse establishes both a fresh desktop origin and a fresh orientation
            // frame. If the manual recenter bind rises on that same report, perform the operation
            // only once. Gyro-stick-only recenters (Absolute/Hybrid) share the same orientation
            // reset but must never move the mouse pointer, so SimulateMoveToScreenCenter stays
            // gated on only the original two conditions.
            if (gyroMouseJustEnabled || manualRecenterRequested ||
                gyroStickActivationRecenter || gyroStickManualRecenterRequested) {
                if (gyroMouseJustEnabled || manualRecenterRequested)
                    form.SimulateMoveToScreenCenter();

                RecenterGyro();
                dt = 0.0f;
                LogGyroMouseDiagnosticMarker(gyroMouseJustEnabled ? "GYRO ENABLED"
                    : (manualRecenterRequested ? "RESET" : "STICK RECENTER"));
            }

            if (GyroAnalogSliders && (other != null || HasDualSticks)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Controller left = isLeft ? this : (HasDualSticks ? this : this.other); Controller right = !isLeft ? this : (HasDualSticks ? this : this.other);

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            if (!UseFilteredIMU &&
                (gyroLeftStickActiveThisReport || gyroRightStickActiveThisReport)) {
                float dx = 0.0f;
                float dy = 0.0f;
                if (!gyroStickRatcheted) {
                    dx = GyroStickSensitivityX * (gyr_g.Z * dt); // yaw
                    dy = -GyroStickSensitivityY * (gyr_g.Y * dt); // pitch
                }
                float[] diagnosticStick = gyroLeftStickActiveThisReport ? stick : stick2;
                float diagnosticPhysicalX = diagnosticStick[0];
                float diagnosticPhysicalY = diagnosticStick[1];

                float diagnosticDx = dx;
                float diagnosticDy = dy;
                if (gyroLeftStickActiveThisReport) {
                    diagnosticDx = ApplyDeflectionLimits(dx, GyroStickMinDeflectionXLeft, GyroStickMaxDeflectionXLeft);
                    diagnosticDy = ApplyDeflectionLimits(dy, GyroStickMinDeflectionYLeft, GyroStickMaxDeflectionYLeft);
                    ApplyGyroToStick(stick, true, diagnosticDx, diagnosticDy);
                }
                if (gyroRightStickActiveThisReport) {
                    diagnosticDx = ApplyDeflectionLimits(dx, GyroStickMinDeflectionXRight, GyroStickMaxDeflectionXRight);
                    diagnosticDy = ApplyDeflectionLimits(dy, GyroStickMinDeflectionYRight, GyroStickMaxDeflectionYRight);
                    ApplyGyroToStick(stick2, false, diagnosticDx, diagnosticDy);
                }

                CaptureGyroStickDiagnosticOutput(true, dt,
                    diagnosticPhysicalX, diagnosticPhysicalY, diagnosticDx, diagnosticDy,
                    diagnosticStick[0], diagnosticStick[1]);
            }

            // Movement itself is applied per IMU sub-sample in ProcessGyroMouseSample. Reconcile
            // button state on every controller report, including reports where gyro-mouse is
            // inactive or this half no longer owns it. Otherwise leaving either condition while
            // a synthetic button is down skips the only path that could send its matching up.
            SimulateMouseActionButton("left_click", (int)WindowsInput.Events.ButtonCode.Left,
                                      gyroMouseActionsEnabled);
            SimulateMouseActionButton("right_click", (int)WindowsInput.Events.ButtonCode.Right,
                                      gyroMouseActionsEnabled);
            SimulateMouseActionButton("center_click", (int)WindowsInput.Events.ButtonCode.Middle,
                                      gyroMouseActionsEnabled);
            SimulateMouseActionScroll("scroll_up", true, gyroMouseActionsEnabled);
            SimulateMouseActionScroll("scroll_down", false, gyroMouseActionsEnabled);
            if (TouchpadColorWheelConsumesTouchpad) {
                touchpadStickContactActive = false;
                ReleaseTouchpadMouseActions();
            } else {
                ProcessTouchpadStick();
                ProcessTouchpadMouse();
            }
        }

        protected static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        protected static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        // Raw 0-255 analog L2/R2 - only ever meaningful when HasAnalogTriggers is true (DualSense
        // today). No-op default returning a neutral zero pair; Joycon overrides this returning its
        // own triggerVal field (still Joycon-only/DualSense-flag-specific pending step 4's later
        // phases).
        protected virtual byte[] TriggerVal => new byte[] { 0, 0 };

        protected static OutputControllerXbox360InputState MapToXbox360Input(Controller input) {
            // A default struct is already the neutral/centered state here - every axis is a
            // signed short (0 = centered), every trigger/button already false/zero. See
            // IsModifierHeld's own comment. Guide/PS is still resolved though: it's an explicit
            // chord binding (ResolveVirtualGuideState), and the entire point of Modifier is to
            // gate raw button/stick passthrough while letting chords that use it as a prefix
            // still fire - defaultState is forced false so the *unbound* Home-passthrough rule
            // stays suppressed, only an explicit "guide" mapping can still produce output here.
            if (input.IsModifierHeld()) {
                return new OutputControllerXbox360InputState {
                    guide = input.ResolveVirtualGuideState(false),
                };
            }

            var output = new OutputControllerXbox360InputState();


            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isLeft = input.isLeft;
            var isSnes = input.Kind == ControllerKind.Snes;
            var is64 = input.Kind == ControllerKind.N64;
            var hasDualSticks = input.HasDualSticks;
            var hasAnalogTriggers = input.HasAnalogTriggers;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.GetButtonsForVigem();
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.axis_right_x = (short) ((buttons[(int)Button.X] ? Int16.MinValue : 0) + (buttons[(int)Button.MINUS] ? Int16.MaxValue : 0));
                output.axis_right_y = (short) ((buttons[(int)Button.SHOULDER2_2] ? Int16.MinValue: 0) + (buttons[(int)Button.Y] ? Int16.MaxValue: 0));

                // N64's stick-drift-tracking calibration (minX/maxX/minY/maxY) is N64-only state
                // that stays on N64Controller - is64 being true already guarantees input actually
                // is one (no other Kind ever reports N64), so this cast is safe.
                var n64Stick = N64Controller.Getn64StickValues((N64Controller)input);

                output.axis_left_x = CastStickValue(n64Stick[0]);
                output.axis_left_y = CastStickValue(n64Stick[1]);

                output.start = buttons[(int)Button.PLUS];
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);

                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];
                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.guide = buttons[(int)Button.HOME];

            }
            else if (hasDualSticks) {
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.y = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.x = buttons[(int)(!swapXY ? Button.Y : Button.X)];

                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];

                output.back = buttons[(int)Button.MINUS];
                output.start = buttons[(int)Button.PLUS];
                output.guide = buttons[(int)Button.HOME];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.thumb_stick_left = buttons[(int)Button.STICK];
                output.thumb_stick_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];

                    output.dpad_up = buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)];
                    output.dpad_down = buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)];
                    output.dpad_left = buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)];
                    output.dpad_right = buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)];

                    output.back = buttons[(int)Button.MINUS];
                    output.start = buttons[(int)Button.PLUS];
                    output.guide = buttons[(int)Button.HOME];

                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];

                    output.thumb_stick_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_stick_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.back = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.start = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_stick_left = buttons[(int)Button.STICK];
                }
            }

            output.guide = input.ResolveVirtualGuideState(output.guide);

            if (!(isSnes || is64)) {
                if (other != null || hasDualSticks) { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (hasAnalogTriggers) {
                    // A DualSense's L2/R2 are genuinely analog, unlike Joy-Con/Pro (which have no
                    // trigger sensor at all and only ever derive a digital 0-or-max value from a
                    // button bit below) - pass the real raw value straight through.
                    output.trigger_left = input.TriggerVal[0];
                    output.trigger_right = input.TriggerVal[1];
                } else if (other != null || hasDualSticks) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Controller input) {
            // Unlike Xbox360's signed-short axes, DualShock4's are unsigned bytes centered at 128
            // - a bare default struct would report every stick fully deflected toward 0, not
            // centered, so the neutral state needs to say so explicitly. See IsModifierHeld's own
            // comment. ps is still resolved for the same reason MapToXbox360Input resolves guide -
            // an explicit chord binding, not raw passthrough, so Modifier shouldn't block it.
            if (input.IsModifierHeld()) {
                return new OutputControllerDualShock4InputState {
                    thumb_left_x = 128,
                    thumb_left_y = 128,
                    thumb_right_x = 128,
                    thumb_right_y = 128,
                    dPad = DpadDirection.None,
                    ps = input.ResolveVirtualGuideState(false),
                };
            }

            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isLeft = input.isLeft;
            var isSnes = input.Kind == ControllerKind.Snes;
            var is64 = input.Kind == ControllerKind.N64;
            var hasDualSticks = input.HasDualSticks;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.GetButtonsForVigem();
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.thumb_right_x = (byte) ((buttons[(int)Button.X] ? Byte.MinValue : 0) + (buttons[(int)Button.MINUS] ? Byte.MaxValue : 0));
                output.thumb_right_y = (byte) ((buttons[(int)Button.SHOULDER2_2] ? Byte.MinValue: 0) + (buttons[(int)Button.Y] ? Byte.MaxValue: 0));

                output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);

                output.options = buttons[(int)Button.PLUS];
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = buttons[(int)Button.SHOULDER_2];
                output.trigger_right = buttons[(int)Button.STICK];
                output.trigger_left_value = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;                
            }

            if (hasDualSticks) {
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.triangle = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.square = buttons[(int)(!swapXY ? Button.Y : Button.X)];


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;

                output.share = buttons[(int)Button.CAPTURE];
                output.options = buttons[(int)Button.PLUS];
                output.ps = buttons[(int)Button.HOME];
                output.touchpad = buttons[(int)(input.HasTouchpad
                    ? Button.TOUCHPAD : Button.MINUS)];
                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];
                output.thumb_left = buttons[(int)Button.STICK];
                output.thumb_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Northwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Northeast;
                        else
                            output.dPad = DpadDirection.North;
                    else if (buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Southwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Southeast;
                        else
                            output.dPad = DpadDirection.South;
                    else if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                        output.dPad = DpadDirection.West;
                    else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                        output.dPad = DpadDirection.East;

                    output.share = buttons[(int)Button.CAPTURE];
                    output.options = buttons[(int)Button.PLUS];
                    output.ps = buttons[(int)Button.HOME];
                    output.touchpad = buttons[(int)Button.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.ps = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.options = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_left = buttons[(int)Button.STICK];
                }
            }

            output.ps = input.ResolveVirtualGuideState(output.ps);

            if (!(isSnes || is64)) {
                if (other != null || hasDualSticks) { // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (other != null || hasDualSticks) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;
            }

            return output;
        }

    }
}
