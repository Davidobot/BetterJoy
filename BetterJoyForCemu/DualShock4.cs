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

namespace BetterJoyForCemu {
    // DualShock4Controller : Controller - a real sibling class, built the same way
    // DualSenseController was (see DOCS/CONTROLLERS-REFACTOR.md and DOCS/DUALSENSE.md). Baseline
    // scope started by matching how DualSense itself began (5e7355b, "Add baseline DualSense
    // support: buttons, sticks, and triggers"). Gyro/accel now feeds the same shared calibrated
    // mouse/stick pipeline as DualSense, while DS4-specific report framing, timestamps, factory
    // calibration layout, sensor axes, and transport-specific lightbar output remain defined
    // here. Touchpad input feeds the shared Controller pipeline.
    //
    // Byte offsets below follow the classic DualShock 4 HID report layout - the single most
    // independently re-verified third-party controller format in existence (DS4Windows, the
    // Linux kernel's hid-sony driver, and a decade of other open-source implementations all
    // agree on it), unlike DualSense's report, which this codebase's own DualSense.cs found to
    // genuinely diverge from every secondhand reference on real hardware. That agreement is
    // still a starting point, not a guarantee for this specific unit/firmware - see
    // LogDualShock4RawDump below, the same raw-hex-dump diagnostic pattern DualSense.cs used to
    // find and fix its own real offset mistakes.
    public class DualShock4Controller : Controller {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => true;
        public override bool HasTouchpad => true;
        protected override int TouchpadMaximumX => 1919;
        protected override int TouchpadMaximumY => 941;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.DualShock4;
        public override string UsbAudioEndpointNameHint => "Wireless Controller";

        public byte[] triggerVal = { 0, 0 }; // raw 0-255 analog L2/R2
        protected override byte[] TriggerVal => triggerVal;

        // USB report is 64 bytes (report ID 0x01). Bluetooth's extended/"full" report (report ID
        // 0x11) is commonly documented at 78 bytes, matching DualSense's own report length on
        // both transports - plausible given DualSense's protocol is DS4's direct descendant, but
        // unconfirmed against real hardware for DS4 specifically. If Bluetooth reports come back
        // a different length than 78, that's the first thing to check - see ReceiveRaw.
        private const int DualShock4MaxReportLen = 78;
        private readonly byte[] dualShock4InputReport = new byte[DualShock4MaxReportLen];
        private long lastDualShock4RawDumpTimestamp = 0;
        private long lastDualShock4ImuLogTimestamp = 0;
        private bool lightbarTransportKnown;
        private bool lightbarUpdatePending = true;
        private byte lightbarRed;
        private byte lightbarGreen;
        private byte lightbarBlue = 255;
        private byte currentLeftMotor;
        private byte currentRightMotor;
        private long lightbarApplyEarliestTimestamp;
        private bool connectionLightFlashStarted;
        // Common DS4 status byte 0, bit 5 is the physical headphone detect signal. -1 means no
        // full report has been parsed yet, so headset-routed audio remains safely idle during
        // attach rather than assuming a jack is present.
        private int headphoneConnectionState = -1;
        public bool HeadphonesConnected => Volatile.Read(ref headphoneConnectionState) == 1;

        // DS4 carries a 16-bit sensor clock at common offsets 9-10. One tick is 16/3 microseconds,
        // as used by DS4Windows; unsigned subtraction handles the frequent 16-bit rollover.
        private ushort? lastImuHardwareTimestamp;
        private ushort lastLoggedImuDeltaTicks;
        private float measuredGyroSubSamplePeriod = ImuSamplePeriodSeconds;
        private const float MinGyroSubSamplePeriod = 0.0005f;
        private const float MaxGyroSubSamplePeriod = 0.02f;
        protected override float GyroSubSamplePeriod => measuredGyroSubSamplePeriod;
        protected override Vector3 GyroStickBiasCorrection => gyroMouseBias;
        // DS4 reports one very short IMU sample at a time. Average those samples over the same
        // 15 ms sensor window a Joy-Con report already contains, otherwise each 1-2 ms sample is
        // expanded into a complete stick update and its cross-axis sensor noise becomes visible.
        // ProcessGyroStickSample holds the last completed result between windows, so this does
        // not reduce button or physical-stick report frequency.
        protected override float GyroStickOutputWindowPeriod => GyroStickReferenceReportPeriod;

        // DS4 uses the same nominal Bosch sensor resolutions as DualSense, but its factory
        // calibration feature report groups gyro extrema differently on USB and Bluetooth.
        private const float GyroLsbPerDegPerSec = 16.0f;
        private const float AccelLsbPerG = 8192.0f;
        private short gyroPitchBias, gyroYawBias, gyroRollBias;
        private short gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus;
        private short gyroSpeedPlus, gyroSpeedMinus;
        private short accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus;
        private bool gyroCalibrationAttempted;
        private bool gyroCalibrationValid;

        public DualShock4Controller(IntPtr handle_, string path, string serialNum, int id = 0) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            rumble_obj = new Rumble(new float[] { 0, 0, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            // Single-unit device, same "primary/solo" convention every non-Joy-Con device uses -
            // see Controller.isLeft's own comment and DualSenseController's identical field.
            isLeft = true;

            PadId = id;
            // Re-derived every packet from actual report length (see ReceiveRaw), matching
            // DualSenseController - the Joy-Con-only placeholder-serial heuristic doesn't apply.
            isUSB = false;
            this.path = path;

            RefreshGyroOnlyButtonReservations();

            // Sony's two modern controller generations share the same physical sensor mounting
            // and benefit from the same pitch-leak correction in Player Space. The report bytes
            // and calibration feeding that shared math remain DS4-specific below.
            gyroMousePlayerSpace.EnableExtendedAxisCorrection = true;
            gyroStickPlayerSpace.EnableExtendedAxisCorrection = true;

            connection = isUSB ? 0x01 : 0x02;
        }

        // Confirmed against the Linux hid-playstation driver's DS4_FEATURE_REPORT_PAIRING_INFO /
        // _SIZE and a public GIMX capture of the same report, which agree exactly.
        private const byte DualShock4PairingInfoFeatureReportId = 0x12;
        private const int DualShock4PairingInfoFeatureReportLen = 16;
        private const int DualShock4PairingDeviceAddressOffset = 1;
        // Confirmed on real hardware (see LogDualShock4PairingInfo): a wired DS4 paired to this PC
        // returned a real host address here, at the same offset DualSense's report 0x09 uses.
        private const int DualShock4PairingHostAddressOffset = 10;

        // Report 0x13 - set pairing. Field layout is identical to DualSense's 0x0A (host address
        // at 1..6 little-endian, 16-byte link key at 7..22); only the length differs, because the
        // DS4 report has no trailing CRC. From a public GIMX capture:
        //   13 AC 9E 17 94 05 B0 | 56 E8 81 38 08 06 51 41 C0 7F 12 AA D9 66 3C CE
        // The link key is write-only in both generations - nothing the controller exposes reads it
        // back, so the key always comes from Windows' own bond store (or is generated there).
        private const byte DualShock4SetPairingFeatureReportId = 0x13;
        private const int DualShock4SetPairingFeatureReportLen = 23;
        private const int DualShock4SetPairingHostAddressOffset = 1;
        private const int DualShock4SetPairingLinkKeyOffset = 7;

        private const int DualShock4PairingRecordCommitTimeoutMs = 2000;
        private const int DualShock4BluetoothHidFinalizeTimeoutMs = 30000;

        private bool automaticBluetoothPairingAttempted;
        private int automaticBluetoothPairingPending;

        public override int Attach() {
            state = state_.ATTACHED;

            HIDapi.hid_set_nonblocking(handle, 1);

            // DS4 has no SPI factory calibration to read (same situation DualSense is in) -
            // stick_cal/stick2_cal would otherwise be left at their class defaults and
            // CenterSticks would divide by that zero the moment it's used. Seed an identity
            // calibration matching DS4's real raw domain (bytes 0-255, center 128); any stored
            // user recalibration (CalibrationState, via the existing wizard) overlays on top
            // exactly the way it already does for Joy-Con/DualSense.
            stick_cal[0] = 127; stick_cal[1] = 127;   // max above center (X, Y)
            stick_cal[2] = 128; stick_cal[3] = 128;   // center (X, Y)
            stick_cal[4] = 128; stick_cal[5] = 128;   // min below center (X, Y)
            stick2_cal[0] = 127; stick2_cal[1] = 127;
            stick2_cal[2] = 128; stick2_cal[3] = 128;
            stick2_cal[4] = 128; stick2_cal[5] = 128;
            deadzone = 8;
            deadzone2 = 8;
            getActiveStickData();

            RequestExtendedReportMode();

            form.AppendTextBox("DualShock 4 attached.\r\n");
            return 0;
        }

        // Groundwork probe for DualShock 4 Bluetooth pairing. READ ONLY - 0x12 is a GET feature
        // report and changes nothing on the controller.
        //
        // Two independent sources agree on the read side: the Linux hid-playstation driver
        // (DS4_FEATURE_REPORT_PAIRING_INFO 0x12, DS4_FEATURE_REPORT_PAIRING_INFO_SIZE 16, with the
        // controller's own MAC at buf[1]) and a public GIMX capture of the same report. Neither
        // establishes where the paired HOST address sits: the kernel never reads it, and the GIMX
        // dump was taken from an unpaired controller so those bytes were all zero. Offset 10 below
        // is therefore inferred from the DualSense parallel (report 0x09, host at offset 10 - see
        // DualSensePairingHostAddressOffset), and nothing has confirmed it against hardware.
        //
        // Everything that would later WRITE a bond (0x13) depends on reading the current host
        // correctly - that is how the pairing flow decides whether a controller is already pointed
        // at this PC or has been taken by another host. Getting an offset wrong there fails
        // silently, which is exactly how the DualSense byte layout went wrong twice. So confirm it
        // from a controller that is actually paired to this PC before building on it.
        // Reconciliation entry point. Only records a request - the ceremony itself issues feature
        // reports on the HID handle and must run on this controller's own Poll thread, same rule
        // the DualSense ceremony follows. One attempt per USB attachment, set here rather than
        // after the Poll thread picks it up so a failed radio/registry operation cannot re-queue
        // on every scan pass.
        public void ApplyAutomaticBluetoothPairing() {
            if (!ControllerMappings.AutomaticBluetoothPairingEnabled(
                    ControllerMappings.ProfileIdFor(this))) {
                automaticBluetoothPairingAttempted = false;
                return;
            }
            if (!isUSB || state <= state_.DROPPED || automaticBluetoothPairingAttempted)
                return;

            automaticBluetoothPairingAttempted = true;
            Interlocked.Exchange(ref automaticBluetoothPairingPending, 1);
        }

        protected override void ApplyQueuedAutomaticBluetoothPairingIfAny() {
            if (Interlocked.Exchange(ref automaticBluetoothPairingPending, 0) != 0)
                PerformAutomaticBluetoothPairing();
        }

        // Repair only, by design. The DualSense investigation established that a bond written by
        // key injection never reaches Windows' "authenticated" state - remembered flips,
        // authenticated never does, and only a live SSP handshake sets it. That is a property of
        // the Windows stack rather than of any one controller, so first-ever pairing stays manual
        // here too. What this does cover is the case that actually recurs: a controller whose
        // onboard bond was taken by a PS4, restored to the bond this PC still owns.
        //
        // The DS4 has no equivalent of the DualSense's report 0x08, so there is no way to tell the
        // controller to leave USB and come up on Bluetooth - confirmed three ways: the GIMX report
        // tables, the Linux hid-playstation driver (which sends nothing of the sort), and this
        // file's own note that DS4 power-off is a radio operation. The handoff is therefore driven
        // from the Windows side instead, by asking it to page the controller.
        private void PerformAutomaticBluetoothPairing() {
            byte[] controllerMac = PadMacAddress?.GetAddressBytes();
            if (controllerMac == null || controllerMac.Length != 6)
                return;

            BluetoothRadio.BeginClassicPairingRegistryTrace(controllerMac);
            byte[] linkKey = null;
            try {
                if (!BluetoothRadio.TryGetOrCreateClassicPairing(controllerMac,
                        out byte[] hostMacLittleEndian, out linkKey, out bool created)) {
                    form.AppendTextBox("Automatic DualShock 4 Bluetooth pairing could not access " +
                        "a local Bluetooth radio or its Windows bond store.\r\n");
                    return;
                }

                // Read who the controller currently answers to. Only rewrite when it is pointed
                // somewhere else - repointing a controller that already answers to this PC gains
                // nothing and needlessly rewrites its onboard record.
                byte[] currentHost = ReadControllerPairedHost();
                bool alreadyPointedHere = currentHost != null &&
                    ByteArraysEqual(currentHost, hostMacLittleEndian);

                if (!alreadyPointedHere) {
                    if (!SendDualShock4PairingFeatureReport(hostMacLittleEndian, linkKey) ||
                            !WaitForPairingHost(hostMacLittleEndian,
                                DualShock4PairingRecordCommitTimeoutMs)) {
                        form.AppendTextBox("DualShock 4 rejected the Bluetooth bond; the Windows " +
                            "bond was left unchanged.\r\n");
                        DebugLog.Write("DualShock 4 pairing: pad=" + PadId +
                            " bondWrite=failed created=" + created);
                        return;
                    }
                    BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                        "controller-bond-written");
                }

                // Windows must hold the same key before it pages the controller: authentication
                // starts the instant the link comes up, and the bond store is consulted then.
                if (!BluetoothRadio.TryCommitClassicLinkKey(
                        hostMacLittleEndian, controllerMac, linkKey)) {
                    form.AppendTextBox("DualShock 4 accepted its Bluetooth bond, but BetterJoy " +
                        "could not commit the matching key to Windows.\r\n");
                    return;
                }
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerMac,
                    "windows-link-key-committed");

                DebugLog.Write("DualShock 4 pairing: pad=" + PadId +
                    " mac=" + BitConverter.ToString(controllerMac).Replace("-", "") +
                    " createdWindowsBond=" + created +
                    " alreadyPointedHere=" + alreadyPointedHere);

                BeginWindowsInitiatedConnect(controllerMac, hostMacLittleEndian);
            } finally {
                if (linkKey != null)
                    Array.Clear(linkKey, 0, linkKey.Length);
            }
        }

