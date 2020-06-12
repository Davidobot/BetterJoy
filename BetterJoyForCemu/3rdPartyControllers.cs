using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using static BetterJoyForCemu.HIDapi;

namespace BetterJoyForCemu {
    public partial class _3rdPartyControllers : Form {
        public class SController {
            public String name;
            public ushort product_id;
            public ushort vendor_id;
            public byte type; // 1 is pro, 2 is left joy, 3 is right joy

            public SController(String name, ushort vendor_id, ushort product_id, byte type) {
                this.product_id = product_id; this.vendor_id = vendor_id; this.type = type;
                this.name = name;
            }

            public override bool Equals(object obj) {
                //Check for null and compare run-time types.
                if ((obj == null) || !this.GetType().Equals(obj.GetType())) {
                    return false;
                } else {
                    SController s = (SController)obj;
                    return (s.product_id == product_id) && (s.vendor_id == vendor_id);
                }
            }

            public override int GetHashCode() {
                return Tuple.Create(product_id, vendor_id).GetHashCode();
            }

            public override string ToString() {
                return name;
            }

            public string Serialise() {
                return String.Format("{0}|{1}|{2}|{3}", name, product_id, vendor_id, type);
            }
        }

        const string PATH = "3rdPartyControllers";

        public _3rdPartyControllers() {
            InitializeComponent();
            /*list_allControllers.DisplayMember = "Text";
            list_allControllers.ValueMember = "Value";
            list_customControllers.DisplayMember = "Text";
            list_customControllers.ValueMember = "Value";*/
            list_allControllers.HorizontalScrollbar = true; list_customControllers.HorizontalScrollbar = true;

            chooseType.Items.AddRange(new String[] { "Pro Controller", "Left Joycon", "Right Joycon" });

            chooseType.FormattingEnabled = true;
            group_props.Controls.Add(chooseType);
            group_props.Enabled = false;

            if (File.Exists(PATH)) {
                using (StreamReader file = new StreamReader(PATH)) {
                    string line = String.Empty;
                    while ((line = file.ReadLine()) != null && (line != String.Empty)) {
                        String[] split = line.Split('|');
                        list_customControllers.Items.Add(new SController(split[0], ushort.Parse(split[1]), ushort.Parse(split[2]), byte.Parse(split[3])));
                    }
                }
            }

            CopyCustomControllers();
            RefreshControllerList();
        }

        public void CopyCustomControllers() {
            Program.thirdPartyCons.Clear();
            foreach (SController v in list_customControllers.Items) {
                Program.thirdPartyCons.Add(v);
            }
        }

        private bool ContainsText(ListBox a, String manu) {
            foreach (SController v in a.Items) {
                if (v == null)
                    continue;
                if (v.name == null)
                    continue;
                if (v.name.Equals(manu))
                    return true;
            }
            return false;
        }

        private void RefreshControllerList() {
            list_allControllers.Items.Clear();
            IntPtr ptr = HIDapi.hid_enumerate(0x0, 0x0);
            IntPtr top_ptr = ptr;

            hid_device_info enumerate; // Add device to list
            while (ptr != IntPtr.Zero) {
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));
                // TODO: try checking against interface number instead
                if (!ContainsText(list_customControllers, enumerate.product_string) && !ContainsText(list_allControllers, enumerate.product_string)) {
                    list_allControllers.Items.Add(new SController(enumerate.product_string, enumerate.vendor_id, enumerate.product_id, 0));
                    // 0 type is undefined
                }

                ptr = enumerate.next;
            }
            HIDapi.hid_free_enumeration(top_ptr);
        }

        private void btn_add_Click(object sender, EventArgs e) {
            if (list_allControllers.SelectedItem != null) {
                list_customControllers.Items.Add(list_allControllers.SelectedItem);
                list_allControllers.Items.Remove(list_allControllers.SelectedItem);

                list_allControllers.ClearSelected();
            }
        }

        private void btn_remove_Click(object sender, EventArgs e) {
            if (list_customControllers.SelectedItem != null) {
                list_allControllers.Items.Add(list_customControllers.SelectedItem);
                list_customControllers.Items.Remove(list_customControllers.SelectedItem);

                list_customControllers.ClearSelected();
            }
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            String sc = "";
            foreach (SController v in list_customControllers.Items) {
                sc += v.Serialise() + "\r\n";
            }
            File.WriteAllText(PATH, sc);
            CopyCustomControllers();
        }

        private void btn_applyAndClose_Click(object sender, EventArgs e) {
            btn_apply_Click(sender, e);
            Close();
        }

        private void _3rdPartyControllers_FormClosing(object sender, FormClosingEventArgs e) {
            btn_apply_Click(sender, e);
        }

        private void btn_refresh_Click(object sender, EventArgs e) {
            RefreshControllerList();
        }

        private void list_allControllers_SelectedValueChanged(object sender, EventArgs e) {
            if (list_allControllers.SelectedItem != null)
                tip_device.Show((list_allControllers.SelectedItem as SController).name, list_allControllers);
        }

        private void list_customControllers_SelectedValueChanged(object sender, EventArgs e) {
            if (list_customControllers.SelectedItem != null) {
                SController v = (list_customControllers.SelectedItem as SController);
                tip_device.Show(v.name, list_customControllers);

                chooseType.SelectedIndex = v.type - 1;

                group_props.Enabled = true;
            } else {
                chooseType.SelectedIndex = -1;
                group_props.Enabled = false;
            }
        }

        private void list_customControllers_MouseDown(object sender, MouseEventArgs e) {
            if (e.Y > list_customControllers.ItemHeight * list_customControllers.Items.Count)
                list_customControllers.SelectedItems.Clear();
        }

        private void list_allControllers_MouseDown(object sender, MouseEventArgs e) {
            if (e.Y > list_allControllers.ItemHeight * list_allControllers.Items.Count)
                list_allControllers.SelectedItems.Clear();
        }

        private void chooseType_SelectedValueChanged(object sender, EventArgs e) {
            if (list_customControllers.SelectedItem != null) {
                SController v = (list_customControllers.SelectedItem as SController);
                v.type = (byte)(chooseType.SelectedIndex + 1);
            }
        }
    }
}
