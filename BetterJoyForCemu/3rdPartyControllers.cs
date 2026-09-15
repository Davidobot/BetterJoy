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
            public string serial_number;
            public byte type; // 1 is pro, 2 is left joy, 3 is right joy

            public SController(String name, ushort vendor_id, ushort product_id, byte type, string serial_number) {
                this.product_id = product_id; this.vendor_id = vendor_id; this.type = type;
                this.serial_number = serial_number;
                this.name = name;
            }

            public override bool Equals(object obj) {
                //Check for null and compare run-time types.
                if ((obj == null) || !this.GetType().Equals(obj.GetType())) {
                    return false;
                } else {
                    SController s = (SController)obj;
                    return (s.product_id == product_id) && (s.vendor_id == vendor_id) && (s.serial_number == serial_number);
                }
            }

            public override int GetHashCode() {
                return Tuple.Create(product_id, vendor_id, serial_number).GetHashCode();
            }

            public override string ToString() {
                return name ?? $"Unidentified Device ({this.product_id})";
            }

            public string Serialise() {
                return String.Format("{0}|{1}|{2}|{3}|{4}", name, vendor_id, product_id, type, serial_number);
            }
        }

        static readonly string path;
        static readonly string blacklistPath;

        static _3rdPartyControllers() {
            path = Path.Combine(AppPaths.DataDir, "3rdPartyControllers");
            blacklistPath = Path.Combine(AppPaths.DataDir, "BlacklistedControllers");
        }

        public _3rdPartyControllers() {
            InitializeComponent();
            list_allControllers.HorizontalScrollbar = true;
            list_customControllers.HorizontalScrollbar = true;
            list_blacklistedControllers.HorizontalScrollbar = true;

            chooseType.Items.AddRange(new String[] { "Pro Controller", "Left Joycon", "Right Joycon" });

            chooseType.FormattingEnabled = true;
            group_props.Controls.Add(chooseType);
            group_props.Enabled = false;

            LoadControllerList(path, list_customControllers);
            LoadControllerList(blacklistPath, list_blacklistedControllers);

            CopyCustomControllers();
            CopyBlacklistedControllers();
            RefreshControllerList();
        }

        private static List<SController> ParseControllerFile(string filePath) {
            var result = new List<SController>();
            if (!File.Exists(filePath))
                return result;

            using (StreamReader file = new StreamReader(filePath)) {
                string line = String.Empty;
                while ((line = file.ReadLine()) != null && (line != String.Empty)) {
                    String[] split = line.Split('|');
                    //won't break existing config file
                    String serial_number = "";
                    if (split.Length > 4) {
                        serial_number = split[4];
                    }
                    result.Add(new SController(split[0], ushort.Parse(split[1]), ushort.Parse(split[2]), byte.Parse(split[3]), serial_number));
                }
            }
            return result;
        }

        private static void LoadControllerList(string filePath, ListBox target) {
            foreach (SController sc in ParseControllerFile(filePath))
                target.Items.Add(sc);
        }

        // Populates Program.thirdPartyCons/blacklistedCons directly from disk, without
        // constructing this Form (and its ListBoxes/child controls) at all - used by headless/
        // service mode (see BetterJoyService), where there's no desktop for a Form to exist on.
        // GUI mode still goes through the Form itself (CopyCustomControllers/
        // CopyBlacklistedControllers), since that's also where the Add Controllers dialog's
        // lists get populated for editing.
        public static void LoadIntoProgramLists() {
            Program.thirdPartyCons.Clear();
            Program.thirdPartyCons.AddRange(ParseControllerFile(path));

            Program.blacklistedCons.Clear();
            Program.blacklistedCons.AddRange(ParseControllerFile(blacklistPath));
        }

        public void CopyCustomControllers() {
            Program.thirdPartyCons.Clear();
            foreach (SController v in list_customControllers.Items) {
                Program.thirdPartyCons.Add(v);
            }
        }

        public void CopyBlacklistedControllers() {
            Program.blacklistedCons.Clear();
            foreach (SController v in list_blacklistedControllers.Items) {
                Program.blacklistedCons.Add(v);
            }
        }

        // HID Usage Page 0x01 (Generic Desktop), Usage 0x04 (Joystick) / 0x05 (Gamepad) / 0x08 (Multi-axis Controller)
        public static bool IsGameController(hid_device_info d) {
            return d.usage_page == 0x01 && (d.usage == 0x04 || d.usage == 0x05 || d.usage == 0x08);
        }

        // Best-effort guess so the user doesn't have to know the type numbering by heart
        public static byte GuessType(hid_device_info d) {
            String haystack = ((d.manufacturer_string ?? "") + " " + (d.product_string ?? "")).ToLowerInvariant();
            if (haystack.Contains("left") || haystack.Contains("(l)"))
                return 2; // Left Joycon
            if (haystack.Contains("right") || haystack.Contains("(r)"))
                return 3; // Right Joycon
            return 1; // default guess: Pro Controller
        }

        // Appends a controller that was auto-detected at runtime so it persists across restarts
        public static void PersistCustomController(SController sc) {
            File.AppendAllText(path, sc.Serialise() + "\r\n");
        }

        // Shared so every code path (manual dialog, auto-add) names a given device identically -
        // otherwise the same physical device ends up with mismatched names in different lists/files
        // and duplicate-detection (which compares by name) fails to recognise it as already added.
        public static string BuildDeviceName(hid_device_info d) {
            String manufacturer = String.IsNullOrEmpty(d.manufacturer_string) ? "" : d.manufacturer_string + " ";
            return manufacturer + d.product_string + '(' + d.vendor_id + '-' + d.product_id + '-' + d.serial_number + ')';
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

                if (enumerate.serial_number == null || !IsGameController(enumerate)) {
                    ptr = enumerate.next;
                    continue;
                }

                String name = BuildDeviceName(enumerate);
                if (!ContainsText(list_customControllers, name) && !ContainsText(list_allControllers, name) && !ContainsText(list_blacklistedControllers, name)) {
                    list_allControllers.Items.Add(new SController(name, enumerate.vendor_id, enumerate.product_id, GuessType(enumerate), enumerate.serial_number));
                    Console.WriteLine("Found controller "+ name);
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

        private void btn_blacklist_Click(object sender, EventArgs e) {
            if (list_allControllers.SelectedItem != null) {
                list_blacklistedControllers.Items.Add(list_allControllers.SelectedItem);
                list_allControllers.Items.Remove(list_allControllers.SelectedItem);

                list_allControllers.ClearSelected();
            }
        }

        private void btn_unblacklist_Click(object sender, EventArgs e) {
            if (list_blacklistedControllers.SelectedItem != null) {
                list_allControllers.Items.Add(list_blacklistedControllers.SelectedItem);
                list_blacklistedControllers.Items.Remove(list_blacklistedControllers.SelectedItem);

                list_blacklistedControllers.ClearSelected();
            }
        }

        private static void SaveControllerList(string filePath, ListBox source) {
            String sc = "";
            foreach (SController v in source.Items) {
                sc += v.Serialise() + "\r\n";
            }
            File.WriteAllText(filePath, sc);
        }

        private void btn_apply_Click(object sender, EventArgs e) {
            SaveControllerList(path, list_customControllers);
            CopyCustomControllers();

            SaveControllerList(blacklistPath, list_blacklistedControllers);
            CopyBlacklistedControllers();
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

        private void list_blacklistedControllers_SelectedValueChanged(object sender, EventArgs e) {
            if (list_blacklistedControllers.SelectedItem != null)
                tip_device.Show((list_blacklistedControllers.SelectedItem as SController).name, list_blacklistedControllers);
        }

        private void list_blacklistedControllers_MouseDown(object sender, MouseEventArgs e) {
            if (e.Y > list_blacklistedControllers.ItemHeight * list_blacklistedControllers.Items.Count)
                list_blacklistedControllers.SelectedItems.Clear();
        }

        private void chooseType_SelectedValueChanged(object sender, EventArgs e) {
            if (list_customControllers.SelectedItem != null) {
                SController v = (list_customControllers.SelectedItem as SController);
                v.type = (byte)(chooseType.SelectedIndex + 1);
            }
        }
    }
}
