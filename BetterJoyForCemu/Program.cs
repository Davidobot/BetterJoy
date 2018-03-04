using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Timers;
using static BetterJoyForCemu.HIDapi;
using System.Net.NetworkInformation;
using System.Diagnostics;

namespace BetterJoyForCemu {
	public class JoyconManager {
		// Settings accessible via Unity
		public bool EnableIMU = true;
		public bool EnableLocalize = false;

		

		// Different operating systems either do or don't like the trailing zero
		private const ushort vendor_id = 0x57e;
		private const ushort vendor_id_ = 0x057e;
		private const ushort product_l = 0x2006;
		private const ushort product_r = 0x2007;
		private const ushort product_pro = 0x2009;

		public List<Joycon> j; // Array of all connected Joy-Cons
		static JoyconManager instance;

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
					Console.WriteLine("No Joy-Cons found!");
				}
			}

			string path = "";
			hid_device_info enumerate;
			while (ptr != IntPtr.Zero) {
				enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

				if (enumerate.product_id == product_l || enumerate.product_id == product_r || enumerate.product_id == product_pro) {
					if (enumerate.product_id == product_l) {
						isLeft = true;
						Console.WriteLine("Left Joy-Con connected.");
					} else if (enumerate.product_id == product_r) {
						isLeft = false;
						Console.WriteLine("Right Joy-Con connected.");
					} else if (enumerate.product_id == product_pro) {
						isLeft = true;
						Console.WriteLine("Pro controller connected.");
					} else {
						Console.WriteLine("Non Joy-Con input device skipped.");
					}

					IntPtr handle = HIDapi.hid_open_path(enumerate.path);
					HIDapi.hid_set_nonblocking(handle, 1);

					j.Add(new Joycon(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, j.Count, enumerate.product_id == product_pro, enumerate.serial_number == "000000000001"));

					byte[] mac = new byte[6];
					for (int n = 0; n < 6; n++)
						mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
					j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);		

					++i;
				}
				ptr = enumerate.next;
			}

			HIDapi.hid_free_enumeration(top_ptr);
		}

		public void Start() {
			for (int i = 0; i < j.Count; ++i) {
				Joycon jc = j[i];
				byte LEDs = 0x0;
				LEDs |= (byte)(0x1 << i);
				jc.Attach(leds_: LEDs);
				jc.Begin();
			}
		}

		public void Update() {
			for (int i = 0; i < j.Count; ++i) {
				j[i].Update();

				/*if (j.Count > 0) {
					Joycon jj = j[i];

					if (jj.GetButtonDown(Joycon.Button.DPAD_DOWN))
						jj.SetRumble(160, 320, 0.6f, 200);
				}*/
			}
		}

		public void OnApplicationQuit() {
			for (int i = 0; i < j.Count; ++i) {
				j[i].Detach();
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
		static double pollsPerSecond = 120.0;

		static void Main(string[] args) {
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
				// Get local BT host MAC
				if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) {
					if (nic.Name.Split()[0] == "Bluetooth") {
						btMAC = nic.GetPhysicalAddress();
					}
				}
			}

			JoyconManager mgr = new JoyconManager();
			mgr.Awake();
			mgr.Start();

			server = new UdpServer(mgr.j);

			//updateThread = new Thread(new ThreadStart(mgr.Update));
			//updateThread.Start();

			server.Start(26760);
			HighResTimer timer = new HighResTimer(pollsPerSecond, new HighResTimer.ActionDelegate(mgr.Update));
			timer.Start();

			Console.Write("Press enter to quit.");
			Console.ReadLine();

			server.Stop();
			timer.Stop();
			mgr.OnApplicationQuit();
		}
	}
}
