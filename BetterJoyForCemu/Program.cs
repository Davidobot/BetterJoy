using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using Microsoft.Win32;
using BetterJoyForCemu.Collections;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.ViGEm.Client;
using static BetterJoyForCemu._3rdPartyControllers;
using static BetterJoyForCemu.HIDapi;

namespace BetterJoyForCemu {
    public partial class JoyconManager {
        public bool EnableIMU = true;
        public bool EnableLocalize = false;

        private const ushort vendor_id = 0x57e;
        private const ushort product_l = 0x2006;
        private const ushort product_r = 0x2007;
        private const ushort product_pro = 0x2009;
        private const ushort product_snes = 0x2017;
        private const ushort product_n64 = 0x2019;

        private const ushort vendor_sony = 0x054C;
        private const ushort product_dualsense = 0x0CE6;
        private const ushort product_dualsense_edge = 0x0DF2;
        // DualShock 4 v2 (CUH-ZCT2x) only - v1 (CUH-ZCT1x, PID 0x05C4) is deliberately not
        // detected here: that PID is identical to vigemDs4ProductId below, ViGEmBus's own default
        // emulated DS4 identity. A real v1 controller would be indistinguishable from BetterJoy's
        // own virtual DS4 output at the VID/PID level with the check below, so it's excluded
        // rather than risk either mis-adding our own output as a new physical controller or
        // silently failing to filter it. Needs a real disambiguator (interface path, bus type, or
        // a verified manufacturer/product string difference) confirmed on real hardware before v1
        // can be added safely.
        private const ushort product_dualshock4_v2 = 0x09CC;

        // ViGEmBus's default emulated identities (CreateXbox360Controller()/CreateDS4Controller()
        // are called with no VID/PID override anywhere in this codebase - see
        // VirtualOutput.OutputControllerXbox360/OutputControllerDualShock4 - so BetterJoy's own
        // virtual output always shows up under these). Windows exposes that virtual pad through a
        // HID interface too, for DirectInput compatibility, which otherwise passes the same
        // generic "is this a gamepad" usage-page/usage check real controllers do - letting
        // AutoAddControllers mistake BetterJoy's own output for a brand new physical controller,
        // whose raw input then just mirrors whatever BetterJoy already sent it, one poll tick
        // later. Checked ahead of the 3rd-party allowlist/auto-add below, not after, so this never
        // becomes a Joycon object in the first place, however AutoAddControllers is configured.
        private const ushort vigemXbox360VendorId = 0x045E;
        private const ushort vigemXbox360ProductId = 0x028E;
        private const ushort vigemDs4VendorId = 0x054C;
        private const ushort vigemDs4ProductId = 0x05C4;

        public ConcurrentList<Controller> j { get; private set; } // Array of all connected controllers
        static JoyconManager instance;

        public IJoyconHost form;

        System.Timers.Timer controllerCheck;

        // Guards a scan pass (CleanUp + CheckForNewControllers) end to end. System.Timers.Timer
        // can fire Elapsed again before a slow pass has finished (HidHide retries, PnP calls),
        // so this also prevents two passes running concurrently against the same controller
        // list/hiddenInstanceIds - not just the shutdown race StopScanning uses it for below.
        readonly object scanLock = new object();

        // A Sony profile which prefers Bluetooth deliberately retires its duplicate wired HID
        // interface while leaving the cable available for charging. Without a quarantine,
        // CleanUp closes that interface and the next scan immediately opens it again. Remember
        // both the path and profile so changing the dropdown back to USB releases it live; an
        // unplug also removes the entry, with no persistent Windows device-disable state.
        readonly object suppressedUsbControllerLock = new object();
        readonly Dictionary<string, string> suppressedUsbControllerProfiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> suppressedUsbPowerOffGraceUntil =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // "USB sleep on connect" is an INITIAL-connection policy. The pad object that applied it is
        // destroyed and rebuilt every time the controller is slept and woken, so a per-object "we
        // did this" flag can never enforce "once" - the rebuilt pad re-applies it and puts the
        // controller straight back to sleep on every wake. Tracked here instead, keyed by the wired
        // path, and released only when that path actually leaves enumeration (a real unplug) - which
        // is what makes the next plug-in genuinely "initial" again. Same reasoning as
        // deliberatePowerOffMacs below.
        readonly HashSet<string> usbSleepOnConnectAppliedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The DualSense firmware ALWAYS runs its own orange flash for about 2-2.5 seconds the moment
        // the controller is plugged in, whatever transport is then used. Engaging the controller
        // before that flash finishes - opening its interface, running the pairing ceremony,
        // attaching it - bleeds red into it, which is the red/pink leak seen on connect. So hold off
        // entirely until the flash has had time to complete: the scan simply skips the device and a
        // later pass (the timer runs every 2s) picks it up once settled. Measured from when the path
        // is FIRST enumerated, which is the real plug-in moment - unlike LightbarConnectSettleSeconds,
        // which starts at IMU_DATA_OK and so only ever begins after we have already engaged.
        const long DualSenseFirmwareConnectFlashSettleMs = 2500;
        readonly Dictionary<string, long> deviceFirstSeenAt =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public bool IsDualSenseFirmwareConnectSettled(string devicePath) {
            if (String.IsNullOrEmpty(devicePath))
                return true;
            long now = Stopwatch.GetTimestamp();
            lock (suppressedUsbControllerLock) {
                if (!deviceFirstSeenAt.TryGetValue(devicePath, out long firstSeen)) {
                    deviceFirstSeenAt[devicePath] = now;
                    return false;
                }
                return (now - firstSeen) >= Stopwatch.Frequency *
                    DualSenseFirmwareConnectFlashSettleMs / 1000L;
            }
        }

        // Returns true only the FIRST time this path is marked - the caller applies the policy only
        // when it wins that race, so a wake (or any later re-attach) never re-applies it.
        public bool TryMarkUsbSleepOnConnectApplied(string devicePath) {
            if (String.IsNullOrEmpty(devicePath))
                return true;
            lock (suppressedUsbControllerLock)
                return usbSleepOnConnectAppliedPaths.Add(devicePath);
        }
        // Wired HID paths that BetterJoy deliberately parked in charge-only / wait-for-press
        // state. This is separate from PreferredTransport=Bluetooth suppression: USB-only
        // pseudo-sleep must keep the same path unadopted while the wake monitor waits for PS/Home.
        readonly HashSet<string> chargeOnlyParkedUsbPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // An Enabled automatic-BT pairing attempt is confirmed by the LIVE signal - the controller's
        // Bluetooth pad actually coming up (IMU_DATA_OK on the 00001124 interface) - not by "a key
        // exists". The USB-side ceremony records the attempt here (keyed by MAC); the reconciliation
        // confirms it against a live BT pad and sleeps that pad, or lets the attempt timeout according
        // to the caller's retry policy. Shares suppressedUsbControllerLock. Keyed by
        // BitConverter.ToString(mac).
        sealed class BluetoothPairingAttempt {
            public string profileId;
            public string wiredPath;
            public long deadlineTimestamp;
            public int attemptCount;
            public bool awaitingReattempt;
            public bool sleepOnConnectAfterConfirmation;
        }
        readonly Dictionary<string, BluetoothPairingAttempt> pendingBluetoothPairingConfirmations =
            new Dictionary<string, BluetoothPairingAttempt>(StringComparer.OrdinalIgnoreCase);
        // We confirm on a STABLE link, not a blip: the BT pad must hold IMU_DATA_OK for this dwell
        // before we sleep it (so we don't cut the connection off mid-pairing). The confirm window
        // (retry timeout) must comfortably exceed pad-up (~2s) + dwell so a settling connection
        // isn't retried out from under itself; failed attempts (pad never holds) still retry within
        // it. More (cheaper) attempts to win faster.
        const long BluetoothPairingStableDwellSeconds = 3L;
        const long BluetoothPairingConfirmWindowSeconds = 10L;
        const int BluetoothPairingMaxAttempts = 6;
        readonly object chargeOnlyWakeMonitorLock = new object();
        int activeChargeOnlyWakeMonitors;

        // Sony firmware can briefly remove and recreate its wired HID interface while an active
        // Bluetooth connection is intentionally shut down. Keep the already charge-only path
        // quarantined across several scan passes so BetterJoy does not wake the USB interface
        // and steal the firmware-owned orange charging indication.
        public void PreserveChargeOnlyUsbAfterLongPressPowerOff(string profileId) {
            if (String.IsNullOrEmpty(profileId))
                return;

            long graceUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10L;
            lock (suppressedUsbControllerLock) {
                foreach (KeyValuePair<string, string> entry in suppressedUsbControllerProfiles) {
                    if (String.Equals(entry.Value, profileId, StringComparison.Ordinal))
                        suppressedUsbPowerOffGraceUntil[entry.Key] = graceUntil;
                }
            }
        }

        public void SuppressUsbControllerForBluetoothPreference(
                string devicePath, string profileId, long removalGraceSeconds = 0L) {
            if (String.IsNullOrEmpty(devicePath) || String.IsNullOrEmpty(profileId))
                return;

            lock (suppressedUsbControllerLock) {
                suppressedUsbControllerProfiles[devicePath] = profileId;
                if (removalGraceSeconds > 0)
                    suppressedUsbPowerOffGraceUntil[devicePath] =
                        Stopwatch.GetTimestamp() +
                        Stopwatch.Frequency * removalGraceSeconds;
                else
                    suppressedUsbPowerOffGraceUntil.Remove(devicePath);
            }
        }

        public void MarkChargeOnlyUsbParked(string devicePath, string profileId) {
            if (String.IsNullOrEmpty(devicePath) || String.IsNullOrEmpty(profileId))
                return;

            lock (suppressedUsbControllerLock) {
                suppressedUsbControllerProfiles[devicePath] = profileId;
                chargeOnlyParkedUsbPaths.Add(devicePath);
            }
        }

        public void ReleaseChargeOnlyUsbPark(string devicePath) {
            if (String.IsNullOrEmpty(devicePath))
                return;

            lock (suppressedUsbControllerLock)
                chargeOnlyParkedUsbPaths.Remove(devicePath);
        }

        // Controllers BetterJoy deliberately powered off (roaming sleep, long-press, inactivity,
        // app exit). Keyed by MAC and held HERE rather than on the pad object on purpose: the pad
        // that a power-off ran on is destroyed and rebuilt by the scan as a brand-new object, so
        // any per-object "we did this" flag is reset by the time a later drop is evaluated - which
        // would make the disappeared-controller wake fire on a controller we put to sleep on
        // purpose. Cleared only when the controller is demonstrably awake and streaming again.
        readonly HashSet<string> deliberatePowerOffMacs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void MarkDeliberatePowerOff(byte[] controllerMac) {
            if (controllerMac == null || controllerMac.Length != 6)
                return;
            lock (suppressedUsbControllerLock)
                deliberatePowerOffMacs.Add(BitConverter.ToString(controllerMac).Replace("-", ""));
        }

        public void ClearDeliberatePowerOff(byte[] controllerMac) {
            if (controllerMac == null || controllerMac.Length != 6)
                return;
            lock (suppressedUsbControllerLock) {
                if (deliberatePowerOffMacs.Count == 0)
                    return;
                deliberatePowerOffMacs.Remove(
                    BitConverter.ToString(controllerMac).Replace("-", ""));
            }
        }

        public bool WasDeliberatelyPoweredOff(byte[] controllerMac) {
            if (controllerMac == null || controllerMac.Length != 6)
                return false;
            lock (suppressedUsbControllerLock)
                return deliberatePowerOffMacs.Contains(
                    BitConverter.ToString(controllerMac).Replace("-", ""));
        }

        // A pairing attempt for this MAC is still in flight - the Bluetooth pad churning up and
        // down is that attempt working, not a controller that died. Never wake into it.
        public bool HasPendingBluetoothPairingAttempt(byte[] controllerMac) {
            if (controllerMac == null || controllerMac.Length != 6)
                return false;
            lock (suppressedUsbControllerLock)
                return pendingBluetoothPairingConfirmations.ContainsKey(
                    BitConverter.ToString(controllerMac).Replace("-", ""));
        }

