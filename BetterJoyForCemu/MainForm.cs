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
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    public partial class MainForm : Form {
        public bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
        public List<Button> con, loc;
        public bool calibrate;
        public List<KeyValuePair<string, float[]>> caliData;
        private Timer countDown;
        private int count;
        public List<int> xG, yG, zG, xA, yA, zA;
        public bool shakeInputEnabled = Boolean.Parse(ConfigurationManager.AppSettings["EnableShakeInput"]);
        public float shakeSesitivity = float.Parse(ConfigurationManager.AppSettings["ShakeInputSensitivity"]);
        public float shakeDelay = float.Parse(ConfigurationManager.AppSettings["ShakeInputDelay"]);

        public enum NonOriginalController : int {
            Disabled = 0,
            DefaultCalibration = 1,
            ControllerCalibration = 2,
        }

        public MainForm() {
            xG = new List<int>(); yG = new List<int>(); zG = new List<int>();
            xA = new List<int>(); yA = new List<int>(); zA = new List<int>();
            caliData = new List<KeyValuePair<string, float[]>> {
                new KeyValuePair<string, float[]>("0", new float[6] {0,0,0,-710,0,0})
            };

            InitializeComponent();

            Font uiFont = I18n.UiFont;
            if (uiFont != null)
                this.Font = uiFont;

            if (!allowCalibration)
                AutoCalibrate.Hide();

            con = new List<Button> { con1, con2, con3, con4 };
            loc = new List<Button> { loc1, loc2, loc3, loc4 };

            BuildSettingsTable();
        }

        private static readonly string[] groupedSettings = {
            "MotionServer", "IP", "Port", "EnableRumble", "LowFreqRumble", "HighFreqRumble", "SwapAB", "SwapXY",
            "EnableShakeInput", "ShakeInputSensitivity", "ShakeInputDelay",
            "stick_cal", "deadzone", "StickScalingFactor", "stick2_cal", "deadzone2", "StickScalingFactor2",
            "GyroMouseSensitivityX", "GyroMouseSensitivityY", "GyroMouseInvertX", "GyroMouseInvertY", "GyroMouseDeadzone",
            "GyroStickSensitivityX", "GyroStickSensitivityY", "GyroStickInvertX", "GyroStickInvertY", "GyroStickDeadzone"
        };

        private void BuildSettingsTable() {
            List<string> keys = ConfigurationManager.AppSettings.AllKeys
                .Where(k => !groupedSettings.Contains(k))
                .ToList();

            // Row: Motion Server toggle
            settingsTable.Controls.Add(CreateSettingRow("MotionServer", CreateSettingValueControl("MotionServer")));

            // Row: IP and Port side by side
            var ipRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            ipRow.Controls.Add(new Label() { Text = I18n.Str("IpLabel"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            ipRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["IP"], Width = 70, Tag = "IP" });
            ipRow.Controls.Add(new Label() { Text = I18n.Str("PortLabel"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            ipRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["Port"], Width = 45, Tag = "Port" });
            settingsTable.Controls.Add(ipRow);

            // Row: Enable Rumble toggle
            settingsTable.Controls.Add(CreateSettingRow("EnableRumble", CreateSettingValueControl("EnableRumble")));

            // Row: Low / High frequency rumble side by side
            var rumbleRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            rumbleRow.Controls.Add(new Label() { Text = I18n.Str("Setting_LowFreqRumble"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            rumbleRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["LowFreqRumble"], Width = 50, Tag = "LowFreqRumble" });
            rumbleRow.Controls.Add(new Label() { Text = I18n.Str("Setting_HighFreqRumble"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            rumbleRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["HighFreqRumble"], Width = 50, Tag = "HighFreqRumble" });
            settingsTable.Controls.Add(rumbleRow);

            // Row: Swap A-B and Swap X-Y side by side
            var swapRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            swapRow.Controls.Add(new Label() { Text = I18n.Str("Setting_SwapAB"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            CheckBox swapAB = new CheckBox() { Checked = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]), AutoSize = true, Tag = "SwapAB" };
            swapAB.MouseClick += cbBox_Changed;
            swapRow.Controls.Add(swapAB);
            swapRow.Controls.Add(new Label() { Text = I18n.Str("Setting_SwapXY"), AutoSize = true, Padding = new Padding(6, 3, 6, 0) });
            CheckBox swapXY = new CheckBox() { Checked = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]), AutoSize = true, Tag = "SwapXY" };
            swapXY.MouseClick += cbBox_Changed;
            swapRow.Controls.Add(swapXY);
            settingsTable.Controls.Add(swapRow);

            // Row: Shake Input section
            settingsTable.Controls.Add(CreateSectionHeader(I18n.Str("Setting_ShakeInputTitle")));
            settingsTable.Controls.Add(CreateSettingRow("EnableShakeInput", CreateSettingValueControl("EnableShakeInput")));

            // Row: Shake sensitivity and delay side by side
            var shakeRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            shakeRow.Controls.Add(new Label() { Text = I18n.Str("Setting_ShakeInputSensitivity"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            shakeRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["ShakeInputSensitivity"], Width = 40, Tag = "ShakeInputSensitivity" });
            shakeRow.Controls.Add(new Label() { Text = I18n.Str("Setting_ShakeInputDelay"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            shakeRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["ShakeInputDelay"], Width = 40, Tag = "ShakeInputDelay" });
            settingsTable.Controls.Add(shakeRow);

            // Row: Left stick calibration section
            settingsTable.Controls.Add(CreateSectionHeader(I18n.Str("Setting_LeftStick")));
            settingsTable.Controls.Add(CreateSettingRow("stick_cal", CreateSettingValueControl("stick_cal")));
            var leftStickRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            leftStickRow.Controls.Add(new Label() { Text = I18n.Str("Setting_deadzone"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            leftStickRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["deadzone"], Width = 40, Tag = "deadzone" });
            leftStickRow.Controls.Add(new Label() { Text = I18n.Str("Setting_StickScalingFactor"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            leftStickRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["StickScalingFactor"], Width = 40, Tag = "StickScalingFactor" });
            settingsTable.Controls.Add(leftStickRow);

            // Row: Right stick calibration section
            settingsTable.Controls.Add(CreateSectionHeader(I18n.Str("Setting_RightStick")));
            settingsTable.Controls.Add(CreateSettingRow("stick2_cal", CreateSettingValueControl("stick2_cal")));
            var rightStickRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            rightStickRow.Controls.Add(new Label() { Text = I18n.Str("Setting_deadzone"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            rightStickRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["deadzone2"], Width = 40, Tag = "deadzone2" });
            rightStickRow.Controls.Add(new Label() { Text = I18n.Str("Setting_StickScalingFactor"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            rightStickRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings["StickScalingFactor2"], Width = 40, Tag = "StickScalingFactor2" });
            settingsTable.Controls.Add(rightStickRow);

            // Remaining settings; insert gyro groups right below AHRS Beta
            foreach (string k in keys) {
                settingsTable.Controls.Add(CreateSettingRow(k, CreateSettingValueControl(k)));
                if (k == "AHRS_beta") {
                    AddGyroAxisGroup("Setting_GyroToMouseTitle", "GyroMouse");
                    AddGyroAxisGroup("Setting_GyroToStickTitle", "GyroStick");
                }
            }
        }

        private FlowLayoutPanel CreateSettingRow(string key, Control valueControl) {
            var row = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            row.Controls.Add(new Label() { Text = I18n.Str("Setting_" + key), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            row.Controls.Add(valueControl);
            return row;
        }

        private FlowLayoutPanel CreateSectionHeader(string text) {
            var row = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            row.Controls.Add(new Label() { Text = text, AutoSize = true, Font = new Font(this.Font, FontStyle.Bold), Padding = new Padding(0, 2, 0, 0) });
            return row;
        }

        private void AddGyroAxisGroup(string titleKey, string keyPrefix) {
            settingsTable.Controls.Add(CreateSectionHeader(I18n.Str(titleKey)));

            // Sensitivity X [tb] Sensitivity Y [tb]
            var sensRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            sensRow.Controls.Add(new Label() { Text = I18n.Str("GyroSensX"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            sensRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings[keyPrefix + "SensitivityX"], Width = 50, Tag = keyPrefix + "SensitivityX" });
            sensRow.Controls.Add(new Label() { Text = I18n.Str("GyroSensY"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            sensRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings[keyPrefix + "SensitivityY"], Width = 50, Tag = keyPrefix + "SensitivityY" });
            settingsTable.Controls.Add(sensRow);

            // Invert X [chb] Invert Y [chb]
            var invRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            invRow.Controls.Add(new Label() { Text = I18n.Str("GyroInvX"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            invRow.Controls.Add(CreateCheckBox(keyPrefix + "InvertX"));
            invRow.Controls.Add(new Label() { Text = I18n.Str("GyroInvY"), AutoSize = true, Padding = new Padding(6, 3, 0, 0) });
            invRow.Controls.Add(CreateCheckBox(keyPrefix + "InvertY"));
            settingsTable.Controls.Add(invRow);

            // Deadzone [tb]
            var dzRow = new FlowLayoutPanel() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            dzRow.Controls.Add(new Label() { Text = I18n.Str("Setting_" + keyPrefix + "Deadzone"), AutoSize = true, Padding = new Padding(0, 3, 6, 0) });
            dzRow.Controls.Add(new TextBox() { Text = ConfigurationManager.AppSettings[keyPrefix + "Deadzone"], Width = 50, Tag = keyPrefix + "Deadzone" });
            settingsTable.Controls.Add(dzRow);
        }

        private CheckBox CreateCheckBox(string key) {
            CheckBox cb = new CheckBox() { Checked = Boolean.Parse(ConfigurationManager.AppSettings[key]), AutoSize = true, Tag = key };
            cb.MouseClick += cbBox_Changed;
            return cb;
        }

        private Control CreateSettingValueControl(string key) {
            var value = ConfigurationManager.AppSettings[key];
            Size childSize = new Size(120, 20);
            if (key == "Language") {
                ComboBox langCombo = CreateCombo(value,
                    new LangOption(I18n.Str("LangSystemDefault"), ""),
                    new LangOption(I18n.Str("LangEnglish"), "en"),
                    new LangOption(I18n.Str("LangSimplifiedChinese"), "zh-CN"));
                langCombo.Tag = key;
                return langCombo;
            } else if (key == "GyroToJoyOrMouse") {
                ComboBox gyroCombo = CreateCombo(value,
                    new LangOption(I18n.Str("GyroModeNone"), "none"),
                    new LangOption(I18n.Str("GyroModeJoyLeft"), "joy_left"),
                    new LangOption(I18n.Str("GyroModeJoyRight"), "joy_right"),
                    new LangOption(I18n.Str("GyroModeMouse"), "mouse"));
                gyroCombo.Tag = key;
                return gyroCombo;
            } else if (value == "true" || value == "false") {
                CheckBox check = new CheckBox() { Checked = Boolean.Parse(value), AutoSize = true, Tag = key };
                check.MouseClick += cbBox_Changed;
                return check;
            } else {
                TextBox text = new TextBox() { Text = value, Size = childSize, Tag = key };
                text.MouseClick += cbBox_Changed;
                return text;
            }
        }

        private ComboBox CreateCombo(string currentValue, params LangOption[] options) {
            var combo = new ComboBox() { Size = new Size(120, 20), DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(options);
            foreach (LangOption o in options) {
                if (o.Code == currentValue) {
                    combo.SelectedItem = o;
                    break;
                }
            }
            if (combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;
            combo.SelectedIndexChanged += cbBox_Changed;
            return combo;
        }

        public class LangOption {
            public string Display { get; set; }
            public string Code { get; set; }

            public LangOption(string display, string code) {
                Display = display;
                Code = code;
            }

            public override string ToString() {
                return Display;
            }
        }

        private void HideToTray() {
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.BalloonTipText = I18n.Str("TrayBalloonTip");
            notifyIcon.ShowBalloonTip(0);
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void ShowFromTray() {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Icon = Properties.Resources.betterjoyforcemu_icon;
            notifyIcon.Visible = false;
        }

        private void MainForm_Resize(object sender, EventArgs e) {
            if (this.WindowState == FormWindowState.Minimized) {
                HideToTray();
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
            ShowFromTray();
        }

        private void MainForm_Load(object sender, EventArgs e) {
            Config.Init(caliData);

            Program.Start();

            passiveScanBox.Checked = Config.IntValue("ProgressiveScan") == 1;
            startInTrayBox.Checked = Config.IntValue("StartInTray") == 1;

            if (Config.IntValue("StartInTray") == 1) {
                HideToTray();
            } else {
                ShowFromTray();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            try {
                Program.Stop();
                Environment.Exit(0);
            } catch { }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e) { // this does not work, for some reason. Fix before release
            try {
                Program.Stop();
                Close();
                Environment.Exit(0);
            } catch { }
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) {
            donationLink.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        private void passiveScanBox_CheckedChanged(object sender, EventArgs e) {
            Config.SetValue("ProgressiveScan", passiveScanBox.Checked ? "1" : "0");
            Config.Save();
        }

        public void AppendTextBox(string value) { // https://stackoverflow.com/questions/519233/writing-to-a-textbox-from-another-thread
            if (InvokeRequired) {
                this.Invoke(new Action<string>(AppendTextBox), new object[] { value });
                return;
            }
            console.AppendText(value);
        }

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);
        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public async void locBtnClickAsync(object sender, EventArgs e) {
            Button bb = sender as Button;

            if (bb.Tag.GetType() == typeof(Button)) {
                Button button = bb.Tag as Button;

                if (button.Tag.GetType() == typeof(Joycon)) {
                    Joycon v = (Joycon)button.Tag;
                    v.SetRumble(160.0f, 320.0f, 1.0f);
                    await Task.Delay(300);
                    v.SetRumble(160.0f, 320.0f, 0);
                }
            }
        }

        bool doNotRejoin = Boolean.Parse(ConfigurationManager.AppSettings["DoNotRejoinJoycons"]);

        public void conBtnClick(object sender, EventArgs e) {
            Button button = sender as Button;

            if (button.Tag.GetType() == typeof(Joycon)) {
                Joycon v = (Joycon)button.Tag;

                if (v.other == null && !v.isPro) { // needs connecting to other joycon (so messy omg)
                    bool succ = false;

                    if (Program.mgr.j.Count == 1 || doNotRejoin) { // when want to have a single joycon in vertical mode
                        v.other = v; // hacky; implement check in Joycon.cs to account for this
                        succ = true;
                    } else {
                        foreach (Joycon jc in Program.mgr.j) {
                            if (!jc.isPro && jc.isLeft != v.isLeft && jc != v && jc.other == null) {
                                v.other = jc;
                                jc.other = v;

                                if (v.out_xbox != null) {
                                    v.out_xbox.Disconnect();
                                    v.out_xbox = null;
                                }

                                if (v.out_ds4 != null) {
                                    v.out_ds4.Disconnect();
                                    v.out_ds4 = null;
                                }

                                // setting the other joycon's button image
                                foreach (Button b in con)
                                    if (b.Tag == jc)
                                        b.BackgroundImage = jc.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;

                                succ = true;
                                break;
                            }
                        }
                    }

                    if (succ)
                        foreach (Button b in con)
                            if (b.Tag == v)
                                b.BackgroundImage = v.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
                } else if (v.other != null && !v.isPro) { // needs disconnecting from other joycon
                    ReenableViGEm(v);
                    ReenableViGEm(v.other);

                    button.BackgroundImage = v.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;

                    foreach (Button b in con)
                        if (b.Tag == v.other)
                            b.BackgroundImage = v.other.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;

                    v.other.other = null;
                    v.other = null;
                }
            }
        }

        private void startInTrayBox_CheckedChanged(object sender, EventArgs e) {
            Config.SetValue("StartInTray", startInTrayBox.Checked ? "1" : "0");
            Config.Save();
        }

        private void btn_open3rdP_Click(object sender, EventArgs e) {
            _3rdPartyControllers partyForm = new _3rdPartyControllers();
            partyForm.ShowDialog();
        }

        private static readonly string[] restartRequiredSettings = {
            "Language", "IP", "Port", "MotionServer",
            "ShowAsXInput", "ShowAsDS4",
            "UseHIDG", "PurgeAffectedDevices", "PurgeWhitelist",
            "AllowCalibration",
            "AHRS_beta", "acc_sensiti", "gyr_sensiti", "stick_cal", "deadzone", "stick2_cal", "deadzone2"
        };

        private void settingsApply_Click(object sender, EventArgs e) {
            var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            var settings = configFile.AppSettings.Settings;

            List<string> changed = new List<string>();
            foreach (Control rowCtl in settingsTable.Controls) {
                if (!(rowCtl is FlowLayoutPanel)) continue;
                foreach (Control c in ((FlowLayoutPanel)rowCtl).Controls) {
                    if (!(c.Tag is string key) || settings[key] == null) continue;
                    string newValue = null;
                    if (c is CheckBox) {
                        newValue = ((CheckBox)c).Checked.ToString().ToLower();
                    } else if (c is ComboBox) {
                        newValue = ((LangOption)((ComboBox)c).SelectedItem).Code;
                    } else if (c is TextBox) {
                        newValue = ((TextBox)c).Text.ToLower();
                    }
                    if (newValue != null && settings[key].Value != newValue) {
                        settings[key].Value = newValue;
                        changed.Add(key);
                    }
                }
            }

            try {
                configFile.Save(ConfigurationSaveMode.Modified);
            } catch (ConfigurationErrorsException) {
                AppendTextBox(I18n.Str("MsgErrorWritingSettings"));
            }

            // Hot-reload: refresh in-memory config so runtime properties pick up new values immediately
            ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            ConfigurationManager.AppSettings["AutoPowerOff"] = "false";  // Prevent joycons poweroff when applying settings

            if (changed.Contains("HomeLEDOn")) {
                bool on = settings["HomeLEDOn"].Value.ToLower() == "true";
                foreach (Joycon j in Program.mgr.j) {
                    j.SetHomeLight(on);
                }
            }

            List<string> restartNeeded = changed.Where(k => restartRequiredSettings.Contains(k)).ToList();
            if (restartNeeded.Count > 0) {
                MessageBox.Show(I18n.Str("MsgRestartRequired") + string.Join(", ", restartNeeded), I18n.Str("FormTitle"));
            }
        }

        void ReenableViGEm(Joycon v) {
            if (showAsXInput && v.out_xbox == null) {
                v.out_xbox = new Controller.OutputControllerXbox360();

                if (toRumble)
                    v.out_xbox.FeedbackReceived += v.ReceiveRumble;
                v.out_xbox.Connect();
            }

            if (showAsDS4 && v.out_ds4 == null) {
                v.out_ds4 = new Controller.OutputControllerDualShock4();

                if (toRumble)
                    v.out_ds4.FeedbackReceived += v.Ds4_FeedbackReceived;
                v.out_ds4.Connect();
            }
        }

        private void foldLbl_Click(object sender, EventArgs e) {
            rightPanel.Visible = !rightPanel.Visible;
            foldLbl.Text = rightPanel.Visible ? "<" : ">";
        }

        private void cbBox_Changed(object sender, EventArgs e) {
            if (sender is CheckBox cb && cb.Tag is string key && key == "HomeLEDOn") {
                foreach (Joycon j in Program.mgr.j) {
                    j.SetHomeLight(cb.Checked);
                }
            }
        }
        private void StartCalibrate(object sender, EventArgs e) {
            if (Program.mgr.j.Count == 0) {
                this.console.Text = I18n.Str("MsgConnectSinglePro");
                return;
            }
            if (Program.mgr.j.Count > 1) {
                this.console.Text = I18n.Str("MsgCalibrateOneAtTime");
                return;
            }
            this.AutoCalibrate.Enabled = false;
            countDown = new Timer();
            this.count = 4;
            this.CountDown(null, null);
            countDown.Tick += new EventHandler(CountDown);
            countDown.Interval = 1000;
            countDown.Enabled = true;
        }

        private void StartGetData() {
            this.xG.Clear(); this.yG.Clear(); this.zG.Clear();
            this.xA.Clear(); this.yA.Clear(); this.zA.Clear();
            countDown = new Timer();
            this.count = 3;
            this.calibrate = true;
            countDown.Tick += new EventHandler(CalcData);
            countDown.Interval = 1000;
            countDown.Enabled = true;
        }

        private void btn_reassign_open_Click(object sender, EventArgs e) {
            Reassign mapForm = new Reassign();
            mapForm.ShowDialog();
        }

        private void CountDown(object sender, EventArgs e) {
            if (this.count == 0) {
                this.console.Text = I18n.Str("MsgCalibrating");
                countDown.Stop();
                this.StartGetData();
            } else {
                this.console.Text = I18n.Str("MsgKeepFlat");
                this.console.Text += I18n.Format("MsgCalibrationStartsIn", this.count);
                this.count--;
            }
        }
        private void CalcData(object sender, EventArgs e) {
            if (this.count == 0) {
                countDown.Stop();
                this.calibrate = false;
                string serNum = Program.mgr.j.First().serial_number;
                int serIndex = this.findSer(serNum);
                float[] Arr = new float[6] { 0, 0, 0, 0, 0, 0 };
                if (serIndex == -1) {
                    this.caliData.Add(new KeyValuePair<string, float[]>(
                         serNum,
                         Arr
                    ));
                } else {
                    Arr = this.caliData[serIndex].Value;
                }
                Random rnd = new Random();
                Arr[0] = (float)quickselect_median(this.xG, rnd.Next);
                Arr[1] = (float)quickselect_median(this.yG, rnd.Next);
                Arr[2] = (float)quickselect_median(this.zG, rnd.Next);
                Arr[3] = (float)quickselect_median(this.xA, rnd.Next);
                Arr[4] = (float)quickselect_median(this.yA, rnd.Next);
                Arr[5] = (float)quickselect_median(this.zA, rnd.Next) - 4010; //Joycon.cs acc_sen 16384
                this.console.Text += I18n.Str("MsgCalibrationCompleted");
                Config.SaveCaliData(this.caliData);
                Program.mgr.j.First().getActiveData();
                this.AutoCalibrate.Enabled = true;
            } else {
                this.count--;
            }

        }
        private double quickselect_median(List<int> l, Func<int, int> pivot_fn) {
            int ll = l.Count;
            if (ll % 2 == 1) {
                return this.quickselect(l, ll / 2, pivot_fn);
            } else {
                return 0.5 * (quickselect(l, ll / 2 - 1, pivot_fn) + quickselect(l, ll / 2, pivot_fn));
            }
        }

        private int quickselect(List<int> l, int k, Func<int, int> pivot_fn) {
            if (l.Count == 1 && k == 0) {
                return l[0];
            }
            int pivot = l[pivot_fn(l.Count)];
            List<int> lows = l.Where(x => x < pivot).ToList();
            List<int> highs = l.Where(x => x > pivot).ToList();
            List<int> pivots = l.Where(x => x == pivot).ToList();
            if (k < lows.Count) {
                return quickselect(lows, k, pivot_fn);
            } else if (k < (lows.Count + pivots.Count)) {
                return pivots[0];
            } else {
                return quickselect(highs, k - lows.Count - pivots.Count, pivot_fn);
            }
        }

        public float[] activeCaliData(string serNum) {
            for (int i = 0; i < this.caliData.Count; i++) {
                if (this.caliData[i].Key == serNum) {
                    return this.caliData[i].Value;
                }
            }
            return this.caliData[0].Value;
        }

        private int findSer(string serNum) {
            for (int i = 0; i < this.caliData.Count; i++) {
                if (this.caliData[i].Key == serNum) {
                    return i;
                }
            }
            return -1;
        }
    }
}
