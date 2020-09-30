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
        private WindowsInput.EventSources.IKeyboardEventSource keyboard;
        private WindowsInput.EventSources.IMouseEventSource mouse;

        ContextMenuStrip menu_joy_buttons = new ContextMenuStrip();

        private Control curAssignment;

        public Reassign() {
            InitializeComponent();

            foreach (int i in Enum.GetValues(typeof(Joycon.Button))) {
                ToolStripMenuItem temp = new ToolStripMenuItem(Enum.GetName(typeof(Joycon.Button), i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);
            }

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;

            foreach (SplitButton c in new SplitButton[] { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_reset_mouse, btn_active_gyro }) {
                c.Tag = c.Name.Substring(4);
                GetPrettyName(c);

                tip_reassign.SetToolTip(c, "Left-click to detect input.\r\nMiddle-click to clear to default.\r\nRight-click to see more options.");
                c.MouseDown += Remap;
                c.Menu = menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }
        }

        private void Menu_joy_buttons_ItemClicked(object sender, ToolStripItemClickedEventArgs e) {
            Control c = sender as Control;

            ToolStripItem clickedItem = e.ClickedItem;

            SplitButton caller = (SplitButton)c.Tag;
            Config.SetValue((string)caller.Tag, "joy_" + (clickedItem.Tag));
            GetPrettyName(caller);
        }

        private void Remap(object sender, MouseEventArgs e) {
            SplitButton c = sender as SplitButton;
            switch (e.Button) {
                case MouseButtons.Left:
                    c.Text = "...";
                    curAssignment = c;
                    break;
                case MouseButtons.Middle:
                    Config.SetValue((string)c.Tag, Config.GetDefaultValue((string)c.Tag));
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

        private void Mouse_MouseEvent(object sender, WindowsInput.EventSources.EventSourceEventArgs<WindowsInput.EventSources.MouseEvent> e) {
            if (curAssignment != null && e.Data.ButtonDown != null) {
                Config.SetValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Keyboard_KeyEvent(object sender, WindowsInput.EventSources.EventSourceEventArgs<WindowsInput.EventSources.KeyboardEvent> e) {
            if (curAssignment != null && e.Data.KeyDown != null) {
                Config.SetValue((string)curAssignment.Tag, "key_" + ((int)e.Data.KeyDown.Key));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
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
                    else
                        c.Text = "";
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
