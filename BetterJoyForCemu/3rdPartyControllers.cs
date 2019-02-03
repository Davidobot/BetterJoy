using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using static BetterJoyForCemu.HIDapi;

namespace BetterJoyForCemu {
    public partial class _3rdPartyControllers : Form {
        public _3rdPartyControllers() {
            InitializeComponent();
            list_allControllers.DisplayMember = "Text";
            list_allControllers.ValueMember = "Value";
            list_customControllers.DisplayMember = "Text";
            list_customControllers.ValueMember = "Value";

            RefreshControllerList();

            group_props.Controls.Add(chk_isLeft);
            group_props.Controls.Add(chk_isPro);
            group_props.Enabled = false;
        }

        private bool ContainsText(ListBox a, String manu) {
            foreach (var v in a.Items)
                if ((v as dynamic).Text.Equals(manu))
                    return true;
            return false;
        }

        private void RefreshControllerList() {
            list_allControllers.Items.Clear();
            IntPtr ptr = HIDapi.hid_enumerate(0x0, 0x0);
            IntPtr top_ptr = ptr;

            hid_device_info enumerate; // Add device to list
            while (ptr != IntPtr.Zero) {
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

                if (!ContainsText(list_customControllers, enumerate.product_string) && !ContainsText(list_allControllers, enumerate.product_string))
                    list_allControllers.Items.Add(new { Text = enumerate.product_string, Value = enumerate });

                ptr = enumerate.next;
            }
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

        private void chk_isPro_CheckedChanged(object sender, EventArgs e) {

        }

        private void chk_isLeft_CheckedChanged(object sender, EventArgs e) {

        }

        private void btn_apply_Click(object sender, EventArgs e) {

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
                tip_device.Show((list_allControllers.SelectedItem as dynamic).Text, list_allControllers);
        }

        private void list_customControllers_SelectedValueChanged(object sender, EventArgs e) {
            if (list_customControllers.SelectedItem != null) {
                tip_device.Show((list_customControllers.SelectedItem as dynamic).Text, list_customControllers);
                group_props.Enabled = true;
            } else
                group_props.Enabled = false;
        }

        private void list_customControllers_MouseDown(object sender, MouseEventArgs e) {
            if (e.Y > list_customControllers.ItemHeight * list_customControllers.Items.Count)
                list_customControllers.SelectedItems.Clear();
        }

        private void list_allControllers_MouseDown(object sender, MouseEventArgs e) {
            if (e.Y > list_allControllers.ItemHeight * list_allControllers.Items.Count)
                list_allControllers.SelectedItems.Clear();
        }
    }
}
