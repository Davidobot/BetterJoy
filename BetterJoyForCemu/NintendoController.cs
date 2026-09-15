using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    // Shared SPI/subcommand protocol layer for every Joy-Con-family device type (step 5 of
    // DOCS/CONTROLLERS-REFACTOR.md, mirroring step 2's Controller extraction one level deeper).
    // Abstract - never instantiated directly, only through a concrete leaf type - even though
    // every one of Controller's abstract members is already fully implemented here using the
    // isPro/isSnes/is64 flags below. Splitting those flags into real leaf types
    // (JoyconController/ProController/SnesController/N64Controller) is deferred to a later
    // sub-step; this one is a pure mechanical move with Joycon left as the sole remaining
    // subclass, zero behavior change.
    public abstract class NintendoController : Controller {
        public bool isPro = false;
        public bool isSnes = false;
        public bool is64 = false;

        // Capability properties - step 1 of DOCS/CONTROLLERS-REFACTOR.md's migration order,
        // promoted to Controller (abstract) as part of step 4 so DualSenseController (now a real
        // sibling class, step 4 Phase J) can answer them without any Joy-Con-family flags at all.
        // isPro was a SUPERSET flag (isPro = isPro || isSnes || is64, plus isDualSense before
        // Phase J - set in the constructor below), so every "if (isPro)" check silently also
        // matched SNES/N64/DualSense whether that's actually intended or not - the exact mechanism
        // behind a real incident this session (a DualSense-scoped change leaked into Joy-Con's own
        // code path via a shared isPro-gated method). These properties name what each call site is
        // ACTUALLY testing for. The overrides below are deliberately literal, behavior-preserving
        // aliases of the original flags for every current Nintendo-family device type - this is a
        // pure rename/naming pass, not a behavior change (SNES/N64 mathematically get the same
        // HasDualSticks=true a raw "isPro" check already gave them, even though SNES genuinely has
        // zero sticks - that real divergence is deferred to when SnesController exists, not fixed
        // here).
        public override bool SupportsPairing => !isPro;  // Joy-Con-only: can combine with another unit into one logical controller
        public override bool HasDualSticks => isPro;     // has two physical sticks/thumb-stick-click buttons on one unit
        public override bool HasGyro => true;             // currently populates real gyr_g/acc_g data
        public override bool HasAnalogTriggers => false;  // L2/R2 report a real analog value, not just a digital button bit
        public override bool UsesNintendoProtocol => true; // speaks the Joy-Con SPI/subcommand protocol (LED, rumble encoding, handshake)

        // Internal-only properties (not part of Controller's public capability contract - see
        // DOCS/CONTROLLERS-REFACTOR.md step 5) that replace the four remaining raw isSnes/is64
        // gate checks below with named, literal restatements of exactly what they test for today.
        // Deliberately NOT unified with HasGyro/HasDualSticks above, which stay exactly as they
        // are (still wrong for SNES/N64 - a known, documented, deliberately-deferred issue, not
        // fixed in this pass) - these three answer a narrower, purely internal question ("does
        // this specific method's logic apply to this instance") separate from the public contract
        // other code already depends on.
        protected bool HasSticks => !isSnes;                        // SNES has zero physical sticks
        protected bool HasImuHardware => !(isSnes || is64);          // SNES/N64 have no gyro/accel to read
        protected bool ReadsCalibrationFromConfig => isSnes || is64; // vs. SPI flash for Joy-Con/Pro

        // Single source of truth for device-kind identity - see DOCS/CONTROLLERS-REFACTOR.md's
        // settings/step-1 notes. DualSense no longer appears here at all as of step 4 Phase J -
        // DualSenseController.Kind answers for itself.
        public override ControllerKind Kind =>
            isSnes ? ControllerKind.Snes :
            is64 ? ControllerKind.N64 :
            isPro ? ControllerKind.Pro :
            (isLeft ? ControllerKind.Left : ControllerKind.Right);

        // Join/split changes which mapping profile this physical half belongs to - see
        // Controller.other's setter, which calls this via OnOtherChanging right before the
        // change takes effect. Kept here (not moved to Controller with other) since it reaches
        // into gyro-mouse/mapping-engine state that isn't shared yet.
        protected override void OnOtherChanging() => PrepareForMappingProfileChange();


        public bool send = true;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private byte[] stick2_raw = { 0, 0, 0 };
        private bool imu_enabled = false;
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };

        private Int16[] gyr_sensiti = { 0, 0, 0 };

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;


        private byte global_count = 0;

        public byte LED { get; private set; } = 0x0;
        public override void SetLEDByPlayerNum(int id) {
            if (!UsesNintendoProtocol)
                return;

            // true: Joy-Con/Pro have always shown player-number LEDs unconditionally before this
            // dropdown existed (see ControllerMappings.PlayerLedEnabled's own comment) - unlike
            // DualSense, a profile that's never touched this setting should keep showing them.
            if (!ControllerMappings.PlayerLedEnabled(ControllerMappings.ProfileIdFor(this), true)) {
                LED = 0x0;
                SetPlayerLED(LED);
                return;
            }

            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["UseIncrementalLights"].Value.ToLower() == "true") {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        // True for anything BetterJoy attached because it matched the 3rd-party-controller
        // allowlist (Program.thirdPartyCons - see CheckForNewControllers), rather than being a
        // real Joy-Con/Pro/SNES/N64 device matched by VID/PID directly. Notably, this also
        // catches BetterJoy's OWN virtual XInput/DS4 output controller getting misidentified as a
        // new physical controller when AutoAddControllers is on - Windows exposes ViGEmBus's
        // emulated pad through a HID interface too (for DirectInput compatibility), which passes
        // the same generic "is this a gamepad" usage-page/usage check real third-party controllers
        // do. Left attached (it may still be something the user genuinely wants passthrough for),
        // but button-mapping detection (Reassign.cs/HeadlessJoyconHost.cs) excludes it - otherwise
        // every physical press doubles into a second "press" mirrored from the virtual pad.
        public bool thirdParty = false;

        // Licensed PowerA wireless pads can expose a USB HID interface that is a Nintendo-family
        // controller identity but does not answer the official Nintendo USB command handshake.
        // Keep owning that HID handle and consume any input it emits, but avoid sending Nintendo
        // output/subcommands that this lane has already shown it will not acknowledge.
        private bool usbInputOnlyFallback = false;
        private int usbInputOnlyReportsLogged = 0;

        public NintendoController(IntPtr handle_, bool imu, bool localize, float alpha,
                bool left, string path, string serialNum, int id = 0, bool isPro = false,
                bool isSnes = false, bool is64 = false, bool thirdParty = false,
                bool? isUsbOverride = null) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes || is64;
            this.isSnes = isSnes;
            this.is64 = is64;
            // Official Nintendo USB devices use the shared placeholder serial below, but some
            // third-party Pro controllers expose a real serial over USB. Prefer the transport
            // resolved from the PnP parent bus and keep the serial check only as a fallback.
            isUSB = isUsbOverride ?? serialNum == "000000000001";
            this.thirdParty = thirdParty;

            this.path = path;

            // Seed the ViGEm-consumption mask before the first input report. Subsequent live
            // configuration changes are picked up once per report in DoThingsWithButtons.
            RefreshGyroOnlyButtonReservations();

            connection = isUSB ? 0x01 : 0x02;

            // Virtual output is created after Attach resolves the controller's durable profile
            // identity (see JoyconManager.CreateOutputControllers). Creating it here used only
            // the old global ShowAs settings and was too early for USB devices whose real MAC is
            // learned during the handshake.
        }


        private bool usbConnectionInitialized;

        // Resolve the physical identity before discovery publishes a profile or virtual device.
        internal void PrepareUsbConnection() {
            if (!isUSB || usbConnectionInitialized)
                return;

            HIDapi.hid_set_nonblocking(handle, 0);

            byte[] a = { 0x0 };

            // Connect
            if (isUSB) {
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                form.AppendTextBox("Using USB.\r\n");

                bool usbHandshakeMatched = UsbCommand(0x02, a, "handshake", 100, true);
                if (!usbHandshakeMatched && !thirdParty) {
                    form.AppendTextBox("Resetting USB connection.\r\n");
                    UsbCommand(0x06, null, "reset", 100);
                    throw new Exception("reset_usb");
                }

                if (!usbHandshakeMatched)
                    form.AppendTextBox("USB Nintendo handshake did not answer; trying input-only USB ownership.\r\n");

                UsbCommand(0x03, a, "baudrate-3m", 100, usbHandshakeMatched);

                bool usbHandshake3mMatched = UsbCommand(0x02, a, "handshake-3m", 100, true);
                if (!usbHandshakeMatched && !usbHandshake3mMatched) {
                    if (!thirdParty) {
                        form.AppendTextBox("Resetting USB connection.\r\n");
                        UsbCommand(0x06, null, "reset", 100);
                        throw new Exception("reset_usb");
                    }
                    usbInputOnlyFallback = true;
                    form.AppendTextBox("USB Nintendo handshake unavailable; consuming input-only HID lane.\r\n");
                    DebugLog.Write("Nintendo USB handshake unavailable for third-party controller; consuming input-only HID lane: pad=" +
                        PadId + " path=" + path);
                }

                // Linux sends this command but allows it to time out; some controllers simply
                // start streaming after it without a matching 0x81/0x04 reply.
                UsbCommand(0x04, a, "no-timeout", 100, false);

                bool statusMatched = UsbCommand(0x01, a, "status", 100, true, 10);
                byte[] controllerMac = null;
                string source = "usb-status";
                bool identityResolved = statusMatched && TryParseIdentityReply(a, true, out controllerMac);
                if (!identityResolved && !thirdParty) {
                    // Linux reads the MAC from device-info data[4..9], in display byte order.
                    byte[] info = Subcommand(0x02, new byte[0], 0, false, 25);
                    identityResolved = TryParseIdentityReply(info, false, out controllerMac);
                    source = "device-info";
                }

                if (identityResolved) {
                    PadMacAddress = new PhysicalAddress(controllerMac);
                    InvalidateMappingProfileCache();
                    DebugLog.Write("Nintendo USB MAC resolved: " + PadMacAddress +
                        " (source=" + source + ") path=" + path);
                } else if (!thirdParty) {
                    throw new Exception("nintendo_usb_identity_unavailable");
                }
            }
            usbConnectionInitialized = true;
        }

        internal static bool TryParseIdentityReply(byte[] reply, bool usbStatus, out byte[] mac) {
            mac = null;
            if (reply == null)
                return false;
            if (usbStatus) {
                if (reply.Length < 10 || reply[0] != 0x81 || reply[1] != 0x01 || reply[3] != 0x03)
                    return false;
            } else if (reply.Length < 25 || reply[0] != 0x21 ||
                    (reply[13] & 0x80) == 0 || reply[14] != 0x02) {
                return false;
            }

            byte[] address = new byte[6];
            for (int i = 0; i < address.Length; i++)
                address[i] = reply[usbStatus ? 9 - i : 19 + i];
            string identity = BitConverter.ToString(address).Replace("-", "");
            if (identity == "000000000000" || identity == "000000000001" ||
                    identity == "010203040506" || identity == "FFFFFFFFFFFF")
                return false;
            mac = address;
            return true;
        }

        public override int Attach() {
            state = state_.ATTACHED;
            HIDapi.hid_set_nonblocking(handle, 0);
            PrepareUsbConnection();
            dump_calibration_data();

            // Bluetooth manual pairing
            byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            if (!usbInputOnlyFallback) {
                Subcommand(0x3, new byte[] { 0x30 }, 1);
                Subcommand(0x48, new byte[] { 0x01 }, 1);

                Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1);

                BlinkHomeLight();
                SetLEDByPlayerNum(PadId);
            }
            DebugPrint("Done with init.", DebugType.COMMS);

            HIDapi.hid_set_nonblocking(handle, 1);

            return 0;
        }

        private bool UsbCommand(byte command, byte[] response, string label, int timeoutMs,
                bool requireAck = true, int minimumResponseLength = 2) {
            byte[] request = { 0x80, command };
            int read = 0;
            int reads = 0;
            int writes = 0;
            byte report = 0;
            byte ack = 0;
            byte state = 0;
            bool hasResponse = response != null && response.Length > 0;
            bool matched = false;
            int maxAttempts = hasResponse ? 2 : 1;
            for (int attempt = 0; attempt < maxAttempts && !matched; attempt++) {
                if (attempt > 0)
                    Thread.Sleep(20);
                int written = HIDapi.hid_write(handle, request, new UIntPtr(2));
                writes++;
                if (!hasResponse) {
                    matched = written > 0;
                    break;
                }
                Array.Clear(response, 0, response.Length);
                Stopwatch timeout = Stopwatch.StartNew();
                int remaining = Math.Max(1, timeoutMs);
                do {
                    read = HIDapi.hid_read_timeout(handle, response,
                        new UIntPtr((uint)response.Length), remaining);
                    if (read <= 0)
                        break;
                    reads++;
                    report = response[0];
                    if (response.Length > 1)
                        ack = response[1];
                    if (response.Length > 3)
                        state = response[3];
                    if (read >= minimumResponseLength && report == 0x81 && ack == command) {
                        matched = true;
                        break;
                    }
                    Array.Clear(response, 0, response.Length);
                    remaining = timeoutMs - (int)timeout.ElapsedMilliseconds;
                } while (remaining > 0);
                if (!matched)
                    DebugPrint("USB command 0x" + command.ToString("X2", CultureInfo.InvariantCulture) +
                        " timed out or received mismatched response.", DebugType.COMMS);
            }
            if (!requireAck)
                matched = true;
            DebugLog.Write("Nintendo USB command: pad=" + PadId +
                " label=" + label +
                " command=0x" + command.ToString("X2", CultureInfo.InvariantCulture) +
                " thirdParty=" + thirdParty +
                " writes=" + writes.ToString(CultureInfo.InvariantCulture) +
                " read=" + read.ToString(CultureInfo.InvariantCulture) +
                " reads=" + reads.ToString(CultureInfo.InvariantCulture) +
                " report=0x" + report.ToString("X2", CultureInfo.InvariantCulture) +
                " ack=0x" + ack.ToString("X2", CultureInfo.InvariantCulture) +
                " state=0x" + state.ToString("X2", CultureInfo.InvariantCulture) +
                " matched=" + matched.ToString(CultureInfo.InvariantCulture));
            return matched;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            if (usbInputOnlyFallback)
                return;
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (thirdParty || !UsesNintendoProtocol)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public override void SetHomeLight(bool on) {
            if (thirdParty || !UsesNintendoProtocol)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                a[0] = 0x1F;
                a[1] = 0xF0;
            } else {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state) {
            byte[] a = { state };
            byte[] reply = Subcommand(0x06, a, 1, false);
            DebugLog.Write("Nintendo HCI state command: pad=" + PadId +
                " requested=0x" + state.ToString("X2", CultureInfo.InvariantCulture) +
                " report=0x" + ReportByte(reply, 0).ToString("X2", CultureInfo.InvariantCulture) +
                " ack=0x" + ReportByte(reply, 13).ToString("X2", CultureInfo.InvariantCulture) +
                " ackSc=0x" + ReportByte(reply, 14).ToString("X2", CultureInfo.InvariantCulture));
        }

        private static byte ReportByte(byte[] reply, int index) {
            return reply != null && reply.Length > index ? reply[index] : (byte)0x00;
        }

        public override void PowerOff(bool shuttingDown = false) {
            if (state > state_.DROPPED && !isUSB) {
                // Preserve Nintendo's controller-side sleep request, then tear down the Windows
                // Bluetooth link with the same radio-level mechanism used by DualSense and
                // DualShock4. Some third-party Pro controllers acknowledge or ignore subcommand
                // 0x06 without actually releasing their HID connection; merely marking the
                // BetterJoy object dropped then lets the scanner rediscover it immediately.
                HIDapi.hid_set_nonblocking(handle, 0);
                SetHCIState(0x00);
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                state = state_.DROPPED;
            }
        }

        // RetireDuplicateConnections' generic MAC-based dedup now lives directly on Controller
        // (step 4 Phase J) - Joy-Con-family no longer needs its own override at all, since the
        // DualSense-only Bluetooth-auto-disconnect tail that used to be spliced into this method
        // moved to DualSenseController.OnDuplicateRetired.

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        // Called by Controller.Detach() while the connection had progressed past NO_JOYCONS, right
        // after the shared hid_set_nonblocking call. Do not send the old USB-to-Bluetooth handoff
        // commands here: a transient USB init/read failure also reaches Detach through CleanUp,
        // and asking the controller to resume Bluetooth there creates the exact reconnect/light
        // flash loop that prevents stable USB ownership.
        protected override void OnDetachingWhileAttached() {
            // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
            //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?
        }

        private byte ts_en;

        // An occasional duplicate timestamp is normal (we can poll faster than the device
        // produces new reports); a run of them is not - it means the report stream has
        // genuinely stalled, which happens when another program (e.g. Steam) grabbed the raw
        // device before HidHide had a chance to hide it (a real race on a fresh boot/cleared
        // settings, since HidHide's hidden-device list isn't there yet to fall back on and
        // Steam may already be running). Detaching and letting the normal reconnect flow
        // (CleanUp -> rediscovered on the next scan) pick it back up clears the stall in
        // practice, so treat a short run the same as a connection-loss timeout.
        private int duplicateTimestampCount = 0;
        private const int MaxConsecutiveDuplicateTimestamps = 3;

        protected override int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;

            byte[] raw_buf = new byte[usbInputOnlyFallback ? 64 : report_len];
            bool captureImuDiagnostics = GyroMouseDebugLogging || GyroStickDebugLogging;
            long hidCallStart = captureImuDiagnostics ? Stopwatch.GetTimestamp() : 0;
            int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5);
            long hidCallEnd = captureImuDiagnostics ? Stopwatch.GetTimestamp() : 0;
            RecordGyroMouseHidCall(ret, ret > 0 ? raw_buf[1] : (byte)0,
                                   hidCallStart, hidCallEnd);

            if (ret > 0) {
                if (usbInputOnlyFallback) {
                    LogUsbInputOnlyReport(ret, raw_buf);
                    if (ProcessUsbInputOnlyReport(raw_buf, ret))
                        return ret;
                    if (raw_buf[0] != 0x30 && raw_buf[0] != 0x31)
                        return ret;
                }
                BeginGyroStickDiagnosticReport();
                // Process packets as soon as they come
                for (int n = 0; n < 3; n++) {
                    ExtractIMUValues(raw_buf, n);
                    AccumulateGyroStickDiagnosticSample();

                    byte lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0) {
                        Timestamp += (ulong)lag * 5000; // add lag once
                        ProcessButtonsAndStick(raw_buf);

                        // process buttons here to have them affect DS4
                        DoThingsWithButtons();

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                            BatteryChanged();
                    }
                    ProcessGyroMouseSample(n == 2);
                    ProcessGyroStickSample(n == 2);
                    Timestamp += 5000; // 5ms difference

                    packetCounter++;
                    if (Program.server != null)
                        Program.server.NewReportIncoming(this);

                    if (out_ds4 != null || out_dualsense != null) {
                        var ds4State = MapToDualShock4Input(this);
                        if (out_ds4 != null) {
                            try {
                                out_ds4.UpdateInput(ds4State);
                            } catch (Exception) {
                                // ignore /shrug
                            }
                        }
                        if (out_dualsense != null) {
                            try {
                                out_dualsense.UpdateInput(ds4State);
                            } catch (Exception) {
                                // ignore /shrug
                            }
                        }
                    }
                }

                RecordGyroStickDiagnosticReport(raw_buf[1], hidCallEnd);

                // no reason to send XInput reports so often
                if (out_xbox != null) {
                    try {
                        out_xbox.UpdateInput(MapToXbox360Input(this));
                    } catch (Exception) {
                        // ignore /shrug
                    }
                }


                if (ts_en == raw_buf[1] && HasImuHardware) {
                    form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
                    DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);

                    duplicateTimestampCount++;
                    if (duplicateTimestampCount >= MaxConsecutiveDuplicateTimestamps) {
                        form.AppendTextBox("Report stream stalled (another program may have grabbed this controller before it was hidden) - reattaching to recover.\r\n");
                        duplicateTimestampCount = 0;
                        state = state_.DROPPED;
                    }
                } else {
                    duplicateTimestampCount = 0;
                }
                ts_en = raw_buf[1];
                DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", ret, raw_buf[1]), DebugType.THREADING);
            }
            return ret;
        }

        protected override bool AllowsSilentHidIdle => usbInputOnlyFallback;

        private bool ProcessUsbInputOnlyReport(byte[] rawReport, int bytesRead) {
            if (bytesRead < 1)
                return false;

            if (rawReport[0] == 0x3F) {
                if (bytesRead < 12)
                    return true;

                ProcessSimpleHidReport(rawReport);
                DispatchParsedInputReport();
                return true;
            }

            return false;
        }

        private void ProcessSimpleHidReport(byte[] report) {
            if (HasDualSticks) {
                stick[0] = NormalizeSimpleHidAxis(report[4], report[5]);
                stick[1] = NormalizeSimpleHidAxis(report[6], report[7]);
                stick2[0] = NormalizeSimpleHidAxis(report[8], report[9]);
                stick2[1] = NormalizeSimpleHidAxis(report[10], report[11]);
            }

            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i)
                        down_[i] = buttons[i];
                }

                buttons = new bool[ButtonCount];
                byte b1 = report[1];
                byte b2 = report[2];
                buttons[(int)Button.DPAD_DOWN] = (b1 & 0x01) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (b1 & 0x02) != 0;
                buttons[(int)Button.DPAD_LEFT] = (b1 & 0x04) != 0;
                buttons[(int)Button.DPAD_UP] = (b1 & 0x08) != 0;
                buttons[(int)Button.SL] = (b1 & 0x10) != 0;
                buttons[(int)Button.SR] = (b1 & 0x20) != 0;

                buttons[(int)Button.MINUS] = (b2 & 0x01) != 0;
                buttons[(int)Button.PLUS] = (b2 & 0x02) != 0;
                buttons[(int)Button.STICK] = (b2 & 0x04) != 0;
                buttons[(int)Button.STICK2] = (b2 & 0x08) != 0;
                buttons[(int)Button.HOME] = (b2 & 0x10) != 0;
                buttons[(int)Button.CAPTURE] = (b2 & 0x20) != 0;

                if (HasDualSticks) {
                    buttons[(int)Button.SHOULDER_1] = (b2 & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_1] = (b2 & 0x40) != 0;
                    buttons[(int)Button.SHOULDER_2] = (b2 & 0x80) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (b2 & 0x80) != 0;
                } else {
                    buttons[(int)Button.SHOULDER_1] = (b2 & 0x40) != 0;
                    buttons[(int)Button.SHOULDER_2] = (b2 & 0x80) != 0;
                }

                CommitButtonState();
            }
        }

        private static float NormalizeSimpleHidAxis(byte lo, byte hi) {
            int raw = lo | (hi << 8);
            float value = (raw - 0x8000) / 32768.0f;
            return Math.Max(-1.0f, Math.Min(1.0f, value));
        }

        private void DispatchParsedInputReport() {
            DoThingsWithButtons();
            packetCounter++;

            if (Program.server != null)
                Program.server.NewReportIncoming(this);

            if (out_ds4 != null || out_dualsense != null) {
                var ds4State = MapToDualShock4Input(this);
                if (out_ds4 != null) {
                    try {
                        out_ds4.UpdateInput(ds4State);
                    } catch (Exception) {
                        // ignore /shrug
                    }
                }
                if (out_dualsense != null) {
                    try {
                        out_dualsense.UpdateInput(ds4State);
                    } catch (Exception) {
                        // ignore /shrug
                    }
                }
            }

            if (out_xbox != null) {
                try {
                    out_xbox.UpdateInput(MapToXbox360Input(this));
                } catch (Exception) {
                    // ignore /shrug
                }
            }
        }

        private void LogUsbInputOnlyReport(int bytesRead, byte[] rawReport) {
            if (!DebugLog.Enabled || usbInputOnlyReportsLogged >= 16)
                return;

            usbInputOnlyReportsLogged++;
            int len = Math.Min(Math.Max(bytesRead, 0), Math.Min(rawReport.Length, 64));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len; i++) {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(rawReport[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            DebugLog.Write("Nintendo USB input-only report: pad=" + PadId +
                " kind=" + Kind +
                " path=" + path +
                " bytes=" + bytesRead.ToString(CultureInfo.InvariantCulture) +
                " reportId=0x" + rawReport[0].ToString("X2", CultureInfo.InvariantCulture) +
                " data=" + sb.ToString());
        }


        // Called from Controller.Poll()'s shared shell whenever rumble_obj's queue has data -
        // SendRumble's actual Nintendo HD-rumble encoding isn't promoted to Controller, so this
        // stays a hook rather than shared logic. DualSense's own simpler dual-motor rumble lives
        // on DualSenseController.SendQueuedRumbleIfAny now (step 4 Phase J).
        protected override void SendQueuedRumbleIfAny() {
            if (usbInputOnlyFallback) {
                while (rumble_obj.queue.Count > 0)
                    rumble_obj.queue.Dequeue();
                return;
            }
            if (rumble_obj.queue.Count > 0) {
                SendRumble(rumble_obj.GetData());
            }
        }

        public float[] otherStick = { 0, 0 };

        float stickScalingFactor = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]);
        float stickScalingFactor2 = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]);

        private int ProcessButtonsAndStick(byte[] report_buf) {
            // A report ID of 0 is never valid for a real Joy-Con/Pro Controller report - this
            // used to throw here, which had nothing catching it anywhere in the call chain
            // (ReceiveRaw/Poll), so a single malformed report crashed the entire process, not
            // just this controller's connection. Skip this report instead: buttons/stick just
            // hold their previous values for one tick, matching how Poll() already tolerates a
            // read error or timeout (a < 0 / a == 0) without tearing anything down.
            if (report_buf[0] == 0x00) {
                DebugPrint("Received a report with report ID 0 - skipping.", DebugType.ALL);
                return -1;
            }
            if (HasSticks) {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (HasDualSticks) {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                CalibrationState.AddStickSample(this, false, stick_precal[0], stick_precal[1]);
                stick = CenterSticks(stick_precal, stick_cal, deadzone, isLeft ? stickScalingFactor : stickScalingFactor2);

                if (HasDualSticks) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    CalibrationState.AddStickSample(this, true, stick2_precal[0], stick2_precal[1]);
                    stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2, stickScalingFactor2);
                }

                // Read other Joycon's sticks
                if (isLeft && other != null && other != this) {
                    stick2 = otherStick;
                    other.otherStick = stick;
                }

                if (!isLeft && other != null && other != this) {
                    Array.Copy(stick, stick2, 2);
                    stick = otherStick;
                    other.otherStick = stick2;
                }
            }
            //

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[ButtonCount];

                buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
                buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (HasDualSticks) {
                    buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                }

                if (other != null && other != this) {
                    buttons[(int)(Button.B)] = other.buttons[(int)Button.DPAD_DOWN];
                    buttons[(int)(Button.A)] = other.buttons[(int)Button.DPAD_RIGHT];
                    buttons[(int)(Button.X)] = other.buttons[(int)Button.DPAD_UP];
                    buttons[(int)(Button.Y)] = other.buttons[(int)Button.DPAD_LEFT];

                    buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
                    buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1];
                    buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2];
                }

                if (isLeft && other != null && other != this) {
                    buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                    buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
                }

                if (!isLeft && other != null && other != this) {
                    buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                }

                CommitButtonState();
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (HasImuHardware) {
                // Must happen before this sample is transformed/added to either estimator. If a
                // join/split changed the basis, the orientation accumulated in the old basis is
                // invalid even though the physical controller itself never disconnected.
                EnsureGyroOrientationBasis();

                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - activeData[3]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.X = (gyr_r[i] - activeData[0]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[i], gyr_r[i]);
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[4]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[1]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[i], gyr_r[i]);
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[5]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[2]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[i], gyr_r[i]);
                                break;
                        }
                    }
                } else {
                    Int16[] offset;
                    if (!SupportsPairing)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                if (other == null && SupportsPairing) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Capture the canonical, Y-up JoyShockLibrary-style IMU frame from acc_g/gyr_g
                // AFTER the solo-controller swap above (not before, as this used to do) - a solo
                // Joy-Con is physically held sideways, a self-paired ("vertical") or joined one
                // is held upright, and those are genuinely different grips that need different
                // axis mappings, the same way the legacy raw pipeline right above already treats
                // them differently. Building the canonical frame from the pre-swap values made
                // solo, self-paired, and joined share one frame, which was wrong for solo.
                //
                // NOTE: an earlier version of this code applied the equivalent swap directly to
                // the canonical Y-up vector instead of reordering like this, and that reportedly
                // suppressed vertical/diagonal pointer motion for solo Joy-Cons - likely because
                // the raw swap's axis correspondence doesn't carry over 1:1 onto the already-
                // remapped canonical axes below, so applying it post-hoc there is a different,
                // NOT equivalent operation to applying it here, pre-remap, in the native frame it
                // was actually written for. This reordering has not been hardware-verified against
                // that history - test solo Joy-Con gyro-mouse/gyro-stick specifically (both
                // orientations) before trusting this.
                UpdateCanonicalGyroMouseImu();

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }


        private void SendRumble(byte[] buf) {
            byte[] buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
        }


        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true,
                int minimumResponseLength = 15) {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            bool matched = false;
            int lastRead = 0;
            int reads = 0;
            int writes = 0;
            for (int sendAttempt = 0; sendAttempt < 2 && !matched; sendAttempt++) {
                if (sendAttempt > 0)
                    Thread.Sleep(isUSB ? 20 : 60);
                if (print) {
                    PrintArray(buf_, DebugType.COMMS, len, 11,
                        "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}");
                }
                HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
                writes++;
                Array.Clear(response, 0, response.Length);
                for (int tries = 0; tries < 10; tries++) {
                    lastRead = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100);
                    if (lastRead < 1) {
                        DebugPrint("No response.", DebugType.COMMS);
                    } else {
                        reads++;
                        if (print) {
                            PrintArray(response, DebugType.COMMS, report_len - 1, 1,
                                "Response ID 0x" + string.Format("{0:X2}", response[0]) +
                                ". Data: 0x{0:S}");
                        }
                        if (lastRead >= minimumResponseLength && response[0] == 0x21 && response[14] == sc) {
                            matched = true;
                            break;
                        }
                    }
                    Array.Clear(response, 0, response.Length);
                }
                if (!matched)
                    DebugPrint("Subcommand 0x" +
                        sc.ToString("X2", CultureInfo.InvariantCulture) +
                        " timed out or received mismatched response.", DebugType.COMMS);
            }
            DebugLog.Write("Nintendo subcommand: pad=" + PadId +
                " sc=0x" + sc.ToString("X2", CultureInfo.InvariantCulture) +
                " thirdParty=" + thirdParty +
                " writes=" + writes.ToString(CultureInfo.InvariantCulture) +
                " read=" + lastRead.ToString(CultureInfo.InvariantCulture) +
                " reads=" + reads.ToString(CultureInfo.InvariantCulture) +
                " report=0x" + response[0].ToString("X2", CultureInfo.InvariantCulture) +
                " ackSc=0x" + (response.Length > 14 ? response[14] : (byte)0).ToString("X2", CultureInfo.InvariantCulture) +
                " matched=" + matched.ToString(CultureInfo.InvariantCulture));

            return response;
        }

        private void dump_calibration_data() {
            if (ReadsCalibrationFromConfig || thirdParty) {
                short[] temp = (short[])ConfigurationManager.AppSettings["acc_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0]; acc_sensiti[1] = temp[1]; acc_sensiti[2] = temp[2];
                temp = (short[])ConfigurationManager.AppSettings["gyr_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0]; gyr_sensiti[1] = temp[1]; gyr_sensiti[2] = temp[2];
                ushort[] temp2 = (ushort[])ConfigurationManager.AppSettings["stick_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0]; stick_cal[1] = temp2[1]; stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3]; stick_cal[4] = temp2[4]; stick_cal[5] = temp2[5];
                deadzone = ushort.Parse(ConfigurationManager.AppSettings["deadzone"]);
                temp2 = (ushort[])ConfigurationManager.AppSettings["stick2_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0]; stick2_cal[1] = temp2[1]; stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3]; stick2_cal[4] = temp2[4]; stick2_cal[5] = temp2[5];
                deadzone2 = ushort.Parse(ConfigurationManager.AppSettings["deadzone2"]);
                getActiveStickData();
                return;
            }

            HIDapi.hid_set_nonblocking(handle, 0);
            byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
            bool found = false;
            for (int i = 0; i < 9; ++i) {
                if (buf_[i] != 0xff) {
                    form.AppendTextBox("Using user stick calibration data.\r\n");
                    found = true;
                    break;
                }
            }
            if (!found) {
                form.AppendTextBox("Using factory stick calibration data.\r\n");
                buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
            }
            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (HasDualSticks) {
                buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
                found = false;
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
                }
                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }
            HIDapi.hid_set_nonblocking(handle, 1);

            getActiveStickData();
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1) {
                    break;
                }
            }
            Array.Copy(buf_, 20, read_buf, 0, len);
            if (print) PrintArray(read_buf, DebugType.COMMS, len);
            return read_buf;
        }
    }
}
