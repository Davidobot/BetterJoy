using Microsoft.Win32.SafeHandles;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BetterJoyForCemu {
    // Desktop input shared by the normal GUI host and the interactive-session helper used by
    // service mode. FakerInput is deliberately optional: when its virtual HID device is not
    // installed, cannot be opened, or is disabled in config, every operation falls back to the
    // WindowsInput/Cursor.Position behavior BetterJoy used before.
    internal sealed class DesktopInputBackend : IDisposable {
        private readonly object sync = new object();
        private readonly bool fakerInputEnabled;
        private readonly bool allowDesktopFallback;
        private FakerInputClient fakerInput;
        private bool fakerInputAttempted;
        private DateTime fakerInputRetryAfterUtc;
        private bool fakerInputUsed;
        private byte heldMouseButtons;
        private bool hasVirtualCursor;
        private Point virtualCursor;
        private Point lastObservedCursor;

        public DesktopInputBackend(bool allowDesktopFallback = true) {
            this.allowDesktopFallback = allowDesktopFallback;
            string configured = ConfigurationManager.AppSettings["UseFakerInput"];
            bool enabled = true;
            if (!String.IsNullOrWhiteSpace(configured) && !Boolean.TryParse(configured, out enabled))
                enabled = true;

            fakerInputEnabled = enabled;
        }

        // Single dispatch table for an InputMessage against a backend instance - shared by
        // InputHelper (interactive-session helper process) and HeadlessJoyconHost (service's
        // own pre-login/lock-screen fallback), which previously kept two independently
        // hand-copied switches over the same InputMessageType enum. The default case makes a
        // future enum addition that only one of those switches used to handle silently no-op
        // loud instead - Debug.Fail rather than throw, so a Release build still executes
        // (harmlessly dropping the unhandled message) instead of taking down a poll/pipe thread.
        public static void Execute(InputMessage msg, DesktopInputBackend backend) {
            switch (msg.Type) {
                case InputMessageType.SimulateKeyClick: backend.KeyClick(msg.A); break;
                case InputMessageType.SimulateKeyHold: backend.KeyHold(msg.A); break;
                case InputMessageType.SimulateKeyRelease: backend.KeyRelease(msg.A); break;
                case InputMessageType.SimulateDesktopAction:
                    backend.ActionClick((DesktopInputAction)msg.A); break;
                case InputMessageType.SimulateButtonClick: backend.ButtonClick(msg.A); break;
                case InputMessageType.SimulateButtonHold: backend.ButtonHold(msg.A); break;
                case InputMessageType.SimulateButtonRelease: backend.ButtonRelease(msg.A); break;
                case InputMessageType.SimulateMoveTo: backend.MoveTo(msg.A, msg.B); break;
                case InputMessageType.SimulateMoveBy: backend.MoveBy(msg.A, msg.B); break;
                case InputMessageType.SimulateCursorMoveBy: backend.CursorMoveBy(msg.A, msg.B); break;
                case InputMessageType.SimulateWrappedCursorMoveBy: backend.WrappedCursorMoveBy(msg.A, msg.B); break;
                case InputMessageType.SimulateMoveToScreenCenter: backend.MoveToScreenCenter(); break;
                case InputMessageType.SimulateScroll: backend.Scroll(msg.A != 0); break;
                default:
                    Debug.Fail("Unhandled InputMessageType in DesktopInputBackend.Execute: " + msg.Type);
                    break;
            }
        }

        public void KeyClick(int keyCode) {
            if (allowDesktopFallback)
                WindowsInput.Simulate.Events().Click((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void KeyHold(int keyCode) {
            if (allowDesktopFallback)
                WindowsInput.Simulate.Events().Hold((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void KeyRelease(int keyCode) {
            if (allowDesktopFallback)
                WindowsInput.Simulate.Events().Release((WindowsInput.Events.KeyCode)keyCode).Invoke();
        }

        public void ActionClick(DesktopInputAction action) {
            lock (sync) {
                switch (action) {
                    case DesktopInputAction.MediaPlay: SendAppCommand(46); return;
                    case DesktopInputAction.MediaPause: SendAppCommand(47); return;
                    case DesktopInputAction.MediaPlayPause: ClickConsumerKey(0x08, 0xB3); return;
                    case DesktopInputAction.MediaStop: ClickConsumerKey(0x04, 0xB2); return;
                    case DesktopInputAction.MediaNextTrack: ClickConsumerKey(0x01, 0xB0); return;
                    case DesktopInputAction.MediaPreviousTrack: ClickConsumerKey(0x02, 0xB1); return;
                    case DesktopInputAction.VolumeMute: ClickConsumerKey(0x10, 0xAD); return;
                    case DesktopInputAction.VolumeDown: ClickConsumerKey(0x20, 0xAE); return;
                    case DesktopInputAction.VolumeUp: ClickConsumerKey(0x40, 0xAF); return;
                    case DesktopInputAction.CtrlAltDelete:
                        if (!TryClickFakerKeyboard(0x05, 0x4C))
                            Debug.WriteLine("Ctrl+Alt+Delete requires the FakerInput driver.");
                        return;
                    case DesktopInputAction.CtrlShiftEscape:
                        ClickKeyboardChord(0x03, new byte[] { 0x29 },
                            new int[] { 0x11, 0x10 }, new int[] { 0x1B });
                        return;
                    case DesktopInputAction.AltTabNext:
                        ClickKeyboardChord(0x04, new byte[] { 0x2B },
                            new int[] { 0x12 }, new int[] { 0x09 });
                        return;
                    case DesktopInputAction.AltShiftTabPrevious:
                        ClickKeyboardChord(0x06, new byte[] { 0x2B },
                            new int[] { 0x12, 0x10 }, new int[] { 0x09 });
                        return;
                    case DesktopInputAction.AltTabLeft:
                        ClickKeyboardSequence(0x04, new byte[] { 0x2B, 0x50 },
                            new int[] { 0x12 }, new int[] { 0x09, 0x25 });
                        return;
                    case DesktopInputAction.AltTabRight:
                        ClickKeyboardSequence(0x04, new byte[] { 0x2B, 0x4F },
                            new int[] { 0x12 }, new int[] { 0x09, 0x27 });
                        return;
                }
            }
        }

        private void ClickConsumerKey(byte mask, int fallbackVirtualKey) {
            if (!TryClickFakerMultimedia(mask) && allowDesktopFallback)
                WindowsInput.Simulate.Events().Click(
                    (WindowsInput.Events.KeyCode)fallbackVirtualKey).Invoke();
        }

        private void ClickKeyboardChord(byte modifiers, byte[] hidKeys,
                int[] fallbackModifiers, int[] fallbackKeys) {
            if (TryClickFakerKeyboard(modifiers, hidKeys))
                return;
            ClickFallbackChord(fallbackModifiers, fallbackKeys);
        }

        private void ClickKeyboardSequence(byte modifiers, byte[] hidKeys,
                int[] fallbackModifiers, int[] fallbackKeys) {
            if (TryClickFakerKeyboardSequence(modifiers, hidKeys))
                return;
            ClickFallbackChord(fallbackModifiers, fallbackKeys);
        }

        private void ClickFallbackChord(int[] modifiers, int[] keys) {
            if (!allowDesktopFallback)
                return;
            try {
                foreach (int key in modifiers)
                    WindowsInput.Simulate.Events().Hold((WindowsInput.Events.KeyCode)key).Invoke();
                foreach (int key in keys)
                    WindowsInput.Simulate.Events().Click((WindowsInput.Events.KeyCode)key).Invoke();
            } finally {
                for (int i = modifiers.Length - 1; i >= 0; i--)
                    WindowsInput.Simulate.Events().Release(
                        (WindowsInput.Events.KeyCode)modifiers[i]).Invoke();
            }
        }

        private bool TryClickFakerKeyboard(byte modifiers, params byte[] keys) {
            if (!EnsureFakerInput() || !fakerInput.TrySendKeyboard(modifiers, keys)) {
                if (fakerInput != null)
                    DisableFakerInput();
                return false;
            }
            fakerInputUsed = true;
            if (!fakerInput.TrySendKeyboard(0))
                DisableFakerInput();
            return true;
        }

        private bool TryClickFakerKeyboardSequence(byte modifiers, params byte[] keys) {
            if (!EnsureFakerInput())
                return false;
            bool sentAny = false;
            foreach (byte key in keys) {
                if (!fakerInput.TrySendKeyboard(modifiers, key)) {
                    DisableFakerInput();
                    return sentAny;
                }
                sentAny = true;
                fakerInputUsed = true;
                if (!fakerInput.TrySendKeyboard(modifiers)) {
                    DisableFakerInput();
                    return true;
                }
            }
            if (!fakerInput.TrySendKeyboard(0))
                DisableFakerInput();
            return true;
        }

        private bool TryClickFakerMultimedia(byte mask) {
            if (!EnsureFakerInput() || !fakerInput.TrySendMultimedia(mask, 0, 0)) {
                if (fakerInput != null)
                    DisableFakerInput();
                return false;
            }
            fakerInputUsed = true;
            if (!fakerInput.TrySendMultimedia(0, 0, 0))
                DisableFakerInput();
            return true;
        }

        private bool SendAppCommand(int command) {
            if (!allowDesktopFallback)
                return false;
            IntPtr target = GetForegroundWindow();
            if (target == IntPtr.Zero)
                return false;
            SendMessage(target, 0x0319, target, new IntPtr(command << 16));
            return true;
        }

        public void ButtonClick(int buttonCode) {
            lock (sync) {
                byte button = ButtonMask(buttonCode);
                if (button != 0 && EnsureFakerInput()) {
                    byte original = heldMouseButtons;
                    if (fakerInput.TrySendRelative((byte)(original | button), 0, 0, 0, 0)) {
                        fakerInputUsed = true;
                        heldMouseButtons = original;
                        if (fakerInput.TrySendRelative(original, 0, 0, 0, 0)) {
                            fakerInputUsed = true;
                            return;
                        }

                        // The down report reached the virtual mouse but the up report did not.
                        // A SendInput release is the best cross-device cleanup still available;
                        // do not emit another click and turn one user action into two.
                        DisableFakerInput();
                        if (allowDesktopFallback)
                            WindowsInput.Simulate.Events().Release((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
                        return;
                    }

                    DisableFakerInput();
                }

                if (allowDesktopFallback)
                    WindowsInput.Simulate.Events().Click((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
            }
        }

        public void ButtonHold(int buttonCode) {
            lock (sync) {
                byte button = ButtonMask(buttonCode);
                if (button != 0 && EnsureFakerInput()) {
                    heldMouseButtons |= button;
                    if (fakerInput.TrySendRelative(heldMouseButtons, 0, 0, 0, 0)) {
                        fakerInputUsed = true;
                        return;
                    }

                    DisableFakerInput();
                }

                if (allowDesktopFallback)
                    WindowsInput.Simulate.Events().Hold((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
            }
        }

        public void ButtonRelease(int buttonCode) {
            lock (sync) {
                byte button = ButtonMask(buttonCode);
                if (button != 0 && EnsureFakerInput()) {
                    heldMouseButtons &= (byte)~button;
                    if (fakerInput.TrySendRelative(heldMouseButtons, 0, 0, 0, 0)) {
                        fakerInputUsed = true;
                        return;
                    }

                    DisableFakerInput();
                }

                if (allowDesktopFallback)
                    WindowsInput.Simulate.Events().Release((WindowsInput.Events.ButtonCode)buttonCode).Invoke();
            }
        }

        public void MoveTo(int x, int y) {
            lock (sync) {
                if (!allowDesktopFallback)
                    return; // service mode has no trustworthy pixel-space desktop geometry

                Point target = ClampToVirtualScreen(new Point(x, y));
                if (TryMoveAbsolute(target))
                    return;

                WindowsInput.Simulate.Events().MoveTo(x, y).Invoke();
            }
        }

        public void MoveBy(int dx, int dy) {
            lock (sync) {
                if (EnsureFakerInput()) {
                    if (fakerInput.TrySendRelative(
                        heldMouseButtons, ClampToShort(dx), ClampToShort(dy), 0, 0)) {
                        fakerInputUsed = true;
                        return;
                    }

                    DisableFakerInput();
                }
                if (allowDesktopFallback)
                    WindowsInput.Simulate.Events().MoveBy(dx, dy).Invoke();
            }
        }

        // Exact cursor mode uses FakerInput's absolute HID mouse rather than relative HID input.
        // That preserves BetterJoy's pixel-space gyro integration instead of handing the delta
        // back to Windows pointer acceleration. A cached position also lets movement continue on
        // the UAC secure desktop when Cursor.Position still reports the last normal-desktop point.
        public void CursorMoveBy(int dx, int dy) {
            CursorMoveBy(dx, dy, false);
        }

        public void WrappedCursorMoveBy(int dx, int dy) {
            CursorMoveBy(dx, dy, true);
        }

        private void CursorMoveBy(int dx, int dy, bool wrapAround) {
            // A LocalSystem service cannot observe the Winlogon desktop's Cursor.Position. HID
            // relative motion needs no desktop coordinates and is routed by Windows to whichever
            // input desktop is active, so it is the safe pre-login equivalent of exact mode. The
            // interactive helper resumes wrapping after login, when monitor geometry is knowable.
            if (!allowDesktopFallback) {
                MoveBy(dx, dy);
                return;
            }

            lock (sync) {
                Point observed = ReadCursorPosition();
                Point current = observed;
                if (fakerInput != null && hasVirtualCursor && observed == lastObservedCursor)
                    current = virtualCursor;

                lastObservedCursor = observed;
                Point target = wrapAround
                    ? WrapToCurrentScreen(current, dx, dy)
                    : ClampToVirtualScreen(new Point(current.X + dx, current.Y + dy));
                if (TryMoveAbsolute(target))
                    return;

                Cursor.Position = wrapAround ? target : new Point(observed.X + dx, observed.Y + dy);
            }
        }

        public void MoveToScreenCenter() {
            if (!allowDesktopFallback) {
                lock (sync) {
                    TryMoveAbsoluteNormalized(16384, 16384);
                }
                return;
            }

            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            MoveTo(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        public void Scroll(bool up) {
            lock (sync) {
                if (EnsureFakerInput()) {
                    if (fakerInput.TrySendRelative(
                        heldMouseButtons, 0, 0, up ? (sbyte)1 : (sbyte)-1, 0)) {
                        fakerInputUsed = true;
                        return;
                    }

                    DisableFakerInput();
                }
                if (allowDesktopFallback) {
                    WindowsInput.Simulate.Events().Scroll(
                        WindowsInput.Events.ButtonCode.VScroll,
                        up ? WindowsInput.Events.ButtonScrollDirection.Forwards : WindowsInput.Events.ButtonScrollDirection.Backwards).Invoke();
                }
            }
        }

        private bool TryMoveAbsolute(Point target) {
            if (!EnsureFakerInput())
                return false;

            Rectangle bounds = SystemInformation.VirtualScreen;
            ushort normalizedX = NormalizeCoordinate(target.X, bounds.Left, bounds.Width);
            ushort normalizedY = NormalizeCoordinate(target.Y, bounds.Top, bounds.Height);
            return TryMoveAbsoluteNormalized(normalizedX, normalizedY, target);
        }

        private bool TryMoveAbsoluteNormalized(ushort normalizedX, ushort normalizedY) {
            return TryMoveAbsoluteNormalized(normalizedX, normalizedY, Point.Empty);
        }

        private bool TryMoveAbsoluteNormalized(ushort normalizedX, ushort normalizedY, Point target) {
            if (!EnsureFakerInput())
                return false;

            if (!fakerInput.TrySendAbsolute(normalizedX, normalizedY)) {
                DisableFakerInput();
                return false;
            }

            fakerInputUsed = true;
            if (allowDesktopFallback) {
                virtualCursor = target;
                hasVirtualCursor = true;
            }
            return true;
        }

        // Used during the service/helper handoff. connectIfNeeded is false when the service is
        // surrendering a backend it may never have used; true after a helper disconnect, where a
        // neutral physical-HID report must clear any state the departed helper could have left.
        public void ReleaseVirtualMouseState(bool connectIfNeeded) {
            lock (sync) {
                bool available = fakerInput != null || (connectIfNeeded && EnsureFakerInput(true));
                if (available && fakerInput.TrySendRelative(0, 0, 0, 0, 0))
                    fakerInputUsed = true;

                heldMouseButtons = 0;
                hasVirtualCursor = false;
            }
        }

        private bool EnsureFakerInput(bool forceRetry = false) {
            if (fakerInput != null)
                return true;
            if (!fakerInputEnabled)
                return false;
            if (fakerInputAttempted) {
                // A service can start while the UMDF device stack is still settling during boot.
                // Desktop helpers retain their one-shot behavior; the service retries at a low
                // rate so cold-login support does not depend on a lucky startup ordering.
                if (!forceRetry && (allowDesktopFallback || DateTime.UtcNow < fakerInputRetryAfterUtc))
                    return false;
                fakerInputAttempted = false;
            }

            fakerInputAttempted = true;
            try {
                fakerInput = FakerInputClient.TryOpen();
            } catch {
                // Optional means optional: an unexpected HID/SetupAPI failure must never
                // prevent BetterJoy itself (or the service's input helper) from running.
                fakerInput = null;
            }

            if (fakerInput == null)
                fakerInputRetryAfterUtc = DateTime.UtcNow.AddSeconds(5);

            return fakerInput != null;
        }

        private void DisableFakerInput() {
            if (fakerInput == null)
                return;

            // Best effort neutral report before falling back. TryWrite already closes the handle
            // on a transport failure, but this still releases held buttons for callers that are
            // explicitly disposing a healthy backend.
            if (fakerInputUsed)
                fakerInput.TryReleaseAll();
            fakerInput.Dispose();
            fakerInput = null;
            fakerInputUsed = false;
            heldMouseButtons = 0;
            hasVirtualCursor = false;
        }

        public void Dispose() {
            lock (sync) {
                DisableFakerInput();
            }
        }

        private static byte ButtonMask(int buttonCode) {
            return buttonCode >= (int)WindowsInput.Events.ButtonCode.Left &&
                   buttonCode <= (int)WindowsInput.Events.ButtonCode.XButton2
                ? (byte)(1 << (buttonCode - 1))
                : (byte)0;
        }

        private static short ClampToShort(int value) {
            return (short)Math.Max(-32767, Math.Min(32767, value));
        }

        private Point ReadCursorPosition() {
            try {
                return Cursor.Position;
            } catch {
                return hasVirtualCursor ? virtualCursor : Point.Empty;
            }
        }

        private static Point ClampToVirtualScreen(Point point) {
            Rectangle bounds = SystemInformation.VirtualScreen;
            return new Point(
                Math.Max(bounds.Left, Math.Min(bounds.Right - 1, point.X)),
                Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, point.Y)));
        }

        private static Point WrapToCurrentScreen(Point current, int dx, int dy) {
            Rectangle bounds = Screen.FromPoint(current).Bounds;
            return new Point(
                WrapCoordinate((long)current.X + dx, bounds.Left, bounds.Width),
                WrapCoordinate((long)current.Y + dy, bounds.Top, bounds.Height));
        }

        private static int WrapCoordinate(long value, int minimum, int length) {
            if (length <= 1)
                return minimum;

            long offset = (value - minimum) % length;
            if (offset < 0)
                offset += length;
            return minimum + (int)offset;
        }

        private static ushort NormalizeCoordinate(int value, int minimum, int length) {
            if (length <= 1)
                return 0;

            double normalized = (value - minimum) / (double)(length - 1);
            return (ushort)Math.Round(Math.Max(0.0, Math.Min(1.0, normalized)) * 32767.0);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    }

    // Minimal native client for FakerInput's vendor control collection. This intentionally uses
    // only Windows HID/SetupAPI calls and the MIT-licensed wire protocol from FakerInput v0.1.1;
    // it avoids redistributing the GPL application wrapper used by other controller mappers.
    internal sealed class FakerInputClient : IDisposable {
        private const ushort VendorId = 0xFE0F;
        private const ushort ProductId = 0x00FF;
        private const ushort ControlUsagePage = 0xFF00;
        private const ushort ControlUsage = 0x0001;
        private const byte ControlReportId = 0x40;
        private const int ControlReportLength = 65;

        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int HidpStatusSuccess = 0x00110000;

        private SafeFileHandle handle;

        private FakerInputClient(SafeFileHandle handle) {
            this.handle = handle;
        }

        public static FakerInputClient TryOpen() {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (deviceInfoSet == new IntPtr(-1))
                return null;

            try {
                for (uint index = 0; ; index++) {
                    var interfaceData = new SpDeviceInterfaceData();
                    interfaceData.Size = Marshal.SizeOf(interfaceData);
                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                        break;

                    uint requiredSize = 0;
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                    if (requiredSize == 0)
                        continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet, ref interfaceData, detail, requiredSize, out requiredSize, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        SafeFileHandle candidate = CreateFile(
                            path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                        if (candidate.IsInvalid) {
                            candidate.Dispose();
                            continue;
                        }

                        if (!IsControlCollection(candidate)) {
                            candidate.Dispose();
                            continue;
                        }

                        return new FakerInputClient(candidate);
                    } finally {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            } finally {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return null;
        }

        public bool TrySendRelative(byte buttons, short x, short y, sbyte wheel, sbyte horizontalWheel) {
            byte[] report = new byte[8];
            report[0] = 0x03;
            report[1] = buttons;
            WriteInt16(report, 2, x);
            WriteInt16(report, 4, y);
            report[6] = unchecked((byte)wheel);
            report[7] = unchecked((byte)horizontalWheel);
            return TryWriteControlReport(report);
        }

        public bool TrySendKeyboard(byte modifiers, params byte[] keys) {
            byte[] report = new byte[9];
            report[0] = 0x01;
            report[1] = modifiers;
            int count = Math.Min(keys == null ? 0 : keys.Length, 6);
            for (int i = 0; i < count; i++)
                report[3 + i] = keys[i];
            return TryWriteControlReport(report);
        }

        public bool TrySendMultimedia(byte keys0, byte keys1, byte keys2) {
            return TryWriteControlReport(new byte[] { 0x02, keys0, keys1, keys2 });
        }

        public void TryReleaseAll() {
            TrySendKeyboard(0);
            TrySendMultimedia(0, 0, 0);
            TrySendRelative(0, 0, 0, 0, 0);
        }

        public bool TrySendAbsolute(ushort x, ushort y) {
            byte[] report = new byte[7];
            report[0] = 0x04;
            report[1] = 0;
            WriteUInt16(report, 2, x);
            WriteUInt16(report, 4, y);
            report[6] = 0;
            return TryWriteControlReport(report);
        }

        private bool TryWriteControlReport(byte[] innerReport) {
            if (handle == null || handle.IsInvalid || handle.IsClosed)
                return false;

            byte[] controlReport = new byte[ControlReportLength];
            controlReport[0] = ControlReportId;
            controlReport[1] = (byte)innerReport.Length;
            Buffer.BlockCopy(innerReport, 0, controlReport, 2, innerReport.Length);

            uint written;
            if (WriteFile(handle, controlReport, (uint)controlReport.Length, out written, IntPtr.Zero) &&
                written == controlReport.Length)
                return true;

            handle.Dispose();
            return false;
        }

        private static bool IsControlCollection(SafeFileHandle candidate) {
            var attributes = new HiddAttributes();
            attributes.Size = Marshal.SizeOf(attributes);
            if (!HidD_GetAttributes(candidate, ref attributes) ||
                attributes.VendorId != VendorId || attributes.ProductId != ProductId)
                return false;

            IntPtr preparsedData;
            if (!HidD_GetPreparsedData(candidate, out preparsedData))
                return false;

            try {
                var caps = new HidpCaps { Reserved = new ushort[17] };
                return HidP_GetCaps(preparsedData, ref caps) == HidpStatusSuccess &&
                    caps.UsagePage == ControlUsagePage && caps.Usage == ControlUsage &&
                    caps.OutputReportByteLength == ControlReportLength;
            } finally {
                HidD_FreePreparsedData(preparsedData);
            }
        }

        private static void WriteInt16(byte[] buffer, int offset, short value) {
            buffer[offset] = unchecked((byte)value);
            buffer[offset + 1] = unchecked((byte)(value >> 8));
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value) {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        public void Dispose() {
            if (handle == null)
                return;
            handle.Dispose();
            handle = null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
            out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle file, byte[] buffer, uint numberOfBytesToWrite,
            out uint numberOfBytesWritten, IntPtr overlapped);
    }
}