        public bool HasLiveBluetoothDualSense(byte[] controllerMac) {
            if (controllerMac == null || controllerMac.Length != 6)
                return false;
            lock (scanLock) {
                foreach (Controller controller in j) {
                    if (!(controller is DualSenseController) || controller.isUSB ||
                            controller.state != Controller.state_.IMU_DATA_OK ||
                            controller.path == null ||
                            controller.path.IndexOf("00001124",
                                StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    byte[] otherMac = controller.PadMacAddress?.GetAddressBytes();
                    if (otherMac != null && otherMac.SequenceEqual(controllerMac))
                        return true;
                }
            }
            return false;
        }

        public bool ShouldMonitorChargeOnlyUsbWake(string devicePath, string profileId) {
            if (scanningStopped || String.IsNullOrEmpty(devicePath) ||
                    String.IsNullOrEmpty(profileId))
                return false;

            lock (suppressedUsbControllerLock) {
                return suppressedUsbControllerProfiles.TryGetValue(devicePath,
                        out string suppressedProfileId) &&
                    String.Equals(suppressedProfileId, profileId,
                        StringComparison.Ordinal) &&
                    (chargeOnlyParkedUsbPaths.Contains(devicePath) ||
                        ControllerMappings.UsablePreferredTransport(profileId) ==
                            ControllerMappings.PreferredTransportBluetooth);
            }
        }

        // The active Bluetooth controller object is destroyed during power-off and recreated on
        // PS wake, while the charge-only USB quarantine deliberately survives in the manager.
        // Let that fresh controller recover the wired HID path by stable profile identity so a
        // second shutdown can reassert the bond and arm another USB wake monitor just like the
        // first one did.
        public bool TryGetChargeOnlyUsbPath(string profileId, out string devicePath) {
            devicePath = null;
            if (String.IsNullOrEmpty(profileId))
                return false;

            lock (suppressedUsbControllerLock) {
                foreach (KeyValuePair<string, string> entry in
                        suppressedUsbControllerProfiles) {
                    if (!String.Equals(entry.Value, profileId,
                            StringComparison.Ordinal))
                        continue;
                    devicePath = entry.Key;
                    return true;
                }
            }
            return false;
        }

        public bool TryBeginChargeOnlyUsbWakeMonitor() {
            lock (chargeOnlyWakeMonitorLock) {
                if (scanningStopped)
                    return false;
                activeChargeOnlyWakeMonitors++;
                return true;
            }
        }

        public void EndChargeOnlyUsbWakeMonitor() {
            lock (chargeOnlyWakeMonitorLock) {
                if (activeChargeOnlyWakeMonitors > 0)
                    activeChargeOnlyWakeMonitors--;
                if (activeChargeOnlyWakeMonitors == 0)
                    Monitor.PulseAll(chargeOnlyWakeMonitorLock);
            }
        }

        private bool IsUsbControllerSuppressed(string devicePath) {
            if (String.IsNullOrEmpty(devicePath))
                return false;

            lock (suppressedUsbControllerLock) {
                if (!suppressedUsbControllerProfiles.TryGetValue(devicePath,
                        out string profileId))
                    return false;

                if (chargeOnlyParkedUsbPaths.Contains(devicePath))
                    return true;

                if (ControllerMappings.UsablePreferredTransport(profileId) ==
                        ControllerMappings.PreferredTransportBluetooth)
                    return true;

                suppressedUsbControllerProfiles.Remove(devicePath);
                suppressedUsbPowerOffGraceUntil.Remove(devicePath);
                chargeOnlyParkedUsbPaths.Remove(devicePath);
                return false;
            }
        }

        private void ReleaseRemovedUsbControllerSuppressions(HashSet<string> enumeratedPaths) {
            long now = Stopwatch.GetTimestamp();
            lock (suppressedUsbControllerLock) {
                foreach (string path in suppressedUsbControllerProfiles.Keys.ToList()) {
                    if (enumeratedPaths.Contains(path))
                        continue;

                    if (suppressedUsbPowerOffGraceUntil.TryGetValue(path,
                            out long graceUntil) && now < graceUntil)
                        continue;

                    suppressedUsbControllerProfiles.Remove(path);
                    suppressedUsbPowerOffGraceUntil.Remove(path);
                    chargeOnlyParkedUsbPaths.Remove(path);
                }

                foreach (string path in suppressedUsbPowerOffGraceUntil.Keys
                        .Where(path => !suppressedUsbControllerProfiles.ContainsKey(path) ||
                            now >= suppressedUsbPowerOffGraceUntil[path]).ToList())
                    suppressedUsbPowerOffGraceUntil.Remove(path);

                // A path that is no longer enumerated has genuinely been unplugged, so the next time
                // it appears is a real initial connection and the sleep-on-connect policy applies
                // again. A wake never reaches here: the wired path stays enumerated throughout.
                foreach (string path in usbSleepOnConnectAppliedPaths.ToList()) {
                    if (!enumeratedPaths.Contains(path))
                        usbSleepOnConnectAppliedPaths.Remove(path);
                }

                // Unplugged: the next appearance is a genuine plug-in, so the firmware runs its
                // orange flash again and the hold-off must start over from that moment.
                foreach (string path in deviceFirstSeenAt.Keys.ToList()) {
                    if (!enumeratedPaths.Contains(path))
                        deviceFirstSeenAt.Remove(path);
                }

                // Drop any pending pairing-confirmation whose wired interface is gone (unplugged),
                // so a stale record can't linger and later block a fresh attempt.
                foreach (KeyValuePair<string, BluetoothPairingAttempt> entry in
                        pendingBluetoothPairingConfirmations.ToList()) {
                    if (enumeratedPaths.Contains(entry.Value.wiredPath))
                        continue;
                    if (suppressedUsbPowerOffGraceUntil.TryGetValue(entry.Value.wiredPath,
                            out long graceUntil) && now < graceUntil)
                        continue;
                    if (!enumeratedPaths.Contains(entry.Value.wiredPath))
                        pendingBluetoothPairingConfirmations.Remove(entry.Key);
                }
            }
        }

        // --- Enabled Bluetooth pairing: live-signal confirmation + capped retry ---
        // All share suppressedUsbControllerLock. MAC key = BitConverter.ToString(mac).

        // Called by the USB-side ceremony after it fires the connect trigger and suppresses the
        // wired path. Upserts the attempt and (re)starts the confirmation window.
        public int RecordBluetoothPairingAttempt(byte[] controllerMac, string profileId,
                string wiredPath, bool sleepOnConnectAfterConfirmation,
                long confirmWindowSeconds = BluetoothPairingConfirmWindowSeconds) {
            if (controllerMac == null || controllerMac.Length != 6)
                return 0;
            string mac = BitConverter.ToString(controllerMac).Replace("-", "");
            long windowSeconds = confirmWindowSeconds > 0
                ? confirmWindowSeconds
                : BluetoothPairingConfirmWindowSeconds;
            long deadline = Stopwatch.GetTimestamp() +
                Stopwatch.Frequency * windowSeconds;
            lock (suppressedUsbControllerLock) {
                if (!pendingBluetoothPairingConfirmations.TryGetValue(mac,
                        out BluetoothPairingAttempt attempt)) {
                    attempt = new BluetoothPairingAttempt { attemptCount = 0 };
                    pendingBluetoothPairingConfirmations[mac] = attempt;
                }
                attempt.profileId = profileId;
                attempt.wiredPath = wiredPath;
                attempt.attemptCount++;
                attempt.deadlineTimestamp = deadline;
                attempt.awaitingReattempt = false;
                attempt.sleepOnConnectAfterConfirmation = sleepOnConnectAfterConfirmation;
                return attempt.attemptCount;
            }
        }

        // Called from the reconciliation when a live BT pad for this MAC reaches IMU_DATA_OK.
        // Consumes the record (returns true) so the caller can sleep that pad. Suppression is left
        // in place - the wake monitor the sleep arms needs it.
        public bool TryConfirmBluetoothPairing(byte[] controllerMac,
                out int attemptNumber, out bool sleepOnConnectAfterConfirmation) {
            attemptNumber = 0;
            sleepOnConnectAfterConfirmation = false;
            if (controllerMac == null || controllerMac.Length != 6)
                return false;
            string mac = BitConverter.ToString(controllerMac).Replace("-", "");
            lock (suppressedUsbControllerLock) {
                if (!pendingBluetoothPairingConfirmations.TryGetValue(mac,
                        out BluetoothPairingAttempt attempt))
                    return false;
                attemptNumber = attempt.attemptCount;
                sleepOnConnectAfterConfirmation =
                    attempt.sleepOnConnectAfterConfirmation;
                pendingBluetoothPairingConfirmations.Remove(mac);
                return true;
            }
        }

        // Called once per reconciliation pass. For any attempt past its window that never confirmed:
        // give up after the cap (drop + release suppression), otherwise mark it for re-attempt and
        // release the USB suppression so the next scan re-adopts the wired pad and re-runs escalated.
        public void ExpireBluetoothPairingConfirmations() {
            long now = Stopwatch.GetTimestamp();
            lock (suppressedUsbControllerLock) {
                foreach (KeyValuePair<string, BluetoothPairingAttempt> entry in
                        pendingBluetoothPairingConfirmations.ToList()) {
                    BluetoothPairingAttempt attempt = entry.Value;
                    if (attempt.awaitingReattempt || now < attempt.deadlineTimestamp)
                        continue;

                    if (attempt.attemptCount >= BluetoothPairingMaxAttempts) {
                        pendingBluetoothPairingConfirmations.Remove(entry.Key);
                        suppressedUsbControllerProfiles.Remove(attempt.wiredPath);
                        suppressedUsbPowerOffGraceUntil.Remove(attempt.wiredPath);
                        DebugLog.Write("BT pairing giving up after " +
                            attempt.attemptCount + " attempts: mac=" + entry.Key);
                        BluetoothRadio.MarkClassicPairingRegistryTrace(entry.Key,
                            "pairing-gave-up");
                        continue;
                    }

                    attempt.awaitingReattempt = true;
                    suppressedUsbControllerProfiles.Remove(attempt.wiredPath);
                    suppressedUsbPowerOffGraceUntil.Remove(attempt.wiredPath);
                    DebugLog.Write("BT pairing timed out: mac=" + entry.Key +
                        " attempt=" + attempt.attemptCount +
                        ", releasing USB suppression to retry");
                    BluetoothRadio.MarkClassicPairingRegistryTrace(entry.Key,
                        "pairing-attempt-" + attempt.attemptCount + "-timed-out");
                }
            }
        }

        // Timer.Stop() only blocks FUTURE ticks - an Elapsed callback that already fired and is
        // queued on the ThreadPool, but hasn't reached the scanLock yet, isn't covered by that.
        // Set before the rendezvous below (not inside it) so a callback which acquires scanLock
        // at any point after this is set - whether it was already queued or not - sees it and
        // exits without doing any scan work, instead of racing full teardown.
        volatile bool scanningStopped = false;
        private readonly ManualResetEventSlim stopping = new ManualResetEventSlim(false);
        private readonly object controllerUsbWorkLock = new object();
        internal bool IsStopping => scanningStopped;
        internal bool WaitForStop(int milliseconds) => stopping.Wait(milliseconds);

        internal void RunControllerUsbWork(Action action) {
            lock (controllerUsbWorkLock) {
                if (!scanningStopped)
                    action();
            }
        }


        public static JoyconManager Instance {
            get { return instance; }
        }

        public void Awake() {
            instance = this;
            j = new ConcurrentList<Controller>();
            HIDapi.hid_init();
        }

        public void Start() {
            controllerCheck = new System.Timers.Timer(2000); // check for new controllers every 2 seconds
            controllerCheck.Elapsed += CheckForNewControllersTime;
            controllerCheck.Start();
        }

        // Stops future scan passes AND blocks until any pass already in progress (or already
        // queued on the ThreadPool, about to start) has exited without doing further work.
        // controllerCheck.Stop() alone only blocks brand new ticks from firing - a callback that
        // fired just before Stop() but hadn't yet acquired scanLock would find it uncontested
        // and run anyway, racing full teardown (OnApplicationQuit detaching every controller and
        // calling HIDapi.hid_exit()). Setting scanningStopped first closes that: any callback
        // that acquires scanLock after this point - already queued or not - checks the flag
        // before doing anything and exits immediately.
        public void StopScanning() {
            scanningStopped = true;
            stopping.Set();
            controllerCheck?.Stop();
            lock (scanLock) { }
            controllerCheck?.Dispose();
            controllerCheck = null;
            lock (controllerUsbWorkLock) { }
            lock (chargeOnlyWakeMonitorLock) {
                while (activeChargeOnlyWakeMonitors > 0)
                    Monitor.Wait(chargeOnlyWakeMonitorLock);
            }
        }

        bool ControllerAlreadyAdded(string path) {
            foreach (Controller v in j)
                if (v.path == path)
                    return true;
            return false;
        }

        public void ApplyControllerProfileOptions() {
            RunExclusiveOfScanning(() => {
                if (scanningStopped)
                    return;
                var handledProfiles = new HashSet<string>(StringComparer.Ordinal);
                foreach (Controller jc in j) {
                    if (jc.state == Controller.state_.DROPPED)
                        continue;
                    // Streaming again = genuinely awake, so an earlier deliberate power-off no
                    // longer describes this controller. Only after this does a later unexplained
                    // disappearance count as the controller dying on its own.
                    if (jc.state == Controller.state_.IMU_DATA_OK && jc is DualSenseController awakePad)
                        ClearDeliberatePowerOff(awakePad.PadMacAddress.GetAddressBytes());
                    // Confirm bridge: proof the bond works is not one blip of input - it's the
                    // Bluetooth link HOLDING. A single IMU_DATA_OK is just one lap of the pairing
                    // loop; sleeping on it yanks the connection down mid-authentication. So require
                    // this BT pad (00001124 interface) to stay live for a dwell before we confirm +
                    // sleep it (PowerOff on its Poll thread). Checked before lighting so a confirmed
                    // pad doesn't flash the lightbar first.
                    if (jc is DualSenseController confirmPad && !confirmPad.isUSB &&
                            confirmPad.state == Controller.state_.IMU_DATA_OK &&
                            confirmPad.path != null &&
                            confirmPad.path.IndexOf("00001124",
                                StringComparison.OrdinalIgnoreCase) >= 0) {
                        long nowTs = Stopwatch.GetTimestamp();
                        if (confirmPad.bluetoothImuStableSince == 0)
                            confirmPad.bluetoothImuStableSince = nowTs;   // start the stability dwell
                        else if (nowTs - confirmPad.bluetoothImuStableSince >=
                                     Stopwatch.Frequency * BluetoothPairingStableDwellSeconds) {
                            bool pairingConfirmed = TryConfirmBluetoothPairing(
                                confirmPad.PadMacAddress.GetAddressBytes(),
                                out int confirmedAttempt,
                                out bool sleepOnConnectAfterConfirmation);
                            bool freshUsbSleepQueued = false;
                            // USB-preferred fresh pairing also needs the live dwell before parking
                            // or duplicate resolution can interrupt its first Bluetooth connection.
                            foreach (Controller candidate in j) {
                                if (candidate is DualSenseController usbPad && usbPad.isUSB &&
                                        Equals(usbPad.PadMacAddress, confirmPad.PadMacAddress))
                                    freshUsbSleepQueued |= usbPad.ConfirmFreshBluetoothPairing();
                            }
                            if (sleepOnConnectAfterConfirmation || freshUsbSleepQueued)
                                confirmPad.RequestRoamingSleepAfterBluetoothConfirmation();
                            if (pairingConfirmed) {
                                DebugLog.Write("DualSense BT pairing confirmed (held): pad=" +
                                    confirmPad.PadId + " mac=" + BitConverter.ToString(
                                        confirmPad.PadMacAddress.GetAddressBytes()).Replace("-", "") +
                                    " attempt=" + confirmedAttempt +
                                    " sleepOnConnect=" + sleepOnConnectAfterConfirmation +
                                    " heldMs=" + ((nowTs - confirmPad.bluetoothImuStableSince) *
                                        1000 / Stopwatch.Frequency));
                                BluetoothRadio.MarkClassicPairingRegistryTrace(
                                    confirmPad.PadMacAddress.GetAddressBytes(),
                                    "pairing-confirmed-held");
                            }
                        }
                    }
                    string profileId = ControllerMappings.ProfileIdFor(jc);
                    ApplyControllerProfileLighting(jc, profileId);
                    if (jc is DualSenseController triggerController) {
                        triggerController.ApplyAutomaticBluetoothPairing();
                        // Each mode keeps its own Start/Secondary/Strength now (see
                        // ControllerMappings.AdaptiveTriggerFieldValue) - read using whichever
                        // mode is actually configured per side, not a shared flat value.
                        string leftMode = ControllerMappings.OptionValue(profileId, "AdaptiveTriggerModeLeft");
                        string rightMode = ControllerMappings.OptionValue(profileId, "AdaptiveTriggerModeRight");
                        triggerController.SetAdaptiveTriggerProfile(
                            leftMode,
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Start", leftMode, 30),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Secondary", leftMode, 70),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Left", "Strength", leftMode, 50),
                            rightMode,
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Start", rightMode, 30),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Secondary", rightMode, 70),
                            ControllerMappings.AdaptiveTriggerFieldValue(profileId, "Right", "Strength", rightMode, 50));
                    }
                    if (jc is DualShock4Controller dualShock4Pairing)
                        dualShock4Pairing.ApplyAutomaticBluetoothPairing();
                    string audioMode = ControllerMappings.ControllerAudioMode(profileId);
                    bool audioEnabled = audioMode != ControllerMappings.ModeDisable;
                    bool requireHeadphones =
                        audioMode == ControllerMappings.AudioModeRequireHeadphones;
                    int audioVolume = ControllerMappings.IntOption(profileId, "ControllerAudioVolume", 75);
                    // Matches the Bluetooth gating just below: "Require headphones" means no
                    // speaker fallback, not just correct routing once already enabled. DS4/
                    // DualSense both expose HeadphonesConnected but don't share a common base
                    // member for it, hence the per-type check here instead of on jc directly.
                    bool usbHeadphonesSatisfied = !requireHeadphones ||
                        (jc is DualShock4Controller ds4Headphones && ds4Headphones.HeadphonesConnected) ||
                        (jc is DualSenseController dualSenseHeadphones && dualSenseHeadphones.HeadphonesConnected);
                    if (audioEnabled && usbHeadphonesSatisfied)
                        jc.PrepareUsbAudio(audioVolume);
                    else
                        // The missing half of the above: without this, turning controller audio
                        // off (or unplugging with Require headphones) only stopped sending audio
                        // and left the controller's own volume wherever it was. Latched inside
                        // the controller so it writes once per transition, not once per pass -
                        // the jack hotplug path above already re-runs this whole reconciliation
                        // on insert and removal, which is what gives Require headphones both edges.
                        jc.SilenceControllerAudio();
                    // Opt-in alternative to the "set the controller as your Windows default
                    // playback device" flow above - off by default (see ControllerMappings'
                    // "false" default for this key). Target is the controller's own selected
                    // endpoint (ControllerAudioEndpointId, the same one PrepareUsbAudio's test
                    // tone already targets) - empty ("Default") resolves via
                    // UsbAudioEndpointNameHint to the controller's own device rather than the
                    // system default, since looping the system default into itself would be a
                    // feedback loop. Source is left empty for now (the system default render
                    // device), matching Bluetooth capture's own fallback rather than adding a
                    // second endpoint picker for this first pass.
                    bool usbLoopbackEnabled =
                        ControllerMappings.BoolOption(profileId, "ControllerAudioUsbLoopback");
                    if (jc.isUSB && audioEnabled && usbHeadphonesSatisfied && usbLoopbackEnabled)
                        form.StartUsbAudioLoopback(jc.PadId, String.Empty,
                            ControllerMappings.OptionValue(profileId, "ControllerAudioEndpointId"),
                            jc.UsbAudioEndpointNameHint, audioVolume);
                    else
                        form.StopUsbAudioLoopback(jc.PadId);
                    // Bluetooth has no audio-class endpoint to prepare - DualShock4Controller owns
                    // a full continuous capture/encode/stream lifecycle instead (see
                    // StartBluetoothAudioStream). Both calls are idempotent no-ops when already in
                    // the requested state, so re-evaluating this every scan pass is cheap and is
                    // what actually starts the stream once a profile is (re)enabled or the
                    // controller reconnects - mid-stream disconnect is handled separately via
                    // OnDetachingWhileAttached, since this loop only reaches attached controllers.
                    if (!jc.isUSB && jc is DualShock4Controller ds4) {
                        // The physical jack always selects speaker vs. headset output. This option
                        // only controls whether speaker fallback is allowed while the jack is empty.
                        // InputHelper runs one independent capture pipeline per pad (see
                        // BluetoothAudioCapture/InputHelper.cs), so this and DualSense's own Start
                        // below don't contend with each other the way they used to when InputHelper
                        // had only one shared capture slot.
                        if (audioEnabled && (!requireHeadphones || ds4.HeadphonesConnected))
                            ds4.StartBluetoothAudioStream(audioVolume,
                                ControllerMappings.OptionValue(profileId, "ControllerAudioEndpointId"),
                                ds4.HeadphonesConnected);
                        else
                            ds4.StopBluetoothAudioStream();
                    }
                    if (jc is DualSenseController dualSense) {
                        string microphoneMode = ControllerMappings.BluetoothMicrophoneMode(profileId);
                        bool bluetoothMicrophoneEnabled = microphoneMode == ControllerMappings.ModeEnable ||
                            microphoneMode == ControllerMappings.MicrophoneModeStartMuted;
                        // The built-in Bluetooth microphone is independent of the 3.5 mm jack.
                        // Controller audio remains the single opt-in for the physical media
                        // transport; a missing optional virtual-mic backend must not disturb the
                        // existing speaker/headset path.
                        if (!jc.isUSB && audioEnabled && bluetoothMicrophoneEnabled)
                            dualSense.StartBluetoothMicrophone(
                                microphoneMode == ControllerMappings.MicrophoneModeStartMuted);
                        else {
                            dualSense.StopBluetoothMicrophone();
                            // StopBluetoothMicrophone only tears down BetterJoy's own worker/
                            // virtual-mic pipeline - it doesn't touch the controller's own
                            // hardware mute state, so without this, Disabled (or Controller audio
                            // itself off) never actually pushes the mute LED/power-save bit to the
                            // controller at all, leaving whatever state an earlier session left it
                            // in instead of the muted state this branch implies.
                            dualSense.ApplyBluetoothMicrophoneMuteDefault();
                        }
                        // USB's mic is a native USB Audio Class endpoint - no BetterJoy-owned
                        // start/stop lifecycle the way Bluetooth's is, so Disabled can't stop a
                        // worker the way it does there; it hard-mutes the real hardware instead
                        // (see SetMicrophoneMuted/DualSensePowerSaveMicMute) and, like Muted,
                        // still needs applying once per connection. Both Disabled and Muted start
                        // muted - only Enable starts unmuted - the physical button can still
                        // toggle either at runtime. Deliberately independent of audioEnabled/
                        // Controller audio: this should reflect this dropdown's own setting, not
                        // get entangled with a separate master switch's timing.
                        if (jc.isUSB)
                            dualSense.ApplyUsbMicrophoneMuteDefault(
                                microphoneMode != ControllerMappings.ModeEnable);
                        if (!jc.isUSB && audioEnabled &&
                            (!requireHeadphones || dualSense.HeadphonesConnected))
                            dualSense.StartBluetoothAudioStream(audioVolume,
                                ControllerMappings.OptionValue(profileId,
                                    "ControllerAudioEndpointId"),
                                dualSense.HeadphonesConnected);
                        else
                            dualSense.StopBluetoothAudioStream();
                    }
                    if (!handledProfiles.Add(profileId))
                        continue;

                    // Covers identities that only exist after joining (a "pair:" profile) or
                    // self-pairing into vertical - the raw per-controller attach hook only ever
                    // sees the solo identity a controller had the instant it connected. No-op,
                    // no-disk-write once the profile already exists (see EnsureProfileSaved), so
                    // this is safe on every call here, not just the ones that follow a connect.
                    ControllerMappings.EnsureProfileSaved(profileId);

                    bool paired = jc.other != null && jc.other != jc;
                    Controller active = jc;
                    Controller passive = null;
                    if (paired) {
                        bool jcHasOutput = jc.out_xbox != null || jc.out_ds4 != null;
                        bool otherHasOutput = jc.other.out_xbox != null || jc.other.out_ds4 != null;
                        active = jcHasOutput || !otherHasOutput ? jc : jc.other;
                        passive = active == jc ? jc.other : jc;
                    }

                    if (passive != null)
                        DestroyOutputControllers(passive);
                    CreateOutputControllers(active);
                }
                // Any Enabled pairing attempt that never confirmed over Bluetooth within its window:
                // give up (capped) or release USB suppression so the next scan re-adopts the wired
                // pad and retries. Runs under RunExclusiveOfScanning, so a released suppression is
                // guaranteed visible to the next CheckForNewControllers pass.
                ExpireBluetoothPairingConfirmations();
                form.RefreshControllerState();
                // The OpenRGB SDK server exposes one fixed device regardless of what's connected
                // (see OpenRgbServer's own comment on why), so there's no device list to notify
                // about here - just resync any controller that's newly eligible (just connected,
                // or Lighting Mode just changed to OpenRGB) to whatever color was last set.
                OpenRgbServer.ApplyCachedColorToEligibleControllers();
            });
        }

