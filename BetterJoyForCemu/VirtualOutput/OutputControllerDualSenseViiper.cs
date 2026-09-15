using System;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace BetterJoyForCemu.VirtualOutput {
    // A genuinely new virtual output type, not an alternative backend for an existing one -
    // ViGEmBus (this project's only other virtual-controller driver) predates DualSense and has
    // no DualSense target at all, only DualShock4. VIIPER's dualsense device package is the only
    // way this project can present a real PS5-shaped virtual controller.
    //
    // Deliberately reuses OutputControllerDualShock4InputState/MapToDualShock4Input as its input
    // shape rather than inventing a parallel one - DualSense's button/stick/trigger surface is a
    // superset of DualShock4's (Create instead of Share, otherwise identical for anything this
    // project currently feeds), and every controller type's existing DS4 mapping call site
    // already produces exactly this struct. No touch/gyro/accel passthrough yet - the existing
    // DS4 output path doesn't feed those either, so this stays at the same feature parity rather
    // than introducing new per-controller-type mapping work unprompted.
    public class OutputControllerDualSenseViiper {
        private UIntPtr busId = UIntPtr.Zero;
        private uint busIdValue;
        private UIntPtr deviceHandle = UIntPtr.Zero;
        private OutputControllerDualShock4InputState current_state;
        private bool hasCurrentState;

        // Kept alive for the device's lifetime - see OutputControllerXbox360Viiper's own comment
        // on why (libVIIPER calls back into this from native code at arbitrary times).
        private readonly LibViiper.DsOutputCallback outputCallback;

        // Reuses OutputControllerDualShock4's own delegate type rather than declaring a parallel
        // one - same (DualShock4FeedbackReceivedEventArgs e) shape, so Controller.Ds4_FeedbackReceived
        // subscribes to either backend interchangeably with no new handler method needed.
        public event OutputControllerDualShock4.DualShock4FeedbackReceivedEventHandler FeedbackReceived;

        public OutputControllerDualSenseViiper() {
            outputCallback = OnOutput;
        }

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

            if (!LibViiper.CreateDualSenseDevice(Program.viiperServerHandle, out UIntPtr newDevice,
                    newBusId, autoAttachLocalhost: true, idVendor: 0, idProduct: 0,
                    meta: IntPtr.Zero)) {
                LibViiper.RemoveUSBBus(Program.viiperServerHandle, newBusId);
                busId = UIntPtr.Zero;
                return;
            }
            deviceHandle = newDevice;
            LibViiper.SetDualSenseOutputCallback(deviceHandle, outputCallback);

            hasCurrentState = false;
            DoUpdateInput(new OutputControllerDualShock4InputState());
        }

        public void Disconnect() {
            if (deviceHandle != UIntPtr.Zero) {
                LibViiper.SetDualSenseOutputCallback(deviceHandle, null);
                LibViiper.RemoveDualSenseDevice(deviceHandle);
                deviceHandle = UIntPtr.Zero;
            }
            if (busId != UIntPtr.Zero) {
                LibViiper.RemoveUSBBus(Program.viiperServerHandle, busIdValue);
                busId = UIntPtr.Zero;
            }
        }

        public bool UpdateInput(OutputControllerDualShock4InputState new_state) {
            if (deviceHandle == UIntPtr.Zero ||
                    (hasCurrentState && current_state.IsEqual(new_state)))
                return false;

            DoUpdateInput(new_state);
            return true;
        }

        private void DoUpdateInput(OutputControllerDualShock4InputState state) {
            uint buttons = 0;
            if (state.square) buttons |= (uint)LibViiper.DsButtons.Square;
            if (state.cross) buttons |= (uint)LibViiper.DsButtons.Cross;
            if (state.circle) buttons |= (uint)LibViiper.DsButtons.Circle;
            if (state.triangle) buttons |= (uint)LibViiper.DsButtons.Triangle;
            if (state.shoulder_left) buttons |= (uint)LibViiper.DsButtons.L1;
            if (state.shoulder_right) buttons |= (uint)LibViiper.DsButtons.R1;
            if (state.trigger_left) buttons |= (uint)LibViiper.DsButtons.L2;
            if (state.trigger_right) buttons |= (uint)LibViiper.DsButtons.R2;
            // DualShock4's Share maps to DualSense's Create - the two controllers' equivalent of
            // the same physical position, everything else lines up one-to-one already.
            if (state.share) buttons |= (uint)LibViiper.DsButtons.Create;
            if (state.options) buttons |= (uint)LibViiper.DsButtons.Options;
            if (state.thumb_left) buttons |= (uint)LibViiper.DsButtons.L3;
            if (state.thumb_right) buttons |= (uint)LibViiper.DsButtons.R3;
            if (state.ps) buttons |= (uint)LibViiper.DsButtons.Ps;
            if (state.touchpad) buttons |= (uint)LibViiper.DsButtons.Touchpad;

            byte dpad = 0;
            switch (state.dPad) {
                case DpadDirection.North: dpad = (byte)LibViiper.DsDpad.Up; break;
                case DpadDirection.Northeast:
                    dpad = (byte)(LibViiper.DsDpad.Up | LibViiper.DsDpad.Right); break;
                case DpadDirection.East: dpad = (byte)LibViiper.DsDpad.Right; break;
                case DpadDirection.Southeast:
                    dpad = (byte)(LibViiper.DsDpad.Down | LibViiper.DsDpad.Right); break;
                case DpadDirection.South: dpad = (byte)LibViiper.DsDpad.Down; break;
                case DpadDirection.Southwest:
                    dpad = (byte)(LibViiper.DsDpad.Down | LibViiper.DsDpad.Left); break;
                case DpadDirection.West: dpad = (byte)LibViiper.DsDpad.Left; break;
                case DpadDirection.Northwest:
                    dpad = (byte)(LibViiper.DsDpad.Up | LibViiper.DsDpad.Left); break;
            }

            // DSDeviceState's sticks are signed, centered at 0 (-128..127); the shared input
            // state's are unsigned, centered at 128 (0..255), matching ViGEm's DualShock4 axis
            // convention - same range, different zero point.
            var deviceState = new LibViiper.DsDeviceState {
                LX = (sbyte)(state.thumb_left_x - 128),
                LY = (sbyte)(state.thumb_left_y - 128),
                RX = (sbyte)(state.thumb_right_x - 128),
                RY = (sbyte)(state.thumb_right_y - 128),
                Buttons = buttons,
                DPad = dpad,
                L2 = state.trigger_left_value,
                R2 = state.trigger_right_value,
            };
            LibViiper.SetDualSenseDeviceState(deviceHandle, deviceState);
            current_state = state;
            hasCurrentState = true;
        }

        private void OnOutput(UIntPtr handle, byte rumbleSmall, byte rumbleLarge, byte ledRed,
                byte ledGreen, byte ledBlue, byte playerLeds) {
            // Same (largeMotor, smallMotor, ...) shape ViGEmBus's own DualShock4 feedback carries -
            // reusing that existing event args type rather than inventing a parallel one, same
            // reasoning as OutputControllerXbox360Viiper's rumble callback.
            FeedbackReceived?.Invoke(new DualShock4FeedbackReceivedEventArgs(
                rumbleLarge, rumbleSmall, new LightbarColor(ledRed, ledGreen, ledBlue)));
        }
    }
}