        // The DS4 half of "tell Windows to connect". BluetoothSetServiceState (inside
        // TryFinalizeClassicHidPairing) is what makes Windows page a remembered device and install
        // its HID profile - the supported substitute for the controller-side connect trigger the
        // DualSense has and this controller does not. Queued off the Poll thread because it blocks
        // for up to the finalize timeout while Windows works.
        private void BeginWindowsInitiatedConnect(byte[] controllerMac,
                byte[] hostMacLittleEndian) {
            if (!BluetoothRadio.IsLocalRadioAvailable()) {
                DebugLog.Write("DualShock 4 pairing: pad=" + PadId +
                    " connect suppressed (no usable local radio)");
                return;
            }

            byte[] controllerCopy = (byte[])controllerMac.Clone();
            byte[] hostCopy = (byte[])hostMacLittleEndian.Clone();
            int padId = PadId;
            ThreadPool.QueueUserWorkItem(_ => {
                bool completed = BluetoothRadio.TryFinalizeClassicHidPairing(
                    hostCopy, controllerCopy, "Wireless Controller", true,
                    DualShock4BluetoothHidFinalizeTimeoutMs);
                DebugLog.Write("DualShock 4 pairing handoff: pad=" + padId +
                    " windowsHidSetup=" + completed);
                BluetoothRadio.MarkClassicPairingRegistryTrace(controllerCopy,
                    completed ? "windows-hid-setup-ok" : "windows-hid-setup-failed");
            });
        }

        // Report 0x12's host field, same read the probe below dumps. Returns null when the report
        // is short or unreadable rather than a zeroed address, so callers can tell "unknown" from
        // "genuinely unpaired".
        private byte[] ReadControllerPairedHost() {
            byte[] info = new byte[DualShock4PairingInfoFeatureReportLen];
            info[0] = DualShock4PairingInfoFeatureReportId;
            int received = HIDapi.hid_get_feature_report(
                handle, info, new UIntPtr((uint)info.Length));
            if (received < DualShock4PairingHostAddressOffset + 6)
                return null;

            byte[] host = new byte[6];
            Buffer.BlockCopy(info, DualShock4PairingHostAddressOffset, host, 0, 6);
            return host;
        }

        private bool SendDualShock4PairingFeatureReport(byte[] hostMacLittleEndian,
                byte[] linkKey) {
            if (hostMacLittleEndian == null || hostMacLittleEndian.Length != 6 ||
                    linkKey == null || linkKey.Length != 16)
                return false;

            byte[] report = new byte[DualShock4SetPairingFeatureReportLen];
            report[0] = DualShock4SetPairingFeatureReportId;
            Buffer.BlockCopy(hostMacLittleEndian, 0, report,
                DualShock4SetPairingHostAddressOffset, 6);
            Buffer.BlockCopy(linkKey, 0, report, DualShock4SetPairingLinkKeyOffset, 16);
            try {
                return HIDapi.hid_send_feature_report(
                    handle, report, new UIntPtr((uint)report.Length)) >= 0;
            } finally {
                Array.Clear(report, 0, report.Length);
            }
        }

