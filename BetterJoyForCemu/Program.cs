using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Timers;

using System.Net.NetworkInformation;
using System.Diagnostics;

using static BetterJoyForCemu.HIDapi;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using System.Net;
using System.Configuration;
using System.Net.Http;
using System.IO;
using System.Windows.Forms;

using System.ServiceProcess;

namespace BetterJoyForCemu {
    public class JoyconManager {
        public bool EnableIMU = true;
        public bool EnableLocalize = false;

        private const ushort vendor_id = 0x57e;
        private const ushort vendor_id_ = 0x057e;
        private const ushort product_l = 0x2006;
        private const ushort product_r = 0x2007;
        private const ushort product_pro = 0x2009;

        public List<Joycon> j; // Array of all connected Joy-Cons
        static JoyconManager instance;

        public MainForm form;

        public static JoyconManager Instance {
            get { return instance; }
        }

        public void Awake() {
            instance = this;
            int i = 0;

            j = new List<Joycon>();
            bool isLeft = false;
            HIDapi.hid_init();

            IntPtr ptr = HIDapi.hid_enumerate(vendor_id, 0x0);
            IntPtr top_ptr = ptr;

            if (ptr == IntPtr.Zero) {
                ptr = HIDapi.hid_enumerate(vendor_id_, 0x0);
                if (ptr == IntPtr.Zero) {
                    HIDapi.hid_free_enumeration(ptr);
                    form.console.Text += "No Joy-Cons found!\r\n";
                }
            }

            hid_device_info enumerate;
            while (ptr != IntPtr.Zero) {
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

                if (enumerate.product_id == product_l || enumerate.product_id == product_r || enumerate.product_id == product_pro) {
                    if (enumerate.product_id == product_l) {
                        isLeft = true;
                        form.console.Text += "Left Joy-Con connected.\r\n";
                    } else if (enumerate.product_id == product_r) {
                        isLeft = false;
                        form.console.Text += "Right Joy-Con connected.\r\n";
                    } else if (enumerate.product_id == product_pro) {
                        isLeft = true;
                        form.console.Text += "Pro controller connected.\r\n";
                    } else {
                        form.console.Text += "Non Joy-Con input device skipped.\r\n";
                    }

                    // Add controller to block-list for HidGuardian
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/affected/add/");
                    string postData = @"hwids=HID\" + enumerate.path.Split('#')[1].ToUpper();
                    var data = Encoding.UTF8.GetBytes(postData);

                    request.Method = "POST";
                    request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                    request.ContentLength = data.Length;

                    using (var stream = request.GetRequestStream())
                        stream.Write(data, 0, data.Length);

                    try {
                        var response = (HttpWebResponse)request.GetResponse();
                        var responseString = new StreamReader(response.GetResponseStream()).ReadToEnd();
                    } catch (Exception e) {
                        form.console.Text += "Unable to add controller to block-list.\r\n";
                    }
                    // -------------------- //

                    IntPtr handle = HIDapi.hid_open_path(enumerate.path);
                    try {
                        HIDapi.hid_set_nonblocking(handle, 1);
                    } catch (Exception e) {
                        form.console.Text += "Unable to open path to device - are you using the correct (64 vs 32-bit) version for your PC?\r\n";
                        break;
                    }

                    j.Add(new Joycon(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, j.Count, enumerate.product_id == product_pro, enumerate.serial_number == "000000000001"));

                    j.Last().form = form;

                    byte[] mac = new byte[6];
                    for (int n = 0; n < 6; n++)
                        mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
                    j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);

                    ++i;
                }
                ptr = enumerate.next;
            }

            int found = 0;
            int minPadID = 10;
            foreach (Joycon v in j) {
                if (!v.isPro) {
                    found++;
                    minPadID = Math.Min(v.PadId, minPadID);
                }
            }

            if (found == 2) {
                form.console.Text += "Both joycons successfully found.\r\n";
                Joycon temp = null;
                foreach (Joycon v in j) {
                    if (!v.isPro) {
                        v.LED = (byte)(0x1 << minPadID);
                        
                        if (temp == null)
                            temp = v;
                        else {
                            temp.other = v;
                            v.other = temp;

                            temp.xin.Dispose();
                            temp.xin = null;
                        }
                    }
                } // Join up the two joycons
            } else if (found != 0)
                form.console.Text += "Only one joycon found. Using in single joycone mode.\r\n";

            HIDapi.hid_free_enumeration(top_ptr);
        }

