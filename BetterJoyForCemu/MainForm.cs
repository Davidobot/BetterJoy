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
		public MainForm() {
			InitializeComponent();
		}

		private void MainForm_Resize(object sender, EventArgs e) {
			if (this.WindowState == FormWindowState.Minimized) {
				notifyIcon.Visible = true;
				notifyIcon.ShowBalloonTip(1);
				this.ShowInTaskbar = false;
			}
		}

		private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e) {
			this.WindowState = FormWindowState.Normal;
			this.ShowInTaskbar = true;
			notifyIcon.Visible = false;
		}

		private void MainForm_Load(object sender, EventArgs e) {
			this.ShowInTaskbar = true;
			notifyIcon.Visible = false;
            this.Show();
            Program.Start();

            Config.Init();

            passiveScanBox.Checked = Config.Value("ProgressiveScan");
		}

		private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
            try {
                Program.Stop();
                Environment.Exit(0);
            } catch { }
        }

		private void exitToolStripMenuItem_Click(object sender, EventArgs e) {
            try {
                Program.Stop();
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
    }
}