        // Verify by reading the record back rather than sleeping a fixed interval - the same
        // improvement the DualSense flow made over arbitrary delays.
        private bool WaitForPairingHost(byte[] expectedHost, int timeoutMs) {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true) {
                byte[] current = ReadControllerPairedHost();
                if (current != null && ByteArraysEqual(current, expectedHost))
                    return true;
                if (elapsed.ElapsedMilliseconds >= timeoutMs)
                    return false;
                Thread.Sleep(50);
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right) {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private bool dualShock4PairingInfoLogged;

        private void LogDualShock4PairingInfo() {
            // Called from ReceiveRaw right after the report ID resolves the transport, NOT from
            // Attach: isUSB is false until the first packet arrives (see the constructor - it is
            // re-derived per packet from the report ID), so an Attach-time call silently skipped
            // the USB check below every single time. Once per attach is enough.
            if (dualShock4PairingInfoLogged)
                return;
            dualShock4PairingInfoLogged = true;

            // Bluetooth framing for feature reports differs from USB; confirm the wired layout
            // first, which is also the only transport a pairing ceremony would run over.
            if (!isUSB) {
                LogDualShock4RawDump("DS4 pairing info 0x12: skipped, not USB");
                return;
            }

            byte[] info = new byte[DualShock4PairingInfoFeatureReportLen];
            info[0] = DualShock4PairingInfoFeatureReportId;
            int received = HIDapi.hid_get_feature_report(
                handle, info, new UIntPtr((uint)info.Length));
            if (received < 0) {
                LogDualShock4RawDump("DS4 pairing info 0x12: read failed ret=" + received);
                return;
            }

            var hex = new StringBuilder();
            for (int i = 0; i < Math.Min(received, info.Length); i++)
                hex.Append(info[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');

            LogDualShock4RawDump("DS4 pairing info 0x12: ret=" + received +
                " raw=" + hex.ToString().TrimEnd() +
                " deviceMac=" + DescribeLittleEndianAddress(info, received,
                    DualShock4PairingDeviceAddressOffset) +
                " hostMacAt10=" + DescribeLittleEndianAddress(info, received,
                    DualShock4PairingHostAddressOffset));
        }

        // The reports store addresses little-endian (GIMX's capture decodes
        // "8B 09 07 6D 66 1C" as 1C:66:6D:07:09:8B), so reverse for display.
        private static string DescribeLittleEndianAddress(byte[] buffer, int received, int offset) {
            if (buffer == null || received < offset + 6)
                return "(short read)";

            var text = new StringBuilder();
            for (int i = 5; i >= 0; i--) {
                text.Append(buffer[offset + i].ToString("X2", CultureInfo.InvariantCulture));
                if (i > 0)
                    text.Append(':');
            }
            return text.ToString();
        }

        // The DS4 power-off operation is likewise a Bluetooth-radio operation, not a command its
        // wired firmware honors. Preferred transport: Bluetooth leaves USB charge-only, so this
        // existing wireless path can still honor the shared long-press timer while plugged in.
        public override void PowerOff(bool shuttingDown = false) {
            if (state > state_.DROPPED && !isUSB) {
                StopBluetoothAudioStream();
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                state = state_.DROPPED;
            }
        }

        public override void PrepareLongPressPowerOff() {
            if (state > state_.DROPPED && !isUSB && PrefersBluetoothTransport()) {
                Program.mgr.PreserveChargeOnlyUsbAfterLongPressPowerOff(
                    ControllerMappings.ProfileIdFor(this));
            }
        }

        // Program.cs's periodic ApplyControllerProfileOptions only reaches attached controllers,
        // so a mid-stream disconnect would otherwise leave the stream worker thread parked in
        // BlockingCollection.Take forever and the input helper's WASAPI capture running with
        // nowhere for its frames to go. Stop explicitly here instead, before the handle closes.
        protected override void OnDetachingWhileAttached() {
            StopBluetoothAudioStream();
        }

        public override void SetLightColor(byte red, byte green, byte blue) {
            // ApplyControllerProfileOptions runs periodically. Avoid emitting the same effect
            // report (and, while audio is active, an ordered audio-lane barrier) every scan.
            if (lightbarTransportKnown && !lightbarUpdatePending &&
                lightbarRed == red && lightbarGreen == green &&
                lightbarBlue == blue)
                return;

            lightbarRed = red;
            lightbarGreen = green;
            lightbarBlue = blue;
            lightbarUpdatePending = true;
            if (lightbarTransportKnown) {
                lightbarUpdatePending = !SendDualShock4Lightbar(
                    lightbarRed, lightbarGreen, lightbarBlue);
            }
        }

        public override (byte Red, byte Green, byte Blue) GetLightColor() {
            return (lightbarRed, lightbarGreen, lightbarBlue);
        }

        private bool PrefersBluetoothTransport() {
            return ControllerMappings.PreferredTransport(ControllerMappings.ProfileIdFor(this)) ==
                ControllerMappings.PreferredTransportBluetooth;
        }

        protected override bool CanResolveDuplicate(Controller other) {
            DualShock4Controller dualShock4 = other as DualShock4Controller;
            return dualShock4 == null || dualShock4.lightbarTransportKnown;
        }

        protected override bool PreferExistingDuplicate(Controller other) {
            if (!(other is DualShock4Controller) || isUSB == other.isUSB)
                return false;

            return PrefersBluetoothTransport()
                ? isUSB && !other.isUSB
                : !isUSB && other.isUSB;
        }

        protected override void OnRetiredAsDuplicate(Controller other) {
            if (!(other is DualShock4Controller))
                return;

            if (isUSB && !other.isUSB) {
                Program.mgr.SuppressUsbControllerForBluetoothPreference(
                    path, ControllerMappings.ProfileIdFor(this));
            } else if (!isUSB && other.isUSB) {
                BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
            }
        }

        protected override void OnDuplicateRetired(Controller other) {
            if (!(other is DualShock4Controller))
                return;

            if (!isUSB && other.isUSB) {
                Program.mgr.SuppressUsbControllerForBluetoothPreference(
                    other.path, ControllerMappings.ProfileIdFor(this));
            } else if (isUSB && !other.isUSB) {
                bool disconnected = BluetoothRadio.DisconnectDevice(PadMacAddress.GetAddressBytes());
                form.AppendTextBox(disconnected
                    ? "Disconnected DualShock 4's Bluetooth link now that USB has taken over.\r\n"
                    : "Could not disconnect DualShock 4's Bluetooth link - it may keep reappearing.\r\n");
            }
        }

        // A DS4 connected over Bluetooth defaults to sending short, basic reports (still labeled
        // with report ID 0x01, the same ID USB uses) until the host signals it understands the
        // extended format - confirmed on real hardware: a raw capture showed buttons/sticks/
        // triggers working (they live in that short report) while every byte past them - battery,
        // gyro, touchpad - was constantly zero for the entire session, never populated at all, not
        // just misread. Reading feature report 0x02 once is the widely-used trick across
        // independent open-source DS4 implementations (the Linux kernel's hid-sony driver reads
        // this same report as part of its own Bluetooth bring-up) to make the controller switch to
        // sending extended 0x11 reports with the full field set. Best-effort: failure here doesn't
        // break the baseline fields already working, so errors are swallowed rather than surfaced.
        // UNVERIFIED against real hardware yet - if dualshock4_raw_debug.log still shows the
        // extended region as constant zero after this, this specific report ID/mechanism needs
        // re-checking.
        private const byte ExtendedReportModeFeatureReportId = 0x02;
        private const int ExtendedReportModeFeatureReportLen = 37;

        private void RequestExtendedReportMode() {
            try {
                byte[] buf = new byte[ExtendedReportModeFeatureReportLen];
                buf[0] = ExtendedReportModeFeatureReportId;
                HIDapi.hid_get_feature_report(handle, buf,
                    new UIntPtr((uint)ExtendedReportModeFeatureReportLen));
            } catch {
                // Best-effort - see method comment.
            }
        }

        private static readonly ConcurrentQueue<string> dualShock4RawDumpQueue = new ConcurrentQueue<string>();
        private static int dualShock4RawDumpWriterStarted;

        // Same async queue + background-writer pattern as DualSense's LogDualSenseRawDump - can't
        // block a controller's own Poll thread on file I/O. Gated behind DualShock4DebugLogging
        // (App.config, default off) so this doesn't write continuously for every user, only when
        // actually troubleshooting a real-hardware offset mismatch.
        internal void LogDualShock4RawDump(string message) {
            if (!Boolean.Parse(ConfigurationManager.AppSettings["DualShock4DebugLogging"]))
                return;

            if (Interlocked.CompareExchange(ref dualShock4RawDumpWriterStarted, 1, 0) == 0) {
                new Thread(DualShock4RawDumpWriterLoop) {
                    IsBackground = true,
                    Name = "DualShock4RawDumpWriter"
                }.Start();
            }
            dualShock4RawDumpQueue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, serial_number, message));
        }

        private static void DualShock4RawDumpWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "dualshock4_raw_debug.log");
            while (true) {
                Thread.Sleep(250);
                if (dualShock4RawDumpQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (dualShock4RawDumpQueue.TryDequeue(out string line))
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

            byte[] buf = dualShock4InputReport;
            // Audio reports must leave on their own 8ms schedule. A nominal 5ms timed read can
            // block for 14-19ms on Windows' Bluetooth HID transport; because sending happens on
            // this same thread, those overruns directly create 20-28ms gaps at the controller
            // despite a healthy encoded-frame queue. Attach already places this handle in
            // nonblocking mode, so use hid_read while streaming and yield briefly when no input
            // is ready. Outside audio playback, retain the ordinary timed read and its lower CPU
            // wakeup rate.
            bool nonblockingAudioRead = bluetoothAudioStreaming;
            long readStartTicks = Stopwatch.GetTimestamp();
            int ret = nonblockingAudioRead
                ? HIDapi.hid_read(handle, buf, new UIntPtr((uint)DualShock4MaxReportLen))
                : HIDapi.hid_read_timeout(handle, buf,
                    new UIntPtr((uint)DualShock4MaxReportLen), 5);
            double readMs = (Stopwatch.GetTimestamp() - readStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (readMs > 8.0)
                AudioDebugLog.Write("DS4Send", "slow hid_read_timeout ms=" + readMs.ToString("F1") +
                    " ret=" + ret);
            if (nonblockingAudioRead && ret == 0)
                Thread.Yield();

            // Packet LENGTH does not reliably indicate transport for this device the way it does
            // for DualSense - confirmed on real hardware: a real capture (dualshock4_raw_debug.log)
            // showed ret==78 with byte 0 still the USB-style single-byte report ID (0x01), not the
            // Bluetooth extended report's 0x11 - Windows/hidapi apparently pads this connection's
            // reads to the full 78-byte buffer regardless of the native report's real length.
            // Trusting ret==64-means-USB against that produced a 2-byte read misalignment that
            // explained three symptoms at once on real hardware: the right stick's data landing in
            // the left stick's output slot (both axes shifted by exactly one stick's worth of
            // bytes), the trigger analog values landing on button-region bytes, and a trigger press
            // appearing to also toggle the guide/PS button. The report-ID byte itself is the actual
            // signal, not length - checked directly below instead.
            if (ret == 64 || ret == 78) {
                byte reportId = buf[0];
                // 0x01: USB-style framing, single report-ID byte, real data starts at byte 1 -
                // confirmed against real hardware (see above). 0x11 is the Bluetooth extended
                // report's documented ID; its 2-byte-header offset (o=3) is not yet confirmed
                // against real BT hardware the way the 0x01 case now is - if BT data looks wrong
                // (sticks not centered near 128, dpad not reading neutral at rest), check
                // dualshock4_raw_debug.log for what reportId actually arrives as over BT.
                isUSB = reportId == 0x01;
                connection = isUSB ? 0x01 : 0x02;
                LogDualShock4PairingInfo();
                int reportOffset = isUSB ? 1 : 3;
                if (!lightbarTransportKnown) {
                    lightbarTransportKnown = true;
                    // A color written during the DS4's Bluetooth startup is accepted by HID but
                    // then overwritten as the controller finishes entering extended-report mode.
                    // Keep the profile color pending until that short initialization window ends.
                    lightbarApplyEarliestTimestamp = Stopwatch.GetTimestamp() +
                        Stopwatch.Frequency / 4;
                }
                if (lightbarUpdatePending) {
                    long lightbarNow = Stopwatch.GetTimestamp();
                    if (lightbarNow >= lightbarApplyEarliestTimestamp) {
                        if (!connectionLightFlashStarted) {
                            // DS4 finishes entering extended-report mode shortly after connect.
                            // Once ready, show the same blue confirmation as DualSense before
                            // applying this controller profile's assigned color.
                            string profileId = ControllerMappings.ProfileIdFor(this);
                            (byte flashRed, byte flashGreen, byte flashBlue) =
                                ControllerMappings.ApplyLightBrightness(profileId, 0, 0, 255);
                            if (SendDualShock4Lightbar(flashRed, flashGreen, flashBlue)) {
                                connectionLightFlashStarted = true;
                                lightbarApplyEarliestTimestamp = lightbarNow +
                                    Stopwatch.Frequency / 4;
                            }
                        } else {
                            lightbarUpdatePending = !SendDualShock4Lightbar(
                                lightbarRed, lightbarGreen, lightbarBlue);
                        }
                    }
                }

                // Same "TEMPORARY diagnostic" pattern DualSense.cs used to find its own real
                // offset mistakes - dump raw bytes to a file instead of guessing twice. Throttled
                // to ~4/sec. Remove once ParseDualShock4Report's offsets are confirmed correct
                // against real hardware.
                long nowTicks = Stopwatch.GetTimestamp();
                if ((nowTicks - lastDualShock4RawDumpTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                    lastDualShock4RawDumpTimestamp = nowTicks;
                    var hex = new StringBuilder();
                    for (int i = 0; i < ret; i++)
                        hex.Append(buf[i].ToString("X2")).Append(' ');
                    LogDualShock4RawDump("DS4 raw[" + ret + "]: " + hex.ToString());
                }

                ParseDualShock4Report(buf, reportOffset);

                // Transport is only trustworthy after seeing the report ID, so defer the DS4's
                // transport-specific factory calibration read until this first valid packet.
                if (!gyroCalibrationAttempted)
                    ReadGyroCalibration();

                BeginGyroStickDiagnosticReport();
                ExtractIMUValues(buf, reportOffset);
                AccumulateGyroStickDiagnosticSample();
                DoThingsWithButtons();

                // DS4 carries one IMU sample per input report, just like DualSense.
                ProcessGyroMouseSample(true);
                ProcessGyroStickSample(true);
                RecordGyroStickDiagnosticReport(buf[6 + reportOffset], Stopwatch.GetTimestamp());

                if (out_xbox != null) {
                    try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
                }
                // Previously never fed for a physical DualSense/DualShock4 (see DualSense.cs's
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
                return ret;
            }

            // An unexpected length means the report stream isn't what this parser expects -
            // matches DualSenseController's own handling, so a genuinely broken connection still
            // reaches DROPPED instead of sitting as a stale, frozen "connected" entry.
            if (ret > 0)
                return -1;
            return ret; // 0 = timeout, <0 = read error - Poll()'s state machine already handles both
        }

        // DualShock 4 report parsing for buttons/sticks/triggers/battery. o is the transport-skip
        // byte count from ReceiveRaw (1 USB / 3 BT). Populates the same
        // buttons[]/stick[]/stick2[]/triggerVal[] fields every controller populates, so no
        // DualShock4-specific code is needed downstream beyond the analog-trigger branch
        // MapToXbox360Input already gates on HasAnalogTriggers.
        private void ParseDualShock4Report(byte[] r, int o) {
            // Classic DS4 field order: left stick X/Y, right stick X/Y, three button bytes (dpad +
            // face buttons, shoulder/stick-click/Share/Options, PS/touchpad-click/counter), then
            // L2/R2 analog. This is the well-established public layout, not yet cross-checked
            // against a real hardware capture the way DualSense's byte offsets were - see class
            // comment. AddStickSample/CenterSticks/Y-inversion mirror DualSenseController exactly.
            UInt16[] stickRaw = { r[0 + o], r[1 + o] };
            CalibrationState.AddStickSample(this, false, stickRaw[0], stickRaw[1]);
            float[] stickResult = CenterSticks(stickRaw, stick_cal, deadzone,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]));
            stick[0] = stickResult[0];
            stick[1] = -stickResult[1];

            UInt16[] stick2Raw = { r[2 + o], r[3 + o] };
            CalibrationState.AddStickSample(this, true, stick2Raw[0], stick2Raw[1]);
            float[] stick2Result = CenterSticks(stick2Raw, stick2_cal, deadzone2,
                float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]));
            stick2[0] = stick2Result[0];
            stick2[1] = -stick2Result[1];

