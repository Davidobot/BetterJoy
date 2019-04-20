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
        public List<Button> con, loc;

		public MainForm() {
			InitializeComponent();

            con = new List<Button> { con1, con2, con3, con4 };
            loc = new List<Button> { loc1, loc2, loc3, loc4 };

            //list all options
            string[] myConfigs = ConfigurationManager.AppSettings.AllKeys;
            Size childSize = new Size(87, 20);
            for (int i = 0; i != myConfigs.Length; i++)
            {
                tableLayoutPanel1.RowCount++;
                tableLayoutPanel1.Controls.Add(new Label() { Text = myConfigs[i], TextAlign=ContentAlignment.BottomLeft, AutoEllipsis=true, Size = childSize }, 0, i);

                var value = ConfigurationManager.AppSettings[myConfigs[i]];
                Control childControl;
                if (value == "true" || value == "false")
                {
                    childControl = new CheckBox() { Checked = Boolean.Parse(value), Size= childSize };
                }
                else
                {
                    childControl = new TextBox() { Text = value, Size = childSize };
                }

                tableLayoutPanel1.Controls.Add(childControl, 1, i);
                childControl.MouseClick += cbBox_Changed;
            }
        }

        private void HideToTray() {
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(1);
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
            Program.Start();

            Config.Init();

            passiveScanBox.Checked = Config.Value("ProgressiveScan");
            startInTrayBox.Checked = Config.Value("StartInTray");

            if (Config.Value("StartInTray")) {
                HideToTray();
            }
            else {
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
            linkLabel1.LinkVisited = true;
            System.Diagnostics.Process.Start("http://paypal.me/DavidKhachaturov/5");
        }

        private void passiveScanBox_CheckedChanged(object sender, EventArgs e) {
            Config.Save("ProgressiveScan", passiveScanBox.Checked);
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

        public void locBtnClick(object sender, EventArgs e) {
            Button bb = sender as Button;

            if (bb.Tag.GetType() == typeof(Button)) {
                Button button = bb.Tag as Button;

                if (button.Tag.GetType() == typeof(Joycon)) {
                    Joycon v = (Joycon) button.Tag;
                    v.SetRumble(20.0f, 400.0f, 1.0f, 300);
                }
            }
        }

        public void conBtnClick(object sender, EventArgs e) {
            Button button = sender as Button;

            if (button.Tag.GetType() == typeof(Joycon)) {
                Joycon v = (Joycon)button.Tag;

                if (v.other == null && !v.isPro) { // needs connecting to other joycon (so messy omg)
                    bool succ = false;
                    foreach (Joycon jc in Program.mgr.j) {
                        if (!jc.isPro && jc.isLeft != v.isLeft && jc != v && jc.other == null) {
                            v.other = jc;
                            jc.other = v;

                            v.xin.Dispose();
                            v.xin = null;

                            foreach (Button b in con)
                                if (b.Tag == jc)
                                        b.BackgroundImage = jc.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;

                            succ = true;
                            break;
                        }
                    }

                    if (succ)
                        foreach (Button b in con)
                            if (b.Tag == v)
                                b.BackgroundImage = v.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
                } else if (v.other != null && !v.isPro) { // needs disconnecting from other joycon
                    if (v.xin == null) {
                        ReenableXinput(v);
                        v.xin.Connect();
                    }

                    if (v.other.xin == null) {
                        ReenableXinput(v.other);
                        v.other.xin.Connect();
                    }

                    button.BackgroundImage = v.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;

                    foreach (Button b in con)
                        if (b.Tag == v.other)
                                b.BackgroundImage = v.other.isLeft ? Properties.Resources.jc_left_s : Properties.Resources.jc_right_s;

                    v.other.other = null;
                    v.other = null;
                }
            }
        }

        private void startInTrayBox_CheckedChanged(object sender, EventArgs e)
        {
            Config.Save("StartInTray", startInTrayBox.Checked);
        }

        private void btn_open3rdP_Click(object sender, EventArgs e) {
            _3rdPartyControllers partyForm = new _3rdPartyControllers();
            partyForm.ShowDialog();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Application.Restart();
            Environment.Exit(0);
        }

        void ReenableXinput(Joycon v) {
            if (showAsXInput) {
                v.xin = new Xbox360Controller(Program.emClient);

                if (toRumble)
                    v.xin.FeedbackReceived += v.ReceiveRumble;
                v.report = new Xbox360Report();
            }
        }

        private void label2_Click(object sender, EventArgs e)
        {
            rightPanel.Visible = !rightPanel.Visible;
            foldLbl.Text = rightPanel.Visible ? "<" : ">";
        }

        private void cbBox_Changed(object sender, EventArgs e)
        {
            var coord = tableLayoutPanel1.GetPositionFromControl(sender as Control);

            var valCtl = tableLayoutPanel1.GetControlFromPosition(coord.Column, coord.Row);
            var KeyCtl = tableLayoutPanel1.GetControlFromPosition(coord.Column - 1, coord.Row).Text;

            try
            {
                var configFile = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                var settings = configFile.AppSettings.Settings;
                if (sender.GetType() == typeof(CheckBox) && settings[KeyCtl] != null)
                {
                    settings[KeyCtl].Value = ((CheckBox)valCtl).Checked.ToString().ToLower();
                }
                else if (sender.GetType() == typeof(TextBox) && settings[KeyCtl] != null)
                {
                    settings[KeyCtl].Value = ((TextBox)valCtl).Text.ToLower();
                }
                configFile.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection(configFile.AppSettings.SectionInformation.Name);
            }
            catch (ConfigurationErrorsException)
            {
                Console.WriteLine("Error writing app settings");
                Trace.WriteLine(String.Format("rw {0}, column {1}, {2}, {3}", coord.Row, coord.Column, sender.GetType(), KeyCtl));
            }
        }
    }
}