        public void Start() {
            for (int i = 0; i < j.Count; ++i) {
                Joycon jc = j[i];

                if (jc.xin != null)
                    jc.xin.Connect();

                //byte LEDs = 0x0;
               // LEDs |= (byte)(0x1 << jc.PadId);
                jc.Attach(leds_: jc.LED);
                jc.Begin();
            }
        }

        public void Update() {
            for (int i = 0; i < j.Count; ++i)
                j[i].Update();
        }

        public void OnApplicationQuit() {
            for (int i = 0; i < j.Count; ++i) {
                j[i].Detach();

                if (j[i].xin != null) {
                    j[i].xin.Disconnect();
                    j[i].xin.Dispose();
                }
            }
        }
    }

    // Custom timer class because system timers have a limit of 15.6ms
    class HighResTimer {
        double interval = 0;
        double frequency = 0;

        Thread thread;

        public delegate void ActionDelegate();
        ActionDelegate func;

        bool run = false;

        public HighResTimer(double f, ActionDelegate a) {
            frequency = f;
            interval = 1.0 / f;

            func = a;
        }

        public void Start() {
            run = true;
            thread = new Thread(new ThreadStart(Run));
            thread.Start();
        }

        void Run() {
            while (run) {
                func();
                int timeToSleep = (int)(interval * 1000);
                Thread.Sleep(timeToSleep);
            }
        }

        public void Stop() {
            run = false;
        }
    }

    class Program {
        public static PhysicalAddress btMAC = new PhysicalAddress(new byte[] { 0, 0, 0, 0, 0, 0 });
        public static UdpServer server;
        static double pollsPerSecond = 60.0;

        public static ViGEmClient emClient;

        private static readonly HttpClient client = new HttpClient();

        static JoyconManager mgr;
        static HighResTimer timer;
        static string pid;

        static MainForm form;

        public static void Start() {
            pid = Process.GetCurrentProcess().Id.ToString(); // get current process id for HidCerberus.Srv

            try {
                var HidCerberusService = new ServiceController("HidCerberus Service");
                if (HidCerberusService.Status == ServiceControllerStatus.Stopped) {
                    form.console.Text += "HidGuardian was stopped. Starting...\r\n";

                    try {
                        HidCerberusService.Start();
                    } catch (Exception e) {
                        form.console.Text += "Unable to start HidGuardian - everything should work fine without it, but if you need it, run the app again as an admin.\r\n";
                    }
                }
            } catch (Exception e) {
                form.console.Text += "Unable to start HidGuardian - everything should work fine without it, but if you need it, install it properly as admin.\r\n";
            }

            HttpWebResponse response;
            if (Boolean.Parse(ConfigurationSettings.AppSettings["PurgeWhitelist"])) {
                try {
                    response = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/whitelist/purge/").GetResponse(); // remove all programs allowed to see controller
                } catch (Exception e) {
                    form.console.Text += "Unable to purge whitelist.\r\n";
                }
            }

            try {
                response = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/whitelist/add/" + pid).GetResponse(); // add BetterJoyForCemu to allowed processes 
            } catch (Exception e) {
                form.console.Text += "Unable to add program to whitelist.\r\n";
            }

            emClient = new ViGEmClient(); // Manages emulated XInput

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                // Get local BT host MAC
                if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) {
                    if (nic.Name.Split()[0] == "Bluetooth") {
                        btMAC = nic.GetPhysicalAddress();
                    }
                }
            }

            mgr = new JoyconManager();
            mgr.form = form;
            mgr.Awake();
            mgr.Start();

            server = new UdpServer(mgr.j);
            server.form = form;

            server.Start(IPAddress.Parse(ConfigurationSettings.AppSettings["IP"]), Int32.Parse(ConfigurationSettings.AppSettings["Port"]));
            timer = new HighResTimer(pollsPerSecond, new HighResTimer.ActionDelegate(mgr.Update));
            timer.Start();

            form.console.Text += "All systems go\r\n";
        }

        public static void Stop() {
            try {
                HttpWebResponse response = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/whitelist/remove/" + pid).GetResponse(); // add BetterJoyForCemu to allowed processes 
            } catch (Exception e) {
                form.console.Text += "Unable to remove program from whitelist.\r\n";
            }

            server.Stop();
            timer.Stop();
            mgr.OnApplicationQuit();
        }

        static void Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            form = new MainForm();
            Application.Run(form);
        }
    }
}