            int buttonFieldBase = 4;

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

                byte btn3 = r[buttonFieldBase + 2 + o];
                b[(int)Button.HOME] = (btn3 & 0x01) != 0; // PS button
                b[(int)Button.TOUCHPAD] = (btn3 & 0x02) != 0;
                // SL/SR have no DualShock 4 equivalent and remain false.

                buttons = b;
                CommitButtonState();
            }

            // DS4 common-report touch contacts begin at offsets 34 and 38. The shared pipeline
            // owns activation, pointer deltas, actions, click lockout, and output inhibition.
            SubmitTouchpadReport(ReadPackedTouchContact(r, 34 + o),
                                 ReadPackedTouchContact(r, 38 + o));

            // Classic layout: L2 analog immediately follows the button bytes, R2 right after.
            triggerVal[0] = r[buttonFieldBase + 3 + o];
            triggerVal[1] = r[buttonFieldBase + 4 + o];

            // status[0] at common-report offset 29: DS4Windows scales the low nibble against
            // 8 while wireless and 11 while a cable is connected, then clamps the result to 100.
            // Preserve that percentage and charge state while the shared Controller helper keeps
            // the legacy 0-4 DSU/color level in sync.
            byte batteryByte = r[29 + o];
            int nextHeadphoneState = (batteryByte & 0x20) != 0 ? 1 : 0;
            int previousHeadphoneState = Interlocked.Exchange(
                ref headphoneConnectionState, nextHeadphoneState);
            if (previousHeadphoneState != nextHeadphoneState) {
                // Profile reconciliation owns stream start/stop decisions. Queue it away from the
                // HID poll thread so plugging or unplugging headphones can transition immediately
                // without making input-report parsing perform capture-pipe or scan-lock work.
                ThreadPool.QueueUserWorkItem(_ => Program.mgr?.ApplyControllerProfileOptions());
            }
            int batteryPercent;
            ControllerBatteryStatus batteryState;
            DecodeBatteryStatus(batteryByte, out batteryPercent, out batteryState);
            SetBatteryStatus(batteryPercent, batteryState);
        }

        internal static void DecodeBatteryStatus(byte value, out int percent,
                                                 out ControllerBatteryStatus status) {
            int level = value & 0x0F;
            bool cableConnected = (value & 0x10) != 0;

            int maximumLevel = cableConnected ? 11 : 8;
            percent = Math.Min(level * 100 / maximumLevel, 100);
            status = cableConnected
                ? (percent >= 100 ? ControllerBatteryStatus.Full : ControllerBatteryStatus.Charging)
                : ControllerBatteryStatus.Discharging;
        }

        private const byte UsbGyroCalibrationFeatureReportId = 0x02;
        private const int UsbGyroCalibrationFeatureReportLen = 37;
        private const byte BluetoothGyroCalibrationFeatureReportId = 0x05;
        private const int BluetoothGyroCalibrationFeatureReportLen = 41;

        // Reads the DS4's factory IMU calibration after ReceiveRaw has established transport.
        // USB report 0x02 uses per-axis plus/minus pairs; Bluetooth report 0x05 groups all plus
        // values before all minus values and appends a CRC32 seeded with 0xA3.
        private void ReadGyroCalibration() {
            gyroCalibrationAttempted = true;
            bool usb = isUSB;
            int length = usb ? UsbGyroCalibrationFeatureReportLen : BluetoothGyroCalibrationFeatureReportLen;
            byte[] buf = new byte[length];
            buf[0] = usb ? UsbGyroCalibrationFeatureReportId : BluetoothGyroCalibrationFeatureReportId;

            bool verified = false;
            int attempts = usb ? 1 : 5;
            for (int attempt = 0; attempt < attempts && !verified; attempt++) {
                int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)length));
                if (ret < length)
                    continue;

                if (usb) {
                    verified = true;
                } else {
                    uint received = (uint)buf[37] | ((uint)buf[38] << 8) |
                                    ((uint)buf[39] << 16) | ((uint)buf[40] << 24);
                    verified = received == Crc32(0xA3, buf, length - 4);
                }
            }

            if (!verified) {
                LogGyroCalibrationFailure(attempts);
                return;
            }

            gyroPitchBias = ReadInt16(buf, 1);
            gyroYawBias = ReadInt16(buf, 3);
            gyroRollBias = ReadInt16(buf, 5);

            if (usb) {
                gyroPitchPlus = ReadInt16(buf, 7);
                gyroPitchMinus = ReadInt16(buf, 9);
                gyroYawPlus = ReadInt16(buf, 11);
                gyroYawMinus = ReadInt16(buf, 13);
                gyroRollPlus = ReadInt16(buf, 15);
                gyroRollMinus = ReadInt16(buf, 17);
            } else {
                gyroPitchPlus = ReadInt16(buf, 7);
                gyroYawPlus = ReadInt16(buf, 9);
                gyroRollPlus = ReadInt16(buf, 11);
                gyroPitchMinus = ReadInt16(buf, 13);
                gyroYawMinus = ReadInt16(buf, 15);
                gyroRollMinus = ReadInt16(buf, 17);
            }

            gyroSpeedPlus = ReadInt16(buf, 19);
            gyroSpeedMinus = ReadInt16(buf, 21);
            accelXPlus = ReadInt16(buf, 23);
            accelXMinus = ReadInt16(buf, 25);
            accelYPlus = ReadInt16(buf, 27);
            accelYMinus = ReadInt16(buf, 29);
            accelZPlus = ReadInt16(buf, 31);
            accelZMinus = ReadInt16(buf, 33);

            gyroCalibrationValid = gyroPitchPlus != gyroPitchMinus &&
                gyroYawPlus != gyroYawMinus && gyroRollPlus != gyroRollMinus &&
                accelXPlus != accelXMinus && accelYPlus != accelYMinus &&
                accelZPlus != accelZMinus && gyroSpeedPlus + gyroSpeedMinus != 0;
            if (!gyroCalibrationValid) {
                LogGyroCalibrationFailure(attempts);
                return;
            }

            LogDualShock4RawDump(string.Format(CultureInfo.InvariantCulture,
                "Gyro calibration OK ({0}): gyroBias=({1},{2},{3}) gyroPlusMinus=({4}/{5},{6}/{7},{8}/{9}) " +
                "gyroSpeedPlusMinus=({10}/{11}) accelPlusMinus=({12}/{13},{14}/{15},{16}/{17})",
                usb ? "USB" : "BT", gyroPitchBias, gyroYawBias, gyroRollBias,
                gyroPitchPlus, gyroPitchMinus, gyroYawPlus, gyroYawMinus, gyroRollPlus, gyroRollMinus,
                gyroSpeedPlus, gyroSpeedMinus,
                accelXPlus, accelXMinus, accelYPlus, accelYMinus, accelZPlus, accelZMinus));
        }

        private void LogGyroCalibrationFailure(int attempts) {
            gyroCalibrationValid = false;
            form.AppendTextBox("DualShock 4 gyro calibration read failed - using uncalibrated nominal scale.\r\n");
            LogDualShock4RawDump("Gyro calibration read failed after " + attempts + " attempt(s).");
        }

        private static short ReadInt16(byte[] buf, int offset) {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }

        // DS4 common-report offsets: timestamp 9, gyro 12/14/16, accel 18/20/22. The sensors use
        // the same physical mounting as DualSense, so the final canonical axis transform matches
        // DualSense while raw layout, timing, and calibration stay local to this definition.
        private void ExtractIMUValues(byte[] r, int o) {
            EnsureGyroOrientationBasis();

            ushort hardwareTimestamp = (ushort)(r[9 + o] | (r[10 + o] << 8));
            if (lastImuHardwareTimestamp.HasValue) {
                ushort deltaTicks = unchecked((ushort)(hardwareTimestamp - lastImuHardwareTimestamp.Value));
                float measuredDt = deltaTicks * (16.0f / 3000000.0f);
                measuredGyroSubSamplePeriod = Math.Max(MinGyroSubSamplePeriod,
                    Math.Min(MaxGyroSubSamplePeriod, measuredDt));
                lastLoggedImuDeltaTicks = deltaTicks;
            }
            lastImuHardwareTimestamp = hardwareTimestamp;

            gyr_r[0] = ReadInt16(r, 12 + o); // pitch/raw channel 0
            gyr_r[1] = ReadInt16(r, 14 + o); // yaw/raw channel 1
            gyr_r[2] = ReadInt16(r, 16 + o); // roll/raw channel 2
            acc_r[0] = ReadInt16(r, 18 + o);
            acc_r[1] = ReadInt16(r, 20 + o);
            acc_r[2] = ReadInt16(r, 22 + o);

            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[0], gyr_r[0]);
                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[1], gyr_r[1]);
                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[2], gyr_r[2]);
            }

            float pitchDegPerSec, yawDegPerSec, rollDegPerSec;
            if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]) && activeData != null) {
                pitchDegPerSec = (gyr_r[0] - activeData[0]) / GyroLsbPerDegPerSec;
                yawDegPerSec = (gyr_r[1] - activeData[1]) / GyroLsbPerDegPerSec;
                rollDegPerSec = (gyr_r[2] - activeData[2]) / GyroLsbPerDegPerSec;
            } else {
                pitchDegPerSec = CorrectGyroSample(gyr_r[0], gyroPitchBias, gyroPitchPlus, gyroPitchMinus);
                yawDegPerSec = CorrectGyroSample(gyr_r[1], gyroYawBias, gyroYawPlus, gyroYawMinus);
                rollDegPerSec = CorrectGyroSample(gyr_r[2], gyroRollBias, gyroRollPlus, gyroRollMinus);
            }

            float accelXG = CorrectAccelSample(acc_r[0], accelXPlus, accelXMinus);
            float accelYG = CorrectAccelSample(acc_r[1], accelYPlus, accelYMinus);
            float accelZG = CorrectAccelSample(acc_r[2], accelZPlus, accelZMinus);

            gyr_g.X = rollDegPerSec;
            gyr_g.Y = yawDegPerSec;
            gyr_g.Z = pitchDegPerSec;
            acc_g.X = accelZG;
            acc_g.Y = -accelYG;
            acc_g.Z = -accelXG;

            UpdateCanonicalGyroMouseImu();
            AHRS.SamplePeriod = measuredGyroSubSamplePeriod;
            const float degToRad = 0.0174533f;
            AHRS.Update(gyr_g.X * degToRad, gyr_g.Y * degToRad, gyr_g.Z * degToRad,
                        acc_g.X, acc_g.Y, acc_g.Z);

            long nowTicks = Stopwatch.GetTimestamp();
            if ((nowTicks - lastDualShock4ImuLogTimestamp) / (double)Stopwatch.Frequency >= 0.25) {
                lastDualShock4ImuLogTimestamp = nowTicks;
                LogDualShock4RawDump(string.Format(CultureInfo.InvariantCulture,
                    "IMU: raw gyro=({0},{1},{2}) raw accel=({3},{4},{5}) " +
                    "gyr_g=({6:F1},{7:F1},{8:F1})deg/s acc_g=({9:F2},{10:F2},{11:F2})g " +
                    "dt={12:F3}ms rawTicksDelta={13}",
                    gyr_r[0], gyr_r[1], gyr_r[2], acc_r[0], acc_r[1], acc_r[2],
                    gyr_g.X, gyr_g.Y, gyr_g.Z, acc_g.X, acc_g.Y, acc_g.Z,
                    measuredGyroSubSamplePeriod * 1000.0f, lastLoggedImuDeltaTicks));
            }
        }

        private float CorrectGyroSample(short raw, short bias, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / GyroLsbPerDegPerSec;

            float sensitivity = (gyroSpeedPlus + gyroSpeedMinus) * GyroLsbPerDegPerSec /
                                (plus - minus);
            return (raw - bias) * sensitivity / GyroLsbPerDegPerSec;
        }

        private float CorrectAccelSample(short raw, short plus, short minus) {
            if (!gyroCalibrationValid || plus == minus)
                return raw / AccelLsbPerG;

            float range = plus - minus;
            float bias = plus - range / 2.0f;
            return (raw - bias) * (2.0f * AccelLsbPerG / range) / AccelLsbPerG;
        }

        // DualShock 4 main output state. USB uses report 0x05 with the common payload directly
        // after the report ID. Bluetooth uses report 0x11, requests HID+CRC handling, and appends
        // an A2-seeded CRC32. Rumble, lightbar, and Bluetooth audio configuration share this one
        // report, so compose them from tracked state instead of letting feature-specific reports
        // silently replace each other.
        private bool SendDualShock4Lightbar(byte red, byte green, byte blue) {
            lock (bluetoothAudioStateLock) {
                return SendDualShock4OutputStateNoLock(false, true,
                    bluetoothAudioStreaming, red, green, blue);
            }
        }

        private void SendDualShock4Rumble(byte leftMotor, byte rightMotor) {
            lock (bluetoothAudioStateLock) {
                // Rumble-disabled profiles enqueue a stop during each reconciliation. Preserve a
                // real nonzero -> zero transition, but do not churn the shared output lane when
                // both physical motors are already stopped.
                if (leftMotor == 0 && rightMotor == 0 &&
                    currentLeftMotor == 0 && currentRightMotor == 0)
                    return;

                currentLeftMotor = leftMotor;
                currentRightMotor = rightMotor;
                SendDualShock4OutputStateNoLock(true, false,
                    bluetoothAudioStreaming);
            }
        }

        private bool SendDualShock4OutputStateNoLock(bool rumbleValid,
            bool lightbarValid, bool audioValid, byte? outputRed = null,
            byte? outputGreen = null, byte? outputBlue = null) {
            bool bt = !isUSB;
            int len = bt ? 78 : 32;
            byte[] buf = new byte[len];
            int commonOffset;
            if (bt) {
                buf[0] = 0x11;
                buf[1] = BtAudioControlFlags;
                // A0 keeps ordinary input reports alive while the speaker/headset lane is armed.
                // A conventional zero here replaces the controller's audio-plane state, so every
                // effect report emitted during streaming must retain A0 as well.
                buf[2] = audioValid ? (byte)0xA0 : (byte)0x00;
                commonOffset = 3;
            } else {
                buf[0] = 0x05;
                commonOffset = 1;
            }

            // An audio control report is a complete state publication. Assert current motor and
            // lightbar values with it so arming/rerouting audio cannot cancel either effect.
            if (audioValid) {
                rumbleValid = true;
                lightbarValid = true;
            }

            byte validFlags = 0;
            if (rumbleValid)
                validFlags |= 0x01;
            if (lightbarValid)
                validFlags |= 0x02;
            if (bt && audioValid)
                validFlags |= bluetoothAudioRouteHeadphones
                    ? BtAudioValidFlagsHeadphones
                    : BtAudioValidFlagsSpeaker;
            buf[commonOffset] = validFlags;

            // Common DS4 effect layout: flag bytes at +0/+1, reserved/copycat byte at +2,
            // right-fast motor at +3, left-heavy motor at +4, then RGB at +5/+6/+7.
            buf[commonOffset + 3] = currentRightMotor;
            buf[commonOffset + 4] = currentLeftMotor;
            buf[commonOffset + 5] = outputRed ?? lightbarRed;
            buf[commonOffset + 6] = outputGreen ?? lightbarGreen;
            buf[commonOffset + 7] = outputBlue ?? lightbarBlue;

            if (bt && audioValid) {
                int volumePercent = Math.Max(0,
                    Math.Min(100, bluetoothAudioVolumePercent));
                byte volume = (byte)(volumePercent * 0x50 / 100);
                if (bluetoothAudioRouteHeadphones) {
                    buf[21] = volume;
                    buf[22] = volume;
                } else {
                    buf[24] = volume;
                }
            }

            if (bt) {
                uint crc = Crc32(0xA2, buf, len - 4);
                buf[len - 4] = (byte)crc;
                buf[len - 3] = (byte)(crc >> 8);
                buf[len - 2] = (byte)(crc >> 16);
                buf[len - 1] = (byte)(crc >> 24);
                return SendDualShock4BluetoothControlReport(buf);
            }
            return HIDapi.hid_write(handle, buf, new UIntPtr((uint)len)) >= 0;
        }

        // Report 0x11 is the DS4's shared Bluetooth control lane: lightbar/effects and audio
        // configuration all use it. While speaker streaming is active, drain older SBC writes,
        // submit the control report on the dedicated audio handle, and wait for completion before
        // allowing newer SBC reports through. This prevents cross-handle reordering without ever
        // running hidapi reads and writes concurrently on BetterJoy's primary handle.
        private bool SendDualShock4BluetoothControlReport(byte[] report) {
            BluetoothAudioWritePool pool = bluetoothAudioWritePool;
            if (pool != null)
                return pool.SendControlBarrier(report);
            return HIDapi.hid_write(handle, report,
                new UIntPtr((uint)report.Length)) >= 0;
        }

        // A wired CUH-ZCT2 exposes a 32 kHz stereo USB Audio Class endpoint. Its firmware chooses
        // the built-in speaker or an attached headset; this report enables their volume fields.
        public override void PrepareUsbAudio(int volumePercent) {
            if (!isUSB || state <= state_.DROPPED)
                return;

            volumePercent = Math.Max(0, Math.Min(100, volumePercent));
            byte volume = (byte)(volumePercent * 0xFF / 100);
            byte[] buf = new byte[32];
            buf[0] = 0x05;
            buf[1] = 0xB0; // left/right headphone + speaker volume fields are valid
            buf[19] = volume;
            buf[20] = volume;
            buf[22] = volume;
            HIDapi.hid_write(handle, buf, new UIntPtr((uint)buf.Length));
        }

        // EXPERIMENTAL, PARTIALLY VALIDATED ON REAL HARDWARE: the transport below objectively
        // reduces Bluetooth audio dropouts but does not eliminate them. Native HID completion is
        // not a controller playback acknowledgement, so this must not be presented as reliable
        // until longer hardware testing proves otherwise. Bluetooth exposes no USB Audio Class endpoint
        // - there is nothing for ControllerAudio/WASAPI to open - so the only route to the built-in
        // speaker/headphone jack over BT is smuggling a real Bluetooth audio codec (SBC) inside HID
        // output reports. Report layout, field offsets, and SBC encoder parameters below are
        // transcribed from nefarius/DS4AudioStreamer (https://github.com/nefarius/DS4AudioStreamer,
        // MIT) - a real, currently maintained, complete implementation by the same author whose
        // ViGEmBus/HidHide binaries this project already bundles. Cross-validated against this
        // file's own already-shipped BT lightbar report: DS4AudioStreamer's lightbar RGB bytes land
        // at the exact same offsets (8/9/10) SendDualShock4Lightbar already uses, and both agree on
        // the Crc32(0xA2, ...) checksum this codebase already uses elsewhere. The SBC codec itself
        // comes from the native libsbc.dll (GPL-2.0, nefarius/libsbc) via SbcEncoder.cs.
        // Narrowed from the reference's proven 0xF3 (rumble|lightbar|flash|HP-L|HP-R|mic|speaker
        // all "valid" at once) - the reference's own documented flag meanings, just a subset of
        // them, kept out of the lightbar/rumble report so pressing the test-tone button (phase 1)
        // or streaming (phase 2) doesn't also blank the lightbar or cancel rumble as a side
        // effect. Selected per output path, not asserted together: an earlier version marked
        // BOTH HP and speaker "valid" always (0xB0, matching PrepareUsbAudio's USB convention),
        // zeroing whichever path wasn't in use rather than leaving its valid bit off entirely -
        // "valid but zero" is a different assertion to the hardware than "not specified at all",
        // and on real hardware that killed audio output completely, not just misrouted it.
        private const byte BtAudioValidFlagsHeadphones = 0x10 | 0x20; // HP-L | HP-R
        private const byte BtAudioValidFlagsSpeaker = 0x80; // Speaker

        // DS4Windows' validated loopback lane is one 16 kHz SBC frame per 0x12 report. Each frame
        // is 8 ms, so this avoids both the fragile 0x17 batching boundary and the transaction load
        // of a 32 kHz one-frame stream.
        private const byte BtAudioStreamProtocol = 0x12;
        private const int BtAudioStreamReportLen = 142;
        private const int BtAudioStreamFramesPerReport = 1;
        private const int BtAudioSbcFrameLength = 109;
        private const byte BtAudioBluetoothPollRate = 4;
        private const byte BtAudioControlFlags = 0xC0 | BtAudioBluetoothPollRate;
        private const byte BtAudioStreamFlags = 0x40 | BtAudioBluetoothPollRate;
        private const byte BtAudioOutputPathSpeaker = 0x02;
        // The payload target is separate from report 0x11's volume-valid flags. Target 0x02
        // feeds the controller speaker; Sony's headset-only target is 0x24. Merely changing the
        // volume fields while continuing to label every SBC packet as speaker audio can produce
        // signal at the jack, but it remains the speaker route rather than the proper stereo
        // headphone lane.
        private const byte BtAudioOutputPathHeadphones = 0x24;

        // Enables the DS4's Bluetooth audio DAC and sets its volume - report 0x11, same ID
        // SendDualShock4Lightbar's BT branch already uses. Must be sent once before any audio
        // stream reports; the controller ignores those otherwise. Only the selected path's valid
        // flag and volume byte(s) are ever written - see the const comment above for why the
        // other path is left unclaimed rather than valid-but-zero.
        public void PrepareBluetoothAudio(int volumePercent, bool routeToHeadphones) {
            if (isUSB || state <= state_.DROPPED)
                return;

            lock (bluetoothAudioStateLock) {
                bluetoothAudioVolumePercent = Math.Max(0,
                    Math.Min(100, volumePercent));
                bluetoothAudioRouteHeadphones = routeToHeadphones;
                // Publish one complete A0 state through the ordered control lane: audio routing,
                // volume, current motors, and current lightbar. No later feature-specific output
                // report is then allowed to replace the active audio mode with byte 2 == 0.
                if (!SendDualShock4OutputStateNoLock(true, true, true))
                    AudioDebugLog.Write("DS4Send",
                        "Bluetooth audio control write failed");
            }
        }

        // Phase 2: continuous live streaming, replacing phase 1's fixed-duration test tone.
        // EnqueueBluetoothAudioFrame receives already-SBC-encoded frames captured/encoded in the
        // per-session input helper process (see BluetoothAudioCapture.cs and
        // IJoyconHost.StartBluetoothAudioCapture - Session 0 cannot do WASAPI loopback capture
        // itself) and forwarded here over the existing helper pipe.
        //
        // Sending used to happen on a dedicated background thread, calling hid_write concurrently
        // with Poll's own hid_read_timeout on the same handle. hidapi's own docs call device
        // functions thread-unsafe when called concurrently, and on real hardware that concurrency
        // corrupted both the audio stream and unrelated input parsing (battery status went bad
        // too). Fixed by moving sending onto Poll itself (SendQueuedBluetoothAudioIfAny, called
        // once per Poll iteration, same pattern as SendQueuedRumbleIfAny) - every read and write
        // for this handle now happens from that one thread, like everything else in this class.
        // Poll's own natural iteration rate (driven by real HID read timing, typically a few ms)
        // paces output well enough on its own; no artificial sleep needed. One concurrent stream
        // per controller instance - each DualShock4Controller owns exactly one HID handle and one
        // Poll thread, so it was never going to send more than one anyway. Multiple different
        // controllers (e.g. this one and a DualSense at the same time) each run their own fully
        // independent capture/encode/send pipeline - see InputHelper.cs's per-pad audioCaptures.
        // Start/StopBluetoothAudioStream run on Program.cs's profile-reconciliation thread while
        // SendQueuedBluetoothAudioIfAny runs on Poll. Live profile changes can restart an existing
        // stream, so the old publish-once ordering is no longer sufficient to protect the pending
        // List. Serialize lifecycle transitions with Poll's pending-list work; encoded-frame
        // delivery itself remains a ConcurrentQueue because it comes from the helper pipe thread.
        private readonly ConcurrentQueue<byte[]> bluetoothAudioFrameQueue = new ConcurrentQueue<byte[]>();
        private readonly List<byte[]> bluetoothAudioPending = new List<byte[]>();
        private readonly object bluetoothAudioStateLock = new object();
        private volatile bool bluetoothAudioStreaming;
        private bool bluetoothAudioPrimed;
        private ushort bluetoothAudioPacketCounter;
        private int bluetoothAudioVolumePercent = -1;
        private string bluetoothAudioEndpointId = String.Empty;
        private bool bluetoothAudioRouteHeadphones;
        private byte bluetoothAudioOutputPath = BtAudioOutputPathSpeaker;

        // Match DS4Windows' current loopback policy: collect 144 ms of source, submit ten unique
        // one-frame reports up front to build 80 ms inside the controller decoder, and retain
        // another eight frames (64 ms) on the host. The old BetterJoy "prime" only waited for a
        // PC-side queue and then sent one 16 ms report; it did not protect the controller from a
        // later radio or scheduler gap at all.
        private const int BtAudioControllerPrimeReportCount = 10;
        private const int BtAudioRetainedSourceFrameCount = 8;
        private const int BtAudioStartupFrameCount =
            BtAudioControllerPrimeReportCount + BtAudioRetainedSourceFrameCount;
        private const int BtAudioMaximumQueuedFrames = BtAudioStartupFrameCount * 3;

        // One SBC block is 128 samples (BluetoothAudioCapture.cs's fixed 16kHz/8-subband/16-block
        // encoder config) = 8 ms of audio. Must match that file's encoder parameters exactly.
        private const double BtAudioMsPerBlock = 8.0;
        private Stopwatch bluetoothAudioStopwatch;
        private double bluetoothAudioNextSendDeadlineMs;
        private double bluetoothAudioLastDiagnosticMs;
        private int bluetoothAudioPrimeReportsRemaining;
        private byte[] bluetoothAudioSilenceFrame;
        private long bluetoothAudioSyntheticSilenceFrames;
        private long bluetoothAudioLastSummarySyntheticSilenceFrames;
        private double bluetoothAudioLastSendMs;
        private double bluetoothAudioMaximumSendGapMs;
        private double bluetoothAudioMaximumLatenessMs;
        private double bluetoothAudioMaximumWriteMs;
        private double bluetoothAudioPrimeStartedMs;
        private int bluetoothAudioSendsSinceSummary;
        private int bluetoothAudioMinimumPending;
        private int bluetoothAudioMaximumPending;
        // Audio owns a second, shareable HID file session. Windows can keep several output
        // reports in flight on this handle while the primary hidapi session remains exclusively
        // responsible for input. This is the production DS4Windows transport boundary and avoids
        // both same-handle thread races and hid_write's lack of native completion telemetry.
        private BluetoothAudioWritePool bluetoothAudioWritePool;
        // Owned exclusively by Poll. Windows requires MMCSS registration and reversion to happen
        // on the same thread, so Start/Stop only change bluetoothAudioStreaming and this handle is
        // acquired/released from SendQueuedBluetoothAudioIfAny itself.
        private IntPtr bluetoothAudioMmcssHandle;
        private bool bluetoothAudioMmcssAttempted;

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

        public bool IsStreamingBluetoothAudio => bluetoothAudioStreaming;

        public void StartBluetoothAudioStream(int volumePercent, string endpointId, bool routeToHeadphones) {
            lock (bluetoothAudioStateLock) {
                if (isUSB || state <= state_.DROPPED)
                    return;

                volumePercent = Math.Max(0, Math.Min(100, volumePercent));
                endpointId = endpointId ?? String.Empty;
                if (bluetoothAudioStreaming) {
                    bool settingsMatch = bluetoothAudioVolumePercent == volumePercent &&
                        String.Equals(bluetoothAudioEndpointId, endpointId, StringComparison.Ordinal) &&
                        bluetoothAudioRouteHeadphones == routeToHeadphones;
                    if (settingsMatch)
                        return;

                    // Program reconciles profile options periodically. A changed endpoint requires
                    // a new helper capture, while volume or route changes require a fresh report
                    // 0x11. Stop and restart as one ordered transition instead of leaving the old
                    // stream alive with stale settings. The lock is reentrant for this call.
                    StopBluetoothAudioStream();
                }

                DisposeBluetoothAudioWritePool();
                bluetoothAudioWritePool = BluetoothAudioWritePool.TryOpen(path,
                    out int audioHandleError);
                if (bluetoothAudioWritePool == null)
                    AudioDebugLog.Write("DS4Send", "Dedicated audio handle unavailable error=" +
                        audioHandleError + "; using primary HID handle fallback");
                else
                    AudioDebugLog.Write("DS4Send", "Dedicated overlapped audio handle opened");

                PrepareBluetoothAudio(volumePercent, routeToHeadphones);
                if (!form.StartBluetoothAudioCapture(PadId, endpointId)) {
                    // The service commonly discovers the controller before its session helper is
                    // connected. Do not publish a false active state: OnHelperConnected performs
                    // another profile reconciliation once capture is genuinely available.
                    bluetoothAudioVolumePercent = -1;
                    bluetoothAudioRouteHeadphones = false;
                    SendDualShock4OutputStateNoLock(true, true, false);
                    DisposeBluetoothAudioWritePool();
                    return;
                }

                while (bluetoothAudioFrameQueue.TryDequeue(out _)) { }
                bluetoothAudioPending.Clear();
                bluetoothAudioPacketCounter = 0;
                bluetoothAudioPrimed = false;
                bluetoothAudioStopwatch = Stopwatch.StartNew();
                bluetoothAudioNextSendDeadlineMs = 0;
                bluetoothAudioLastDiagnosticMs = 0;
                bluetoothAudioPrimeReportsRemaining = 0;
                bluetoothAudioSyntheticSilenceFrames = 0;
                bluetoothAudioLastSummarySyntheticSilenceFrames = 0;
                bluetoothAudioLastSendMs = 0;
                bluetoothAudioMaximumSendGapMs = 0;
                bluetoothAudioMaximumLatenessMs = 0;
                bluetoothAudioMaximumWriteMs = 0;
                bluetoothAudioPrimeStartedMs = 0;
                bluetoothAudioSendsSinceSummary = 0;
                bluetoothAudioMinimumPending = Int32.MaxValue;
                bluetoothAudioMaximumPending = 0;
                if (bluetoothAudioSilenceFrame == null)
                    bluetoothAudioSilenceFrame = CreateBluetoothAudioSilenceFrame();
                bluetoothAudioVolumePercent = volumePercent;
                bluetoothAudioEndpointId = endpointId;
                bluetoothAudioRouteHeadphones = routeToHeadphones;
                bluetoothAudioOutputPath = routeToHeadphones
                    ? BtAudioOutputPathHeadphones
                    : BtAudioOutputPathSpeaker;
                bluetoothAudioStreaming = true;
                AudioDebugLog.Write("DS4Send", "Start pad=" + PadId + " volume=" + volumePercent +
                    " endpoint=" + (String.IsNullOrEmpty(endpointId) ? "(default)" : endpointId) +
                    " headphones=" + routeToHeadphones);
            }
        }

        public void StopBluetoothAudioStream() {
            lock (bluetoothAudioStateLock) {
                // Sent unconditionally, before the bluetoothAudioStreaming check below: Start's
                // live-settings-change restart path (above) stops the old stream and starts a
                // new one as two separate fire-and-forget pipe messages with no delivery
                // confirmation, so this flag can end up false while the helper is still actually
                // capturing. OnDetachingWhileAttached is the one guaranteed last chance to clean
                // that up before the handle closes - a stop sent to an already-idle helper is a
                // harmless no-op (BluetoothAudioCapture.Stop is idempotent), but skipping it here
                // when it turns out to be needed orphans the capture with nothing left to ever
                // stop it.
                form.StopBluetoothAudioCapture(PadId);

                if (!bluetoothAudioStreaming)
                {
                    DisposeBluetoothAudioWritePool();
                    return;
                }

                bluetoothAudioStreaming = false;
                bluetoothAudioVolumePercent = -1;
                bluetoothAudioEndpointId = String.Empty;
                bluetoothAudioRouteHeadphones = false;
                // Explicitly release A0 audio-plane ownership while the ordered handle still
                // exists, restoring the latest motor/lightbar state in the same transaction.
                SendDualShock4OutputStateNoLock(true, true, false);
                DisposeBluetoothAudioWritePool();
                AudioDebugLog.Write("DS4Send", "Stop pad=" + PadId);
            }
        }

        // Controller's generic feedback queue already normalizes virtual-controller feedback to
        // an amplitude. DS4 still needs its own wire-format drain: right is the light/fast motor,
        // left is the heavy/slow motor. Until now this override was absent, so queued DS4 rumble
        // was never sent to the physical controller at all.
        protected override void SendQueuedRumbleIfAny() {
            if (rumble_obj.queue.Count == 0)
                return;

            float amp = rumble_obj.queue.Dequeue()[2];
            byte motor = (byte)(Math.Max(0f, Math.Min(1f, amp)) * 255f);
            SendDualShock4Rumble(motor, motor);
        }

        private void DisposeBluetoothAudioWritePool() {
            BluetoothAudioWritePool pool = bluetoothAudioWritePool;
            bluetoothAudioWritePool = null;
            if (pool != null)
                pool.Dispose();
        }

        // Called from HeadlessJoyconHost's helper-pipe read thread as frames arrive - the only
        // part of this feature that legitimately runs off the Poll thread, since it only touches
        // an in-memory queue, never the HID handle itself.
        public void EnqueueBluetoothAudioFrame(byte[] frame) {
            if (frame == null || frame.Length != BtAudioSbcFrameLength)
                return;

            bluetoothAudioFrameQueue.Enqueue(frame);
            while (bluetoothAudioFrameQueue.Count > BtAudioMaximumQueuedFrames)
                bluetoothAudioFrameQueue.TryDequeue(out _);
        }

        // Called once per Poll iteration. Startup deliberately presents ten reports as quickly as
        // the controller accepts them to create a real hardware cushion; steady state then sends
        // one 8 ms frame at each wall-clock deadline. This never sleeps or drains the whole source
        // queue in one call, so controller input continues to share this single HID-owning thread.
        protected override void SendQueuedBluetoothAudioIfAny() {
            lock (bluetoothAudioStateLock) {
                if (!bluetoothAudioStreaming) {
                    ReleaseBluetoothAudioPollScheduling();
                    return;
                }

                EnsureBluetoothAudioPollScheduling();

                int dequeued = 0;
                while (bluetoothAudioFrameQueue.TryDequeue(out byte[] frame)) {
                    bluetoothAudioPending.Add(frame);
                    dequeued++;
                }
                if (bluetoothAudioPending.Count > BtAudioMaximumQueuedFrames)
                    bluetoothAudioPending.RemoveRange(0,
                        bluetoothAudioPending.Count - BtAudioMaximumQueuedFrames);

                double nowMs = bluetoothAudioStopwatch.Elapsed.TotalMilliseconds;

                if (!bluetoothAudioPrimed) {
                    if (bluetoothAudioPending.Count < BtAudioStartupFrameCount)
                        return;
                    bluetoothAudioPrimed = true;
                    bluetoothAudioPrimeReportsRemaining = BtAudioControllerPrimeReportCount;
                    bluetoothAudioPrimeStartedMs = nowMs;
                    AudioDebugLog.Write("DS4Send", "Source primed pending=" +
                        bluetoothAudioPending.Count + " controllerPrimeReports=" +
                        bluetoothAudioPrimeReportsRemaining);
                }

                if (bluetoothAudioPrimeReportsRemaining > 0) {
                    if (bluetoothAudioPending.Count < BtAudioStreamFramesPerReport)
                        return;

                    double primeWriteMs = SendBluetoothAudioFrame(
                        bluetoothAudioPending[0], ref bluetoothAudioPacketCounter,
                        out bool primeSubmitted);
                    bluetoothAudioMaximumWriteMs = Math.Max(
                        bluetoothAudioMaximumWriteMs, primeWriteMs);
                    if (!primeSubmitted)
                        return;
                    bluetoothAudioPending.RemoveAt(0);
                    bluetoothAudioPrimeReportsRemaining--;
                    if (bluetoothAudioPrimeReportsRemaining == 0) {
                        nowMs = bluetoothAudioStopwatch.Elapsed.TotalMilliseconds;
                        bluetoothAudioNextSendDeadlineMs = nowMs + BtAudioMsPerBlock;
                        AudioDebugLog.Write("DS4Send", "Controller primed retained=" +
                            bluetoothAudioPending.Count + " nextDeadlineMs=" +
                            bluetoothAudioNextSendDeadlineMs.ToString("F1") +
                            " primeElapsedMs=" + (nowMs - bluetoothAudioPrimeStartedMs).ToString("F1") +
                            " maxWriteMs=" + bluetoothAudioMaximumWriteMs.ToString("F2"));
                        bluetoothAudioMaximumWriteMs = 0;
                    }
                    return;
                }

                if (nowMs < bluetoothAudioNextSendDeadlineMs)
                    return;

                int batchSize = BtAudioStreamFramesPerReport;
                bool syntheticSilence = bluetoothAudioPending.Count == 0;
                byte[] nextFrame = syntheticSilence
                    ? bluetoothAudioSilenceFrame
                    : bluetoothAudioPending[0];
                if (nextFrame == null)
                    return;

                double latenessMs = nowMs - bluetoothAudioNextSendDeadlineMs;
                double sendGapMs = bluetoothAudioLastSendMs <= 0
                    ? 0
                    : nowMs - bluetoothAudioLastSendMs;
                double writeMs = SendBluetoothAudioFrame(nextFrame,
                    ref bluetoothAudioPacketCounter, out bool submitted);
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
                bluetoothAudioMaximumWriteMs = Math.Max(
                    bluetoothAudioMaximumWriteMs, writeMs);
                bluetoothAudioSendsSinceSummary++;
                bluetoothAudioMinimumPending = Math.Min(
                    bluetoothAudioMinimumPending, bluetoothAudioPending.Count);
                bluetoothAudioMaximumPending = Math.Max(
                    bluetoothAudioMaximumPending, bluetoothAudioPending.Count);

                bool anomalousSend = sendGapMs > BtAudioMsPerBlock * 1.5 ||
                    latenessMs > BtAudioMsPerBlock / 2 || writeMs > 5.0;
                bool periodicSummary = nowMs - bluetoothAudioLastDiagnosticMs >= 1000.0;
                BluetoothAudioWriteStatus hidStatus =
                    default(BluetoothAudioWriteStatus);
                bool hasHidStatus = bluetoothAudioWritePool != null &&
                    (anomalousSend || periodicSummary);
                if (hasHidStatus)
                    hidStatus = bluetoothAudioWritePool.GetStatus();
                bool transportAnomaly = hasHidStatus &&
                    (hidStatus.IntervalSaturations > 0 ||
                     hidStatus.CompletionFailures > 0 ||
                     hidStatus.MaximumIntervalCompletionMs > 16.0 ||
                     hidStatus.OldestPendingMs > 16.0);
                if (anomalousSend || transportAnomaly || periodicSummary) {
                    long intervalSilence = bluetoothAudioSyntheticSilenceFrames -
                        bluetoothAudioLastSummarySyntheticSilenceFrames;
                    AudioDebugLog.Write("DS4Send", "sends=" +
                        bluetoothAudioSendsSinceSummary +
                        " maxGapMs=" + bluetoothAudioMaximumSendGapMs.ToString("F2") +
                        " maxLateMs=" + bluetoothAudioMaximumLatenessMs.ToString("F2") +
                        " maxWriteMs=" + bluetoothAudioMaximumWriteMs.ToString("F2") +
                        " pendingMinMax=" +
                        (bluetoothAudioMinimumPending == Int32.MaxValue ? 0 :
                            bluetoothAudioMinimumPending) + "/" + bluetoothAudioMaximumPending +
                        " dequeuedLast=" + dequeued +
                        " silence=" + intervalSilence + "/" +
                        bluetoothAudioSyntheticSilenceFrames +
                        (hasHidStatus
                            ? " hidPending=" + hidStatus.PendingWrites +
                              " hidOldestMs=" + hidStatus.OldestPendingMs.ToString("F2") +
                              " hidMaxCompleteMs=" +
                                  hidStatus.MaximumIntervalCompletionMs.ToString("F2") +
                              " hidSaturated=" + hidStatus.IntervalSaturations +
                              " hidFailures=" + hidStatus.CompletionFailures +
                              " hidShort=" + hidStatus.ShortTransfers
                            : " hid=primary-sync") +
                        " anomaly=" + (anomalousSend || transportAnomaly));
                    bluetoothAudioLastDiagnosticMs = nowMs;
                    bluetoothAudioLastSummarySyntheticSilenceFrames =
                        bluetoothAudioSyntheticSilenceFrames;
                    bluetoothAudioMaximumSendGapMs = 0;
                    bluetoothAudioMaximumLatenessMs = 0;
                    bluetoothAudioMaximumWriteMs = 0;
                    bluetoothAudioSendsSinceSummary = 0;
                    bluetoothAudioMinimumPending = Int32.MaxValue;
                    bluetoothAudioMaximumPending = 0;
                }

                // Anchor the next deadline off the target schedule, not "now" - if this call ran a
                // little late, catch up gradually via subsequent early-arriving Poll iterations
                // rather than permanently shifting the whole schedule later.
                bluetoothAudioNextSendDeadlineMs += batchSize * BtAudioMsPerBlock;

                // A pause longer than the controller cushion cannot be repaired by flooding stale
                // reports. Rebase and resume the 8 ms presentation clock instead.
                if (nowMs - bluetoothAudioNextSendDeadlineMs >
                    BtAudioControllerPrimeReportCount * BtAudioMsPerBlock)
                    bluetoothAudioNextSendDeadlineMs = nowMs + BtAudioMsPerBlock;
            }
        }

        private void EnsureBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero || bluetoothAudioMmcssAttempted)
                return;

            bluetoothAudioMmcssAttempted = true;
            try {
                uint taskIndex = 0;
                bluetoothAudioMmcssHandle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                    AvSetMmThreadPriority(bluetoothAudioMmcssHandle, AvrtPriority.Critical);
                    AudioDebugLog.Write("DS4Send", "Poll thread registered with MMCSS Pro Audio");
                }
            } catch (DllNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            } catch (EntryPointNotFoundException) {
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
        }

        private void ReleaseBluetoothAudioPollScheduling() {
            if (bluetoothAudioMmcssHandle != IntPtr.Zero) {
                // This method is reached from SendQueuedBluetoothAudioIfAny on the same Poll
                // thread that registered the handle, as required by avrt.dll.
                try { AvRevertMmThreadCharacteristics(bluetoothAudioMmcssHandle); } catch { }
                bluetoothAudioMmcssHandle = IntPtr.Zero;
            }
            bluetoothAudioMmcssAttempted = false;
        }

        private static byte[] CreateBluetoothAudioSilenceFrame() {
            try {
                using (var encoder = new SbcEncoder(16000, SbcSubBandCount.Sb8, 48,
                    SbcChannelMode.JointStereo, SbcAllocationMode.Snr, SbcBlockCount.Blk16)) {
                    var pcm = new byte[encoder.CodeSize];
                    var frame = new byte[encoder.FrameSize];
                    int encoded = encoder.Encode(pcm, frame);
                    return encoded == BtAudioSbcFrameLength ? frame : null;
                }
            } catch {
                // The real capture encoder uses the same native library. If it is unavailable,
                // audio startup will fail independently; simply leave the continuity fallback
                // disabled rather than allowing a secondary initialization failure to escape.
                return null;
            }
        }

        private double SendBluetoothAudioFrame(byte[] frame, ref ushort packetCounter,
            out bool submitted) {
            byte protocol = BtAudioStreamProtocol;
            int reportLen = BtAudioStreamReportLen;

            byte[] buf = new byte[reportLen];
            buf[0] = protocol;
            // Preserve the 4 ms input interval on every audio report. Sending bare 0x40 here
            // repeatedly resets the controller to interval zero, creating avoidable inbound
            // Bluetooth traffic that competes with the outbound SBC stream.
            buf[1] = BtAudioStreamFlags;
            buf[2] = 0xa0; // preserve ordinary HID input while carrying outbound SBC audio
            buf[3] = (byte)(packetCounter & 0xFF);
            buf[4] = (byte)((packetCounter >> 8) & 0xFF);
            buf[5] = bluetoothAudioOutputPath;

            Buffer.BlockCopy(frame, 0, buf, 6, frame.Length);

            uint crc = Crc32(0xA2, buf, reportLen - 4);
            buf[reportLen - 4] = (byte)crc;
            buf[reportLen - 3] = (byte)(crc >> 8);
            buf[reportLen - 2] = (byte)(crc >> 16);
            buf[reportLen - 1] = (byte)(crc >> 24);

            // Submission is intentionally separate from completion. The dedicated overlapped
            // pool exposes actual HIDCLASS completion latency in the once-per-second summary;
            // hid_write's return time on the fallback handle cannot provide that information.
            long writeStartTicks = Stopwatch.GetTimestamp();
            bool hardFailure = false;
            submitted = bluetoothAudioWritePool != null
                ? bluetoothAudioWritePool.TrySend(buf, out hardFailure)
                : HIDapi.hid_write(handle, buf, new UIntPtr((uint)reportLen)) >= 0;
            double writeMs = (Stopwatch.GetTimestamp() - writeStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (!submitted && hardFailure)
                AudioDebugLog.Write("DS4Send", "Bluetooth audio native write failed");
            else if (writeMs > 5.0)
                AudioDebugLog.Write("DS4Send", "slow audio submit ms=" +
                    writeMs.ToString("F1"));

            if (submitted)
                packetCounter++;
            return writeMs;
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

        // A DS4 Bluetooth audio report is an ordinary HID output report, but Windows does not
        // guarantee that a synchronous hid_write return corresponds to delivery through the
        // Bluetooth stack. Keep a bounded set of native OVERLAPPED writes on a second shared HID
        // file session, matching DS4Windows' production transport. The 640-byte backing buffers
        // are intentional: genuine CUH-ZCT2 HIDCLASS completions have been observed reporting up
        // to 547 bytes even for shorter variable-length output reports.
        private sealed class BluetoothAudioWritePool : IDisposable {
            private const int SlotCount = 32;
            private const int NativeBackingBufferLength = 640;
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 3;
            private const uint FileFlagOverlapped = 0x40000000;
            private const uint WaitObject0 = 0;
            private const uint WaitTimeout = 258;
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
                                "Could not create a DS4 audio completion event.");
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

                // Write-only: this pool only ever calls WriteFile. See DualSense.cs's identical
                // pool for why GENERIC_READ was dropped - unverified as the actual duplicate-input
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

                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBefore) {
                        hardFailure = true;
                        return false;
                    }

                    int slot = FindFreeSlotNoLock();
                    if (slot < 0) {
                        intervalSaturations++;
                        return false;
                    }

                    if (!SubmitNoLock(slot, report, out int submitError)) {
                        completionFailures++;
                        hardFailure = true;
                        AudioDebugLog.Write("DS4Send",
                            "Overlapped audio submit failed error=" + submitError);
                        return false;
                    }
                    return true;
                }
            }

            public bool SendControlBarrier(byte[] report) {
                if (report == null || report.Length == 0 ||
                    report.Length > NativeBackingBufferLength)
                    return false;

                lock (gate) {
                    if (disposed)
                        return false;

                    long failuresBeforeDrain = completionFailures;
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBeforeDrain)
                        return false;
                    for (int slot = 0; slot < SlotCount; slot++) {
                        if (!outstanding[slot])
                            continue;
                        if (WaitForSingleObject(events[slot], 1000) != WaitObject0)
                            return false;
                    }
                    ReapCompletedNoLock();
                    if (completionFailures != failuresBeforeDrain)
                        return false;

                    int controlSlot = FindFreeSlotNoLock();
                    int submitError = 0;
                    if (controlSlot < 0 ||
                        !SubmitNoLock(controlSlot, report, out submitError)) {
                        if (submitError != 0)
                            AudioDebugLog.Write("DS4Send",
                                "Overlapped audio control submit failed error=" + submitError);
                        return false;
                    }

                    if (WaitForSingleObject(events[controlSlot], 1000) !=
                        WaitObject0) {
                        CancelIoEx(nativeHandle, overlapped[controlSlot]);
                        return false;
                    }

                    long failuresBefore = completionFailures;
                    ReapCompletedNoLock();
                    return completionFailures == failuresBefore &&
                        !outstanding[controlSlot];
                }
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

            private int FindFreeSlotNoLock() {
                for (int offset = 0; offset < SlotCount; offset++) {
                    int candidate = (nextSlot + offset) % SlotCount;
                    if (!outstanding[candidate])
                        return candidate;
                }
                return -1;
            }

            private bool SubmitNoLock(int slot, byte[] report,
                out int submitError) {
                Array.Clear(buffers[slot], 0, buffers[slot].Length);
                Buffer.BlockCopy(report, 0, buffers[slot], 0, report.Length);
                ResetEvent(events[slot]);
                ResetOverlapped(slot);
                bool completedSynchronously = WriteFile(nativeHandle,
                    pins[slot].AddrOfPinnedObject(), (uint)report.Length,
                    IntPtr.Zero, overlapped[slot]);
                submitError = completedSynchronously ? 0 :
                    Marshal.GetLastWin32Error();
                if (!completedSynchronously && submitError != ErrorIoPending) {
                    SetEvent(events[slot]);
                    return false;
                }

                outstanding[slot] = true;
                expectedLengths[slot] = report.Length;
                submittedTimestamps[slot] = Stopwatch.GetTimestamp();
                nextSlot = (slot + 1) % SlotCount;
                return true;
            }

            private void ReapCompletedNoLock() {
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
                        continue;
                    }

                    // HIDCLASS legitimately reports zero bytes for some output completions.
                    // A nonzero short completion is retained as telemetry, not retried (which
                    // would duplicate an SBC packet and make the artifact worse).
                    if (transferred != 0 && transferred < expectedLengths[slot])
                        shortTransfers++;
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
                        // The kernel can still reference this OVERLAPPED and pinned buffer. A
                        // bounded teardown leak is safer than freeing live native I/O memory.
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