        internal static void ApplyControllerProfileLighting(Controller controller,
                                                             string profileId) {
            // In any BT-auto mode (Enabled/Repair) with Bluetooth preferred, a controller still on
            // its USB interface is charge-only and pending its Bluetooth connection (it's about to
            // be paired/repaired and stepped off). Sending any lighting here would leak an LED
            // through while it should stay dark until it's actually up on Bluetooth. Leave it dark;
            // once it's up over Bluetooth (isUSB == false) lighting applies normally.
            if (controller.isUSB &&
                    ControllerMappings.UsablePreferredTransport(profileId) ==
                        ControllerMappings.PreferredTransportBluetooth &&
                    ControllerMappings.AutomaticBluetoothPairingEnabled(profileId))
                return;

            // While either color-wheel activation style is active, its live preview is the
            // lighting source of truth. Reapplying the last persisted profile color from this
            // periodic reconciliation pass would make the lightbar jump backward mid-swipe.
            if (controller.TouchpadColorWheelActive)
                return;

            string lightingMode = ControllerMappings.LightingMode(profileId);
            // Default and OpenRGB both mean BetterJoy never sends a single lighting command for
            // this profile: not the Home LED, RGB lightbar, player indicators, or a
            // toggle_lighting press. This leaves firmware or another application (OpenRGB
            // included) as the sole lighting owner even if the profile's separate Player LED
            // option is enabled.
            if (ControllerMappings.LightingModeIsHandsOff(profileId))
                return;

            // Player LED (the small player-number indicator LEDs, DualSense only) is a separate
            // physical strip with its own Enable/Disable setting in every managed lighting mode.
            if (controller is DualSenseController dualSensePlayerLed)
                dualSensePlayerLed.RequestLEDUpdate(dualSensePlayerLed.PadId);

            controller.RequestHomeLightUpdate(
                ControllerMappings.BoolOption(profileId, "HomeLEDOn"));

            byte red, green, blue;
            // LightingOff (toggle_lighting binding) never touches the user's actual LightColor
            // setting or Lighting mode - it's a separate flag applied on top of whichever of those
            // is otherwise in effect, so what was there before is always still there to go back to
            // the instant it's toggled back on, nothing to save/restore. Disabled is the same
            // black output but as a persistent saved mode instead of a runtime toggle.
            if (lightingMode == ControllerMappings.LightingModeDisabled ||
                    ControllerMappings.BoolOption(profileId, "LightingOff")) {
                red = green = blue = 0;
            } else if (lightingMode == ControllerMappings.LightingModeBattery) {
                (red, green, blue) = BatteryLightColor(controller.batteryPercent);
            } else {
                ControllerMappings.GetLightColor(profileId, out red, out green, out blue);
            }
            (red, green, blue) = ControllerMappings.ApplyLightBrightness(
                profileId, red, green, blue);

            // Deliberately unconditional, not deduped up here - both DualSenseController and
            // DualShock4Controller's own SetLightColor already dedupe identical colors
            // internally, but specifically only once their transport is known and no update is
            // still pending, precisely so a stale/pre-transport call doesn't suppress the retry
            // that's needed once it becomes known (DualShock4's own comment calls this the
            // "ordered audio-lane barrier" while audio is active). An earlier version of this
            // method added a second, transport-unaware cache here that looked redundant but
            // wasn't - it could permanently prevent that retry from ever being reached, which
            // broke DualShock4 Bluetooth audio outright. Let each controller's own SetLightColor
            // decide when a resend is actually redundant.
            controller.SetLightColor(red, green, blue);
        }

        // Pure green/yellow/red bands rather than a gradient - deliberately coarse (matches the
        // "update only when the charge crosses into a different band" requirement this exists
        // for) so the lightbar doesn't need a fresh color sent on every small percentage change.
        // Luminosity capped at 15 per channel rather than full 255 - a lightbar at max brightness
        // for something meant to be a subtle, glanceable indicator is uncomfortably bright.
        private const byte BatteryLightLuminosity = 15;

