using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterJoyForCemu {
    // Windows exposes XInput-compatible controllers (Microsoft and licensed third-party pads)
    // through a synthetic IG_ HID endpoint, but the controller state itself comes from XInput. Keep the HID handle for BetterJoy's existing
    // ownership/HidHide lifecycle and normalize the native XInput state directly into BetterJoy's
    // canonical positional codes. No Nintendo-label or physical-to-physical translation occurs.
    public sealed class XboxController : Controller {
        private const uint ErrorSuccess = 0;
        private const uint ErrorDeviceNotConnected = 1167;
        private const ushort GuideButton = 0x0400;
        private const int MaximumLoggedStates = 64;
        private const int MaximumConsecutiveInputFailures = 240;
        private static readonly object XInputSlotLock = new object();
        private static readonly bool[] ClaimedXInputSlots = new bool[4];
        private static bool extendedStateAvailable = true;
        private static bool extendedCapabilitiesAvailable = true;

        private readonly ushort vendorId;
        private readonly ushort productId;
        private readonly byte[] triggerVal = new byte[2];
        private int xInputSlot = -1;
        private bool hasLastPacket;
        private uint lastPacketNumber;
        private int statesLogged;
        private int consecutiveInputFailures;

        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => false;
        public override bool HasAnalogTriggers => true;
        public override bool UsesNintendoProtocol => false;
        public override ControllerKind Kind => ControllerKind.Xbox;
        protected override byte[] TriggerVal => triggerVal;
        protected override bool AllowsSilentHidIdle => false;

        public XboxController(IntPtr handle_, string path, string serialNum,
                bool isUsb, ushort vendorId, ushort productId, int id = 0) {
            handle = handle_;
            this.path = path;
            serial_number = serialNum;
            this.isUSB = isUsb;
            this.vendorId = vendorId;
            this.productId = productId;
            isLeft = true;
            PadId = id;
            connection = isUsb ? 0x01 : 0x02;
            activeData = new float[6];
            rumble_obj = new Rumble(new float[] { 0, 0, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            RefreshGyroOnlyButtonReservations();
        }

        public override int Attach() {
            HIDapi.hid_set_nonblocking(handle, 1);
            xInputSlot = ClaimXInputSlot(vendorId, productId);
            if (xInputSlot < 0) {
                if (DebugLog.Enabled) {
                    DebugLog.Write("[XboxInput.Attach] no matching unclaimed XInput slot for vid=0x" +
                        vendorId.ToString("X4", CultureInfo.InvariantCulture) + " pid=0x" +
                        productId.ToString("X4", CultureInfo.InvariantCulture) + " path=" + path);
                }
                throw new InvalidOperationException(
                    "Xbox controller is visible to HID but no matching XInput slot is available.");
            }

            state = state_.ATTACHED;
            if (DebugLog.Enabled) {
                DebugLog.Write("[XboxInput.Attach] pad=" + PadId +
                    " xinputSlot=" + xInputSlot.ToString(CultureInfo.InvariantCulture) +
                    " transport=" + (isUSB ? "USB/adapter" : "Bluetooth") +
                    " vid=0x" + vendorId.ToString("X4", CultureInfo.InvariantCulture) +
                    " pid=0x" + productId.ToString("X4", CultureInfo.InvariantCulture) +
                    " serial=\"" + (serial_number ?? String.Empty) + "\" path=" + path);
            }
            form.AppendTextBox("Xbox controller attached.\r\n");
            return 0;
        }

        protected override int ReceiveRaw() {
            if (xInputSlot < 0)
                return -2;

            try {
                XInputState nativeState;
                uint result = GetNativeState((uint)xInputSlot, out nativeState);
                if (result != ErrorSuccess)
                    return RecordInputFailure(result);

                if ((!hasLastPacket || nativeState.PacketNumber != lastPacketNumber) &&
                        !CurrentSlotIdentityMatches()) {
                    if (ReconcileNativeInputSlot())
                        return 0;
                    return RecordInputFailure(ErrorDeviceNotConnected);
                }

                if (consecutiveInputFailures > 0) {
                    if (DebugLog.Enabled) {
                        DebugLog.Write("[XboxInput.Read] recovered pad=" + PadId +
                            " xinputSlot=" + xInputSlot.ToString(CultureInfo.InvariantCulture) +
                            " afterFailures=" + consecutiveInputFailures.ToString(
                                CultureInfo.InvariantCulture));
                    }
                    consecutiveInputFailures = 0;
                }

                if (hasLastPacket && nativeState.PacketNumber == lastPacketNumber) {
                    // XInput is immediate rather than a blocking HID read. Yield here so an idle
                    // controller does not turn BetterJoy's dedicated polling thread into a busy loop.
                    Thread.Sleep(1);
                    // XInput answered successfully, so the controller is connected even though
                    // nothing changed. Returning 0 here let Poll's stale-connection check drop an
                    // untouched pad after 3 seconds; a real disconnect returns an XInput error.
                    return 1;
                }

                hasLastPacket = true;
                lastPacketNumber = nativeState.PacketNumber;
                LogNativeState(nativeState);
                ApplyParsedReport(MapXInputState(nativeState));
                DispatchParsedInputReport();
                return 1;
            } catch {
                consecutiveInputFailures++;
                if (ShouldReleaseInputSlot(consecutiveInputFailures))
                    ReleaseXInputSlot();
                throw;
            }
        }

        private int RecordInputFailure(uint result) {
            consecutiveInputFailures++;
            if (DebugLog.Enabled && (consecutiveInputFailures == 1 ||
                    ShouldReleaseInputSlot(consecutiveInputFailures))) {
                DebugLog.Write("[XboxInput.Read] failed pad=" + PadId +
                    " xinputSlot=" + xInputSlot.ToString(CultureInfo.InvariantCulture) +
                    " result=" + result.ToString(CultureInfo.InvariantCulture) +
                    " consecutive=" + consecutiveInputFailures.ToString(
                        CultureInfo.InvariantCulture));
            }
            if (ShouldReleaseInputSlot(consecutiveInputFailures))
                ReleaseXInputSlot();
            return result == ErrorDeviceNotConnected ? -1 : -2;
        }

        internal static bool ShouldReleaseInputSlot(int consecutiveFailures) {
            return consecutiveFailures >= MaximumConsecutiveInputFailures;
        }

        private void ApplyParsedReport(ParsedReport parsed) {
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; i++)
                        down_[i] = buttons[i];
                }

                buttons = parsed.Buttons;
                stick[0] = parsed.LeftX;
                stick[1] = parsed.LeftY;
                stick2[0] = parsed.RightX;
                stick2[1] = parsed.RightY;
                triggerVal[0] = parsed.LeftTrigger;
                triggerVal[1] = parsed.RightTrigger;
                CommitButtonState();
            }
        }

        private void DispatchParsedInputReport() {
            DoThingsWithButtons();
            Timestamp += 1000;
            packetCounter++;

            if (Program.server != null)
                Program.server.NewReportIncoming(this);

            if (out_xbox != null) {
                try { out_xbox.UpdateInput(MapToXbox360Input(this)); } catch (Exception) { }
            }
            if (out_ds4 != null || out_dualsense != null) {
                var output = MapToDualShock4Input(this);
                if (out_ds4 != null) {
                    try { out_ds4.UpdateInput(output); } catch (Exception) { }
                }
                if (out_dualsense != null) {
                    try { out_dualsense.UpdateInput(output); } catch (Exception) { }
                }
            }
        }

        // Input-only first pass: keep the existing feedback queue bounded without sending a raw
        // GIP packet through Windows' synthetic HID endpoint.
        protected override void SendQueuedRumbleIfAny() {
            while (rumble_obj.queue.Count > 0)
                rumble_obj.queue.Dequeue();
        }

        // Release on every detach, not only while attached: CleanUp detaches controllers Poll has
        // already marked DROPPED (stale or failed connections), and a leaked slot would stop this
        // controller from ever latching on again until the service restarts.
        protected override void OnDetached() {
            if (DebugLog.Enabled && xInputSlot >= 0) {
                DebugLog.Write("[XboxInput.Slot] released pad=" + PadId +
                    " slot=" + xInputSlot.ToString(CultureInfo.InvariantCulture));
            }
            ReleaseXInputSlot();
        }

        // ViGEm can alter XInput user-index assignment when its virtual target connects. Resolve
        // the physical VID/PID again at that boundary so this receiver never starts polling its
        // own 045E:028E output and feeding the generated state back into itself.
        internal bool ReconcileNativeInputSlot() {
            int currentSlot = xInputSlot;
            if (currentSlot >= 0 && CurrentSlotIdentityMatches())
                return true;

            int replacement = ClaimXInputSlot(vendorId, productId);
            if (replacement < 0)
                return false;

            lock (XInputSlotLock) {
                if (currentSlot >= 0 && currentSlot < ClaimedXInputSlots.Length &&
                        currentSlot != replacement) {
                    ClaimedXInputSlots[currentSlot] = false;
                }
                xInputSlot = replacement;
            }
            hasLastPacket = false;
            consecutiveInputFailures = 0;
            if (DebugLog.Enabled) {
                DebugLog.Write("[XboxInput.Slot] rebound pad=" + PadId +
                    " from=" + currentSlot.ToString(CultureInfo.InvariantCulture) +
                    " to=" + replacement.ToString(CultureInfo.InvariantCulture) +
                    " expected=0x" + vendorId.ToString("X4", CultureInfo.InvariantCulture) +
                    ":0x" + productId.ToString("X4", CultureInfo.InvariantCulture));
            }
            return true;
        }

        private bool CurrentSlotIdentityMatches() {
            int currentSlot = xInputSlot;
            if (currentSlot < 0 || currentSlot >= ClaimedXInputSlots.Length)
                return false;
            // Never keep reading a slot BetterJoy's own virtual output occupies, even when the
            // runtime cannot report VID/PID for it.
            if (OwnVirtualXInputSlots()[currentSlot])
                return false;
            ushort foundVendorId;
            ushort foundProductId;
            // If the extended identity API is unavailable, retain the already-claimed slot; the
            // single-slot fallback used during Attach is the only unambiguous option available.
            return !TryGetNativeIdentity((uint)currentSlot, out foundVendorId,
                out foundProductId) || (!JoyconManager.IsVigemVirtualController(
                    foundVendorId, foundProductId) && foundVendorId == vendorId &&
                    foundProductId == productId);
        }

        private void ReleaseXInputSlot() {
            lock (XInputSlotLock) {
                if (xInputSlot >= 0 && xInputSlot < ClaimedXInputSlots.Length)
                    ClaimedXInputSlots[xInputSlot] = false;
                xInputSlot = -1;
            }
        }

        // XInput slots held by BetterJoy's own ViGEm Xbox 360 targets. ViGEm reports the exact user
        // index each target was assigned, so ownership is known directly rather than inferred from
        // VID/PID, which some XInput runtimes cannot report. VIIPER targets report -1 and remain
        // covered only by the VID/PID check.
        private static bool[] OwnVirtualXInputSlots() {
            var userIndices = new List<int>();
            JoyconManager manager = Program.mgr;
            if (manager?.j != null) {
                foreach (Controller controller in manager.j) {
                    if (controller.out_xbox != null)
                        userIndices.Add(controller.out_xbox.UserIndex);
                }
            }
            return MarkOwnedXInputSlots(userIndices);
        }

        internal static bool[] MarkOwnedXInputSlots(IEnumerable<int> userIndices) {
            bool[] owned = new bool[ClaimedXInputSlots.Length];
            foreach (int index in userIndices) {
                if (index >= 0 && index < owned.Length)
                    owned[index] = true;
            }
            return owned;
        }

        private static int ClaimXInputSlot(ushort wantedVendorId, ushort wantedProductId) {
            bool[] ownVirtualSlots = OwnVirtualXInputSlots();
            lock (XInputSlotLock) {
                int fallback = -1;
                int fallbackCount = 0;
                for (int slot = 0; slot < ClaimedXInputSlots.Length; slot++) {
                    if (ClaimedXInputSlots[slot])
                        continue;
                    if (ownVirtualSlots[slot]) {
                        if (DebugLog.Enabled) {
                            DebugLog.Write("[XboxInput.Slot] skipped=" +
                                slot.ToString(CultureInfo.InvariantCulture) +
                                " reason=betterjoy-virtual-output");
                        }
                        continue;
                    }

                    XInputState candidateState;
                    uint stateResult = GetNativeState((uint)slot, out candidateState);
                    if (stateResult != ErrorSuccess)
                        continue;

                    ushort candidateVendorId;
                    ushort candidateProductId;
                    bool hasIdentity = TryGetNativeIdentity((uint)slot,
                        out candidateVendorId, out candidateProductId);
                    if (DebugLog.Enabled) {
                        DebugLog.Write("[XboxInput.Slot] candidate=" +
                            slot.ToString(CultureInfo.InvariantCulture) +
                            " identity=" + (hasIdentity ?
                                "0x" + candidateVendorId.ToString("X4", CultureInfo.InvariantCulture) +
                                ":0x" + candidateProductId.ToString("X4", CultureInfo.InvariantCulture) :
                                "unavailable"));
                    }
                    if (hasIdentity && JoyconManager.IsVigemVirtualController(
                            candidateVendorId, candidateProductId)) {
                        continue;
                    }
                    if (hasIdentity && candidateVendorId == wantedVendorId &&
                            candidateProductId == wantedProductId) {
                        ClaimedXInputSlots[slot] = true;
                        return slot;
                    }
                    if (!hasIdentity) {
                        fallback = slot;
                        fallbackCount++;
                    }
                }

                // Older XInput runtimes do not expose VID/PID. A single unclaimed connected slot
                // is still unambiguous; never guess when two or more devices could be selected.
                if (fallbackCount == 1) {
                    ClaimedXInputSlots[fallback] = true;
                    return fallback;
                }
                return -1;
            }
        }

        private static uint GetNativeState(uint slot, out XInputState nativeState) {
            if (extendedStateAvailable) {
                try {
                    return XInputGetStateEx(slot, out nativeState);
                } catch (EntryPointNotFoundException) {
                    extendedStateAvailable = false;
                }
            }
            return XInputGetState(slot, out nativeState);
        }

        private static bool TryGetNativeIdentity(uint slot, out ushort foundVendorId,
                out ushort foundProductId) {
            foundVendorId = 0;
            foundProductId = 0;
            if (!extendedCapabilitiesAvailable)
                return false;
            try {
                XInputCapabilitiesEx capabilities;
                if (XInputGetCapabilitiesEx(1, slot, 0, out capabilities) != ErrorSuccess)
                    return false;
                foundVendorId = capabilities.VendorId;
                foundProductId = capabilities.ProductId;
                return foundVendorId != 0 || foundProductId != 0;
            } catch (EntryPointNotFoundException) {
                extendedCapabilitiesAvailable = false;
                return false;
            }
        }

        private void LogNativeState(XInputState nativeState) {
            if (!DebugLog.Enabled || statesLogged >= MaximumLoggedStates)
                return;
            statesLogged++;
            XInputGamepad gamepad = nativeState.Gamepad;
            DebugLog.Write("[XboxInput.State] pad=" + PadId +
                " xinputSlot=" + xInputSlot.ToString(CultureInfo.InvariantCulture) +
                " sample=" + statesLogged.ToString(CultureInfo.InvariantCulture) +
                "/64-changes packet=" + nativeState.PacketNumber.ToString(CultureInfo.InvariantCulture) +
                " buttons=0x" + gamepad.Buttons.ToString("X4", CultureInfo.InvariantCulture) +
                " triggers=" + gamepad.LeftTrigger.ToString(CultureInfo.InvariantCulture) +
                "," + gamepad.RightTrigger.ToString(CultureInfo.InvariantCulture) +
                " sticks=" + gamepad.LeftThumbX.ToString(CultureInfo.InvariantCulture) +
                "," + gamepad.LeftThumbY.ToString(CultureInfo.InvariantCulture) +
                "," + gamepad.RightThumbX.ToString(CultureInfo.InvariantCulture) +
                "," + gamepad.RightThumbY.ToString(CultureInfo.InvariantCulture));
        }

        internal struct ParsedReport {
            internal bool[] Buttons;
            internal byte LeftTrigger;
            internal byte RightTrigger;
            internal float LeftX;
            internal float LeftY;
            internal float RightX;
            internal float RightY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputGamepad {
            internal ushort Buttons;
            internal byte LeftTrigger;
            internal byte RightTrigger;
            internal short LeftThumbX;
            internal short LeftThumbY;
            internal short RightThumbX;
            internal short RightThumbY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputState {
            internal uint PacketNumber;
            internal XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputVibration {
            internal ushort LeftMotorSpeed;
            internal ushort RightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputCapabilities {
            internal byte Type;
            internal byte SubType;
            internal ushort Flags;
            internal XInputGamepad Gamepad;
            internal XInputVibration Vibration;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputCapabilitiesEx {
            internal XInputCapabilities Capabilities;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort VersionNumber;
            internal ushort Unknown1;
            internal uint Unknown2;
        }

        // Pure mapper kept separate from native polling so Tools/Test-XboxController.ps1 can lock
        // down the physical-XInput-bit -> canonical-position -> virtual-output contract.
        internal static ParsedReport MapXInputState(XInputState nativeState) {
            XInputGamepad gamepad = nativeState.Gamepad;
            ushort value = gamepad.Buttons;
            bool[] b = new bool[ButtonCount];

            b[(int)Button.DPAD_UP] = (value & 0x0001) != 0;
            b[(int)Button.DPAD_DOWN] = (value & 0x0002) != 0;
            b[(int)Button.DPAD_LEFT] = (value & 0x0004) != 0;
            b[(int)Button.DPAD_RIGHT] = (value & 0x0008) != 0;
            b[(int)Button.PLUS] = (value & 0x0010) != 0;
            b[(int)Button.MINUS] = (value & 0x0020) != 0;
            b[(int)Button.STICK] = (value & 0x0040) != 0;
            b[(int)Button.STICK2] = (value & 0x0080) != 0;
            b[(int)Button.SHOULDER_1] = (value & 0x0100) != 0;
            b[(int)Button.SHOULDER2_1] = (value & 0x0200) != 0;
            b[(int)Button.HOME] = (value & GuideButton) != 0;
            b[(int)Button.B] = (value & 0x1000) != 0;
            b[(int)Button.A] = (value & 0x2000) != 0;
            b[(int)Button.Y] = (value & 0x4000) != 0;
            b[(int)Button.X] = (value & 0x8000) != 0;
            b[(int)Button.SHOULDER_2] = gamepad.LeftTrigger != 0;
            b[(int)Button.SHOULDER2_2] = gamepad.RightTrigger != 0;

            return new ParsedReport {
                Buttons = b,
                LeftTrigger = gamepad.LeftTrigger,
                RightTrigger = gamepad.RightTrigger,
                LeftX = NormalizeAxis(gamepad.LeftThumbX),
                LeftY = NormalizeAxis(gamepad.LeftThumbY),
                RightX = NormalizeAxis(gamepad.RightThumbX),
                RightY = NormalizeAxis(gamepad.RightThumbY)
            };
        }

        private static float NormalizeAxis(short value) {
            return value >= 0 ? value / 32767.0f : value / 32768.0f;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint userIndex, out XInputState nativeState);

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(uint userIndex, out XInputState nativeState);

        [DllImport("xinput1_4.dll", EntryPoint = "#108")]
        private static extern uint XInputGetCapabilitiesEx(uint reserved, uint userIndex,
            uint flags, out XInputCapabilitiesEx capabilities);
    }
}
