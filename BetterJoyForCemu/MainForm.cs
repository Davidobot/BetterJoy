using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
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
        }

        private void HideToTray() {
            this.WindowState = FormWindowState.Minimized;
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(1);
            this.ShowInTaskbar = false;
        }

        private void ShowFromTray() {
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
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

                    int found = 0;
                    int minPadID = 10;
                    foreach (Joycon jc in Program.mgr.j) { // current system is designed for a maximum of two joycons connected to the PC
                        if (!jc.isPro) {
                            found++;
                            minPadID = Math.Min(jc.PadId, minPadID);
                        }
                        jc.LED = (byte)(0x1 << jc.PadId);
                    }

                    if (found == 2) {
                        AppendTextBox("Both joycons successfully found.\r\n");
                        Joycon temp = null;
                        foreach (Joycon jc in Program.mgr.j) {
                            if (!jc.isPro) {
                                jc.LED = (byte)(0x1 << minPadID);

                                if (temp == null)
                                    temp = jc;
                                else {
                                    temp.other = jc;
                                    jc.other = temp;

                                    temp.xin.Dispose();
                                    temp.xin = null;
                                }

                                foreach (Button b in con) {
                                    if (b.Tag == jc) {
                                        if (jc.isLeft)
                                            b.BackgroundImage = Properties.Resources.jc_left;
                                        else
                                            b.BackgroundImage = Properties.Resources.jc_right;
                                    }
                                }
                            }
                        } // Join up the two joycons
                    }
                } else if (v.other != null && !v.isPro) { // needs disconnecting from other joycon
                    if (v.xin == null) {
                        ReenableXinput(v);
                        v.xin.Connect();
                    }

                    if (v.other.xin == null) {
                        ReenableXinput(v.other);
                        v.other.xin.Connect();
                    }

                    if (v.isLeft)
                        button.BackgroundImage = Properties.Resources.jc_left_s;
                    else
                        button.BackgroundImage = Properties.Resources.jc_right_s;

                    foreach (Button b in con) {
                        if (b.Tag == v.other) {
                            if (v.other.isLeft)
                                b.BackgroundImage = Properties.Resources.jc_left_s;
                            else
                                b.BackgroundImage = Properties.Resources.jc_right_s;
                        }
                    }

                    v.other.other = null;
                    v.other = null;
                }
            }
        }

        private void startInTrayBox_CheckedChanged(object sender, EventArgs e)
        {
            Config.Save("StartInTray", startInTrayBox.Checked);
        }

        void ReenableXinput(Joycon v) {
            if (showAsXInput) {
                v.xin = new Xbox360Controller(Program.emClient);

                if (toRumble)
                    v.xin.FeedbackReceived += v.ReceiveRumble;
                v.report = new Xbox360Report();
            }
        }
    }
}