        private static (byte, byte, byte) BatteryLightColor(int batteryPercent) {
            if (batteryPercent <= 33)
                return (BatteryLightLuminosity, 0, 0);
            if (batteryPercent <= 66)
                return (BatteryLightLuminosity, BatteryLightLuminosity, 0);
            return (0, BatteryLightLuminosity, 0);
        }

        void CheckForNewControllersTime(Object source, ElapsedEventArgs e) {
            lock (scanLock) {
                if (scanningStopped)
                    return;

                // An exception escaping this pass was permanent, not transient. The timer keeps
                // firing, but every pass re-entered the same work in the same order and died in the
                // same place, so nothing was ever enumerated or cleaned up again and no controller
                // could be adopted until the service was restarted - silently, with no trace,
                // because there was nothing here to log it. Seen after a Bluetooth radio toggle:
                // the scan simply stopped and a controller that then connected was never picked up.
                // One bad pass must cost one pass, so swallow it and let the next tick try again.
                try {
                    RunScanPass();
                } catch (Exception ex) {
                    DebugLog.Write("Scan pass failed, retrying next tick: " +
                        ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace);
                }
            }
        }

        void RunScanPass() {
            // Read the real blocklist into the in-memory set first so an external change (a device
            // manually unhidden in the HidHide UI, another app's entry) is reconciled this pass.
            SyncHiddenInstanceCacheFromRegistry();

            CleanUp();
            if (Boolean.Parse(ConfigurationManager.AppSettings["PassiveScan"])) {
                CheckForNewControllers();
            }

            // Blocks themselves are applied through the API the instant a node is seen (see
            // BlockInstance), so nothing is batched here any more. This is only the tidy-up:
            // drop entries whose device is gone, so the churned-away Bluetooth child ids don't
            // accumulate in HidHide's list.
            PruneAbsentDeviceEntries();
        }

