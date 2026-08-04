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

        ContextMenuStrip menu_joy_buttons = new ContextMenuStrip();

        private Control curAssignment;

        public Reassign() {
            InitializeComponent();

            Font uiFont = I18n.UiFont;
            if (uiFont != null)
                this.Font = uiFont;

            foreach (int i in Enum.GetValues(typeof(Joycon.Button))) {
                ToolStripMenuItem temp = new ToolStripMenuItem(GetButtonDisplayName((Joycon.Button)i));
                temp.Tag = i;
                menu_joy_buttons.Items.Add(temp);
            }

            menu_joy_buttons.ItemClicked += Menu_joy_buttons_ItemClicked;

            foreach (SplitButton c in new SplitButton[] { btn_capture, btn_home, btn_sl_l, btn_sl_r, btn_sr_l, btn_sr_r, btn_shake, btn_reset_mouse, btn_active_gyro }) {
                c.Tag = c.Name.Substring(4);
                GetPrettyName(c);

                tip_reassign.SetToolTip(c, I18n.Str("TipRemap"));
                c.MouseDown += Remap;
                c.Menu = menu_joy_buttons;
                c.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }
        }

        private string GetButtonDisplayName(Joycon.Button button) {
            switch (button) {
                case Joycon.Button.SHOULDER_1: return I18n.Str("BtnL");
                case Joycon.Button.SHOULDER_2: return I18n.Str("BtnZL");
                case Joycon.Button.SHOULDER2_1: return I18n.Str("BtnR");
                case Joycon.Button.SHOULDER2_2: return I18n.Str("BtnZR");
                case Joycon.Button.STICK: return I18n.Str("BtnLStick");
                case Joycon.Button.STICK2: return I18n.Str("BtnRStick");
                case Joycon.Button.DPAD_UP: return I18n.Str("BtnDpadUp");
                case Joycon.Button.DPAD_DOWN: return I18n.Str("BtnDpadDown");
                case Joycon.Button.DPAD_LEFT: return I18n.Str("BtnDpadLeft");
                case Joycon.Button.DPAD_RIGHT: return I18n.Str("BtnDpadRight");
                case Joycon.Button.PLUS: return I18n.Str("BtnPlus");
                case Joycon.Button.MINUS: return I18n.Str("BtnMinus");
                case Joycon.Button.CAPTURE: return I18n.Str("BtnCapture");
                default: return Enum.GetName(typeof(Joycon.Button), button);
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
                    c.Text = I18n.Str("Detecting");
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

        private void Mouse_MouseEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.MouseEvent> e) {
            if (curAssignment != null && e.Data.ButtonDown != null) {
                Config.SetValue((string)curAssignment.Tag, "mse_" + ((int)e.Data.ButtonDown.Button));
                AsyncPrettyName(curAssignment);
                curAssignment = null;
                e.Next_Hook_Enabled = false;
            }
        }

        private void Keyboard_KeyEvent(object sender, WindowsInput.Events.Sources.EventSourceEventArgs<WindowsInput.Events.Sources.KeyboardEvent> e) {
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
                        c.Text = I18n.Str("Guide");
                    else
                        c.Text = "";
                    break;
                default:
                    Type t = val.StartsWith("joy_") ? typeof(Joycon.Button) : (val.StartsWith("key_") ? typeof(WindowsInput.Events.KeyCode) : typeof(WindowsInput.Events.ButtonCode));
                    c.Text = (t == typeof(Joycon.Button)) ? GetButtonDisplayName((Joycon.Button)Int32.Parse(val.Substring(4))) : Enum.GetName(t, Int32.Parse(val.Substring(4)));
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
