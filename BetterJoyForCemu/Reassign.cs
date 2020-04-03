using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BetterJoyForCemu {
	public partial class Reassign : Form {
		private WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
		private WindowsInput.Events.Sources.IMouseEventSource mouse;

		private Control curAssignment;

		public Reassign() {
			InitializeComponent();

			foreach (Control c in new Control[] { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_reset_mouse }) {
				c.Tag = c.Name.Substring(4);
				GetPrettyName(c);

				tip_reassign.SetToolTip(c, "Left-click to detect input.\r\nMiddle-click to clear to default.\r\nRight-click to see more options.");
				c.MouseDown += Remap;
			}
		}

		private void Remap(object sender, MouseEventArgs e) {
			Button c = sender as Button;
			Debug.WriteLine(e.Button);
			switch (e.Button) {
				case MouseButtons.Left:
					c.Text = "...";
					curAssignment = c;
					break;
				case MouseButtons.Middle:
					Config.SetValue((string) c.Tag, Config.GetDefaultValue((string) c.Tag));
					GetPrettyName(c);
					break;
				case MouseButtons.Right:
					break;
			}
		}

		private void Reassign_Load(object sender, EventArgs e) {
			keyboard = WindowsInput.Capture.Global.KeyboardAsync();
			keyboard.KeyEvent += Keyboard_KeyEvent;
			mouse = WindowsInput.Capture.Global.MouseAsync();
			mouse.MouseEvent += Mouse_MouseEvent;
		}

		private void Mouse_MouseEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.MouseEvent> e) {
			if (curAssignment != null && e.Data.ButtonDown != null) {
				Config.SetValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
				AsyncPrettyName(curAssignment);
				curAssignment = null;
			}
		}

		private void Keyboard_KeyEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.KeyboardEvent> e) {
			if (curAssignment != null && e.Data.KeyDown != null) {
				Config.SetValue((string)curAssignment.Tag, "key_" + ((int)e.Data.KeyDown.Key));
				AsyncPrettyName(curAssignment);
				curAssignment = null;
			}
		}

		private void Reassign_FormClosing(object sender, FormClosingEventArgs e) {
			keyboard.Dispose();
			mouse.Dispose();
		}

		private void AsyncPrettyName(Control c) {
			if (InvokeRequired) {
				this.Invoke(new Action<Control>(AsyncPrettyName), new object[] { c });
				return;
			}
			GetPrettyName(c);
		}

		private void GetPrettyName(Control c) {
			string val;
			switch (val = Config.Value((string)c.Tag)) {
				case "0":
					if (c == btn_home)
						c.Text = "Guide";
					break;
				default:
					Type t = val.StartsWith("joy_") ? typeof(Joycon.Button) : (val.StartsWith("key_") ? typeof(WindowsInput.Events.KeyCode) : typeof(WindowsInput.Events.ButtonCode));
					c.Text = Enum.GetName(t, Int32.Parse(val.Substring(4)));
					break;
			}
		}

		private void btn_apply_Click(object sender, EventArgs e) {
			Config.Save();
		}

		private void btn_close_Click(object sender, EventArgs e) {
			btn_apply_Click(sender, e);
			Close();
		}
	}
}
