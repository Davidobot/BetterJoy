using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    public partial class MainForm : Form, IJoyconHost {
        public bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
        public List<Button> con, loc;
        private Timer clickTimer;
        private Timer rightClickTimer;
        private readonly DesktopInputBackend desktopInput;
        private readonly string[] displayedConfigKeys;
        private static readonly Bitmap SlotIconCross = Properties.Resources.cross;
        private static readonly Bitmap SlotIconPlus = Properties.Resources.plus;
        private static readonly Bitmap SlotIconCalibrate = Properties.Resources.calibrate;
        private static readonly Bitmap SlotIconPro = Properties.Resources.pro;
        private static readonly Bitmap SlotIconDualSense = Properties.Resources.dualsense;
        private static readonly Bitmap SlotIconDualShock4 = Properties.Resources.ds4;
        private static readonly Bitmap SlotIconSnes = Properties.Resources.snes;
        private static readonly Bitmap SlotIconN64 = Properties.Resources.ultra;
        private static readonly Bitmap SlotIconJoyconLeft = Properties.Resources.jc_left;
        private static readonly Bitmap SlotIconJoyconLeftSideways = Properties.Resources.jc_left_s;
        private static readonly Bitmap SlotIconJoyconRight = Properties.Resources.jc_right;
        private static readonly Bitmap SlotIconJoyconRightSideways = Properties.Resources.jc_right_s;
        private static readonly Rectangle SlotIconJoyconLeftBounds =
            GetOpaqueBounds(SlotIconJoyconLeft);
        private static readonly Rectangle SlotIconJoyconRightBounds =
            GetOpaqueBounds(SlotIconJoyconRight);
        private readonly Dictionary<string, Bitmap> joinedIconCache =
            new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private static readonly HashSet<string> ProfileOwnedConfigKeys =
            new HashSet<string>(StringComparer.Ordinal) {
                "ShowAsXInput", "ShowAsDS4", "AutoPowerOff", "PowerOffInactivity",
                "HomeLongPowerOff", "EnableRumble", "GyroHoldToggle",
                "GyroMouseInhibitButtons",
                "DragToggle", "TouchpadMouseInhibitButtons", "TouchpadSensitivity",
                "TouchpadStickSensitivity",
                "TouchpadHorizontalScale", "TouchpadVerticalScale",
                "TouchpadTapAndHold", "TouchpadClickMovementLockout",
                "TouchpadTwoFingerScroll",
                "SwapAB", "SwapXY", "HomeLEDOn", "LightColor",
            };
        // When a Windows Service already owns the hardware (see ServiceControlProtocol/
        // HeadlessJoyconHost), this GUI never runs its own HID/ViGEm pipeline at all - it just
        // shows live status pushed over ServiceControlClient and forwards a handful of commands
        // (rumble test/join-split/calibration) instead of acting on a live Joycon directly.
        private ServiceControlClient serviceClient;
        private List<ControllerRecord> lastControllerSnapshot = new List<ControllerRecord>();
        private Timer serviceReconnectTimer;
        private bool serviceReconnectInProgress;
        private int serviceReconnectAttempts;
        private const int ServiceReconnectIntervalMs = 1000;
        private const int ServiceReconnectPipeTimeoutMs = 500;

        public enum NonOriginalController : int {
            Disabled = 0,
            DefaultCalibration = 1,
            ControllerCalibration = 2,
        }

        public MainForm() {
            desktopInput = new DesktopInputBackend();
            clickTimer = new Timer { Interval = 250 };
            clickTimer.Tick += ClickTimer_Tick;
            rightClickTimer = new Timer { Interval = 250 };
            rightClickTimer.Tick += RightClickTimer_Tick;

            InitializeComponent();

            // Read from the assembly instead of hardcoding a string here, so this can't drift
            // out of sync with AssemblyInfo.cs's version the way the old static Designer text did.
            version_lbl.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            if (AppPaths.ServiceModeEnabled) {
                btn_enableServiceMode.Text = "Config Synced with Service";
                btn_enableServiceMode.Enabled = false;
            }

            con = new List<Button> { con1, con2, con3, con4 };
            loc = new List<Button> { loc1, loc2, loc3, loc4 };

            // Wired once here (rather than per-connect in Program.cs) so empty slots stay
            // hoverable/clickable - they start with Tag == null and never get disabled, and
            // conBtnMouseClick/MouseEnter/MouseLeave branch on that to offer "add a controller"
            // instead of the connected-controller behavior. Uses MouseUp rather than MouseClick:
            // Button/ButtonBase only synthesizes the compound Click/MouseClick event for the
            // left mouse button, so a right-click handler on MouseClick silently never fires.
            // MouseDown/MouseUp aren't synthesized that way and reliably report e.Button for
            // any button.
            foreach (Button v in con) {
                v.Font = new Font(v.Font.FontFamily, 7F, FontStyle.Bold);
                v.ForeColor = Color.Black;
                v.TextAlign = ContentAlignment.BottomRight;
                v.MouseUp += new MouseEventHandler(conBtnMouseClick);
                v.MouseEnter += new EventHandler(conBtnMouseEnter);
                v.MouseLeave += new EventHandler(conBtnMouseLeave);
                SetEmptySlotTooltip(v);
            }

            // Gyro outputs are now selected independently in Controller Profiles. Keep the old
            // key in App.config only as a one-time compatibility hint for legacy mappings; it is
            // no longer a runtime setting and should not be editable here.
            displayedConfigKeys = ConfigurationManager.AppSettings.AllKeys
                .Where(key => key != "GyroToJoyOrMouse" &&
                              !ProfileOwnedConfigKeys.Contains(key) &&
                              !ApplicationSettings.IsGlobalOption(key))
                .ToArray();
            Size childSize = new Size(150, 20);
            for (int i = 0; i != displayedConfigKeys.Length; i++) {
                settingsTable.RowCount++;
                settingsTable.Controls.Add(new Label() { Text = displayedConfigKeys[i], TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, Size = childSize }, 0, i);

                var value = ConfigurationManager.AppSettings[displayedConfigKeys[i]];
                Control childControl;
                if (value == "true" || value == "false") {
                    // MouseClick is correct here - a click on a checkbox already IS the new
                    // value, nothing to wait for.
                    childControl = new DarkCheckBox() {
                        Checked = Boolean.Parse(value),
                        Size = childSize,
                        BackColor = Color.Transparent,
                    };
                    childControl.MouseClick += cbBox_Changed;
                } else {
                    // Leave, not MouseClick - a text field's new value only exists once the user
                    // has actually finished typing it, not the instant they click into the box
                    // (which fires with whatever text was already sitting there, before any of
                    // the edit happens). MouseClick here meant typing a new value and tabbing/
                    // clicking away never saved it at all - cbBox_Changed only ever saw the old
                    // text, from that very first click.
                    childControl = new TextBox() { Text = value, Size = childSize };
                    childControl.Leave += cbBox_Changed;
                }

                settingsTable.Controls.Add(childControl, 1, i);
            }
        }

        private bool isExiting = false;

        private void HideToTray() {
            if (isExiting) return;
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.BalloonTipText = "Double click the tray icon to maximise!";
            notifyIcon.ShowBalloonTip(0);
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void ShowFromTray() {
            if (isExiting) return;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Icon = Properties.Resources.betterjoyforcemu_icon;
            notifyIcon.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e) {
            if (isExiting) return;
            if (this.WindowState == FormWindowState.Minimized) {
                HideToTray();
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
            if (isExiting) return;
            ShowFromTray();
        }

        private void MainForm_Load(object sender, EventArgs e) {
            // BetterJoy always runs as the Windows Service now - this window is a pure status/
            // control client, never a controller owner itself. Try connecting first (no SCM
            // pre-check gating it - a service that's genuinely up answers regardless of what the
            // SCM reports at this exact instant), and only consult IsBetterJoyServiceRunning()
            // afterward, purely to shape the error message if that connection attempt failed.
            // Config.Init() normally runs inside Program.Start() (see its comment there for
            // why) - remote mode never calls that, so without this, Config.variables stays
            // completely empty and every Config.Value(...) lookup returns "". Controller Profiles
            // (Reassign.GetPrettyName) doesn't guard against that, so it crashed with
            // ArgumentOutOfRangeException the moment anyone opened it in remote mode.
            Config.Init(CalibrationState.CaliData, CalibrationState.StickCaliData, CalibrationState.Stick2CaliData);

            // Add Controllers/blacklist (_3rdPartyControllers dialog) reads/edits these two
            // in-memory lists - normally populated by Program.Start()'s GUI branch, which
            // never runs here. Load them the same way headless mode does, so the dialog
            // isn't working from an empty list.
            _3rdPartyControllers.LoadIntoProgramLists();

            if (!AppPaths.ServiceModeEnabled)
                AppendTextBox("Config isn't synced with the service yet - settings/remap changes made here won't reach it until you use \"Sync Config with Service\".\r\n");

            ServiceControlClient initialClient = new ServiceControlClient();
            if (TryConnectWithRetries(initialClient)) {
                AdoptServiceClient(initialClient, "Connected to the BetterJoy service - it owns the controllers; this window shows live status only.\r\n");
            } else {
                initialClient.Dispose();
                AppendTextBox(IsBetterJoyServiceRunning()
                    ? "The BetterJoy service is running, but its status connection is not ready yet. Waiting to reconnect...\r\n"
                    : "The BetterJoy service is not reachable yet. Waiting to reconnect...\r\n");
                RenderSnapshot(new List<ControllerRecord>());
                StartServiceReconnectLoop();
            }

            console.Visible = !Boolean.Parse(ConfigurationManager.AppSettings["HideStatus"]);
            if (!console.Visible) {
                // Close up the gap console leaves behind by pulling the settings gear/version
                // label and profile button up into its row instead of leaving them down where
                // console used to end.
                // console.Top itself is the stable anchor here - it doesn't change when Visible
                // is toggled off. The form is AutoSize/GrowAndShrink, so it naturally shrinks to
                // fit afterward - and grows back to fit rightPanel when settings gets toggled
                // open later, since that's sized independently of this.
                btn_settings.Top = console.Top;
                version_lbl.Top = console.Top;
            }

            // Keep the two utility icons as one unit after the HideStatus layout adjustment (and
            // after WinForms DPI scaling). The Designer coordinates alone leave Profiles behind
            // at the old bottom row when the status console is hidden.
            btn_controllerProfiles.Location = new Point(
                btn_settings.Left - btn_controllerProfiles.Width - 6,
                btn_settings.Top);

            if (Boolean.Parse(ConfigurationManager.AppSettings["StartInTray"])) {
                HideToTray();
            } else {
                ShowFromTray();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            ExitApplication();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e) {
            ExitApplication();
        }

        // Single exit path, guarded against being entered twice (Close() re-enters via
        // MainForm_FormClosing). MainForm never owns the controllers itself - the service keeps
        // running independently of this window closing, and the pipe closes with the process -
        // so there's nothing of ours to tear down beyond the desktop-input backend.
        private void ExitApplication() {
            if (isExiting) return;
            isExiting = true;

            notifyIcon.Visible = false; // remove the tray icon immediately so no further tray messages can reach it

            desktopInput.Dispose();
            serviceReconnectTimer?.Stop();
            serviceReconnectTimer?.Dispose();
            serviceClient?.Dispose();
            Environment.Exit(0);
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            donationLink.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        public void AppendTextBox(string value) { // https://stackoverflow.com/questions/519233/writing-to-a-textbox-from-another-thread
            if (InvokeRequired) {
                this.Invoke(new Action<string>(AppendTextBox), new object[] { value });
                return;
            }
            console.AppendText(value);
        }

        // GUI and service-helper modes share the same optional virtual-HID backend so elevated
        // windows behave identically regardless of which process currently owns the controllers.
        public void SimulateKeyClick(int keyCode) {
            desktopInput.KeyClick(keyCode);
        }

        public void SimulateKeyHold(int keyCode) {
            desktopInput.KeyHold(keyCode);
        }

        public void SimulateKeyRelease(int keyCode) {
            desktopInput.KeyRelease(keyCode);
        }

        public void SimulateDesktopAction(int actionCode) {
            desktopInput.ActionClick((DesktopInputAction)actionCode);
        }

        public void SimulateButtonClick(int buttonCode) {
            desktopInput.ButtonClick(buttonCode);
        }

        public void SimulateButtonHold(int buttonCode) {
            desktopInput.ButtonHold(buttonCode);
        }

        public void SimulateButtonRelease(int buttonCode) {
            desktopInput.ButtonRelease(buttonCode);
        }

        public void SimulateMoveTo(int x, int y) {
            desktopInput.MoveTo(x, y);
        }

        public void SimulateMoveBy(int dx, int dy) {
            desktopInput.MoveBy(dx, dy);
        }

        public void SimulateCursorMoveBy(int dx, int dy) {
            desktopInput.CursorMoveBy(dx, dy);
        }

        public void SimulateWrappedCursorMoveBy(int dx, int dy) {
            desktopInput.WrappedCursorMoveBy(dx, dy);
        }

        public void SimulateMoveToScreenCenter() {
            desktopInput.MoveToScreenCenter();
        }

        public void SimulateScroll(bool up) {
            desktopInput.Scroll(up);
        }

        // No-op: BetterJoy always runs as the service now (see IsBetterJoyServiceRunning's
        // comment above) - MainForm never actually owns a live Controller that could call this.
        public bool StartBluetoothAudioCapture(int padId, string endpointId,
            BluetoothAudioCodec codec = BluetoothAudioCodec.DualShock4Sbc) { return false; }
        public void StopBluetoothAudioCapture(int padId) { }
        public bool StartUsbAudioLoopback(int padId, string sourceEndpointId,
            string targetEndpointId, string targetNameHint, int volumePercent) { return false; }
        public void StopUsbAudioLoopback(int padId) { }

        // Only ever consulted after a direct pipe-connect attempt has already failed, to decide
        // which error message to show - "not installed/not running, go start it" vs. "reported
        // Running but unreachable, something's interfering." There is no local fallback either
        // answer leads to; BetterJoy always runs as the service now.
        private static bool IsBetterJoyServiceRunning() {
            try {
                using (var sc = new ServiceController("BetterJoy2")) {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            } catch {
                return false; // not installed, access denied, etc.
            }
        }

        // A handful of quick retries covers the narrow legitimate race (the service was
        // reported Running an instant before its control pipe is actually ready to accept -
        // though in practice StartControlServer runs synchronously before OnStart even returns,
        // so this window is mostly theoretical) without turning a startup that's ultimately
        // going to fail into a long hang.
        private static bool TryConnectWithRetries(ServiceControlClient client, int attempts = 3, int perAttemptTimeoutMs = 1000, int delayBetweenAttemptsMs = 500) {
            for (int attempt = 0; attempt < attempts; attempt++) {
                if (client.Connect(perAttemptTimeoutMs))
                    return true;

                if (attempt < attempts - 1)
                    System.Threading.Thread.Sleep(delayBetweenAttemptsMs);
            }
            return false;
        }

        private void AdoptServiceClient(ServiceControlClient client, string message) {
            serviceReconnectTimer?.Stop();
            serviceReconnectInProgress = false;
            serviceReconnectAttempts = 0;

            ServiceControlClient oldClient = serviceClient;
            serviceClient = client;
            WireServiceClientEvents(client);
            oldClient?.Dispose();

            AppendTextBox(message);
            client.RequestSnapshot();
        }

        private void StartServiceReconnectLoop() {
            if (isExiting)
                return;

            serviceClient?.Dispose();
            serviceClient = null;

            if (serviceReconnectTimer == null) {
                serviceReconnectTimer = new Timer {
                    Interval = ServiceReconnectIntervalMs
                };
                serviceReconnectTimer.Tick += ServiceReconnectTimer_Tick;
            }

            if (!serviceReconnectTimer.Enabled)
                serviceReconnectTimer.Start();
        }

        private void ServiceReconnectTimer_Tick(object sender, EventArgs e) {
            if (isExiting || serviceReconnectInProgress)
                return;

            serviceReconnectInProgress = true;
            ServiceControlClient client = new ServiceControlClient();
            try {
                if (client.Connect(ServiceReconnectPipeTimeoutMs)) {
                    AdoptServiceClient(client, "Reconnected to the BetterJoy service.\r\n");
                    return;
                }

                client.Dispose();
                serviceReconnectAttempts++;
                if (serviceReconnectAttempts == 1 || serviceReconnectAttempts % 10 == 0) {
                    AppendTextBox(IsBetterJoyServiceRunning()
                        ? "Still waiting for the BetterJoy service status connection...\r\n"
                        : "Still waiting for the BetterJoy service to start...\r\n");
                }
            } finally {
                serviceReconnectInProgress = false;
            }
        }

        // All ServiceControlClient events fire from a background read thread - each handler
        // marshals onto the UI thread itself (RenderSnapshot does its own Invoke check; the
        // rest are simple enough to wrap inline here).
        private void WireServiceClientEvents(ServiceControlClient client) {
            client.SnapshotReceived += RenderSnapshot;

            client.CalibrationStarted += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text = "Calibration started." + "\r\n";
                if (calibDialog == null) {
                    calibDialog = new CalibrationDialog();
                    calibDialog.ButtonClicked += OnRemoteCalibButtonClicked;
                    calibDialog.Show(this);
                }
            }));

            // Step name/instruction/UI mode all come from the service (HeadlessJoyconHost's
            // StartCalibration) - calibDialog here is a passive renderer, same as it is for the
            // local flow, just fed over the pipe instead of driven by a local state machine. The
            // service sends one message per second for the gyro countdown, so there's no need
            // for a local cosmetic timer the way an earlier version of this had - the displayed
            // number is always exactly what the service just said.
            client.CalibrationStep += step => this.Invoke(new MethodInvoker(delegate {
                if (calibDialog == null) {
                    calibDialog = new CalibrationDialog();
                    calibDialog.ButtonClicked += OnRemoteCalibButtonClicked;
                    calibDialog.Show(this);
                }
                calibDialog.SetStep(step.StepNumber, step.TotalSteps, step.StepName);
                calibDialog.SetInstruction(step.Instruction);
                remoteCalibPadId = step.PadId;

                switch (step.UiMode) {
                    case CalibStepUiMode.Start:
                        calibDialog.ShowStartPrompt();
                        break;
                    case CalibStepUiMode.Done:
                        calibDialog.ShowDonePrompt();
                        break;
                    case CalibStepUiMode.Countdown:
                        calibDialog.ShowCountdown(step.Count);
                        break;
                }
            }));

            client.CalibrationComplete += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration completed!!!\r\n";
                RestoreCalibrateIcon();
                CloseRemoteCalibDialog("Calibration complete!");
            }));

            client.CalibrationFailed += padId => this.Invoke(new MethodInvoker(delegate {
                console.Text += "\r\nCalibration failed - was the controller disconnected?\r\n";
                RestoreCalibrateIcon();
                CloseRemoteCalibDialog("Failed - was the controller disconnected?");
            }));

            client.Disconnected += () => this.Invoke(new MethodInvoker(delegate {
                if (client != serviceClient || isExiting)
                    return;

                AppendTextBox("Lost connection to the BetterJoy service.\r\n");
                CloseRemoteCalibDialog("Lost connection to the service.");
                RenderSnapshot(new List<ControllerRecord>());
                StartServiceReconnectLoop();
            }));

            // Not Invoke-wrapped, matching the prior inline behavior in the local
            // NotifyLowBattery this replaces - NotifyIcon operations aren't Control-handle-affine
            // the way Buttons are, so the fire-and-forget ServiceControlClient read thread can
            // call this directly.
            client.LowBattery += info => {
                notifyIcon.Visible = true;
                notifyIcon.BalloonTipText = String.Format("Controller {0} ({1}) - low battery notification!",
                    info.PadId, ControllerKindLabel(info.Kind));
                notifyIcon.ShowBalloonTip(0);
            };
        }

        private int remoteCalibPadId;

        // The dialog's one button always just reports "clicked" back to the service as
        // CalibrationReady, whether that means Start or Done - the service knows which one it
        // asked for (it's the only thing that can be pending), same as it's the sole source of
        // truth for what the button currently means (see the CalibrationStep handler above).
        private void OnRemoteCalibButtonClicked() {
            if (!HasServiceConnection())
                return;

            calibDialog.HidePrompt();
            serviceClient.CalibrationReady(remoteCalibPadId);
        }

        private bool HasServiceConnection() {
            if (serviceClient != null && serviceClient.IsConnected)
                return true;

            AppendTextBox("Waiting for the BetterJoy service connection to come back.\r\n");
            StartServiceReconnectLoop();
            return false;
        }

        private void CloseRemoteCalibDialog(string message) {
            CalibrationDialog dialogToClose = calibDialog;
            calibDialog = null;
            if (dialogToClose == null)
                return;

            dialogToClose.ShowResult(message);
            Timer closeTimer = new Timer { Interval = 1500 };
            closeTimer.Tick += (s, e) => {
                closeTimer.Stop();
                closeTimer.Dispose();
                dialogToClose.Close();
            };
            closeTimer.Start();
        }

        // Full re-render from a snapshot rather than incremental diffing against previous state
        // - simpler and self-healing, and snapshots only arrive when something actually changed
        // (see HeadlessJoyconHost.BroadcastSnapshot), not continuously.
        private void RenderSnapshot(List<ControllerRecord> records) {
            if (InvokeRequired) {
                this.Invoke(new Action<List<ControllerRecord>>(RenderSnapshot), new object[] { records });
                return;
            }

            lastControllerSnapshot = records == null
                ? new List<ControllerRecord>()
                : new List<ControllerRecord>(records);

            foreach (Button b in con) {
                b.Tag = null;
                b.BackColor = Color.FromArgb(0x00, SystemColors.Control);
                b.BackgroundImage = SlotIconCross;
                b.Text = String.Empty;
                SetEmptySlotTooltip(b);
            }

            // BuildSnapshot (HeadlessJoyconHost.cs) sends exactly one record per virtual
            // controller - a joined pair's passive half is never included, so records maps 1:1
            // onto slots with no de-duplication needed here.
            int slotIndex = 0;

            foreach (ControllerRecord record in lastControllerSnapshot) {
                if (slotIndex >= con.Count)
                    continue;

                Button button = con[slotIndex];
                bool isPair = record.OtherPadId >= 0;

                if (isPair) {
                    button.BackgroundImage = ComposeJoinedIcon(button.Width, button.Height);
                } else {
                    button.BackgroundImage = IconFor(record);
                }

                button.Tag = (int)record.PadId;
                button.BackColor = record.Battery >= 0 ? Controller.GetBatteryColor(record.Battery) : Color.FromArgb(0x00, SystemColors.Control);
                button.Text = record.BatteryPercent >= 0 ? record.BatteryPercent + "%" : String.Empty;
                SetConnectionTooltip(button,
                    !isPair && (record.Kind == ControllerKind.Pro ||
                                record.Kind == ControllerKind.DualSense ||
                                record.Kind == ControllerKind.DualShock4),
                    record);

                // Mirrors AssignJoyconToSlot's loc-button wiring - unsubscribe first since this
                // whole method reruns on every snapshot push, unlike AssignJoyconToSlot which
                // only runs once per new connection.
                Button locButton = loc[slotIndex];
                locButton.Tag = button;
                locButton.Click -= locBtnClickAsync;
                locButton.Click += locBtnClickAsync;

                slotIndex++;
            }
        }

        private Bitmap IconFor(ControllerRecord record) {
            switch (record.Kind) {
                case ControllerKind.Pro: return SlotIconPro;
                case ControllerKind.DualSense: return SlotIconDualSense;
                case ControllerKind.DualShock4: return SlotIconDualShock4;
                case ControllerKind.Snes: return SlotIconSnes;
                case ControllerKind.N64: return SlotIconN64;
                case ControllerKind.Left:
                    return record.IsVertical ? SlotIconJoyconLeft : SlotIconJoyconLeftSideways;
                default:
                    return record.IsVertical ? SlotIconJoyconRight : SlotIconJoyconRightSideways;
            }
        }

        private void StartRemoteCalibrate(Button button) {
            if (!(button.Tag is int)) {
                RestoreCalibrateIcon();
                return;
            }

            int padId = (int)button.Tag;
            console.Text = "Requesting calibration from service...";
            if (!HasServiceConnection()) {
                RestoreCalibrateIcon();
                return;
            }
            serviceClient.StartCalibration(padId);
            // calibrateIconButton keeps flashing until CalibrationComplete/Failed arrives (see
            // WireServiceClientEvents) - not cleared immediately here.
        }

        public void locBtnClickAsync(object sender, EventArgs e) {
            Button bb = sender as Button;

            if (bb.Tag.GetType() == typeof(Button)) {
                Button button = bb.Tag as Button;
                if (button.Tag is int && HasServiceConnection())
                    serviceClient.TestRumble((int)button.Tag);
            }
        }

        // Left click on any controller (Pro or Joycon) opens Controller Profiles; right click on a
        // Joycon joins/splits it instead (also triggered by double-clicking the stick in
        // hardware, via JoinOrSplitJoycon directly - see Joycon.cs). Left click on an empty
        // slot (Tag == null) opens Add Controllers instead.
        public void conBtnMouseClick(object sender, MouseEventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null) {
                if (e.Button == MouseButtons.Left)
                    btn_open3rdP_Click(sender, e);
                return;
            }

            if (!(button.Tag is int))
                return;

            if (e.Button == MouseButtons.Right) {
                HandlePossibleOrientationDoubleClick(button);
            } else if (e.Button == MouseButtons.Left) {
                if (allowCalibration) {
                    HandlePossibleDoubleClick(button);
                } else {
                    btn_reassign_open_Click(sender, e);
                }
            }
        }

        // Left-click disambiguation between "controller profiles" (single click) and "calibrate"
        // (double click), only relevant when AllowCalibration is on - otherwise every left
        // click opens Controller Profiles immediately, same as before this existed. A single click is
        // held for clickTimer's interval to see if a second one follows on the same button
        // before committing to it - a plain WinForms DoubleClick event isn't usable here since
        // it fires in addition to, not instead of, the Click for the first press. While waiting,
        // and for the whole calibration process if a double click is confirmed, the button
        // flashes the calibrate icon - restored by StartRemoteCalibrate/OnRemoteCalibButtonClicked
        // once calibration either fails to start or actually finishes (see RestoreCalibrateIcon
        // call sites).
        private Button calibrateIconButton = null;
        private Image calibrateIconOriginalImage = null;

        private void HandlePossibleDoubleClick(Button button) {
            if (clickTimer.Enabled && calibrateIconButton == button) {
                clickTimer.Stop();
                StartRemoteCalibrate(button);
            } else {
                clickTimer.Stop();
                RestoreCalibrateIcon();

                calibrateIconButton = button;
                calibrateIconOriginalImage = button.BackgroundImage;
                button.BackgroundImage = SlotIconCalibrate;

                clickTimer.Start();
            }
        }

        private void ClickTimer_Tick(object sender, EventArgs e) {
            clickTimer.Stop();
            if (calibrateIconButton != null) {
                Button button = calibrateIconButton;
                RestoreCalibrateIcon();
                btn_reassign_open_Click(button, EventArgs.Empty);
            }
        }

        private void RestoreCalibrateIcon() {
            if (calibrateIconButton != null)
                calibrateIconButton.BackgroundImage = calibrateIconOriginalImage;

            calibrateIconButton = null;
            calibrateIconOriginalImage = null;
        }

        // Right-click disambiguation, mirroring HandlePossibleDoubleClick above: a single
        // right-click still joins/splits normally, just deferred by rightClickTimer's interval
        // (previously instant) so a following second click on the same slot can be detected. A
        // genuine double right-click forces this Joycon to self-pair (vertical orientation) even
        // when other Joycons are connected and a plain single click would otherwise have searched
        // for one of them to join with instead - see JoinOrSplitJoycon's forceSelfPair parameter.
        private Button orientationClickButton = null;

        private void HandlePossibleOrientationDoubleClick(Button button) {
            if (rightClickTimer.Enabled && orientationClickButton == button) {
                rightClickTimer.Stop();
                orientationClickButton = null;
                ExecuteJoinOrSplit(button, forceSelfPair: true);
            } else {
                rightClickTimer.Stop();
                orientationClickButton = button;
                rightClickTimer.Start();
            }
        }

        private void RightClickTimer_Tick(object sender, EventArgs e) {
            rightClickTimer.Stop();
            if (orientationClickButton != null) {
                Button button = orientationClickButton;
                orientationClickButton = null;
                ExecuteJoinOrSplit(button, forceSelfPair: false);
            }
        }

        private void ExecuteJoinOrSplit(Button button, bool forceSelfPair) {
            if (button.Tag is int padId) {
                if (!HasServiceConnection())
                    return;

                if (forceSelfPair)
                    serviceClient.ForceSelfPair(padId);
                else
                    serviceClient.JoinOrSplit(padId);
            }
        }

        // Empty slots swap their red X for a plus icon on hover, as a visual hint that
        // clicking opens Add Controllers (see conBtnMouseClick).
        public void conBtnMouseEnter(object sender, EventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null)
                button.BackgroundImage = SlotIconPlus;
        }

        public void conBtnMouseLeave(object sender, EventArgs e) {
            Button button = sender as Button;
            if (button.Tag == null)
                button.BackgroundImage = SlotIconCross;
        }

        public void SetConnectionTooltip(Button button, bool isPro, ControllerRecord record) {
            string tip = isPro ? "Left-click to edit controller profile" : "Right-click to split / left-click to edit controller profile";
            if (allowCalibration)
                tip += ", double click to calibrate";
            if (record.BatteryPercent >= 0)
                tip = BatteryStatusText(record) + "\r\n" + tip;
            SetTooltipIfChanged(button, tip);
        }

        private static string BatteryStatusText(ControllerRecord record) {
            string state;
            switch (record.BatteryStatus) {
                case ControllerBatteryStatus.Charging: state = "charging"; break;
                case ControllerBatteryStatus.Full: state = "full"; break;
                case ControllerBatteryStatus.NotCharging: state = "not charging"; break;
                case ControllerBatteryStatus.Discharging: state = "discharging"; break;
                default: state = "status unknown"; break;
            }
            return "Battery: " + record.BatteryPercent + "% (" + state + ")";
        }

        public void SetEmptySlotTooltip(Button button) {
            SetTooltipIfChanged(button, "Add a controller");
        }

        private void SetTooltipIfChanged(Control control, string tip) {
            if (btnTip.GetToolTip(control) != tip)
                btnTip.SetToolTip(control, tip);
        }

        // jc_left.png/jc_right.png are drawn as literal left/right halves of one combined-pair
        // silhouette (their flat edges meet in the middle), so cropping each to its actual
        // artwork (they're padded within a much larger transparent square canvas), scaling by
        // height only to keep proportions matching the other slot icons, and flushing each
        // half against the shared center seam recreates the combined shape within a single
        // slot - edges touching in the middle, matching margin on the outer edges - instead of
        // either spanning two slots or looking stretched/warped filling the box edge to edge.
        public Bitmap ComposeJoinedIcon(int width, int height) {
            string cacheKey = width.ToString() + "x" + height.ToString();
            Bitmap cached;
            if (joinedIconCache.TryGetValue(cacheKey, out cached))
                return cached;

            Bitmap leftSource = SlotIconJoyconLeft;
            Bitmap rightSource = SlotIconJoyconRight;
            Rectangle leftBounds = SlotIconJoyconLeftBounds;
            Rectangle rightBounds = SlotIconJoyconRightBounds;

            const float fit = 0.58f; // leaves margin similar to the other slot icons, which
                                      // have padding baked into their own source canvas
            int halfWidth = width / 2;
            float targetHeight = height * fit;

            const int seamGap = 1; // small visible gap so the two halves read as distinct icons

            var composite = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(composite)) {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                DrawHalfFlushToSeam(g, leftSource, leftBounds, 0, halfWidth, height, targetHeight, flushRight: true, seamGap: 0);
                DrawHalfFlushToSeam(g, rightSource, rightBounds, halfWidth, width - halfWidth, height, targetHeight, flushRight: false, seamGap: seamGap);
            }
            joinedIconCache[cacheKey] = composite;
            return composite;
        }

        private static void DrawHalfFlushToSeam(Graphics g, Bitmap source, Rectangle sourceBounds, int xOffset, int halfWidth, int height, float targetHeight, bool flushRight, int seamGap) {
            float scale = targetHeight / sourceBounds.Height;
            int destWidth = Math.Max(1, (int)(sourceBounds.Width * scale));
            int destHeight = Math.Max(1, (int)(sourceBounds.Height * scale));
            int destX = flushRight ? xOffset + halfWidth - destWidth : xOffset + seamGap;
            int destY = (height - destHeight) / 2;

            g.DrawImage(source, new Rectangle(destX, destY, destWidth, destHeight), sourceBounds, GraphicsUnit.Pixel);
        }

        // Scans a bitmap's alpha channel for the tightest rectangle containing its non-
        // transparent artwork, so ComposeJoinedIcon can crop out the surrounding padding
        // instead of relying on hardcoded pixel coordinates tied to one specific asset.
        private static Rectangle GetOpaqueBounds(Bitmap bitmap) {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try {
                int stride = data.Stride;
                byte[] pixels = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
                for (int y = 0; y < bitmap.Height; y++) {
                    for (int x = 0; x < bitmap.Width; x++) {
                        byte alpha = pixels[y * stride + x * 4 + 3];
                        if (alpha > 10) {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX < minX || maxY < minY)
                    return new Rectangle(0, 0, bitmap.Width, bitmap.Height);

                return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            } finally {
                bitmap.UnlockBits(data);
            }
        }

        // Retired along with local controller ownership: MainForm never holds a live Controller
        // reference anymore (Program.mgr is always null in this process - see MainForm_Load),
        // so these IJoyconHost members are structurally required but never actually invoked.
        // Kept as literal no-ops (matching RefreshControllerState's existing pattern) rather than
        // removed, since MainForm still implements IJoyconHost for now - dropping the interface
        // entirely is a separate follow-up commit once the rest of this is verified stable.
        public void CollapseJoinedPair(JoyconController left, JoyconController right) { }

        public void AssignSlot(Controller controller) { }

        public void HandleJoyconDropped(Controller dropped, JoyconController survivingPartner) { }

        // Never invoked in this process - MainForm has no live Controller to call this with (see
        // the comment above). The actual balloon is now shown from WireServiceClientEvents'
        // LowBattery handler, fed over the service protocol instead (see ControllerKindLabel).
        public void NotifyLowBattery(Controller controller) { }

        public void UpdateBatteryColor(Controller controller) { }

        public void HandleCalibrationConfirm(Controller controller) { }

        public void RefreshOrientationIcon(JoyconController v) { }

        public void JoinOrSplitJoycon(JoyconController v, bool forceSelfPair = false) { }

        // Extracted from the old local NotifyLowBattery's balloon-tip text, kept for Phase 3 to
        // reuse once a low-battery event is actually pushed to this window over the service
        // protocol - not called from anywhere yet.
        private static string ControllerKindLabel(ControllerKind kind) {
            switch (kind) {
                case ControllerKind.DualSense: return "DualSense Controller";
                case ControllerKind.DualShock4: return "DualShock 4 Controller";
                case ControllerKind.Snes: return "SNES Controller";
                case ControllerKind.N64: return "N64 Controller";
                case ControllerKind.Pro: return "Pro Controller";
                default: return kind == ControllerKind.Left ? "Joycon Left" : "Joycon Right";
            }
        }

        private void btn_open3rdP_Click(object sender, EventArgs e) {
            _3rdPartyControllers partyForm = new _3rdPartyControllers();
            partyForm.ShowDialog();
        }

        private void settingsApply_Click(object sender, EventArgs e) {
            var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            var settings = configFile.AppSettings.Settings;

            for (int row = 0; row < displayedConfigKeys.Length; row++) {
                var valCtl = settingsTable.GetControlFromPosition(1, row);
                var KeyCtl = settingsTable.GetControlFromPosition(0, row).Text;

                if (valCtl is CheckBox && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                } else if (valCtl.GetType() == typeof(ComboBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((ComboBox)valCtl).SelectedItem.ToString();
                } else if (valCtl.GetType() == typeof(TextBox) && settings[KeyCtl] != null) {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }
            }

            try {
                configFile.Save(ConfigurationSaveMode.Modified);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings.\r\n");
            }

            Application.Restart();
            Environment.Exit(0);
        }

        // Copies the GUI's per-user config/calibration/controller lists to the shared location
        // (%ProgramData%\BetterJoy) a Windows Service uses (see AppPaths.EnableServiceMode),
        // and switches this and future GUI launches to read/write there too - otherwise settings
        // changed here would never be seen by a running service at all, since it runs as SYSTEM
        // and has its own separate profile.
        private void btn_enableServiceMode_Click(object sender, EventArgs e) {
            if (AppPaths.ServiceModeEnabled) {
                MessageBox.Show("Configuration is already shared with the Windows Service.", "BetterJoy2");
                return;
            }

            DialogResult result = MessageBox.Show(
                "This copies your current settings, calibration data, and controller lists to a " +
                "shared location (%ProgramData%\\BetterJoy) so a Windows Service running BetterJoy " +
                "can use the same configuration. Continue?",
                "Sync Config with Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            try {
                AppPaths.EnableServiceMode();
                btn_enableServiceMode.Text = "Config Synced with Service";
                btn_enableServiceMode.Enabled = false;

                MessageBox.Show(
                    "Done - restart BetterJoy for this to take effect. If the Windows Service " +
                    "isn't installed yet, run this from an elevated PowerShell/cmd:\r\n\r\n" +
                    "sc create BetterJoy2 binPath= \"\\\"" + Application.ExecutablePath + "\\\" -service\" start= auto",
                    "BetterJoy2");
            } catch (Exception ex) {
                MessageBox.Show("Failed to sync configuration: " + ex.Message, "BetterJoy2");
            }
        }

        private void btn_settings_Click(object sender, EventArgs e) {
            rightPanel.Visible = !rightPanel.Visible;
        }

        private void cbBox_Changed(object sender, EventArgs e) {
            var coord = settingsTable.GetPositionFromControl(sender as Control);

            var valCtl = settingsTable.GetControlFromPosition(coord.Column, coord.Row);
            var KeyCtl = settingsTable.GetControlFromPosition(coord.Column - 1, coord.Row).Text;

            try {
                string value = null;
                if (valCtl is CheckBox) {
                    value = ((CheckBox)valCtl).Checked.ToString().ToLowerInvariant();
                } else if (valCtl.GetType() == typeof(ComboBox)) {
                    value = ((ComboBox)valCtl).SelectedItem.ToString();
                } else if (valCtl.GetType() == typeof(TextBox)) {
                    value = ((TextBox)valCtl).Text.ToLowerInvariant();
                }

                if (value != null)
                    ApplicationSettings.SetValue(KeyCtl, value);
            } catch (ConfigurationErrorsException) {
                AppendTextBox("Error writing app settings\r\n");
                Trace.WriteLine(String.Format("rw {0}, column {1}, {2}, {3}", coord.Row, coord.Column, sender.GetType(), KeyCtl));
            }
        }
        private CalibrationDialog calibDialog;

        private void btn_reassign_open_Click(object sender, EventArgs e) {
            // Reassign uses serviceClient to relay controller-button presses for its
            // "left-click then press" auto-detect, since this process never has any Joycon
            // instances of its own to poll (the service owns the hardware).
            if (!HasServiceConnection())
                return;

            string preferredProfileId = null;
            Button sourceButton = sender as Button;
            if (sourceButton != null && sourceButton.Tag is int) {
                int padId = (int)sourceButton.Tag;
                ControllerRecord record = lastControllerSnapshot.FirstOrDefault(r => r.PadId == padId);
                preferredProfileId = record.ProfileId;
            }

            Reassign mapForm = new Reassign(serviceClient, lastControllerSnapshot, preferredProfileId);
            mapForm.ShowDialog();
        }

        // The headless host uses this hook to push a fresh service snapshot after a USB
        // handshake resolves the real per-unit MAC; no additional main-window rendering is
        // needed here.
        public void RefreshControllerState() { }
    }
}