        // Lets a caller outside the scan loop (see HeadlessJoyconHost's config-file watcher)
        // mutate state a scan pass reads - e.g. Program.thirdPartyCons/blacklistedCons via
        // _3rdPartyControllers.LoadIntoProgramLists - without racing CheckForNewControllers'
        // own iteration over them. Those are plain Lists, not ConcurrentList, so a Clear()+
        // AddRange() from another thread while a scan enumerates them could throw "Collection
        // was modified" or hand device classification a half-rebuilt list.
        // Scanning and USB workers must already be stopped. This closes the active controller
        // handles after each Sony controller has received its low-power command; suppressed USB
        // wake-monitor handles were already closed by StopScanning.
        internal List<UsbDeviceReenumerator.PortTarget> ReleaseControllersForSuspend() {
            var usbPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (suppressedUsbControllerLock) {
                foreach (string path in suppressedUsbControllerProfiles.Keys) {
                    if (path.IndexOf("vid_054c&pid_0ce6",
                                StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf("vid_054c&pid_0df2",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                        usbPaths.Add(path);
                }
            }
            foreach (Controller controller in j.ToList()) {
                if (controller is DualSenseController && controller.isUSB &&
                        !String.IsNullOrEmpty(controller.path))
                    usbPaths.Add(controller.path);
            }

            var portTargets = new List<UsbDeviceReenumerator.PortTarget>();
            foreach (string usbPath in usbPaths) {
                bool resolved = UsbDeviceReenumerator.TryResolveDualSensePort(
                    usbPath, out UsbDeviceReenumerator.PortTarget target,
                    out string detail);
                DebugLog.Write("Power: suspend USB port resolve result=" + resolved +
                    " path=" + usbPath + " detail=" + detail);
                if (resolved && !portTargets.Any(existing =>
                        String.Equals(existing.HubPath, target.HubPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        existing.PortNumber == target.PortNumber))
                    portTargets.Add(target);
            }

            foreach (Controller controller in j.ToList()) {
                if (controller is DualSenseController)
                    controller.RequestStopPollingForSuspend();
            }
            bool allReleased = true;
            foreach (Controller controller in j.ToList()) {
                try {
                    if (controller is DualSenseController dualSense) {
                        if (!dualSense.ReleaseForSuspend())
                            allReleased = false;
                    }
                    else
                        controller.DisconnectForSuspend();
                } catch (Exception ex) {
                    allReleased = false;
                    DebugLog.Write("Power: suspend release failed for pad=" + controller.PadId + ": " + ex.Message);
                }
            }
            if (!allReleased) {
                DebugLog.Write("Power: suspend USB port cycle skipped because a controller handle did not release");
                portTargets.Clear();
            }
            return portTargets;
        }

        public void RunExclusiveOfScanning(Action action) {
            lock (scanLock) {
                action();
            }
        }

        // Attempts to hide a device via HidHide, retrying a few times (short delay) within this
        // pass since a freshly-plugged-in device's PnP instance can occasionally not be settled
        // yet. Returns true if hidden (or HidHide isn't in use), false if hiding failed after
        // retries - callers decide what that means for them (e.g. skip attaching this pass, or
        // for an already-blacklisted device, nothing further at all).
        //
        // Skips the actual AddBlockedInstanceId call entirely when hiddenInstanceIds already has
        // this instance - a reconnect cycle (Bluetooth especially, but also repeated pairing
        // attempts) calls this again for the same physical device, and every call was previously
        // an unconditional write into HidHide's persisted registry blocklist regardless of
        // whether it was already there. A long session with heavy reconnect churn could add the
        // same instance ID many times over; the driver's own list-serialization code has a real,
        // documented history of choking (nefarius/HidHide#83, #215) on ERROR_INVALID_PARAMETER,
        // and an unusually large/duplicate-heavy list is a plausible trigger. Real hardware
        // incident this session: HidHide's blacklist ended up in a state where every client
        // (GUI, CLI, and this app's own driver calls) failed identically until it was cleared.
        private bool TryHideController(hid_device_info enumerate) {
            if (!Program.useHidHide)
                return true;

            // Every node is blocked by its own full instance id. A DualSense needs all three, and
            // each earns its place - none of them stands in for another:
            //   BTHENUM\...\A&<radio>&0&<MAC>_C00000000  - the MAC-stable bond. Manufactured from
            //       the MAC (HideBluetoothBondByMac), so it is added preemptively, survives the
            //       child renumbering below, and exists before the BT node ever does.
            //   HID\VID_054C&PID_0CE6&MI_03\a&<id>&0&0000 - the wired interface.
            //   HID\{00001124-...}\b&<bus>&<N>&0000       - the live BT child HID node.
            // 03b828b skipped the child here, on the premise that the bond covered the subtree
            // beneath it. It does not, and this is the worst place to leave a gap: this runs BEFORE
            // hid_open_path, so the pad got enumerated, opened and attached with its child node
            // still visible, covered only later if CreateOutputControllers reached
            // ReconcileHidHideForController. The child id renumbers on every reconnect - that churn
            // is what PruneAbsentDeviceEntries exists to clean up, not a reason to skip the block.

            bool hidden = false;
            for (int hideAttempt = 0; hideAttempt < 5 && !hidden; hideAttempt++) {
                if (hideAttempt > 0)
                    Thread.Sleep(50);

                string instanceId;
                try {
                    instanceId = PnPDevice.GetInstanceIdFromInterfaceId(enumerate.path);
                } catch {
                    continue;
                }
                hidden = BlockInstance(instanceId);
                if (enumerate.vendor_id == vendor_id &&
                        enumerate.path.IndexOf("{00001124-", StringComparison.OrdinalIgnoreCase) >= 0) {
                    // Manual pairing has no USB preparation step. Block the actual HID service
                    // parent as well as its child before opening the controller.
                    hidden = TryGetBluetoothHidParentInstanceId(enumerate.path, out string bondId) &&
                        BlockInstance(bondId) && hidden;
                }
            }

            return hidden;
        }

        // Reads HidHide's persisted blocklist (registry MULTI_SZ ...\HidHide\Parameters\
        // BlacklistedDeviceInstancePaths) INTO the in-memory set at the start of each scan pass, so an
        // external edit (a device manually unhidden in the GUI, another app's entry) is reconciled:
        // the cache stops claiming an id is blocked when it no longer is, so BlockInstance calls the
        // driver again for it on this pass rather than skipping it as already-done. Read-only - the
        // driver's list is now changed only through the API. Leaves the set unchanged on a read
        // failure rather than dropping every entry.
        public static void SyncHiddenInstanceCacheFromRegistry() {
            if (!Program.useHidHide)
                return;
            string[] blocked;
            try {
                using (RegistryKey hidHideParams = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide\Parameters", false)) {
                    blocked = hidHideParams?.GetValue("BlacklistedDeviceInstancePaths") as string[];
                }
            } catch {
                return;
            }
            if (blocked == null)
                blocked = Array.Empty<string>();
            lock (Program.hiddenInstanceIdsLock) {
                Program.hiddenInstanceIds.Clear();
                foreach (string id in blocked)
                    if (!String.IsNullOrEmpty(id) && !Program.hiddenInstanceIds.Contains(id))
                        Program.hiddenInstanceIds.Add(id);
            }
        }

        // HidHide checks its live blocklist on file opens. Apply through the driver immediately;
        // updating the list cannot revoke a handle another process already opened.
        //
        // Idempotent against the in-memory cache, so a steady-state scan pass issues no IOCTL at all;
        // the driver lock serializes the read-modify-write of the driver's list, which is what keeps
        // repeated calls from wedging it (nefarius/HidHide#83, #215).
        private static bool BlockInstance(string instanceId) {
            if (!Program.useHidHide || Program.hidHide == null || String.IsNullOrEmpty(instanceId))
                return false;
            lock (Program.hidHideDriverLock) {
                lock (Program.hiddenInstanceIdsLock) {
                    if (Program.hiddenInstanceIds.Contains(instanceId))
                        return true;
                }
                try {
                    Program.hidHide.AddBlockedInstanceId(instanceId);
                } catch (Exception e) {
                    DebugLog.Write("HidHide block failed: " + instanceId +
                        " (" + e.GetType().Name + ": " + e.Message + ")");
                    return false;
                }
                lock (Program.hiddenInstanceIdsLock) {
                    if (!Program.hiddenInstanceIds.Contains(instanceId))
                        Program.hiddenInstanceIds.Add(instanceId);
                }
                DebugLog.Write("HidHide blocked: " + instanceId);
                return true;
            }
        }

        // API counterpart of BlockInstance - REMOVE, also immediate. Idempotent the same way: no
        // driver call when the id isn't known-blocked. Returns true if it was blocked and now isn't.
        private static bool UnblockInstance(string instanceId) {
            if (!Program.useHidHide || Program.hidHide == null || String.IsNullOrEmpty(instanceId))
                return false;
            lock (Program.hidHideDriverLock) {
                lock (Program.hiddenInstanceIdsLock) {
                    if (!Program.hiddenInstanceIds.Contains(instanceId))
                        return false;
                }
                try {
                    Program.hidHide.RemoveBlockedInstanceId(instanceId);
                } catch (Exception e) {
                    DebugLog.Write("HidHide unblock failed: " + instanceId +
                        " (" + e.GetType().Name + ": " + e.Message + ")");
                    return false;
                }
                lock (Program.hiddenInstanceIdsLock)
                    Program.hiddenInstanceIds.Remove(instanceId);
                DebugLog.Write("HidHide unblocked: " + instanceId);
                return true;
            }
        }

        // Drops blocklist entries whose device instance no longer exists under Enum - a churned-away
        // BT child (Windows renumbers it every reconnect) or a bond for a device that's gone. Keeps
        // static USB entries and anything still present (incl. phantom-present unplugged devices).
        // Fail-safe: on a read error for an entry, keep it rather than risk dropping a live block.
        private static void PruneAbsentDeviceEntries() {
            List<string> current;
            lock (Program.hiddenInstanceIdsLock)
                current = new List<string>(Program.hiddenInstanceIds);
            List<string> gone = null;
            foreach (string id in current) {
                // A manufactured BTHENUM bond is intentionally persistent: it is preemptively added
                // so the pad is born hidden on its first/next Bluetooth connect, and it has NO live
                // Enum key whenever the controller is powered off. An absence check would delete the
                // very preemptive hide we want to keep, so never prune our own bonds here - they are
                // removed only when their profile is retired.
                if (id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) &&
                        id.EndsWith("_C00000000", StringComparison.OrdinalIgnoreCase) &&
                        id.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                bool exists;
                try {
                    using (RegistryKey k = RegistryKey
                            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                            .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id, false))
                        exists = k != null;
                } catch {
                    exists = true;
                }
                if (!exists)
                    (gone ?? (gone = new List<string>())).Add(id);
            }
            if (gone == null)
                return;
            // Removals go through the driver too, not just the cache - otherwise the churned-away
            // child ids stay in HidHide's own list forever and it grows without bound, which is the
            // problem the old child-sibling de-dupe existed to solve.
            foreach (string id in gone)
                UnblockInstance(id);
        }

        // The "A&<radio>&0&" middle of a BTHENUM instance id is the local Bluetooth radio - identical
        // for every BT device on this PC (proven: all Enum\BTHENUM instances share it). Read it once
        // from the live tree (not hardcoded - it changes only if the adapter changes) so any
        // controller's bond id can be manufactured from just its MAC.
        private static string GetBluetoothRadioSegment() {
            string cached = Program.cachedBluetoothRadioSegment;
            if (cached != null)
                return cached;
            try {
                using (RegistryKey bthenum = RegistryKey
                        .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM", false)) {
                    if (bthenum != null) {
                        foreach (string svc in bthenum.GetSubKeyNames()) {
                            using (RegistryKey svcKey = bthenum.OpenSubKey(svc, false)) {
                                if (svcKey == null)
                                    continue;
                                foreach (string inst in svcKey.GetSubKeyNames()) {
                                    int p = inst.IndexOf("&0&", StringComparison.OrdinalIgnoreCase);
                                    if (p > 0) {
                                        Program.cachedBluetoothRadioSegment =
                                            inst.Substring(0, p + 3).ToUpperInvariant();
                                        return Program.cachedBluetoothRadioSegment;
                                    }
                                }
                            }
                        }
                    }
                }
            } catch { }
            return null;
        }

        // Manufacture a controller's MAC-stable BTHENUM bond instance id (uppercased for HidHide) from
        // its VID/PID/MAC + the local radio segment. Template proven from Enum\BTHENUM:
        //   BTHENUM\{00001124-...}_VID&<source><vid>_PID&<pid>\A&<radio>&0&<MAC>_C00000000
        // The 0x0002 VID source is USB-IF, matching every observed Sony BT entry (DualSense/DualShock).
        private static bool TryManufactureBluetoothBondInstanceId(
                ushort vid, ushort pid, byte[] mac, out string bondId) {
            bondId = null;
            if (mac == null || mac.Length != 6)
                return false;
            string radio = GetBluetoothRadioSegment();
            if (String.IsNullOrEmpty(radio))
                return false;
            uint vidField = 0x00020000u | vid;
            string macHex = BitConverter.ToString(mac).Replace("-", "").ToUpperInvariant();
            bondId = String.Format(CultureInfo.InvariantCulture,
                "BTHENUM\\{{00001124-0000-1000-8000-00805F9B34FB}}_VID&{0:X8}_PID&{1:X4}\\{2}{3}_C00000000",
                vidField, pid, radio, macHex);
            return true;
        }

        // Register the MAC-stable HID service parent before the Bluetooth connection starts.
        // The live child is also blocked during discovery.
        private void HideBluetoothBondByMac(ushort vid, ushort pid, byte[] mac) {
            if (!Program.useHidHide || mac == null || mac.Length != 6)
                return;
            // Pre-blocking a bond that may not exist yet is an optimisation, not a requirement -
            // the block is applied again from the node itself when the controller actually
            // connects. It reaches into HidHide and the BTHENUM subtree, both of which are being
            // rebuilt underneath us while the Bluetooth radio cycles, so a failure here must cost
            // this call and nothing else. Unhandled it escaped the whole scan pass.
            try {
                if (TryManufactureBluetoothBondInstanceId(vid, pid, mac, out string bondId))
                    BlockInstance(bondId);
            } catch (Exception e) {
                DebugLog.Write("HideBluetoothBondByMac failed, will re-block from the node: " +
                    e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool TryGetBluetoothHidParentInstanceId(string hidPath, out string bondId) {
            bondId = null;
            try {
                IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
                for (int depth = 0; device != null && depth < 8; depth++, device = device.Parent) {
                    string instanceId = device.InstanceId;
                    if (instanceId != null && instanceId.StartsWith(
                            @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}",
                            StringComparison.OrdinalIgnoreCase)) {
                        bondId = instanceId;
                        return true;
                    }
                }
            } catch (Exception ex) {
                DebugLog.Write("Bluetooth HID parent lookup failed: path=" + hidPath +
                    " message=\"" + ex.Message + "\"");
            }
            return false;
        }

        private static bool TryGetNintendoBluetoothBondInstanceId(NintendoController controller,
                out string bondId) {
            bondId = null;
            if (!controller.isUSB)
                return TryGetBluetoothHidParentInstanceId(controller.path, out bondId);
            if (controller.thirdParty)
                return false;

            ushort pid;
            switch (controller.Kind) {
                case ControllerKind.Left: pid = product_l; break;
                case ControllerKind.Right: pid = product_r; break;
                case ControllerKind.Pro: pid = product_pro; break;
                case ControllerKind.Snes: pid = product_snes; break;
                case ControllerKind.N64: pid = product_n64; break;
                default: return false;
            }
            return TryManufactureBluetoothBondInstanceId(vendor_id, pid,
                controller.PadMacAddress?.GetAddressBytes(), out bondId);
        }

        // Adds or removes jc's own HidHide block after the fact - the one case that needs this is
        // a profile set to ControllerMappings.UseAsPassthrough, which wants its physical device
        // visible to other programs again instead of staying exclusively blocked the way
        // TryHideController leaves every newly-connected controller by default. Called from
        // CreateOutputControllers on every pass (attach, profile change, AssignPadId, survivor
        // restoration) - for a long-lived connection that's every ~2s scan tick for the entire
        // session, not just once. Previously assumed harmless to call unconditionally since
        // HidHide's block list is supposed to be idempotent; a real hardware incident this
        // session (the driver's persisted blocklist ending up in a state where every client
        // failed with ERROR_INVALID_PARAMETER - see nefarius/HidHide#83/#215) argues against
        // trusting that assumption when hiddenInstanceIds already has the answer for free.
        private void ReconcileHidHideForController(Controller jc, bool wantHidden) {
            if (!Program.useHidHide || Program.hidHide == null || String.IsNullOrEmpty(jc.path))
                return;

            string instanceId;
            try {
                instanceId = PnPDevice.GetInstanceIdFromInterfaceId(jc.path);
            } catch {
                return;
            }

            if (wantHidden) {
                BlockInstance(instanceId);
            } else if (UnblockInstance(instanceId)) {
                // We only get here on a real hidden->visible transition (UnblockInstance returns
                // false otherwise), which is the only time OpenRGB's raw HID access just changed.
                if (ControllerMappings.LightingMode(ControllerMappings.ProfileIdFor(jc)) ==
                        ControllerMappings.LightingModeOpenRgb) {
                    DebugLog.Write("OpenRgbRescan: triggered from HidHide hidden->visible, instanceId=" + instanceId);
                    OpenRgbRescan.RequestRescan();
                }
            }

            if (jc is NintendoController nintendo &&
                    TryGetNintendoBluetoothBondInstanceId(nintendo, out string bondId)) {
                if (wantHidden)
                    BlockInstance(bondId);
                else
                    UnblockInstance(bondId);
            }
        }

        // A joined Joy-Con pair is two separate physical devices sharing one profile/logical
        // controller - passthrough (or re-hiding out of it) needs to reach both halves, not just
        // whichever one currently holds the ViGEm/VIIPER target.
        private void ReconcileHidHideForProfile(Controller jc, bool wantHidden) {
            ReconcileHidHideForController(jc, wantHidden);
            if (jc.other != null && jc.other != jc)
                ReconcileHidHideForController(jc.other, wantHidden);
        }

        // Reads the DualSense's own Bluetooth MAC address over USB via HID feature report 0x09
        // ("pairing info"). hidapi's hid_get_feature_report convention: caller sets buf[0] to the
        // report ID before the call: the returned buffer still has the report ID at index 0
        // followed by the report body, and the return value counts that leading byte. Report
        // layout confirmed against the Linux kernel's hid-playstation.c driver
        // (DS_FEATURE_REPORT_PAIRING_INFO, size 20, MAC at buf[1..6]).
        private const byte DualSensePairingInfoReportId = 0x09;
        private const int DualSensePairingInfoReportSize = 20;

        private static bool TryGetDualSenseMac(IntPtr handle, byte[] mac) {
            byte[] buf = new byte[DualSensePairingInfoReportSize];
            buf[0] = DualSensePairingInfoReportId;
            int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)buf.Length));
            if (ret < 7)
                return false;

            // Confirmed on real hardware: the feature report's 6 MAC bytes (buf[1..6]) are the
            // exact byte-reversal of what the same physical controller reports as its
            // serial_number when paired over Bluetooth - reverse here so both transports resolve
            // to the same PadMacAddress/profile identity.
            for (int i = 0; i < 6; i++)
                mac[i] = buf[6 - i];
            return true;
        }

        // Same idea for the DualShock 4, which has the identical problem: over USB it reports no
        // usable serial, so its identity fell through to the path hash and the same physical pad
        // resolved to a DIFFERENT profile on USB than on Bluetooth. Report 0x12 exposes its real
        // Bluetooth address over the cable.
        //
        // Report id and size match the Linux hid-playstation driver's
        // DS4_FEATURE_REPORT_PAIRING_INFO / _SIZE (0x12, 16) and its own
        // memcpy(mac_address, &buf[1], 6). Confirmed against real hardware here: a wired DS4
        // returned 12 AF 5B 6C 95 30 84 ..., which reverses to 84:30:95:6C:5B:AF and matches that
        // controller's own BTHENUM node (BTHENUM\DEV_8430956C5BAF, VID&0002054C_PID&09CC) exactly.
        private const byte DualShock4PairingInfoReportId = 0x12;
        private const int DualShock4PairingInfoReportSize = 16;

        private static bool TryGetDualShock4Mac(IntPtr handle, byte[] mac) {
            byte[] buf = new byte[DualShock4PairingInfoReportSize];
            buf[0] = DualShock4PairingInfoReportId;
            int ret = HIDapi.hid_get_feature_report(handle, buf, new UIntPtr((uint)buf.Length));
            if (ret < 7)
                return false;

            // Stored little-endian, same as the DualSense's 0x09 - reverse so USB and Bluetooth
            // resolve to one PadMacAddress and therefore one profile.
            for (int i = 0; i < 6; i++)
                mac[i] = buf[6 - i];
            return true;
        }

        // Walks up the PnP device tree from a HID interface to find the underlying bus (USB or
        // Bluetooth) it's actually connected through. GetInstanceIdFromInterfaceId only resolves
        // to the HID-level device node itself (always prefixed "HID\...", for either transport),
        // not its parent bus device, so checking that string directly never actually
        // distinguishes USB from Bluetooth - the real answer is a few levels up the tree.
        private static void GetControllerTransport(string hidPath, out bool isUsb, out bool isBluetooth) {
            isUsb = false;
            isBluetooth = false;

            IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
            for (int depth = 0; device != null && depth < 8; depth++) {
                if (device.InstanceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) {
                    isUsb = true;
                    return;
                }
                if (device.InstanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) {
                    isBluetooth = true;
                    return;
                }
                device = device.Parent;
            }
        }

        private static bool TryGetNintendoBluetoothMac(string hidPath, byte[] mac) {
            if (mac == null || mac.Length != 6)
                return false;

            IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
            for (int depth = 0; device != null && depth < 8; depth++) {
                string instanceId = device.InstanceId;
                if (!String.IsNullOrEmpty(instanceId) &&
                        instanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) {
                    int marker = instanceId.LastIndexOf("&0&",
                        StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0) {
                        int start = marker + 3;
                        int end = instanceId.IndexOf("_C", start,
                            StringComparison.OrdinalIgnoreCase);
                        if (end < 0)
                            end = Math.Min(start + 12, instanceId.Length);
                        string candidate = instanceId.Substring(start, end - start);
                        if (candidate.Length == 12 && candidate.All(Uri.IsHexDigit)) {
                            for (int i = 0; i < 6; i++)
                                mac[i] = byte.Parse(candidate.Substring(i * 2, 2),
                                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            return true;
                        }
                    }
                }
                device = device.Parent;
            }
            return false;
        }

        // See this method's call site for why VID/PID alone can't identify VIIPER's own virtual
        // DualSense output. Walks the same PnP parent chain GetControllerTransport does, looking
        // for usbip-win2's virtual host controller hardware ID instead of a real USB/Bluetooth
        // bus - present on every device VIIPER creates, never on a real physical controller.
        private const string ViiperVirtualBusHardwareId = "ROOT\\USBIP_WIN2\\UDE";

        private static bool IsUnderViiperVirtualBus(string hidPath) {
            // Same retry-with-short-delay shape as TryHideController just above, and for the same
            // reason: a device this fresh (this runs on the very first scan pass after VIIPER
            // creates it) can have a PnP instance/parent chain that isn't fully populated yet.
            // Without retrying here, that first-pass failure fell through to the real-controller
            // path and got it HidHidden before a later, successful pass ever got a chance to
            // exclude it - confirmed on real hardware: hidden exactly once, only on first
            // creation, never again after a manual unhide.
            for (int attempt = 0; attempt < 5; attempt++) {
                if (attempt > 0)
                    Thread.Sleep(50);

                try {
                    IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
                    for (int depth = 0; device != null && depth < 8; depth++) {
                        if (device.HardwareIds != null && device.HardwareIds.Any(id =>
                                String.Equals(id, ViiperVirtualBusHardwareId, StringComparison.OrdinalIgnoreCase)))
                            return true;
                        device = device.Parent;
                    }
                    return false;
                } catch {
                    // Not settled yet - fall through and retry rather than treating this as a
                    // real controller on the strength of one failed attempt.
                }
            }
            return false;
        }

        private ushort TypeToProdId(byte type) {
            switch (type) {
                case 1:
                    return product_pro;
                case 2:
                    return product_l;
                case 3:
                    return product_r;
            }
            return 0;
        }

        public void CheckForNewControllers() {
            // move all code for initializing devices here and well as the initial code from Start()
            bool isLeft = false;
            IntPtr ptr = HIDapi.hid_enumerate(0x0, 0x0);
            IntPtr top_ptr = ptr;
            var enumeratedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            hid_device_info enumerate; // Add device to list
            bool foundNew = false;
            while (ptr != IntPtr.Zero) {
                SController thirdParty = null;
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

                if (!String.IsNullOrEmpty(enumerate.path))
                    enumeratedPaths.Add(enumerate.path);

                if (enumerate.serial_number == null) {
                    ptr = enumerate.next; // can't believe it took me this long to figure out why USB connections used up so much CPU.
                                          // it was getting stuck in an inf loop here!
                    continue;
                }

                // A Bluetooth-preferred DualSense/DS4 keeps this matching wired HID interface
                // charge-only. Keep tracking the path above so unplugging releases its quarantine,
                // but do not hide, open, or attach it while that profile preference remains.
                if (IsUsbControllerSuppressed(enumerate.path)) {
                    ptr = enumerate.next;
                    continue;
                }

                // BetterJoy's own virtual output, mistaken for a brand new physical controller -
                // see the constants above. Unlike the blacklist case below, deliberately NOT
                // hidden via HidHide: other programs (games, Steam) are supposed to see this as a
                // normal Xbox360/DS4 controller, that's the entire point of it existing.
                if ((enumerate.vendor_id == vigemXbox360VendorId && enumerate.product_id == vigemXbox360ProductId) ||
                    (enumerate.vendor_id == vigemDs4VendorId && enumerate.product_id == vigemDs4ProductId)) {
                    ptr = enumerate.next;
                    continue;
                }

                // Same idea, for OutputControllerDualSenseViiper's own virtual output: unlike
                // ViGEmBus's Xbox360/DS4 targets above, VIIPER's DualSense device has no separate
                // "this is obviously virtual" VID/PID to check - it deliberately reports Sony's
                // real DualSense VID/PID (0x054C/0x0CE6), the same ones a genuine physical
                // DualSense uses, since that's what makes games/Windows actually recognize it as
                // one. VID/PID alone can't tell the two apart, so this checks where the device
                // actually lives instead: usbip-win2's virtual USB host controller registers with
                // hardware ID ROOT\USBIP_WIN2\UDE (confirmed via Get-PnpDeviceProperty on the live
                // driver), which a real physical controller - USB or Bluetooth - never sits under.
                if (enumerate.vendor_id == vendor_sony && enumerate.product_id == product_dualsense &&
                        IsUnderViiperVirtualBus(enumerate.path)) {
                    ptr = enumerate.next;
                    continue;
                }

                // Blacklisted devices (set from the Add Controllers dialog - e.g. a 3rd-party
                // controller that identifies differently over USB vs Bluetooth, where only one
                // of those identities should be usable) are skipped unconditionally, ahead of
                // both the manual custom-controller match and auto-add below. Still hide it from
                // other programs, though - the same physical controller may be reachable through
                // this identity from another program (e.g. Steam) while BetterJoy uses a
                // different transport/identity for it, which would otherwise let that other
                // program read duplicate raw input alongside BetterJoy's own virtual output.
                bool isBlacklisted = false;
                foreach (SController v in Program.blacklistedCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        isBlacklisted = true;
                        break;
                    }
                }
                if (isBlacklisted) {
                    TryHideController(enumerate);
                    ptr = enumerate.next;
                    continue;
                }

                // Checked as known, exactly-identified devices ahead of the generic 3rd-party
                // allowlist/auto-add below, not folded into it - that path can only guess a
                // Nintendo shape (SController.type: Pro/Left Joy-Con/Right Joy-Con). A known
                // device connected before a VID/PID-specific path existed may already have a
                // stale guessed entry in Program.thirdPartyCons; the loop below must not be
                // allowed to overwrite thirdParty for a device we now identify definitively.
                bool isDualSenseDevice = enumerate.vendor_id == vendor_sony &&
                    (enumerate.product_id == product_dualsense || enumerate.product_id == product_dualsense_edge);
                bool isDualShock4Device = enumerate.vendor_id == vendor_sony &&
                    enumerate.product_id == product_dualshock4_v2;
                if ((isDualSenseDevice || isDualShock4Device) &&
                        !IsGameController(enumerate)) {
                    ptr = enumerate.next;
                    continue;
                }
                bool validController = isDualSenseDevice || isDualShock4Device ||
                    ((enumerate.product_id == product_l || enumerate.product_id == product_r ||
                      enumerate.product_id == product_pro || enumerate.product_id == product_snes || enumerate.product_id == product_n64) && enumerate.vendor_id == vendor_id);
                bool isKnownNintendoDevice = enumerate.vendor_id == vendor_id &&
                    (enumerate.product_id == product_l || enumerate.product_id == product_r ||
                     enumerate.product_id == product_pro || enumerate.product_id == product_snes ||
                     enumerate.product_id == product_n64);
                // check list of custom controllers specified
                foreach (SController v in (isDualSenseDevice || isDualShock4Device || isKnownNintendoDevice)
                        ? Enumerable.Empty<SController>() : Program.thirdPartyCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        validController = true;
                        thirdParty = v;
                        break;
                    }
                }

                // auto-detect and register new 3rd-party controllers instead of requiring manual setup
                if (!validController && Boolean.Parse(ConfigurationManager.AppSettings["AutoAddControllers"]) && IsGameController(enumerate)) {
                    bool blockedByTransport = false;
                    if (Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"]) || Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"])) {
                        try {
                            GetControllerTransport(enumerate.path, out bool isUsbDevice, out bool isBluetoothDevice);
                            blockedByTransport = (isUsbDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"])) ||
                                                  (isBluetoothDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"]));
                        } catch {
                            // Can't determine transport - fall through and allow auto-add rather
                            // than silently blocking a device we couldn't actually classify.
                        }
                    }

                    if (!blockedByTransport) {
                        thirdParty = new SController(BuildDeviceName(enumerate), enumerate.vendor_id, enumerate.product_id, GuessType(enumerate), enumerate.serial_number);
                        Program.thirdPartyCons.Add(thirdParty);
                        _3rdPartyControllers.PersistCustomController(thirdParty);
                        validController = true;
                        form.AppendTextBox("Auto-added new controller: " + thirdParty + "\r\n");
                    } else {
                        // Same reasoning as the blacklist case above: BetterJoy won't use this
                        // device, but the same physical controller may still be reachable
                        // through it from another program (e.g. Steam) over the transport we're
                        // not using, so hide it from other programs anyway.
                        TryHideController(enumerate);
                    }
                }

                ushort prod_id = thirdParty == null ? enumerate.product_id : TypeToProdId(thirdParty.type);
                if (prod_id == 0) {
                    ptr = enumerate.next; // controller was not assigned a type, but advance ptr anyway
                    continue;
                }

                if (isDualSenseDevice) {
                    DebugLog.Write("CheckForNewControllers: DualSense enumerated path=" +
                        enumerate.path + " validController=" + validController +
                        " alreadyAdded=" + ControllerAlreadyAdded(enumerate.path));
                }

                if (validController && !ControllerAlreadyAdded(enumerate.path)) {
                    switch (prod_id) {
                        case product_l:
                            isLeft = true;
                            form.AppendTextBox("Left Joy-Con connected.\r\n"); break;
                        case product_r:
                            isLeft = false;
                            form.AppendTextBox("Right Joy-Con connected.\r\n"); break;
                        case product_pro:
                            isLeft = true;
                            form.AppendTextBox("Pro controller connected.\r\n"); break;
                        case product_snes:
                            isLeft = true;
                            form.AppendTextBox("SNES controller connected.\r\n"); break;
                        case product_n64:
                            isLeft = true;
                            form.AppendTextBox("N64 controller connected.\r\n"); break;
                        case product_dualsense:
                        case product_dualsense_edge:
                            isLeft = true;
                            form.AppendTextBox("DualSense controller connected.\r\n"); break;
                        case product_dualshock4_v2:
                            isLeft = true;
                            form.AppendTextBox("DualShock 4 controller connected.\r\n"); break;
                        default:
                            form.AppendTextBox("Non Joy-Con Nintendo input device skipped.\r\n"); break;
                    }

                    // Hide this controller (Joycon, Pro, SNES, or N64 - all share this same
                    // connect path) from other programs (e.g. Steam) via HidHide, before opening/
                    // attaching it ourselves below. If it's still not ready to hide after a few
                    // retries, skip attaching it this pass entirely (rather than falling through
                    // and opening it unhidden) so other programs can't grab the raw device and
                    // end up double-processing input alongside our virtual output. The next
                    // periodic scan (2s later) retries again from there.
                    if (!TryHideController(enumerate)) {
                        form.AppendTextBox("Controller not ready to hide yet, will retry.\r\n");
                        ptr = enumerate.next;
                        continue;
                    }
                    // -------------------- //

                    // hid_open_path returns a null handle (rather than throwing) when it can't
                    // open the device - already exclusively held by another process racing for
                    // the same physical device, momentarily unavailable mid-HidHide-toggle, etc.
                    // Passing that straight into hid_set_nonblocking used to crash the whole
                    // process with an unrecoverable AccessViolationException: native access
                    // violations are a "corrupted state exception" the CLR deliberately lets
                    // bypass ordinary try/catch (since .NET 4.0), so the catch here never
                    // actually protected against this - validate the handle first instead.
                    IntPtr handle = HIDapi.hid_open_path(enumerate.path);
                    if (handle == IntPtr.Zero) {
                        form.AppendTextBox("Unable to open path to device - are you using the correct (64 vs 32-bit) version for your PC?\r\n");
                        break;
                    }
                    HIDapi.hid_set_nonblocking(handle, 1);

                    bool isPro = prod_id == product_pro;
                    bool isSnes = prod_id == product_snes;
                    bool is64 = prod_id == product_n64;
                    bool isDualSense = prod_id == product_dualsense || prod_id == product_dualsense_edge;
                    bool isDualShock4 = prod_id == product_dualshock4_v2;
                    bool? nintendoIsUsb = null;
                    if (!isDualSense && !isDualShock4) {
                        try {
                            GetControllerTransport(enumerate.path,
                                out bool nintendoIsUsbBus, out bool nintendoIsBtBus);
                            if (nintendoIsBtBus)
                                nintendoIsUsb = false;
                            else if (nintendoIsUsbBus)
                                nintendoIsUsb = true;
                        } catch {
                            // Fall back to NintendoController's historical serial-number
                            // heuristic if the PnP parent chain is not ready yet.
                        }
                        DebugLog.Write("Nintendo transport resolved: vid=0x" +
                            enumerate.vendor_id.ToString("X4", CultureInfo.InvariantCulture) +
                            " pid=0x" +
                            enumerate.product_id.ToString("X4", CultureInfo.InvariantCulture) +
                            " serial=\"" + (enumerate.serial_number ?? String.Empty) +
                            "\" isUsb=" +
                            (nintendoIsUsb.HasValue
                                ? nintendoIsUsb.Value.ToString(CultureInfo.InvariantCulture)
                                : "unknown") +
                            " path=" + enumerate.path);
                    }
                    // j.Count (list size, not a stable slot) duplicates an existing PadId the
                    // moment a middle controller disconnects and a new one connects afterward -
                    // e.g. with PadIds 0/1/2 connected, 1 drops, the next new controller would
                    // also get j.Count == 2, colliding with the controller still holding it.
                    // Remote-mode commands (TestRumble/JoinOrSplit/StartCalibration) resolve a
                    // controller by PadId alone, so a collision could route a command to the
                    // wrong physical controller, not just misrender a GUI slot.
                    Controller newController;
                    DualSenseController newDualSense = null;
                    if (isDualSense) {
                        // Resolve wire vs Bluetooth authoritatively from the PnP bus (USB vs
                        // BTHENUM) so the pad's isUSB is correct from packet zero - read length is
                        // unreliable (Windows pads reads). If the PnP tree can't be resolved this
                        // pass (fresh device / throw), fall back to the interface GUID in the path
                        // (00001124 = Bluetooth HID) rather than guessing.
                        bool dualSenseIsUsb;
                        try {
                            GetControllerTransport(enumerate.path,
                                out bool dsIsUsbBus, out bool dsIsBtBus);
                            dualSenseIsUsb = dsIsBtBus ? false
                                : dsIsUsbBus ? true
                                : enumerate.path.IndexOf("00001124",
                                    StringComparison.OrdinalIgnoreCase) < 0;
                        } catch {
                            dualSenseIsUsb = enumerate.path.IndexOf("00001124",
                                StringComparison.OrdinalIgnoreCase) < 0;
                        }
                        newDualSense = new DualSenseController(handle, enumerate.path,
                            enumerate.serial_number, dualSenseIsUsb, NextAvailablePadId());
                        newController = newDualSense;
                    } else if (isDualShock4) {
                        newController = new DualShock4Controller(handle, enumerate.path, enumerate.serial_number, NextAvailablePadId());
                    } else if (isSnes) {
                        newController = new SnesController(handle, EnableIMU,
                            EnableLocalize & EnableIMU, 0.05f, enumerate.path,
                            enumerate.serial_number, NextAvailablePadId(),
                            thirdParty != null, nintendoIsUsb);
                    } else if (is64) {
                        newController = new N64Controller(handle, EnableIMU,
                            EnableLocalize & EnableIMU, 0.05f, enumerate.path,
                            enumerate.serial_number, NextAvailablePadId(),
                            thirdParty != null, nintendoIsUsb);
                    } else if (isPro) {
                        newController = new ProController(handle, EnableIMU,
                            EnableLocalize & EnableIMU, 0.05f, enumerate.path,
                            enumerate.serial_number, NextAvailablePadId(),
                            thirdParty != null, nintendoIsUsb);
                    } else {
                        newController = new JoyconController(handle, EnableIMU,
                            EnableLocalize & EnableIMU, 0.05f, isLeft,
                            enumerate.path, enumerate.serial_number,
                            NextAvailablePadId(), thirdParty != null,
                            nintendoIsUsb);
                    }

                    byte[] mac = new byte[6];
                    bool macParsed = false;
                    string macSource = "serial";
                    bool isNintendoBluetooth =
                        !isDualSense && !isDualShock4 && nintendoIsUsb == false;
                    bool isNintendoUsbPlaceholderSerial =
                        !isDualSense && !isDualShock4 && nintendoIsUsb == true &&
                        String.Equals(enumerate.serial_number, "000000000001",
                            StringComparison.Ordinal);
                    if (isNintendoBluetooth && TryGetNintendoBluetoothMac(enumerate.path, mac)) {
                        macParsed = true;
                        macSource = "nintendo-bthenum";
                    } else if (isNintendoUsbPlaceholderSerial) {
                        macSource = "nintendo-usb-placeholder";
                    } else {
                        try {
                            for (int n = 0; n < 6; n++)
                                mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            macParsed = true;
                        } catch (Exception) {
                            // could not parse mac address
                        }
                    }
                    if (!macParsed) {
                        // A device whose serial_number doesn't parse as a MAC (confirmed on real
                        // hardware: a wired DualSense reports an empty serial_number over USB)
                        // would otherwise silently leave every byte at 0 - a fixed, shared value
                        // ANY other device hitting this same fallback would also get, which
                        // RetireDuplicateConnections (PadMacAddress.Equals) would then treat as
                        // the same physical controller and wrongly drop one of them.
                        //
                        // For a DualSense specifically this has a real fix, not just a workaround:
                        // the controller exposes its own Bluetooth MAC address to the host over
                        // USB via HID feature report 0x09 ("pairing info", 20 bytes, MAC at bytes
                        // 1-6 - confirmed against the Linux kernel's hid-playstation.c driver,
                        // DS_FEATURE_REPORT_PAIRING_INFO). That's the actual hardware identity, not
                        // a synthesized stand-in - stable across app restarts AND identical to what
                        // the same controller reports as its serial_number when paired over
                        // Bluetooth, so USB and BT connections of the same physical unit now
                        // resolve to the same profile.
                        if (isDualSense && TryGetDualSenseMac(handle, mac)) {
                            macParsed = true;
                            macSource = "dualsense-feature-report";
                        } else if (isDualShock4 && TryGetDualShock4Mac(handle, mac)) {
                            macParsed = true;
                            macSource = "dualshock4-feature-report";
                        } else {
                            macSource = "path-hash";
                            // Fallback for anything else that reaches here (or if the feature
                            // report read fails): enumerate.path is OS-assigned and unique per
                            // physical device instance, so hash that into the 6 bytes instead -
                            // not a real MAC, but guaranteed not to collide with an unrelated
                            // device the way an untouched all-zero default could. MUST be
                            // deterministic across app runs, not just within one - a real
                            // regression found on real hardware: string.GetHashCode() is
                            // randomized per-process by default in .NET Framework (a security
                            // mitigation), so hashing with it produced a DIFFERENT PadMacAddress
                            // for the same physical device on every restart, which
                            // ControllerMappings.DeviceId/DeviceSuffix (using PadMacAddress as
                            // profile identity) turned into a new profile each launch. FNV-1a is a
                            // plain, non-cryptographic string hash with no such randomization.
                            uint hash = 2166136261;
                            foreach (byte pb in Encoding.UTF8.GetBytes(enumerate.path ?? enumerate.serial_number ?? String.Empty)) {
                                hash ^= pb;
                                hash *= 16777619;
                            }
                            byte[] hashBytes = BitConverter.GetBytes(hash);
                            Array.Copy(hashBytes, 0, mac, 0, Math.Min(hashBytes.Length, mac.Length));
                        }
                    }
                    if (isDualSense) {
                        // Comparing the resolved MAC across USB vs Bluetooth connections of the
                        // same physical DualSense, since real hardware testing found they don't
                        // currently match. Gated behind DualSenseDebugLogging (see
                        // LogDualSenseRawDump) - file-only, never the GUI panel.
                        newDualSense?.LogDualSenseRawDump(string.Format(CultureInfo.InvariantCulture,
                            "DualSense MAC resolved: {0} (source={1}, serial=\"{2}\")",
                            BitConverter.ToString(mac).Replace("-", ""), macSource, enumerate.serial_number));
                    }
                    if (isNintendoBluetooth)
                        DebugLog.Write(String.Format(CultureInfo.InvariantCulture,
                            "Nintendo Bluetooth MAC resolved: {0} (source={1}, serial=\"{2}\")",
                            BitConverter.ToString(mac).Replace("-", ""), macSource, enumerate.serial_number));
                    if (isDualShock4)
                        DebugLog.Write(String.Format(CultureInfo.InvariantCulture,
                            "DualShock 4 MAC resolved: {0} (source={1}, serial=\"{2}\")",
                            BitConverter.ToString(mac).Replace("-", ""), macSource, enumerate.serial_number));
                    newController.PadMacAddress = new PhysicalAddress(mac);
                    // As soon as we know a DualSense's real Bluetooth MAC (feature-report over USB, or
                    // serial over BT - never the path-hash fallback), preemptively block its MAC-stable
                    // BTHENUM bond so its whole BT HID subtree is hidden the instant it connects
                    // wirelessly, even on a first-ever pair before the BT node exists.
                    if (isDualSense && macParsed &&
                            !String.Equals(macSource, "path-hash", StringComparison.Ordinal))
                        HideBluetoothBondByMac(enumerate.vendor_id, enumerate.product_id, mac);
                    newController.InvalidateMappingProfileCache();
                    newController.form = form;

                    if (newController is NintendoController nintendoUsb &&
                            nintendoUsb.isUSB && !nintendoUsb.thirdParty) {
                        try {
                            nintendoUsb.PrepareUsbConnection();
                        } catch (Exception ex) {
                            DebugLog.Write("Nintendo USB preparation failed: path=" + enumerate.path +
                                " message=\"" + ex.Message + "\"");
                            newController.state = Controller.state_.DROPPED;
                            newController.Detach(true);
                            ptr = enumerate.next;
                            continue;
                        }
                        // The controller's real MAC is known now rather than after Attach, which
                        // is what HidHide reconciliation needs to block the MAC-stable BTHENUM
                        // bond rather than the unstable HID path. Pre-register its HID service
                        // parent while that identity is fresh.
                        ReconcileHidHideForController(nintendoUsb,
                            ControllerMappings.OptionValue(ControllerMappings.ProfileIdFor(nintendoUsb),
                                "UseAs") != ControllerMappings.UseAsPassthrough);
                    }

                    // The pairing ceremony talks to the Bluetooth radio and the bond registry, so it
                    // is the most exposed call in the adopt path while the radio is cycling - and
                    // it runs before the pad is even in the list, so a throw took the scan pass with
                    // it and left nothing enumerating. Treat a failure as "not handled by pairing"
                    // and fall through to the ordinary attach below, which is what happens anyway
                    // when the ceremony declines.
                    bool pairingHandledPad = false;
                    if (newDualSense != null) {
                        try {
                            pairingHandledPad = newDualSense.TryRunAutomaticBluetoothPairingBeforeAttach();
                        } catch (Exception ex) {
                            DebugLog.Write("Automatic Bluetooth pairing failed, falling through to " +
                                "the normal attach: path=" + enumerate.path +
                                " exception=" + ex.GetType().Name +
                                " message=\"" + ex.Message + "\"");
                        }
                    }

                    if (pairingHandledPad) {
                        foundNew = true;
                        ptr = enumerate.next;
                        continue;
                    }

                    j.Add(newController);
                    DumpState("Connect: new controller added, pad=" + newController.PadId.ToString(CultureInfo.InvariantCulture));
                    ResolveStalePadIdCollisions();
                    DumpState("Connect: after ResolveStalePadIdCollisions");

                    foundNew = true;
                    form.AssignSlot(newController);
                }

                ptr = enumerate.next;
            }

            HIDapi.hid_free_enumeration(top_ptr);
            ReleaseRemovedUsbControllerSuppressions(enumeratedPaths);

            // Connect/attach every newly-found device BEFORE auto-join runs below. Auto-join's
            // pairing (Joycon.other's setter) sends a player-LED subcommand immediately, which
            // needs the physical controller to have already been through its handshake (Attach)
            // to respond correctly. Pairing a controller that just connected this same pass (not
            // yet attached) with one left over from an earlier pass (already attached) used to
            // send that LED command before the fresh one was ready for it - silently dropped, or
            // leaving its protocol state confused. Reliably reproduced: whichever Joycon happened
            // to connect second kept showing its own solo player number instead of the pair's,
            // and the pair's virtual controller didn't actually work in games afterward even
            // though it looked fine in joy.cpl.
            foreach (Controller jc in j) { // Connect device straight away
                if (jc.state == Controller.state_.NOT_ATTACHED) {
                    try {
                        jc.Attach();
                    } catch (Exception ex) {
                        DebugLog.Write("Attach failed: pad=" + jc.PadId +
                            " kind=" + jc.Kind +
                            " path=" + jc.path +
                            " exception=" + ex.GetType().Name +
                            " message=\"" + ex.Message + "\"");
                        jc.state = Controller.state_.DROPPED;
                        continue;
                    }

                    // Publish attachment state before polling begins. Official Nintendo USB
                    // identity is already resolved before the initial slot is published.
                    form.RefreshControllerState();

                    // Attach() itself has been guarded forever (just above), but the tail after
                    // it never was - and a throw here is worse, because the pad is already in j.
                    // It stays there attached, with no virtual controller, no profile policy
                    // applied and no Poll thread, while every later pass sees alreadyAdded=True and
                    // leaves it exactly like that. Only a service restart cleared it. Drop it
                    // instead, the same recovery Attach()'s own catch performs, and the next pass
                    // adopts it cleanly.
                    try {
                        CreateOutputControllers(jc);
                        string profileId = ControllerMappings.ProfileIdFor(jc);
                        bool usbSleepOnConnectQueued =
                            jc is DualSenseController dualSense &&
                            dualSense.ApplyUSBSleepOnConnectAfterAttach();
                        if (!usbSleepOnConnectQueued)
                            ApplyControllerProfileLighting(jc, profileId);
                        ControllerMappings.EnsureProfileSaved(profileId);
                        // Fresh connection: OpenRGB's own device list won't have this controller yet
                        // unless it happens to already be running a scan - nudge it once it's had a
                        // moment to see the HID device (OpenRgbRescan's own settle delay).
                        if (!usbSleepOnConnectQueued &&
                                ControllerMappings.LightingMode(profileId) ==
                                ControllerMappings.LightingModeOpenRgb) {
                            DebugLog.Write("OpenRgbRescan: triggered from Attach, profileId=" + profileId);
                            OpenRgbRescan.RequestRescan();
                        }

                        jc.Begin();
                        if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                            jc.getActiveData();
                        }
                    } catch (Exception ex) {
                        // Full stack, not just the message: the tail spans HidHide, ViGEm and
                        // VIIPER calls, and which one threw is the entire diagnosis.
                        DebugLog.Write("Attach tail failed: pad=" + jc.PadId +
                            " kind=" + jc.Kind +
                            " path=" + jc.path +
                            " exception=" + ex.GetType().Name +
                            " message=\"" + ex.Message + "\"\r\n" + ex.StackTrace);
                        jc.state = Controller.state_.DROPPED;
                        continue;
                    }
                }
            }

            if (foundNew && !Boolean.Parse(ConfigurationManager.AppSettings["DoNotRejoinJoycons"])) { // attempt to auto join-up joycons on connection
                JoyconController temp = null;
                // Pairing is Joy-Con-only (see Controller.other's comment) - the auto-join
                // decision itself stays JoyconController-typed/filtered here rather than moving
                // to the generic lifecycle module, per DOCS/CONTROLLERS-REFACTOR.md's design.
                foreach (Controller vBase in j) {
                    // Do not attach two controllers if they are either:
                    // - Not a JoyconController, or one that doesn't support pairing
                    // - Already attached to another one (that isn't itself)
                    if (!(vBase is JoyconController v) || !v.SupportsPairing || (v.other != null && v.other != v)) {
                        continue;
                    }

                    // Otherwise, iterate through and find the controller with the lowest
                    // id that has not been attached already (Does not include self)
                    if (temp == null)
                        temp = v;
                    else if (temp.isLeft != v.isLeft && v.other == null) {
                        temp.other = v;
                        v.other = temp;

                        // Disconnect whichever controller was created later - see Joycon.
                        // virtualControllerSequence - not just whichever happened to be found
                        // first in this iteration (usually but not guaranteed to be the older
                        // one). Its controller was just Connect()ed moments ago by the "connect
                        // device straight away" loop above, so this is a real disconnect, not a
                        // no-op - matches a real unplug (clean, no leftover state); the other one
                        // is left untouched as the pair's shared controller. The loser DECISION is
                        // pairing-specific and stays here (Joy-Con is the only type that pairs at
                        // all - see DOCS/CONTROLLERS-REFACTOR.md's virtual-controller-lifecycle
                        // section), but the actual destroy goes through the same pairing-ignorant
                        // primitive AssignPadId/ApplyControllerProfileOptions use, not a duplicate.
                        JoyconController loser = temp.virtualControllerSequence > v.virtualControllerSequence ? temp : v;
                        DestroyOutputControllers(loser);

                        JoyconController left = temp.isLeft ? temp : v;
                        JoyconController right = temp.isLeft ? v : temp;
                        form.CollapseJoinedPair(left, right);
                        DumpState("AutoJoin: paired");

                        temp = null;    // repeat
                    }
                }

                // Anything still solo after the join pass above (no available opposite-handed
                // partner) self-pairs into vertical orientation if that profile's saved default
                // says to - matching what a manual double right-click/ForceSelfPair would do,
                // just automatically on connect. Profile lookup uses this controller's still-solo
                // identity (ProfileIdFor), since that's the identity it had before this decision.
                foreach (Controller vBase in j) {
                    if (!(vBase is JoyconController v) || !v.SupportsPairing || v.other != null)
                        continue;
                    string profileId = ControllerMappings.ProfileIdFor(v);
                    if (ControllerMappings.OptionValue(profileId, "DefaultOrientation") ==
                        ControllerMappings.OrientationVertical) {
                        v.other = v;
                        form.RefreshOrientationIcon(v);
                    }
                }

                DumpState("AutoJoin: end of pass");
            }

            // Not Joy-Con-specific and not conditional on DoNotRejoinJoycons. This is the attach
            // path that starts saved controller behavior—including DS4 Bluetooth audio—without
            // requiring the user to toggle an option after every connection. Reconciliation is
            // idempotent and also picks up jack-state changes on periodic scan passes.
            ApplyControllerProfileOptions();
        }

