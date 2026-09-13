namespace BetterJoyForCemu {
    // Abstracts everything Program.cs/Joycon.cs/UdpServer.cs need from a "host" - a real MainForm
    // in GUI mode, or a no-UI headless host when running as a Windows Service (Session 0 has no
    // desktop, so there's nothing for a tray icon/controller slots to exist on). Implementations
    // own whatever thread-marshaling their own state needs internally (e.g. MainForm.Invoke), so
    // callers never need to know whether they're talking to a real Control or not.
    public interface IJoyconHost {
        void AppendTextBox(string message);

        // UI-only - real work in GUI mode; safe no-ops headless, since there's no controller
        // slot/tray icon to update without a desktop. Controller-typed where any device kind is
        // meaningful; left JoyconController-typed where the operation is inherently Joy-Con
        // pairing/orientation-specific (no other device type pairs two physical units into one
        // logical controller - see Controller.other's comment) - callers already narrow via
        // "is JoyconController"/SupportsPairing before reaching these (see
        // MainForm.ExecuteJoinOrSplit), so a non-pairing controller (e.g. DualSenseController)
        // simply never reaches them, no crash.
        void AssignSlot(Controller controller);
        void CollapseJoinedPair(JoyconController left, JoyconController right);
        void HandleJoyconDropped(Controller dropped, JoyconController survivingPartner);
        // forceSelfPair: skip searching for an opposite-handed partner and self-pair (vertical
        // orientation) even when other Joycons are connected - the double right-click override,
        // see MainForm.HandlePossibleOrientationDoubleClick. Ignored on the split side (a Joycon
        // that already has a partner just splits either way, there's no ambiguity to override).
        void JoinOrSplitJoycon(JoyconController joycon, bool forceSelfPair = false);
        void NotifyLowBattery(Controller controller);

        // Keeps a solo-vs-self-paired ("vertical") slot icon in sync with Controller.other, for
        // orientation changes that don't go through JoinOrSplitJoycon itself (e.g. Program.cs's
        // DefaultOrientation auto-self-pair on connect). Safe no-op headless, same as the rest of
        // this UI-only group.
        void RefreshOrientationIcon(JoyconController joycon);
        void UpdateBatteryColor(Controller controller);
        void RefreshControllerState();

        // Called from the controller's own Poll thread (see Joycon.DoThingsWithButtons) when a
        // face button is pressed while CalibrationState.PendingConfirmController names this exact
        // controller - lets the user confirm a calibration Start/Done prompt from the controller
        // itself instead of reaching for the mouse. A no-op whenever nothing is actually pending.
        // Controller-typed since stick calibration (unlike gyro) already applies generically -
        // see Controller.CenterSticks's comment on the three init strategies feeding it.
        void HandleCalibrationConfirm(Controller controller);

        // Keyboard/mouse injection for remap (SL/SR/Capture -> key/mouse bind) and gyro-mouse.
        // Codes are WindowsInput.Events.KeyCode/ButtonCode cast to int, kept untyped here so this
        // interface doesn't need a WindowsInput dependency. Needs an interactive desktop to
        // actually do anything - GUI mode calls WindowsInput.Simulate directly (same as always);
        // headless/service mode forwards the request over a pipe to a session-launched helper
        // process that can (see HeadlessJoyconHost/SessionLauncher), since Session 0 has none.
        void SimulateKeyClick(int keyCode);
        void SimulateKeyHold(int keyCode);
        void SimulateKeyRelease(int keyCode);
        void SimulateDesktopAction(int actionCode);
        void SimulateButtonClick(int buttonCode);
        void SimulateButtonHold(int buttonCode);
        void SimulateButtonRelease(int buttonCode);
        void SimulateMoveTo(int x, int y);
        void SimulateMoveBy(int dx, int dy);
        void SimulateCursorMoveBy(int dx, int dy);
        void SimulateWrappedCursorMoveBy(int dx, int dy);

        // Deliberately not "SimulateMoveTo(screenWidth/2, screenHeight/2)" computed by the
        // caller - Screen.PrimaryScreen is only meaningful wherever the actual desktop is, which
        // for a Windows Service is the session-launched helper, not the service process itself.
        // Each implementation resolves "center" in its own context.
        void SimulateMoveToScreenCenter();

        // One scroll wheel tick - up (true) or down (false). Left/right/middle click reuse the
        // existing SimulateButtonHold/Release above (ButtonCode.Left/Right/Middle); scroll has no
        // hold/release equivalent, just a discrete tick per press.
        void SimulateScroll(bool up);

        // Continuous Bluetooth audio capture for a controller's live speaker stream - same cross-session
        // problem as Simulate*: WASAPI loopback capture needs an interactive desktop, which
        // Session 0 doesn't have. Headless/service mode forwards to the session-launched helper
        // process over the same pipe Simulate* already uses; endpointId selects which render
        // device to loop back (falls back to the system default when null/not found); codec keeps
        // encoding generic while the owning controller class defines the actual HID transport.
        // True only when the command reached a connected desktop helper. Callers must not mark
        // the controller stream active when Session 0 had nowhere to start WASAPI capture.
        bool StartBluetoothAudioCapture(int padId, string endpointId,
            BluetoothAudioCodec codec = BluetoothAudioCodec.DualShock4Sbc);
        void StopBluetoothAudioCapture(int padId);

        // Opt-in USB loopback audio router (UsbAudioLoopback.cs) - captures sourceEndpointId
        // (falls back to the system default render device when null/not found) and renders it
        // into targetEndpointId, the controller's own USB Audio Class endpoint, entirely inside
        // the desktop helper process. Unlike Bluetooth capture this never streams audio data back
        // over the pipe. When targetEndpointId is empty ("Default"), targetNameHint (the
        // controller's own UsbAudioEndpointNameHint) resolves which real device that means - the
        // controller's own endpoint, not the system default, since looping the system default
        // back into itself would be a feedback loop. True only when the command reached a
        // connected desktop helper, same reasoning as StartBluetoothAudioCapture.
        bool StartUsbAudioLoopback(int padId, string sourceEndpointId, string targetEndpointId,
            string targetNameHint, int volumePercent);
        void StopUsbAudioLoopback(int padId);
    }
}
