using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterJoyForCemu {
    // IJoyconHost implementation for running as a Windows Service (see BetterJoyService) - no
    // desktop, so no controller slots/tray icon/dialogs exist. UI-only members are safe no-ops;
    // logging goes to the Windows Event Log instead of a console TextBox. After login, desktop
    // input is forwarded to a session helper for hooks and ordinary Windows fallback. Before
    // login (or whenever that helper disconnects), controller mouse output goes straight from
    // LocalSystem through FakerInput's virtual HID device; it needs no interactive desktop.
    // Also runs the service side of the GUI control pipe (see ServiceControlProtocol) so a GUI
    // that has deferred hardware ownership here can still show live controller status and
    // trigger rumble test/join-split/calibration.
    public class HeadlessJoyconHost : IJoyconHost {
        private const string EventSource = "BetterJoy2";

        private readonly object pipeLock = new object();
        private NamedPipeServerStream pipe;
        private readonly DesktopInputBackend serviceInput = new DesktopInputBackend(false);
        private readonly HashSet<int> heldDesktopMouseButtons = new HashSet<int>();
        private bool helperReady;
        private bool helperOutputSent;
        private bool loggedNoHelperConnected;

        private readonly object controlPipeLock = new object();
        private NamedPipeServerStream controlPipe;
        // The pipe currently parked in BeginWaitForConnection. Nothing used to hold a reference to
        // it, which was fine while the only way to stop accepting was the process exiting - see
        // Shutdown, which has to actually cancel that wait.
        private NamedPipeServerStream pendingControlPipe;
        private volatile bool controlServerStopped;
        private NamedPipeServerStream bindingCaptureOwner;
        private volatile bool bindingCaptureSuppressesMappedOutput;

        public void AppendTextBox(string message) {
            try {
                if (!EventLog.SourceExists(EventSource))
                    EventLog.CreateEventSource(EventSource, "Application");
                EventLog.WriteEntry(EventSource, message, EventLogEntryType.Information);
            } catch {
                // Logging must never be the reason the service goes down.
            }
        }

        public void AssignSlot(Controller controller) { BroadcastSnapshot(); }
        public void RefreshOrientationIcon(JoyconController joycon) { BroadcastSnapshot(); }
        public void CollapseJoinedPair(JoyconController left, JoyconController right) { BroadcastSnapshot(); }
        public void HandleJoyconDropped(Controller dropped, JoyconController survivingPartner) { BroadcastSnapshot(); }
        public void UpdateBatteryColor(Controller controller) { BroadcastSnapshot(); }
        public void RefreshControllerState() { BroadcastSnapshot(); }

        public void NotifyLowBattery(Controller controller) {
            AppendTextBox(String.Format("Controller {0} - low battery.", controller.PadId));
            SendControlMessage(w => ServiceControlIpc.WriteLowBattery(w, (byte)controller.PadId, controller.Kind));
        }

        // Mirrors MainForm.JoinOrSplitJoycon (MainForm.cs) minus the button/icon updates, which
        // don't exist headless - used both for the hardware stick-double-click path (Controller.cs
        // calls this via IJoyconHost the same as GUI mode) and the remote JoinOrSplit command
        // from a connected GUI (see JoinOrSplitByPadId below).
        public void JoinOrSplitJoycon(JoyconController v, bool forceSelfPair = false) {
            if (v.other == null && v.SupportsPairing) {
                if (forceSelfPair || Program.mgr.j.Count == 1) {
                    v.other = v; // self-pair - single joycon in vertical mode
                } else {
                    foreach (Controller jcBase in Program.mgr.j) {
                        if (!(jcBase is JoyconController jc))
                            continue;
                        if (jc.SupportsPairing && jc.isLeft != v.isLeft && jc != v && jc.other == null) {
                            v.other = jc;
                            jc.other = v;

                            // Disconnect whichever controller was created later - see Joycon.
                            // virtualControllerSequence. The older one is the one most likely
                            // already locked onto by a running game, so it's left untouched; the
                            // newer one is safe to actually disconnect (matching a real unplug)
                            // and recreate later from each solo profile. The loser DECISION stays
                            // here (pairing-specific, Joy-Con-only); the destroy itself goes
                            // through the same pairing-ignorant primitive Program.cs uses, not a
                            // duplicate - see DestroyOutputControllers.
                            JoyconController loser = v.virtualControllerSequence > jc.virtualControllerSequence ? v : jc;
                            Program.mgr.DestroyOutputControllers(loser);
                            break;
                        }
                    }
                }
            } else if (v.other != null && v.SupportsPairing) {
                JoyconController partner = v.other;

                // Whichever half doesn't currently drive a virtual controller was the passive
                // side of the pair - see ReassignSplitOffJoycon (Program.cs) for why it needs a
                // fresh PadId now that it's standalone again.
                JoyconController passiveHalf = v.out_xbox == null && v.out_ds4 == null ? v
                    : partner.out_xbox == null && partner.out_ds4 == null ? partner : null;

                partner.other = null;
                v.other = null;

                if (passiveHalf != null)
                    Program.mgr.ReassignSplitOffJoycon(passiveHalf);
            }

            Program.mgr.ApplyControllerProfileOptions();
            BroadcastSnapshot();
        }

        // ---------------------------------------------------------------------------------
        // Input helper pipe (keyboard/mouse remap) - see InputHelper/SessionLauncher.
        // ---------------------------------------------------------------------------------

        // Starts listening on a brand new named pipe for the next input helper instance to
        // connect to - BetterJoyService launches that helper (via SessionLauncher) right after
        // calling this, passing back the returned pipe name on its command line. Any previous
        // connection is torn down first: used both for the very first helper launch and for
        // relaunching into a newly active session, where the old helper (still holding the now-
        // closed pipe) notices the drop and exits on its own (see InputHelper.Run).
        public string StartNewHelperSession() {
            lock (pipeLock) {
                ClosePipeLocked();

                string pipeName = InputIpc.PipeNamePrefix + Guid.NewGuid().ToString("N");
                var newPipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    InputIpc.CreateCrossSessionPipeSecurity());

                pipe = newPipe;
                newPipe.BeginWaitForConnection(OnHelperConnected, newPipe);
                return pipeName;
            }
        }

        private void OnHelperConnected(IAsyncResult result) {
            var connectedPipe = (NamedPipeServerStream)result.AsyncState;
            try {
                connectedPipe.EndWaitForConnection(result);
            } catch {
                return; // torn down/superseded before the helper connected - fine
            }

            lock (pipeLock) {
                if (connectedPipe != pipe)
                    return; // a newer session already replaced this one

                // Stop the service-side virtual mouse before allowing the helper writer path to
                // observe helperReady=true. WriteMessage uses this same lock, making ownership a
                // strict handoff rather than a race where both processes can emit one report.
                serviceInput.ReleaseVirtualMouseState(false);
                helperReady = true;
                helperOutputSent = false;
                ReapplyHeldButtonsToHelperLocked(connectedPipe);
            }

            AppendTextBox("Input helper connected.");
            loggedNoHelperConnected = false;

            var reader = new BinaryReader(connectedPipe);
            Task.Run(() => ReadLoop(connectedPipe, reader));

            // A controller can attach before the interactive-session helper finishes connecting.
            // StartBluetoothAudioCapture reports that early attempt as unavailable, so reconcile
            // the saved profiles again now that WASAPI capture can actually be created. Stop any
            // stream left active by a superseded helper first; the stop/start commands are ordered
            // on this new pipe and prevent a session handoff from retaining stale capture state.
            if (Program.mgr != null) {
                foreach (Controller controller in Program.mgr.j) {
                    if (controller is DualShock4Controller ds4)
                        ds4.StopBluetoothAudioStream();
                    else if (controller is DualSenseController dualSense)
                        dualSense.StopBluetoothAudioStream();
                }
                Program.mgr.ApplyControllerProfileOptions();
            }
        }

        private void ReadLoop(NamedPipeServerStream connectedPipe, BinaryReader reader) {
            try {
                while (connectedPipe.IsConnected) {
                    InputMessage msg = InputMessage.ReadFrom(reader);
                    switch (msg.Type) {
                        case InputMessageType.KeyDown: Program.OnKeyDown(msg.A); break;
                        case InputMessageType.KeyUp: Program.OnKeyUp(msg.A); break;
                        case InputMessageType.MouseButtonDown: Program.OnMouseButtonDown(msg.A); break;
                        case InputMessageType.MouseButtonUp: Program.OnMouseButtonUp(msg.A); break;
                        case InputMessageType.AudioFrame: {
                            byte[] frame = reader.ReadBytes(msg.B);
                            Controller controller = Program.mgr?.j.FirstOrDefault(j => j.PadId == msg.A);
                            if (controller is DualShock4Controller ds4)
                                ds4.EnqueueBluetoothAudioFrame(frame);
                            else if (controller is DualSenseController dualSense)
                                dualSense.EnqueueBluetoothAudioFrame(frame);
                            break;
                        }
                    }
                }
            } catch {
                // helper disconnected/crashed - BetterJoyService relaunches on the next session
                // change; nothing to do here but stop reading.
            } finally {
                lock (pipeLock) {
                    if (connectedPipe == pipe) {
                        TransferOutputToServiceLocked();
                    } else if (!helperReady) {
                        // A deliberately superseded helper has now completed its own neutral
                        // shutdown report. It is finally safe to restore any controller button
                        // still physically held if its replacement did not connect in time.
                        serviceInput.ReleaseVirtualMouseState(true);
                        foreach (int buttonCode in heldDesktopMouseButtons)
                            serviceInput.ButtonHold(buttonCode);
                    }
                }
                AppendTextBox("Input helper disconnected.");
            }
        }

        private void ClosePipeLocked() {
            // This is the deliberate replacement path (logon/unlock/console switch), not an
            // unexpected helper loss. Neutralize the old owner but do not immediately restore a
            // held button through the service: the old helper still has to observe the pipe close
            // and its own final neutral report could otherwise release the freshly restored hold.
            if (helperReady || helperOutputSent) {
                helperReady = false;
                if (helperOutputSent)
                    serviceInput.ReleaseVirtualMouseState(true);
                helperOutputSent = false;
            }
            if (pipe != null) {
                try { pipe.Dispose(); } catch { }
                pipe = null;
            }
        }

        // SendMessage is called inline from a Joycon's own poll thread - for gyro-mouse, once per
        // packet, at controller report rate. A synchronous pipe write there would stall that
        // thread for however long the write+flush takes (named pipe buffer contention, the helper
        // process being briefly busy, scheduling noise, ...), which delays draining the next HID
        // report right along with it - one source of the lag/jitter reported in service mode vs
        // the GUI (which calls WindowsInput in-process, no pipe involved). SendMessage now only
        // enqueues; a dedicated writer thread (started once, for the host's lifetime) does the
        // actual I/O off that critical path. Bounded so a genuinely stuck/disconnected pipe can't
        // grow this without limit - if the writer thread has fallen far enough behind to fill it,
        // dropping the newest message is preferable to blocking the caller waiting for room.
        private readonly BlockingCollection<InputMessage> outgoingMessages =
            new BlockingCollection<InputMessage>(new ConcurrentQueue<InputMessage>(), 64);

        // Relative and exact-cursor MoveBy deltas bypass outgoingMessages and accumulate here
        // instead - a dropped discrete event (a click, a key) is a one-off glitch, but a dropped
        // move is displacement that's just gone, permanently, and gyro-mouse calls this at
        // controller report rate. Coalescing means a writer thread that falls behind under
        // sustained motion never loses movement, just delivers it in a bigger jump next flush
        // instead of a bounded queue silently dropping the newest deltas - which is what a
        // fixed-capacity queue was doing here before, and reads as exactly the "constrained, then
        // releases" cycling reported (queue fills during a burst, drains during a lull).
        private readonly object pendingMoveLock = new object();
        private int pendingMoveDx, pendingMoveDy;
        private bool hasPendingMove;
        private int pendingCursorMoveDx, pendingCursorMoveDy;
        private bool hasPendingCursorMove;
        private int pendingWrappedCursorMoveDx, pendingWrappedCursorMoveDy;
        private bool hasPendingWrappedCursorMove;

        public HeadlessJoyconHost() {
            new Thread(PipeWriterLoop) { IsBackground = true, Name = "InputPipeWriter" }.Start();
        }

        // Bounded-wait instead of GetConsumingEnumerable's indefinite block, so this thread wakes
        // up on its own to flush a pending mouse move even when no discrete event is queued -
        // gyro-mouse alone, with nothing else bound, would otherwise never produce one.
        private void PipeWriterLoop() {
            while (true) {
                if (outgoingMessages.TryTake(out InputMessage msg, 5))
                    WriteMessage(msg);

                FlushPendingMove();
            }
        }

        private void FlushPendingMove() {
            int dx, dy, cursorDx, cursorDy, wrappedCursorDx, wrappedCursorDy;
            bool sendRelative, sendCursor, sendWrappedCursor;
            lock (pendingMoveLock) {
                if (!hasPendingMove && !hasPendingCursorMove && !hasPendingWrappedCursorMove)
                    return;
                dx = pendingMoveDx;
                dy = pendingMoveDy;
                cursorDx = pendingCursorMoveDx;
                cursorDy = pendingCursorMoveDy;
                wrappedCursorDx = pendingWrappedCursorMoveDx;
                wrappedCursorDy = pendingWrappedCursorMoveDy;
                sendRelative = hasPendingMove;
                sendCursor = hasPendingCursorMove;
                sendWrappedCursor = hasPendingWrappedCursorMove;
                pendingMoveDx = 0;
                pendingMoveDy = 0;
                pendingCursorMoveDx = 0;
                pendingCursorMoveDy = 0;
                pendingWrappedCursorMoveDx = 0;
                pendingWrappedCursorMoveDy = 0;
                hasPendingMove = false;
                hasPendingCursorMove = false;
                hasPendingWrappedCursorMove = false;
            }

            if (sendRelative)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateMoveBy, A = dx, B = dy });
            if (sendCursor)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateCursorMoveBy, A = cursorDx, B = cursorDy });
            if (sendWrappedCursor)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateWrappedCursorMoveBy, A = wrappedCursorDx, B = wrappedCursorDy });
        }

        private void WriteMessage(InputMessage msg) {
            lock (pipeLock) {
                UpdateHeldButtonStateLocked(msg);

                if (!helperReady || pipe == null || !pipe.IsConnected) {
                    if (!loggedNoHelperConnected) {
                        loggedNoHelperConnected = true;
                        AppendTextBox("No input helper connected - routing supported controller mouse, media, and shortcut output through FakerInput for the login/lock screen when available. Physical input-hook mappings wait for login.");
                    }
                    ExecuteServiceInputLocked(msg);
                    return;
                }

                try {
                    var writer = new BinaryWriter(pipe);
                    msg.WriteTo(writer);
                    writer.Flush();
                    helperOutputSent = true;
                } catch {
                    // Transfer the current report too: a mid-write disconnect is exactly when
                    // pre-login/lock-screen fallback is needed, and mouse displacement must not
                    // disappear merely because output ownership changed.
                    TransferOutputToServiceLocked();

                    // A hold was already folded into heldDesktopMouseButtons by
                    // UpdateHeldButtonStateLocked above, so the transfer loop just re-applied it -
                    // this only reaches here with helperReady true (the early-return above takes
                    // any !helperReady case), so that loop always runs and always includes it.
                    // Executing it again here would hold it a second time.
                    if (msg.Type != InputMessageType.SimulateButtonHold)
                        ExecuteServiceInputLocked(msg);
                }
            }
        }

        private void UpdateHeldButtonStateLocked(InputMessage msg) {
            if (msg.Type == InputMessageType.SimulateButtonHold)
                heldDesktopMouseButtons.Add(msg.A);
            else if (msg.Type == InputMessageType.SimulateButtonRelease)
                heldDesktopMouseButtons.Remove(msg.A);
        }

        private void ExecuteServiceInputLocked(InputMessage msg) {
            DesktopInputBackend.Execute(msg, serviceInput);
        }

        private void TransferOutputToServiceLocked() {
            if (!helperReady && !helperOutputSent)
                return;

            helperReady = false;
            if (helperOutputSent)
                serviceInput.ReleaseVirtualMouseState(true);
            helperOutputSent = false;

            foreach (int buttonCode in heldDesktopMouseButtons)
                serviceInput.ButtonHold(buttonCode);
        }

        private void ReapplyHeldButtonsToHelperLocked(NamedPipeServerStream connectedPipe) {
            if (heldDesktopMouseButtons.Count == 0)
                return;

            try {
                var writer = new BinaryWriter(connectedPipe);
                foreach (int buttonCode in heldDesktopMouseButtons)
                    new InputMessage { Type = InputMessageType.SimulateButtonHold, A = buttonCode }.WriteTo(writer);
                writer.Flush();
                helperOutputSent = true;
            } catch {
                TransferOutputToServiceLocked();
            }
        }

        // Full teardown of everything this host started - the counterpart to OnStart's
        // StartControlServer/StartConfigWatcher pair, which never needed one while the only thing
        // that ever stopped them was the process exiting. It does now: the service runs its own
        // stop/start routines in-process across a system suspend, and without this the old host's
        // accept loop keeps its named pipe and the old FileSystemWatcher keeps firing, so the next
        // start would stack a second one of each on top.
        public void Shutdown() {
            StopInputRouting();

            controlServerStopped = true;
            lock (controlPipeLock) {
                if (pendingControlPipe != null) {
                    try { pendingControlPipe.Dispose(); } catch { }
                    pendingControlPipe = null;
                }
                if (controlPipe != null) {
                    try { controlPipe.Dispose(); } catch { }
                    controlPipe = null;
                }
            }

            if (configWatcher != null) {
                try {
                    configWatcher.EnableRaisingEvents = false;
                    configWatcher.Dispose();
                } catch { }
                configWatcher = null;
            }

            if (reloadDebounceTimer != null) {
                try {
                    reloadDebounceTimer.Stop();
                    reloadDebounceTimer.Dispose();
                } catch { }
                reloadDebounceTimer = null;
            }
        }

        public void StopInputRouting() {
            lock (pipeLock) {
                helperReady = false;
                helperOutputSent = false;
                serviceInput.ReleaseVirtualMouseState(false);
                serviceInput.Dispose();
                if (pipe != null) {
                    try { pipe.Dispose(); } catch { }
                    pipe = null;
                }
            }
        }

        private void SendMessage(InputMessageType type, int a = 0, int b = 0) {
            outgoingMessages.TryAdd(new InputMessage { Type = type, A = a, B = b });
        }

        // Hold/Release represent persistent state on the desktop (a key or mouse button actually
        // staying down) rather than a one-off action - silently dropping a Release under queue
        // pressure the way SendMessage's TryAdd does leaves whatever it was supposed to release
        // stuck down indefinitely, which is a correctness bug, not just a missed input. Blocks
        // (via BlockingCollection.Add) instead of dropping. Safe from deadlock even with no
        // helper ever connected: the writer thread keeps draining outgoingMessages regardless of
        // pipe state (WriteMessage no-ops but still consumes the item), so the queue always frees
        // up - this only actually blocks the caller (a Joycon's poll thread) for the rare moment
        // the queue is genuinely full, not routinely like the old synchronous pipe write did.
        private void SendStatefulMessage(InputMessageType type, int a = 0, int b = 0) {
            outgoingMessages.Add(new InputMessage { Type = type, A = a, B = b });
        }

        public void SimulateKeyClick(int keyCode) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendMessage(InputMessageType.SimulateKeyClick, keyCode);
        }
        public void SimulateKeyHold(int keyCode) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendStatefulMessage(InputMessageType.SimulateKeyHold, keyCode);
        }
        public void SimulateKeyRelease(int keyCode) => SendStatefulMessage(InputMessageType.SimulateKeyRelease, keyCode);
        public void SimulateDesktopAction(int actionCode) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendMessage(InputMessageType.SimulateDesktopAction, actionCode);
        }
        public void SimulateButtonClick(int buttonCode) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendMessage(InputMessageType.SimulateButtonClick, buttonCode);
        }
        public void SimulateButtonHold(int buttonCode) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendStatefulMessage(InputMessageType.SimulateButtonHold, buttonCode);
        }
        public void SimulateButtonRelease(int buttonCode) => SendStatefulMessage(InputMessageType.SimulateButtonRelease, buttonCode);
        public void SimulateMoveTo(int x, int y) => SendMessage(InputMessageType.SimulateMoveTo, x, y);

        public void SimulateMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingMoveDx += dx;
                pendingMoveDy += dy;
                hasPendingMove = true;
            }
        }

        public void SimulateCursorMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingCursorMoveDx += dx;
                pendingCursorMoveDy += dy;
                hasPendingCursorMove = true;
            }
        }

        public void SimulateWrappedCursorMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingWrappedCursorMoveDx += dx;
                pendingWrappedCursorMoveDy += dy;
                hasPendingWrappedCursorMove = true;
            }
        }

        public void SimulateMoveToScreenCenter() => SendMessage(InputMessageType.SimulateMoveToScreenCenter);
        public void SimulateScroll(bool up) {
            if (!bindingCaptureSuppressesMappedOutput)
                SendMessage(InputMessageType.SimulateScroll, up ? 1 : 0);
        }

        // Sent directly, not through outgoingMessages/WriteMessage: those exist to keep a
        // controller's report-rate poll thread off pipe I/O (see SendMessage's comment) and carry
        // no notion of a fallback when no helper is connected - there is none here, Session 0
        // genuinely cannot do WASAPI loopback capture. Start/stop only fire on connect/disconnect/
        // a settings toggle, not at report rate, so a brief synchronous write is fine.
        public bool StartBluetoothAudioCapture(int padId, string endpointId,
            BluetoothAudioCodec codec = BluetoothAudioCodec.DualShock4Sbc) {
            lock (pipeLock) {
                if (!helperReady || pipe == null || !pipe.IsConnected)
                    return false;
                try {
                    var writer = new BinaryWriter(pipe);
                    new InputMessage {
                        Type = InputMessageType.StartAudioCapture,
                        A = padId,
                        B = (int)codec
                    }.WriteTo(writer);
                    writer.Write(endpointId ?? String.Empty);
                    writer.Flush();
                    return true;
                } catch {
                    // best-effort - if the helper is gone, there's simply nothing to capture from
                    return false;
                }
            }
        }

        public void StopBluetoothAudioCapture(int padId) {
            lock (pipeLock) {
                if (!helperReady || pipe == null || !pipe.IsConnected)
                    return;
                try {
                    var writer = new BinaryWriter(pipe);
                    new InputMessage { Type = InputMessageType.StopAudioCapture, A = padId }.WriteTo(writer);
                    writer.Flush();
                } catch {
                    // best-effort
                }
            }
        }

        public bool StartUsbAudioLoopback(int padId, string sourceEndpointId, string targetEndpointId,
            string targetNameHint, int volumePercent) {
            lock (pipeLock) {
                if (!helperReady || pipe == null || !pipe.IsConnected)
                    return false;
                try {
                    var writer = new BinaryWriter(pipe);
                    new InputMessage {
                        Type = InputMessageType.StartUsbAudioLoopback,
                        A = padId,
                        B = volumePercent
                    }.WriteTo(writer);
                    writer.Write(sourceEndpointId ?? String.Empty);
                    writer.Write(targetEndpointId ?? String.Empty);
                    writer.Write(targetNameHint ?? String.Empty);
                    writer.Flush();
                    return true;
                } catch {
                    // best-effort - if the helper is gone, there's simply nowhere to render to
                    return false;
                }
            }
        }

        public void StopUsbAudioLoopback(int padId) {
            lock (pipeLock) {
                if (!helperReady || pipe == null || !pipe.IsConnected)
                    return;
                try {
                    var writer = new BinaryWriter(pipe);
                    new InputMessage { Type = InputMessageType.StopUsbAudioLoopback, A = padId }.WriteTo(writer);
                    writer.Flush();
                } catch {
                    // best-effort
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // GUI control pipe (live status + rumble test/join-split/calibration commands) - see
        // ServiceControlProtocol. Unlike the input helper pipe above, this one is long-lived
        // and reconnectable: a GUI may start/stop independently of the service's own lifetime,
        // so this keeps accepting new connections for as long as the service runs.
        // ---------------------------------------------------------------------------------

        public void StartControlServer() {
            AcceptNextControlConnection();
        }

        // The real fix for the DACL missing a SYSTEM grant lives in
        // InputIpc.CreateCrossSessionPipeSecurity (see its comment) - creating a brand new
        // object succeeds regardless of its own DACL, but every instance after the first of an
        // already-existing named pipe is checked against it, so without an explicit grant for
        // the service's own account, only the first accept-loop iteration ever worked. Reusing
        // one PipeSecurity instance here isn't load-bearing for that bug, but avoids rebuilding
        // it on every reconnect for no reason.
        private static readonly PipeSecurity ControlPipeSecurity = InputIpc.CreateCrossSessionPipeSecurity();

        private void AcceptNextControlConnection() {
            // This whole control channel is a status/convenience feature layered on top of the
            // core HID/ViGEm pipeline, which has nothing to do with it - an exception here
            // (pipe creation failing for any reason) must never be allowed to propagate and take
            // the whole service down with it, the way the DACL bug above did. Log and stop
            // trying rather than crash; a GUI just won't get live status until the service is
            // restarted with whatever caused this fixed.
            if (controlServerStopped)
                return;

            try {
                var newPipe = new NamedPipeServerStream(
                    ServiceControlIpc.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    ControlPipeSecurity);

                lock (controlPipeLock)
                    pendingControlPipe = newPipe;
                newPipe.BeginWaitForConnection(OnControlClientConnected, newPipe);
            } catch (Exception ex) {
                AppendTextBox("GUI control pipe stopped accepting connections: " + ex.Message);
            }
        }

        private void OnControlClientConnected(IAsyncResult result) {
            var connectedPipe = (NamedPipeServerStream)result.AsyncState;
            try {
                connectedPipe.EndWaitForConnection(result);
            } catch {
                return; // service stopping - the pipe was disposed out from under the wait
            }

            if (controlServerStopped) {
                try { connectedPipe.Dispose(); } catch { }
                return;
            }

            lock (controlPipeLock) {
                if (controlPipe != null) {
                    EndBindingCapture(controlPipe);
                    try { controlPipe.Dispose(); } catch { }
                }
                controlPipe = connectedPipe;
            }

            AppendTextBox("GUI control connection established.");
            BroadcastSnapshot();

            var reader = new BinaryReader(connectedPipe);
            Task.Run(() => ControlReadLoop(connectedPipe, reader));

            // Keep listening immediately, independent of this connection's lifetime, so a GUI
            // relaunch (or a second one, however unlikely given its own single-instance mutex)
            // always finds the pipe accepting.
            AcceptNextControlConnection();
        }

        private void ControlReadLoop(NamedPipeServerStream connectedPipe, BinaryReader reader) {
            try {
                while (connectedPipe.IsConnected) {
                    var type = (ControlMessageType)reader.ReadByte();
                    switch (type) {
                        case ControlMessageType.RequestSnapshot:
                            BroadcastSnapshot();
                            break;
                        case ControlMessageType.TestRumble:
                            TestRumble(reader.ReadByte());
                            break;
                        case ControlMessageType.JoinOrSplit:
                            JoinOrSplitByPadId(reader.ReadByte(), forceSelfPair: false);
                            break;
                        case ControlMessageType.ForceSelfPair:
                            JoinOrSplitByPadId(reader.ReadByte(), forceSelfPair: true);
                            break;
                        case ControlMessageType.StartCalibration:
                            StartCalibration(reader.ReadByte());
                            break;
                        case ControlMessageType.CalibrationReady:
                            reader.ReadByte(); // padId - only one calibration can be in progress at a time, guarded by calibrationInProgress
                            CompleteCalibReady();
                            break;
                        case ControlMessageType.StartButtonCapture:
                            StartButtonCapture();
                            break;
                        case ControlMessageType.StopButtonCapture:
                            StopButtonCapture();
                            break;
                        case ControlMessageType.BeginBindingCapture:
                            BeginBindingCapture(connectedPipe);
                            break;
                        case ControlMessageType.EndBindingCapture:
                            EndBindingCapture(connectedPipe);
                            break;
                        case ControlMessageType.PrepareUsbAudio:
                            PrepareUsbAudio(reader.ReadByte(), reader.ReadByte());
                            break;
                    }
                }
            } catch {
                // GUI closed/disconnected - fine, a fresh accept-loop is already listening.
            } finally {
                EndBindingCapture(connectedPipe);
                AppendTextBox("GUI control connection closed.");
            }
        }

        private void BeginBindingCapture(NamedPipeServerStream owner) {
            lock (controlPipeLock) {
                bindingCaptureOwner = owner;
                bindingCaptureSuppressesMappedOutput = true;
            }
        }

        private void EndBindingCapture(NamedPipeServerStream owner) {
            lock (controlPipeLock) {
                if (bindingCaptureOwner != owner)
                    return;

                bindingCaptureSuppressesMappedOutput = false;
                bindingCaptureOwner = null;
            }
        }

        private void TestRumble(int padId) {
            Controller jc = Program.mgr?.j.FirstOrDefault(j => j.PadId == padId);
            if (jc == null || !jc.RumbleEnabled)
                return;

            jc.SetRumble(160.0f, 320.0f, 1.0f);
            Task.Delay(300).ContinueWith(_ => jc.SetRumble(160.0f, 320.0f, 0));
        }

        private void PrepareUsbAudio(int padId, int volumePercent) {
            Controller controller = Program.mgr?.j.FirstOrDefault(j => j.PadId == padId);
            controller?.PrepareUsbAudio(volumePercent);
        }

        private void JoinOrSplitByPadId(int padId, bool forceSelfPair) {
            // Joining/splitting is Joy-Con-only pairing logic (see JoinOrSplitJoycon's own
            // JoyconController-typed parameter) - a non-pairing match here is simply a no-op.
            JoyconController jc = Program.mgr?.j.FirstOrDefault(j => j.PadId == padId) as JoyconController;
            if (jc != null)
                JoinOrSplitJoycon(jc, forceSelfPair);
        }

        // Mirrors MainForm's calibration step sequence exactly - a connected GUI renders the
        // same CalibrationDialog off of the CalibrationStep messages pushed below.
        // CalibrationState.CalibratingController scopes sample admission to this one padId, so
        // other connected controllers staying connected/polling can't contaminate the buffers -
        // no need to require exactly one controller connected total.
        //
        // int, not bool: admission is a check-and-set (see StartCalibration), which needs
        // Interlocked.CompareExchange to be atomic - a plain volatile bool read-then-write could
        // let two StartCalibration calls on different threads both pass the check before either
        // sets it, if a stale ControlReadLoop connection hasn't yet realized it's been
        // superseded (see AcceptNextControlConnection) and a new one calls in around the same time.
        private int calibrationInProgress = 0;

        // Completed by an incoming CalibrationReady message (see ControlReadLoop) whenever the
        // GUI's Start or Done button is clicked - both use the same message/wait, since only one
        // can ever be pending at a time and the flow below knows from context which it means.
        // Gyro is the one phase that doesn't wait on this a second time (no Done click - see
        // CalibrationDialog for why): it runs on its own fixed Task.Delay instead once started.
        private TaskCompletionSource<bool> pendingCalibReady;

        private async void StartCalibration(int padId) {
            // Captured once, before the guard - re-querying for it a second time after admission
            // (the controller this validated a moment earlier could disconnect in between) could
            // throw with the guard already set and no continuation left to ever release it.
            // FirstOrDefault never throws either way.
            Controller jc = Program.mgr?.j.FirstOrDefault(v => v.PadId == padId);
            // Tracks whichever controller currently holds the gyro calibration claim, if any -
            // for a joined pair this may be jc.other, not jc itself, once the second gyro step
            // starts. The catch block below needs this to release the right claim rather than
            // assuming it's always jc.
            Controller activeClaimHolder = null;
            if (jc == null) {
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
                return;
            }

            if (Interlocked.CompareExchange(ref calibrationInProgress, 1, 0) != 0) {
                // Already calibrating something - a second request arriving mid-window would
                // call ClearSamples() over an in-progress collection and leave two pending
                // completions racing the same CalibrationState buffers.
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
                return;
            }

            SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationStarted, padId));

            // Target/Secondary mirror MainForm's CalibStep - a Pro controller's two sticks live
            // on ONE Joycon object (distinguished by Secondary), but a joined pair's two sticks -
            // and, just as much, its two IMUs - live on TWO SEPARATE Joycon objects, each needing
            // its own calibration pass against that specific instance. Without this, only
            // whichever half jc happens to be would ever actually get recalibrated - the
            // partner's stick (and, before gyroSteps existed, its gyro/accelerometer too) would
            // silently stay untouched every single time this pair is calibrated.
            bool isPair = jc.other != null && jc.other != jc;
            var gyroSteps = new List<(Controller Target, string Label)>();
            // No gyro support yet (ExtractIMUValues/CalibrationState.AddSample are never reached
            // for a DualSense - see TryAutoCalibrate's own !HasGyro guard, and MainForm's local
            // StartCalibrate has the same skip) - leaving gyroSteps empty here just runs the stick
            // steps below with no gyro phase, same as the local path.
            if (!jc.HasGyro) {
                // no gyro steps
            } else if (isPair && jc.SupportsPairing) {
                Controller leftGyroJc = jc.isLeft ? jc : jc.other;
                Controller rightGyroJc = jc.isLeft ? jc.other : jc;
                gyroSteps.Add((leftGyroJc, "Left Gyroscope"));
                gyroSteps.Add((rightGyroJc, "Right Gyroscope"));
            } else {
                gyroSteps.Add((jc, "Gyroscope"));
            }

            var stickSteps = new List<(Controller Target, bool Secondary, string Label)>();
            if (jc.HasDualSticks) {
                stickSteps.Add((jc, false, "Left Stick"));
                stickSteps.Add((jc, true, "Right Stick"));
            } else if (isPair) {
                Controller leftJc = jc.isLeft ? jc : jc.other;
                Controller rightJc = jc.isLeft ? jc.other : jc;
                stickSteps.Add((leftJc, false, "Left Stick"));
                stickSteps.Add((rightJc, false, "Right Stick"));
            } else {
                stickSteps.Add((jc, false, jc.isLeft ? "Left Stick" : "Right Stick"));
            }
            int totalSteps = gyroSteps.Count + stickSteps.Count;

            try {
                for (int g = 0; g < gyroSteps.Count; g++) {
                    Controller gyroTarget = gyroSteps[g].Target;
                    string gyroLabel = gyroSteps[g].Label;
                    int gyroStepNumber = g + 1;

                    SendCalibrationStep(padId, gyroStepNumber, totalSteps, gyroLabel, "Place the controller on a flat, still surface.", CalibStepUiMode.Start, 0);
                    CalibrationState.PendingConfirmController = gyroTarget;
                    await WaitForCalibReady();

                    CalibrationState.ForceClaim(gyroTarget);
                    activeClaimHolder = gyroTarget;
                    for (int t = 3; t >= 0; t--) {
                        SendCalibrationStep(padId, gyroStepNumber, totalSteps, gyroLabel, "Hold still...", CalibStepUiMode.Countdown, t);
                        if (t > 0)
                            await Task.Delay(1000);
                    }

                    CalibrationState.FinishCalibration(gyroTarget);
                    activeClaimHolder = null; // FinishCalibration already released it on success
                    gyroTarget.getActiveData();
                }

                for (int i = 0; i < stickSteps.Count; i++) {
                    Controller target = stickSteps[i].Target;
                    bool secondary = stickSteps[i].Secondary;
                    string stepName = stickSteps[i].Label;
                    int stepNumber = gyroSteps.Count + i + 1;

                    CalibrationState.ClearStickSamples();
                    CalibrationState.StickCalibratingController = target;
                    CalibrationState.StickCalibrating = true;
                    CalibrationState.CurrentStickTarget = secondary ? CalibrationState.StickTarget.Secondary : CalibrationState.StickTarget.Primary;
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    // Center gets a Start gate (the user needs a moment to actually let go of/
                    // center the stick before admission begins), then just a brief automatic
                    // capture - centering is a quick snapshot, not something that benefits from
                    // open-ended time. Rotate below skips the Start gate too: a few stray samples
                    // before the user starts moving are harmless, they just fall inside the
                    // eventual min/max instead of corrupting it.
                    SendCalibrationStep(padId, stepNumber, totalSteps, stepName, String.Format("Leave the {0} centered - don't touch it.", stepName.ToLower()), CalibStepUiMode.Start, 0);
                    CalibrationState.PendingConfirmController = target;
                    await WaitForCalibReady();
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Center;
                    await Task.Delay(1000);
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Range;
                    SendCalibrationStep(padId, stepNumber, totalSteps, stepName, String.Format("Now rotate the {0} in full circles out to its edges.", stepName.ToLower()), CalibStepUiMode.Done, 0);
                    CalibrationState.PendingConfirmController = target;
                    await WaitForCalibReady();
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    CalibrationState.FinishStickCalibration(target.serial_number, secondary);
                    target.getActiveStickData();
                }

                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationComplete, padId));
            } catch {
                // Any step can fail (e.g. the controller disconnected mid-window) -
                // calibrationInProgress and the CalibrationState flags MUST still clear here or
                // every future calibration request fails until the service restarts. No-op if
                // activeClaimHolder doesn't currently hold the claim (e.g. it was already
                // released normally, or never claimed it - a stick-step failure).
                CalibrationState.Release(activeClaimHolder);
                CalibrationState.StickCalibrating = false;
                CalibrationState.StickCalibratingController = null;
                CalibrationState.PendingConfirmController = null;
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
            } finally {
                pendingCalibReady = null;
                Interlocked.Exchange(ref calibrationInProgress, 0);
            }
        }

        private Task WaitForCalibReady() {
            // RunContinuationsAsynchronously - TrySetResult below fires from either the pipe's
            // read loop thread (ControlReadLoop) or a physical controller's own Poll thread (see
            // HandleCalibrationConfirm); without this the rest of StartCalibration's async
            // continuation would run inline on whichever one completed it, delaying that thread
            // from getting back to its own work.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingCalibReady = tcs;
            return tcs.Task;
        }

        // Shared completion path for both an incoming CalibrationReady pipe message (a remote
        // GUI's mouse click) and a physical confirm button press on the controller itself (see
        // HandleCalibrationConfirm below) - whichever fires first is the one that counts.
        private void CompleteCalibReady() {
            CalibrationState.PendingConfirmController = null;
            pendingCalibReady?.TrySetResult(true);
        }

        // IJoyconHost - called from the calibrating controller's own Poll thread (see Joycon.
        // DoThingsWithButtons) when a face button is pressed while a Start/Done prompt is
        // showing. No live GUI here to update directly (unlike MainForm's implementation) - just
        // completing the same wait a CalibrationReady pipe message would have is enough; the
        // async StartCalibration continuation does the rest, including pushing the next
        // CalibrationStep to whatever remote GUI is connected.
        public void HandleCalibrationConfirm(Controller controller) {
            CompleteCalibReady();
        }

        private void SendCalibrationStep(int padId, int stepNumber, int totalSteps, string stepName, string instruction, CalibStepUiMode uiMode, int count) {
            SendControlMessage(w => ServiceControlIpc.WriteCalibrationStep(w, new CalibrationStepInfo {
                PadId = padId,
                StepNumber = stepNumber,
                TotalSteps = totalSteps,
                StepName = stepName,
                Instruction = instruction,
                UiMode = uiMode,
                Count = count,
            }));
        }

        // Server-side counterpart to Reassign.JoyPoll_Tick's local polling - the GUI has no
        // Joycon instances of its own to poll when it's deferred hardware ownership here, so
        // this does the same rising/falling edge detection against Program.mgr.j (which IS
        // populated in this process) and pushes each transition over the control pipe instead of
        // acting on it directly. System.Timers.Timer, not a WinForms Timer - this process has no
        // message pump for a WM_TIMER-based timer to ever fire on.
        private System.Timers.Timer buttonCapturePoll;
        private readonly Dictionary<Controller, bool[]> buttonCapturePrev = new Dictionary<Controller, bool[]>();

        private void StartButtonCapture() {
            if (buttonCapturePoll != null)
                return; // already running for this (or another) connected GUI

            buttonCapturePrev.Clear();
            buttonCapturePoll = new System.Timers.Timer(30);
            buttonCapturePoll.Elapsed += (s, e) => PollButtonCapture();
            buttonCapturePoll.AutoReset = true;
            buttonCapturePoll.Start();
        }

        private void StopButtonCapture() {
            buttonCapturePoll?.Stop();
            buttonCapturePoll?.Dispose();
            buttonCapturePoll = null;
            buttonCapturePrev.Clear();
        }

        private void PollButtonCapture() {
            if (Program.mgr == null)
                return;

            int buttonCount = Enum.GetValues(typeof(Controller.Button)).Length;
            foreach (Controller jc in Program.mgr.j) {
                // See the identical skip in Reassign.cs's JoyPoll_Tick - a joined pair's two
                // halves cross-reference each other's raw buttons, so polling both independently
                // reports one physical press as two (e.g. "DPAD_DOWN+B" for a single B press).
                // The left half alone already has a complete, correctly-labeled view of the pair.
                if (jc.other != null && jc.other != jc && !jc.isLeft)
                    continue;

                string profileId = ControllerMappings.ProfileIdFor(jc);

                if (!buttonCapturePrev.TryGetValue(jc, out bool[] prev)) {
                    prev = new bool[buttonCount];
                    for (int bi = 0; bi < buttonCount; bi++)
                        prev[bi] = jc.GetButton((Controller.Button)bi);
                    buttonCapturePrev[jc] = prev;
                    continue; // baseline only - don't report a controller's already-held buttons as fresh presses
                }

                for (int bi = 0; bi < buttonCount; bi++) {
                    bool now = jc.GetButton((Controller.Button)bi);
                    if (now == prev[bi])
                        continue;
                    prev[bi] = now;
                    int capturedBi = bi;
                    bool capturedNow = now;
                    SendControlMessage(w => ServiceControlIpc.WriteButtonTransition(w, profileId, capturedBi, capturedNow));
                }
            }
        }

        private void BroadcastSnapshot() {
            SendControlMessage(w => ServiceControlIpc.WriteSnapshot(w, BuildSnapshot()));
        }

        private List<ControllerRecord> BuildSnapshot() {
            var records = new List<ControllerRecord>();
            if (Program.mgr == null)
                return records;

            foreach (Controller jc in Program.mgr.j) {
                // A joined pair's passive half isn't a virtual controller - it has no out_xbox/
                // out_ds4 of its own, its LED just mirrors its active partner's (see
                // ReassignPadIds), and its PadId is stale/unstable while parked (see
                // NextAvailablePadId). Rendering is keyed off actual virtual controllers, not
                // physical Joycons - one record per pair, not two - so skip it here rather than
                // emitting a record that could numerically collide with an unrelated one and get
                // de-duplicated away.
                bool isPassiveHalf = jc.other != null && jc.other != jc && jc.out_xbox == null && jc.out_ds4 == null;
                if (isPassiveHalf)
                    continue;

                ControllerKind kind = jc.Kind;

                sbyte otherPadId = (jc.other != null && jc.other != jc) ? (sbyte)jc.other.PadId : (sbyte)-1;
                ControllerProfileInfo profile = ControllerMappings.ProfileFor(jc);

                records.Add(new ControllerRecord {
                    PadId = (byte)jc.PadId,
                    Kind = kind,
                    Battery = (sbyte)jc.battery,
                    BatteryPercent = (sbyte)jc.batteryPercent,
                    BatteryStatus = jc.batteryStatus,
                    OtherPadId = otherPadId,
                    ProfileId = profile.ProfileId,
                    ProfileName = profile.DisplayName,
                    ConnectionSequence = profile.ConnectionSequence,
                    IsVertical = jc.other == jc,
                    IsUsb = jc.IsUsbConnection,
                    AudioEndpointNameHint = jc.UsbAudioEndpointNameHint,
                });
            }

            // See DebugLog (off by default, gated behind the DebugLogging AppSetting) - this is
            // the exact list RenderSnapshot (MainForm.cs) renders slots from, in this order, so
            // it's the ground truth for diagnosing "a connected controller isn't shown" bugs.
            var sb = new StringBuilder("BuildSnapshot:");
            foreach (ControllerRecord r in records) {
                sb.AppendFormat(CultureInfo.InvariantCulture, " [pad={0} other={1} kind={2}]",
                    r.PadId, r.OtherPadId, r.Kind);
            }
            DebugLog.Write(sb.ToString());

            return records;
        }

        private void SendControlMessage(Action<BinaryWriter> write) {
            lock (controlPipeLock) {
                if (controlPipe == null || !controlPipe.IsConnected)
                    return;

                try {
                    var writer = new BinaryWriter(controlPipe);
                    write(writer);
                } catch {
                    // best-effort - a mid-write disconnect just drops this one push
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Shared config auto-reload - picks up settings/keybind/controller-list changes a GUI
        // (or anything else) writes to the shared AppPaths.DataDir location, without needing
        // to restart the service. See EntryPoint.RedirectConfigToAppData/AppPaths for why these
        // files live where they do.
        // ---------------------------------------------------------------------------------

        private static readonly HashSet<string> WatchedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "settings", "BetterJoy2.exe.config", ControllerMappings.FileName,
            "3rdPartyControllers", "BlacklistedControllers",
        };

        private FileSystemWatcher configWatcher;
        private System.Timers.Timer reloadDebounceTimer;

        public void StartConfigWatcher() {
            reloadDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
            reloadDebounceTimer.Elapsed += (sender, e) => ReloadSharedConfig();

            configWatcher = new FileSystemWatcher(AppPaths.DataDir) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            configWatcher.Changed += OnConfigFileChanged;
            configWatcher.Created += OnConfigFileChanged;
            configWatcher.Renamed += OnConfigFileChanged;
            configWatcher.EnableRaisingEvents = true;
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e) {
            if (!WatchedFileNames.Contains(e.Name))
                return;

            // FileSystemWatcher reliably fires more than once per save (e.g. a write-then-
            // rename pattern some editors/APIs use) - restart a short idle timer instead of
            // reloading on every single event.
            reloadDebounceTimer.Stop();
            reloadDebounceTimer.Start();
        }

        private void ReloadSharedConfig() {
            try {
                // Settings/keybinds only, not calibration data (see Config.ReloadSettingsOnly) -
                // calibration is handled entirely in-process by StartCalibration and never
                // needs a file-driven reload, so there's no reason to risk it here.
                Config.ReloadSettingsOnly();
                ControllerMappings.Reload();
                ConfigurationManager.RefreshSection("appSettings");
                OpenRgbServer.SyncEnabledState();

                // Program.thirdPartyCons/blacklistedCons are plain Lists a scan pass iterates
                // directly - rebuilding them (Clear()+AddRange()) outside the scan lock could
                // race that iteration. See JoyconManager.RunExclusiveOfScanning.
                Program.mgr?.RunExclusiveOfScanning(_3rdPartyControllers.LoadIntoProgramLists);
                Program.mgr?.ApplyControllerProfileOptions();

                AppendTextBox("Reloaded shared configuration after a change.");
            } catch (Exception ex) {
                AppendTextBox("Failed to reload shared configuration: " + ex.Message);
            }
        }
    }
}