        public void OnApplicationQuit() {
            foreach (Controller v in j) {
                if (ControllerMappings.BoolOption(ControllerMappings.ProfileIdFor(v), "AutoPowerOff"))
                    v.PowerOff(shuttingDown: true);

                form.StopUsbAudioLoopback(v.PadId);
                v.Detach();

                // A target that was created but never actually got plugged in (or was already
                // unplugged) throws VigemTargetNotPluggedInException on Disconnect() - same
                // "wasn't connected in the first place" case already guarded against elsewhere
                // (see the auto-join block above), just missed here. Left unhandled, this was
                // surfacing as a failed/hung service stop (DeferredStop propagating the
                // exception back to the SCM) rather than a clean shutdown.
                if (v.out_xbox != null) {
                    try { v.out_xbox.Disconnect(); } catch { }
                }

                if (v.out_ds4 != null) {
                    try { v.out_ds4.Disconnect(); } catch { }
                }

                if (v.out_dualsense != null) {
                    try { v.out_dualsense.Disconnect(); } catch { }
                }
            }

            StopScanning();
            HIDapi.hid_exit();
        }
    }

    class Program {
        public static PhysicalAddress btMAC = new PhysicalAddress(new byte[] { 0, 0, 0, 0, 0, 0 });
        public static UdpServer server;

