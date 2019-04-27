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

        System.Timers.Timer controllerCheck;

        bool useHIDG = Boolean.Parse(ConfigurationManager.AppSettings["UseHIDG"]);

        public static JoyconManager Instance {
            get { return instance; }
        }

        public void Awake() {
            instance = this;
            j = new List<Joycon>();
            HIDapi.hid_init();
        }

        public void Start() {
            controllerCheck = new System.Timers.Timer(2000); // check every 2 seconds
            controllerCheck.Elapsed += CheckForNewControllersTime;
            controllerCheck.Start();
        }

        bool ControllerAlreadyAdded(string path) {
            foreach (Joycon v in j)
                if (v.path == path)
                    return true;
            return false;
        }

        void CleanUp() { // removes dropped controllers from list
            List<Joycon> rem = new List<Joycon>();
            for (int i = 0; i < j.Count; i++) {
                Joycon v = j[i];
                if (v.state == Joycon.state_.DROPPED) {
                    if (v.other != null)
                        v.other.other = null; // The other of the other is the joycon itself

                    v.Detach(); rem.Add(v);

                    foreach (Button b in form.con) {
                        if (b.Enabled & b.Tag == v) {
                            b.Invoke(new MethodInvoker(delegate {
                                b.BackColor = System.Drawing.Color.FromArgb(0x00, System.Drawing.SystemColors.Control);
                                b.Enabled = false;
                                b.BackgroundImage = Properties.Resources.cross;
                            }));
                            break;
                        }
                    }

                    form.AppendTextBox("Removed dropped controller to list. Can be reconnected.\r\n");
                }
            }

            foreach (Joycon v in rem)
                j.Remove(v);
        }

        void CheckForNewControllersTime(Object source, ElapsedEventArgs e) {
            if (Config.Value("ProgressiveScan")) {
                CheckForNewControllers();
            }
        }

        public void CheckForNewControllers() {
            CleanUp();

            // move all code for initializing devices here and well as the initial code from Start()
            bool isLeft = false;
            IntPtr ptr = HIDapi.hid_enumerate(vendor_id, 0x0);
            IntPtr top_ptr = ptr;

            hid_device_info enumerate; // Add device to list
            bool foundNew = false;
            while (ptr != IntPtr.Zero) {
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));
                if (form.nonOriginal)
                {
                    enumerate.product_id = product_pro;
                }

                if ((enumerate.product_id == product_l || enumerate.product_id == product_r || enumerate.product_id == product_pro) && !ControllerAlreadyAdded(enumerate.path)) {
                    switch (enumerate.product_id) {
                        case product_l:
                            isLeft = true;
                            form.AppendTextBox("Left Joy-Con connected.\r\n"); break;
                        case product_r:
                            isLeft = false;
                            form.AppendTextBox("Right Joy-Con connected.\r\n"); break;
                        case product_pro:
                            isLeft = true;
                            form.AppendTextBox("Pro controller connected.\r\n"); break;
                        default:
                            form.AppendTextBox("Non Joy-Con Nintendo input device skipped.\r\n"); break;
                    }

                    // Add controller to block-list for HidGuardian
                    if (useHIDG) {
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
                        } catch {
                            form.AppendTextBox("Unable to add controller to block-list.\r\n");
                        }
                    } else { // Remove affected devices from list
                        try {
                            HttpWebResponse r1 = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/affected/purge/").GetResponse();
                        } catch { }
                    }
                    // -------------------- //

                    IntPtr handle = HIDapi.hid_open_path(enumerate.path);
                    try {
                        HIDapi.hid_set_nonblocking(handle, 1);
                    } catch {
                        form.AppendTextBox("Unable to open path to device - are you using the correct (64 vs 32-bit) version for your PC?\r\n");
                        break;
                    }

                    j.Add(new Joycon(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, enumerate.path, enumerate.serial_number, j.Count, enumerate.product_id == product_pro));

                    foundNew = true;
                    j.Last().form = form;

                    if (j.Count < 5) {
                        int ii = -1;
                        foreach (Button v in form.con) {
                            ii++;
                            if (!v.Enabled) {
                                System.Drawing.Bitmap temp;
                                switch (enumerate.product_id) {
                                    case (product_l):
                                        temp = Properties.Resources.jc_left_s; break;
                                    case (product_r):
                                        temp = Properties.Resources.jc_right_s; break;
                                    case (product_pro):
                                        temp = Properties.Resources.pro; break;
                                    default:
                                        temp = Properties.Resources.cross; break;
                                }

                                v.Invoke(new MethodInvoker(delegate {
                                    v.Tag = j.Last(); // assign controller to button
                                    v.Enabled = true;
                                    v.Click += new EventHandler(form.conBtnClick);
                                    v.BackgroundImage = temp;
                                }));

                                form.loc[ii].Invoke(new MethodInvoker(delegate {
                                    form.loc[ii].Tag = v;
                                    form.loc[ii].Click += new EventHandler(form.locBtnClick);
                                }));

                                break;
                            }
                        }
                    }

                    byte[] mac = new byte[6];
                    for (int n = 0; n < 6; n++)
                        mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
                    j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);
                }

                ptr = enumerate.next;
            }

            if (foundNew) { // attempt to auto join-up joycons on connection
                Joycon temp = null;
                foreach (Joycon v in j) {
                    if (!v.isPro) {
                        if (temp == null)
                            temp = v;
                        else if (temp.isLeft != v.isLeft && v.other == null) {
                            temp.other = v;
                            v.other = temp;

                            //Set both Joycon LEDs to the one with the lowest ID
                            byte led = temp.LED <= v.LED ? temp.LED : v.LED;
                            temp.LED = led;
                            v.LED = led;
                            temp.SetLED(led);
                            v.SetLED(led);

                            temp.xin.Dispose();
                            temp.xin = null;

                            foreach (Button b in form.con)
                                if (b.Tag == v || b.Tag == temp) {
                                    Joycon tt = (b.Tag == v) ? v : (b.Tag == temp) ? temp : v;
                                    b.BackgroundImage = tt.isLeft ? Properties.Resources.jc_left : Properties.Resources.jc_right;
                                }

                            temp = null;    // repeat
                        }       
                    }
                }
            }

            HIDapi.hid_free_enumeration(top_ptr);

            foreach (Joycon jc in j) { // Connect device straight away
                if (jc.state == Joycon.state_.NOT_ATTACHED) {
                    if (jc.xin != null)
                        jc.xin.Connect();

                    jc.Attach(leds_: jc.LED);
                    jc.Begin();
                    if (form.nonOriginal)
                    {
                        jc.getActiveData();
                    }
                    
                }
            }
        }

        public void Update() {
            for (int i = 0; i < j.Count; ++i)
                j[i].Update();
        }

        public void OnApplicationQuit() {
            foreach (Joycon v in j) {
                v.Detach();

                if (v.xin != null) {
                    v.xin.Disconnect();
                    v.xin.Dispose();
                }
            }

            controllerCheck.Stop();
            HIDapi.hid_exit();
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
            thread.IsBackground = true;
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
        static double pollsPerSecond = 120.0;

        public static ViGEmClient emClient;

        private static readonly HttpClient client = new HttpClient();

        public static JoyconManager mgr;
        static HighResTimer timer;
        static string pid;

        static MainForm form;

        static bool useHIDG = Boolean.Parse(ConfigurationManager.AppSettings["UseHIDG"]);

        public static void Start() {
            pid = Process.GetCurrentProcess().Id.ToString(); // get current process id for HidCerberus.Srv

            if (useHIDG) {
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
                if (Boolean.Parse(ConfigurationManager.AppSettings["PurgeWhitelist"])) {
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
            } else {
                form.console.Text += "HidGuardian is disabled.\r\n";
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
            mgr.CheckForNewControllers();
            mgr.Start();

            server = new UdpServer(mgr.j);
            server.form = form;

            server.Start(IPAddress.Parse(ConfigurationManager.AppSettings["IP"]), Int32.Parse(ConfigurationManager.AppSettings["Port"]));
            timer = new HighResTimer(pollsPerSecond, new HighResTimer.ActionDelegate(mgr.Update));
            timer.Start();

            form.console.Text += "All systems go\r\n";
        }

        public static void Stop() {
            try {
                HttpWebResponse response = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/whitelist/remove/" + pid).GetResponse();
            } catch (Exception e) {
                form.console.Text += "Unable to remove program from whitelist.\r\n";
            }

            if (Boolean.Parse(ConfigurationManager.AppSettings["PurgeAffectedDevices"])) {
                try {
                    HttpWebResponse r1 = (HttpWebResponse)WebRequest.Create(@"http://localhost:26762/api/v1/hidguardian/affected/purge/").GetResponse();
                } catch { }
            }

            server.Stop();
            timer.Stop();
            mgr.OnApplicationQuit();

            form.console.Text += "";
        }

        static void Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            form = new MainForm();
            Application.Run(form);
        }
    }
}