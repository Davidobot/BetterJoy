using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

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

			Program.Start();
		}

		private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
			Program.Stop();
		}

		private void exitToolStripMenuItem_Click(object sender, EventArgs e) {
			Program.Stop();
			Application.Exit();
		}
	}
}