        public static ViGEmClient emClient;
        private static readonly object emClientLock = new object();

        // One libVIIPER USB server for the whole process, mirroring emClient above - every
        // VIIPER-backed virtual controller (regardless of profile) shares this single server and
        // gets its own bus/device on it via OutputControllerXbox360Viiper. UIntPtr.Zero is never a
        // real handle (libVIIPER's cgo.Handle values start at 1), so it doubles as "not created".
        public static UIntPtr viiperServerHandle = UIntPtr.Zero;
        private static readonly object viiperServerLock = new object();
        private static readonly VirtualOutput.LibViiper.LogCallback viiperLogCallback = ViiperLog;

        private static void ViiperLog(VirtualOutput.LibViiper.LogLevel level, string message) {
            if (level >= VirtualOutput.LibViiper.LogLevel.Warn)
                form?.AppendTextBox("VIIPER: " + message + "\r\n");
        }

        public static bool EnsureViiperServer() {
            if (viiperServerHandle != UIntPtr.Zero)
                return true;
            lock (viiperServerLock) {
                if (viiperServerHandle != UIntPtr.Zero)
                    return true;
                try {
                    var config = new VirtualOutput.LibViiper.ServerConfig { Addr = "localhost:0" };
                    if (VirtualOutput.LibViiper.NewUSBServer(
                            ref config, out UIntPtr handle, viiperLogCallback)) {
                        viiperServerHandle = handle;
                        return true;
                    }
                } catch (DllNotFoundException) {
                    // libVIIPER.dll missing or usbip-win2 not installed - same "tell the user,
                    // don't crash" handling as EnsureVigemClient's VigemBusNotFoundException.
                }
                form?.AppendTextBox(
                    "Could not start the VIIPER virtual USB server. Make sure the VIIPER/" +
                    "usbip-win2 drivers are installed correctly.\r\n");
                return false;
            }
        }

