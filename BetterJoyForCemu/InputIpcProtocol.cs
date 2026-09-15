using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterJoyForCemu {
    // Encoding format selected by the controller that owns the Bluetooth audio protocol. The
    // desktop helper only captures/resamples/encodes; it never constructs controller reports.
    public enum BluetoothAudioCodec : int {
        DualShock4Sbc = 0,
        DualSenseOpus = 1,
    }

    // One-shot desktop actions exposed by Custom binds. These travel as an int through the
    // existing fixed-size input pipe, while DesktopInputBackend chooses the appropriate
    // implementation (FakerInput keyboard/consumer HID or a Windows app command).
    public enum DesktopInputAction : int {
        MediaPlay = 1,
        MediaPause = 2,
        MediaPlayPause = 3,
        MediaStop = 4,
        MediaNextTrack = 5,
        MediaPreviousTrack = 6,
        VolumeUp = 7,
        VolumeDown = 8,
        VolumeMute = 9,
        CtrlAltDelete = 10,
        CtrlShiftEscape = 11,
        AltTabNext = 12,
        AltShiftTabPrevious = 13,
        AltTabLeft = 14,
        AltTabRight = 15,
    }

    // Message types carried over the named pipe between BetterJoyService (Session 0, no
    // desktop) and the session-launched input helper (has one) - see HeadlessJoyconHost for the
    // service side and InputHelper for the helper side.
    public enum InputMessageType : byte {
        // Helper -> service: raw captured events, forwarded on as-is for Program.OnKeyDown/
        // OnKeyUp/OnMouseButtonDown/OnMouseButtonUp to make the actual reset_mouse/active_gyro
        // bind decisions - the helper itself has no config/decision logic at all.
        KeyDown = 1,
        KeyUp = 2,
        MouseButtonDown = 3,
        MouseButtonUp = 4,

        // Service -> helper: one message per IJoyconHost Simulate* method.
        SimulateKeyClick = 10,
        SimulateKeyHold = 11,
        SimulateKeyRelease = 12,
        SimulateButtonClick = 13,
        SimulateButtonHold = 14,
        SimulateButtonRelease = 15,
        SimulateMoveTo = 16,
        SimulateMoveBy = 17,
        SimulateMoveToScreenCenter = 18,
        SimulateScroll = 19, // A: 1 = scroll up (Forwards), 0 = scroll down (Backwards)
        SimulateCursorMoveBy = 20, // Exact pixel delta; bypasses Windows relative-pointer scaling.
        SimulateWrappedCursorMoveBy = 21, // Exact delta; wraps inside the cursor's current monitor.
        SimulateDesktopAction = 22, // A = DesktopInputAction; one click per chord activation.

        // Service -> helper: start/stop continuous WASAPI loopback capture and controller-selected
        // encoding. A = padId, B = BluetoothAudioCodec. StartAudioCapture carries one extra
        // trailer beyond the fixed header: the capture
        // endpoint ID as a length-prefixed string (BinaryWriter.Write(string)/ReadString()) -
        // empty means "use the system default render device".
        StartAudioCapture = 30,
        StopAudioCapture = 31,

        // Helper -> service: one encoded audio frame. A = padId, B = payload length in bytes;
        // the fixed 9-byte header is immediately followed by B raw bytes (the frame itself,
        // written/read with BinaryWriter.Write(byte[])/ReadBytes - no separate length prefix,
        // since B already carries it).
        AudioFrame = 32,

        // Service -> helper: start/stop the opt-in USB loopback audio router (UsbAudioLoopback.cs)
        // - captures the system's default playback device and renders it into the controller's
        // own USB Audio Class endpoint, entirely inside the helper process (both capture and
        // render need an interactive desktop). Unlike StartAudioCapture this never sends audio
        // data back over the pipe at all: capture and render both happen locally in the helper.
        // A = padId, B = volume percent (0-100). StartUsbAudioLoopback carries three trailing
        // length-prefixed strings (BinaryWriter.Write(string)/ReadString(): source, target, then
        // target's name-hint fallback) - an empty source means "use the system default render
        // device"; an empty target means "use the controller's own endpoint", resolved via the
        // name-hint substring match when the target ID itself doesn't resolve.
        StartUsbAudioLoopback = 33,
        StopUsbAudioLoopback = 34,
    }

    // Fixed-size (9 byte) message: 1 byte type + two 4-byte ints. Simple and robust over a
    // reliable local/cross-session pipe - no need for length-prefixing or variable framing.
    // Unused fields (e.g. B for a single-code message) are just 0.
    public struct InputMessage {
        public InputMessageType Type;
        public int A;
        public int B;

        public void WriteTo(BinaryWriter writer) {
            writer.Write((byte)Type);
            writer.Write(A);
            writer.Write(B);
        }

        public static InputMessage ReadFrom(BinaryReader reader) {
            return new InputMessage {
                Type = (InputMessageType)reader.ReadByte(),
                A = reader.ReadInt32(),
                B = reader.ReadInt32(),
            };
        }
    }

    public static class InputIpc {
        public const string PipeNamePrefix = "BetterJoy2InputHelper_";

        // The pipe crosses session boundaries (service in Session 0, helper/GUI in the
        // interactive session), so the default ACL - which for a SYSTEM-created pipe would not
        // otherwise grant a regular user access - needs to be widened explicitly.
        public static PipeSecurity CreateCrossSessionPipeSecurity() {
            var security = new PipeSecurity();

            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(authenticatedUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            // A custom DACL REPLACES the default one entirely rather than adding to it - without
            // an explicit grant here, the service's own account (SYSTEM) has no listed rights on
            // its own pipe. That doesn't stop the first CreateNamedPipe call (which establishes
            // the object), but every instance after that - the normal case for a long-lived,
            // reconnectable server pipe like this one - fails with UnauthorizedAccessException,
            // since creating an additional instance of an existing named pipe requires rights on
            // the existing object too, not just on connecting to it as a client.
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

            return security;
        }
    }
}
