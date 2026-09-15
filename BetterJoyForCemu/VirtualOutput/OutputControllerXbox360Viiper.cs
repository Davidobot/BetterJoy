using System;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu.VirtualOutput {
    // Alternative to OutputControllerXbox360 (ViGEmBus-backed) using libVIIPER instead - a
    // userspace virtual USB device emulator over the same usbip-win2 virtual USB host controller
    // already bundled for the DualSense Bluetooth microphone bridge (see ViiperMicrophoneEndpoint.cs).
    // Unlike that mic bridge, this needs no separate session-launched helper process: creating a
    // virtual USB device over usbip-win2's kernel driver works fine from Session 0, the same way
    // ViGEmBus's own kernel bus driver already does for the primary Xbox 360 backend.
    //
    // One shared libVIIPER USB server for the whole process (Program.EnsureViiperServer/
    // viiperServerHandle, mirroring emClient) - each instance of this class gets its own bus and
    // Xbox 360 device on that shared server, auto-attached to the local usbip-win2 driver the
    // moment it's created (libVIIPER's own job, not something this class drives step by step).
    public class OutputControllerXbox360Viiper : IOutputControllerXbox360 {
        private UIntPtr busId = UIntPtr.Zero;
        private uint busIdValue;
        private UIntPtr deviceHandle = UIntPtr.Zero;
        private OutputControllerXbox360InputState current_state;

        // Kept alive for the device's lifetime - libVIIPER calls back into this from native code
        // at arbitrary times, so it must never be collected while the device exists.
        private readonly LibViiper.Xbox360RumbleCallback rumbleCallback;

        public event OutputControllerXbox360.Xbox360FeedbackReceivedEventHandler FeedbackReceived;

        public OutputControllerXbox360Viiper() {
            rumbleCallback = OnRumble;
        }

        // No Windows API exposes "which XInput slot did libVIIPER's emulated device land in" the
        // way ViGEmBus's IXbox360Controller.UserIndex does - libVIIPER only knows about the USB
        // wire protocol it emulates, not XInput's own enumeration on top of it. Controller
        // Profiles' "open properties for exactly this instance" therefore can't target a specific
        // VIIPER-backed controller the way it can a ViGEmBus one; -1 matches the same fallback
        // OutputControllerXbox360.UserIndex uses when ViGEmBus itself can't answer.
        public int UserIndex => -1;

        public void Connect() {
            if (deviceHandle != UIntPtr.Zero)
                return;
            if (!Program.EnsureViiperServer())
                return;

            uint newBusId = 0;
            if (!LibViiper.CreateUSBBus(Program.viiperServerHandle, ref newBusId))
                return;
            busIdValue = newBusId;
            busId = (UIntPtr)newBusId;

            if (!LibViiper.CreateXbox360Device(Program.viiperServerHandle, out UIntPtr newDevice,
                    newBusId, autoAttachLocalhost: true, idVendor: 0, idProduct: 0,
                    xinputSubType: 0)) {
                LibViiper.RemoveUSBBus(Program.viiperServerHandle, newBusId);
                busId = UIntPtr.Zero;
                return;
            }
            deviceHandle = newDevice;
            LibViiper.SetXbox360RumbleCallback(deviceHandle, rumbleCallback);

            DoUpdateInput(new OutputControllerXbox360InputState());
        }

        public void Disconnect() {
            if (deviceHandle != UIntPtr.Zero) {
                LibViiper.SetXbox360RumbleCallback(deviceHandle, null);
                LibViiper.RemoveXbox360Device(deviceHandle);
                deviceHandle = UIntPtr.Zero;
            }
            if (busId != UIntPtr.Zero) {
                LibViiper.RemoveUSBBus(Program.viiperServerHandle, busIdValue);
                busId = UIntPtr.Zero;
            }
        }

        public bool UpdateInput(OutputControllerXbox360InputState new_state) {
            if (deviceHandle == UIntPtr.Zero || current_state.IsEqual(new_state))
                return false;

            DoUpdateInput(new_state);
            return true;
        }

        private void DoUpdateInput(OutputControllerXbox360InputState state) {
            uint buttons = 0;
            if (state.dpad_up) buttons |= (uint)LibViiper.Xbox360Buttons.DPadUp;
            if (state.dpad_down) buttons |= (uint)LibViiper.Xbox360Buttons.DPadDown;
            if (state.dpad_left) buttons |= (uint)LibViiper.Xbox360Buttons.DPadLeft;
            if (state.dpad_right) buttons |= (uint)LibViiper.Xbox360Buttons.DPadRight;
            if (state.start) buttons |= (uint)LibViiper.Xbox360Buttons.Start;
            if (state.back) buttons |= (uint)LibViiper.Xbox360Buttons.Back;
            if (state.thumb_stick_left) buttons |= (uint)LibViiper.Xbox360Buttons.LThumb;
            if (state.thumb_stick_right) buttons |= (uint)LibViiper.Xbox360Buttons.RThumb;
            if (state.shoulder_left) buttons |= (uint)LibViiper.Xbox360Buttons.LShoulder;
            if (state.shoulder_right) buttons |= (uint)LibViiper.Xbox360Buttons.RShoulder;
            if (state.guide) buttons |= (uint)LibViiper.Xbox360Buttons.Guide;
            if (state.a) buttons |= (uint)LibViiper.Xbox360Buttons.A;
            if (state.b) buttons |= (uint)LibViiper.Xbox360Buttons.B;
            if (state.x) buttons |= (uint)LibViiper.Xbox360Buttons.X;
            if (state.y) buttons |= (uint)LibViiper.Xbox360Buttons.Y;

            var deviceState = new LibViiper.Xbox360DeviceState {
                Buttons = buttons,
                LT = state.trigger_left,
                RT = state.trigger_right,
                LX = state.axis_left_x,
                LY = state.axis_left_y,
                RX = state.axis_right_x,
                RY = state.axis_right_y,
            };
            LibViiper.SetXbox360DeviceState(deviceHandle, deviceState);
            current_state = state;
        }

        private void OnRumble(UIntPtr handle, byte leftMotor, byte rightMotor) {
            // Same (largeMotor, smallMotor, ledNumber) shape ViGEmBus's own feedback carries -
            // see IOutputControllerXbox360's own comment for why this reuses that type instead of
            // a parallel one. leftMotor is the low-frequency/large motor on a real Xbox 360 pad,
            // matching ViGEmBus's own left/large convention.
            FeedbackReceived?.Invoke(new Xbox360FeedbackReceivedEventArgs(leftMotor, rightMotor, 0));
        }
    }
}