        public static JoyconManager mgr;

        static IJoyconHost form;

        // Lets a non-GUI host (BetterJoyService, running headless with no MainForm at all) wire
        // itself in before calling Start(). GUI mode sets this itself via Main() below.
        public static void SetHost(IJoyconHost host) {
            form = host;
        }

        static public bool useHidHide = Boolean.Parse(ConfigurationManager.AppSettings["UseHidHide"]);
        public static IHidHideControlService hidHide;
        public static readonly List<string> hiddenInstanceIds = new List<string>();

        // Guards hiddenInstanceIds - a plain List<string> accessed from both the scan timer's
        // background thread (JoyconManager.TryHideController, adding) and Stop() below
        // (enumerating + clearing) on whatever thread calls it. Stopping the scan timer first
        // (see StopScanning) closes most of the window but doesn't wait for a callback already
        // in flight to finish - this lock is what actually guarantees no concurrent mutation.
        public static readonly object hiddenInstanceIdsLock = new object();

        // Serializes the driver-side blocklist calls. AddBlockedInstanceId/RemoveBlockedInstanceId are
        // each a read-modify-write of HidHide's own list, so two of them racing (scan thread vs the
        // profile-reconciliation path) can wedge it - see nefarius/HidHide#83, #215, and the real
        // incident where every client failed with ERROR_INVALID_PARAMETER until the list was cleared.
        // Held across the cache check and the call so the idempotency check can't go stale mid-flight.
        public static readonly object hidHideDriverLock = new object();

        // The "A&<radio>&0&" segment shared by every Enum\BTHENUM instance on this PC - the local
        // Bluetooth radio, resolved once and reused to manufacture controller bond ids from a MAC.
        public static volatile string cachedBluetoothRadioSegment;

        public static List<SController> thirdPartyCons = new List<SController>();
        public static List<SController> blacklistedCons = new List<SController>();

        private static WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private static WindowsInput.Events.Sources.IMouseEventSource mouse;

        public static bool EnsureVigemClient() {
            if (emClient != null)
                return true;
            lock (emClientLock) {
                if (emClient != null)
                    return true;
                try {
                    emClient = new ViGEmClient();
                    return true;
                } catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException) {
                    form?.AppendTextBox(
                        "Could not start VigemBus. Make sure drivers are installed correctly.\r\n");
                    return false;
                }
            }
        }

        // Releases the ViGEm client so the next EnsureVigemClient builds a fresh one. Stop() used to
        // leave it alone, which was harmless while Stop() only ever ran at process exit - it died
        // with the process. Now that a suspend/resume runs Stop() and Start() in the same process,
        // leaving it would leak a client per sleep. Must run after every target is disconnected.
        private static void ReleaseVigemClient() {
            lock (emClientLock) {
                if (emClient != null) {
                    try { emClient.Dispose(); } catch { }
                    emClient = null;
                }
            }
        }

        public static void Start() {
            // Previously only ever called from MainForm_Load, so a Windows Service (which never
            // constructs a MainForm) silently never loaded remap keybinds (capture/home/sl_*/
            // sr_*/shake/reset_mouse/legacy active_gyro) at all - every Config.Value(...) lookup for
            // them returned "" under service mode. Moved here so both modes get it.
            Config.Init(CalibrationState.CaliData, CalibrationState.StickCaliData, CalibrationState.Stick2CaliData);

            if (useHidHide) {
                try {
                    // HidHide's config lives in a read/write-protected registry key (not file-based,
                    // intentionally, so it can't be casually edited outside the driver API):
                    // https://github.com/nefarius/HidHide/discussions/130
                    hidHide = new HidHideControlService();
                    if (!hidHide.IsInstalled) {
                        form.AppendTextBox("HidHide isn't installed - controllers won't be hidden from other programs.\r\n");
                        useHidHide = false;
                    } else {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        if (!hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                            hidHide.AddApplicationPath(exePath);
                        hidHide.IsActive = true;

                        // Mirror the in-memory cache to HidHide's persisted blocklist at startup (the
                        // scan pass refreshes it thereafter - see SyncHiddenInstanceCacheFromRegistry).
                        JoyconManager.SyncHiddenInstanceCacheFromRegistry();
                    }
                } catch (Exception e) {
                    form.AppendTextBox("Unable to configure HidHide - everything should work fine without it. (" + e.GetType().Name + ": " + e.Message + ")\r\n");
                    useHidHide = false;
                }
            }

            if (ControllerMappings.AnyVirtualOutputEnabled())
                EnsureVigemClient();

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                // Get local BT host MAC
                if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) {
                    if (nic.Name.Split()[0] == "Bluetooth") {
                        btMAC = nic.GetPhysicalAddress();
                    }
                }
            }

            // GUI mode goes through the actual Form (also where the Add Controllers dialog's
            // lists get populated for editing); headless/service mode has no desktop for a Form
            // to exist on, so it loads the persisted lists directly instead - see
            // _3rdPartyControllers.LoadIntoProgramLists.
            if (form is MainForm) {
                // a bit hacky
                _3rdPartyControllers partyForm = new _3rdPartyControllers();
                partyForm.CopyCustomControllers();
                partyForm.CopyBlacklistedControllers();
            } else {
                _3rdPartyControllers.LoadIntoProgramLists();
            }

            mgr = new JoyconManager();
            mgr.form = form;
            mgr.Awake();
            // A failure in the initial pass must not cost us the scan timer below. Without that
            // timer nothing ever cleans up a controller this pass already adopted and nothing
            // notices a replug, so one bad pass becomes a permanent zombie pad holding a virtual
            // controller. The timer's next tick retries exactly this work anyway.
            try {
                mgr.CheckForNewControllers();
            } catch (Exception e) {
                DebugLog.Write("Start: initial controller scan failed, timer will retry: " +
                    e.GetType().Name + ": " + e.Message);
            }
            mgr.Start();

            server = new UdpServer(mgr.j);
            server.form = form;

            server.Start(IPAddress.Parse(ConfigurationManager.AppSettings["IP"]), Int32.Parse(ConfigurationManager.AppSettings["Port"]));

            OpenRgbServer.SyncEnabledState();

            // Global keyboard/mouse hooks need an interactive desktop - fine in GUI mode, but
            // Session 0 (where a Windows Service runs) has none, so this would throw/do nothing
            // useful there. Service mode instead gets forwarded events over a pipe from a
            // session-launched helper process that can (see HeadlessJoyconHost/SessionLauncher).
            if (form is MainForm) {
                keyboard = WindowsInput.Capture.Global.KeyboardAsync();
                keyboard.KeyEvent += (sender, e) => {
                    if (e.Data.KeyDown != null) OnKeyDown((int)e.Data.KeyDown.Key);
                    if (e.Data.KeyUp != null) OnKeyUp((int)e.Data.KeyUp.Key);
                };
                mouse = WindowsInput.Capture.Global.MouseAsync();
                mouse.MouseEvent += (sender, e) => {
                    if (e.Data.ButtonDown != null) OnMouseButtonDown((int)e.Data.ButtonDown.Button);
                    if (e.Data.ButtonUp != null) OnMouseButtonUp((int)e.Data.ButtonUp.Button);
                };
            }

            form.AppendTextBox("All systems go\r\n");
        }

        // reset_mouse and the legacy shared active_gyro used to be decided here (single
        // key_/mse_ trigger only), but
        // now that both support a "+"-joined combo mixing controller/keyboard/mouse inputs
        // together (see Joycon.IsComboHeld), they need simultaneous visibility into all three at
        // once - evaluated once per packet in Joycon.DoThingsWithButtons instead, which already
        // runs per connected controller and can check its own buttons directly. These handlers
        // now just feed InputState so that check has an up-to-date view of which keys/mouse
        // buttons are currently held - kept independent of *how* the raw event was observed, so
        // both GUI mode's direct WindowsInput.Capture.Global hook and service mode's pipe-
        // forwarded events from the session-launched helper feed the exact same tracker.
        public static void OnKeyDown(int keyCode) => InputState.KeyDown(keyCode);
        public static void OnKeyUp(int keyCode) => InputState.KeyUp(keyCode);
        public static void OnMouseButtonDown(int buttonCode) => InputState.MouseDown(buttonCode);
        public static void OnMouseButtonUp(int buttonCode) => InputState.MouseUp(buttonCode);

        public static List<UsbDeviceReenumerator.PortTarget> Stop(bool suspending = false) {
            var suspendPortTargets = new List<UsbDeviceReenumerator.PortTarget>();
            try {
                // Stop the background scan first - otherwise it can still be adding to
                // hiddenInstanceIds (TryHideController) while the loop below enumerates/clears it.
                mgr.StopScanning();
                if (suspending)
                    suspendPortTargets = mgr.ReleaseControllersForSuspend();

                if (!suspending && useHidHide && hidHide != null && Boolean.Parse(ConfigurationManager.AppSettings["UnhideOnExit"])) {
                    lock (hiddenInstanceIdsLock) {
                        foreach (string id in hiddenInstanceIds) {
                            try { hidHide.RemoveBlockedInstanceId(id); } catch { }
                        }
                        hiddenInstanceIds.Clear();
                    }
                }

                keyboard?.Dispose(); mouse?.Dispose();
                server.Stop();
                mgr.OnApplicationQuit();
            } finally {
                // Last, so every virtual target has already been disconnected off it - but
                // unconditionally, because a Stop() that failed partway is exactly when a following
                // Start() must not inherit a stale client.
                ReleaseVigemClient();
            }
            return suspendPortTargets;
        }

        // Sleeping yanks the HID stack out from under every open handle. An attached
        // controller's Poll thread dies where it stands without ever reaching state_.DROPPED,
        // so CleanUp() - which removes only DROPPED pads - never releases it, its virtual
        // target stays plugged, and ControllerAlreadyAdded goes on matching the dead path so
        // the replug is ignored forever. That is the ghost pad. So mark them dropped ourselves
        // and let the ordinary scan pass handle it from there, exactly as it does for a real
        // disconnect - deliberately NOT a Stop()/Start() cycle, which discards the manager's
        // USB/Bluetooth suppression state and so adopts one physical pad twice on the way back.
        //
        private static string appGuid = "1bf709e9-c133-41df-933a-c9ff3f664c7b"; // randomly-generated
        public static void Main(string[] args) {
            using (Mutex mutex = new Mutex(false, "Global\\" + appGuid)) {
                if (!mutex.WaitOne(0, false)) {
                    MessageBox.Show("Instance already running.", "BetterJoy2");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm mainForm = new MainForm();
                form = mainForm;
                Application.Run(mainForm);
            }
        }

    }
}
