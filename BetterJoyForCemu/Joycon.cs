using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;

using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    public class Joycon {
        float timing = 120.0f;

        public string path = String.Empty;
        public bool isPro = false;
        bool isUSB = false;
        public Joycon other;

        public bool send = true;

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
        };
        public DebugType debug_type = DebugType.NONE;
        public bool isLeft;
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };
        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        private IntPtr handle;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone;
        private UInt16[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone2;
        private UInt16[] stick2_precal = { 0, 0 };

        private bool stop_polling = false;
        private int timestamp;
        private bool first_imu_packet = true;
        private bool imu_enabled = false;
        private Int16[] acc_r = { 0, 0, 0 };
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g;

        private Int16[] gyr_r = { 0, 0, 0 };
        private Int16[] gyr_neutral = { 0, 0, 0 };
        private Int16[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g;

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private struct Rumble {
            private float h_f, l_f;
            public float t, amp, fullamp;
            public bool timed_rumble;

            public void set_vals(float low_freq, float high_freq, float amplitude, int time = 0) {
                h_f = high_freq;
                amp = amplitude;
                fullamp = amplitude;
                l_f = low_freq;
                timed_rumble = false;
                t = 0;
                if (time != 0) {
                    t = time / 1000f;
                    timed_rumble = true;
                }
            }
            public Rumble(float low_freq, float high_freq, float amplitude, int time = 0) {
                h_f = high_freq;
                amp = amplitude;
                fullamp = amplitude;
                l_f = low_freq;
                timed_rumble = false;
                t = 0;
                if (time != 0) {
                    t = time / 1000f;
                    timed_rumble = true;
                }
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }
            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                if (amp == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    l_f = clamp(l_f, 40.875885f, 626.286133f);
                    amp = clamp(amp, 0.0f, 1.0f);
                    h_f = clamp(h_f, 81.75177f, 1252.572266f);
                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(h_f * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(l_f * 0.1f, 2)) - 0x40);
                    byte hf_amp;
                    if (amp == 0) hf_amp = 0;
                    else if (amp < 0.117) hf_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                    else if (amp < 0.23) hf_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                    else hf_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;
                    rumble_data = new byte[8];
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)((hf >> 8) & 0xff);
                    rumble_data[2] = lf;
                    rumble_data[1] += hf_amp;
                    rumble_data[2] += (byte)((lf_amp >> 8) & 0xff);
                    rumble_data[3] += (byte)(lf_amp & 0xff);
                }
                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private byte global_count = 0;
        private string debug_str;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public Xbox360Controller xin;
        public Xbox360Report report;

        int rumblePeriod = Int32.Parse(ConfigurationManager.AppSettings["RumblePeriod"]);
        int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);

        public MainForm form;

        public byte LED = 0x0;

		public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, int id = 0, bool isPro=false, bool usb = false) {
			handle = handle_;
			imu_enabled = imu;
			do_localize = localize;
			rumble_obj = new Rumble(160, 320, 0);
			filterweight = alpha;
			isLeft = left;

			PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro;
			isUSB = usb;

            this.path = path;

			connection = isUSB ? 0x01 : 0x02;

            if (showAsXInput) {
                xin = new Xbox360Controller(Program.emClient);

                if (toRumble)
                    xin.FeedbackReceived += ReceiveRumble;
                report = new Xbox360Report();
            }
		}

		public void ReceiveRumble(object sender, Nefarius.ViGEm.Client.Targets.Xbox360.Xbox360FeedbackReceivedEventArgs e) {
			SetRumble(lowFreq, highFreq, (float) e.LargeMotor / (float) 255, rumblePeriod);

			if (other != null)
				other.SetRumble(lowFreq, highFreq, (float)e.LargeMotor / (float)255, rumblePeriod);
		}

		public void DebugPrint(String s, DebugType d) {
			if (debug_type == DebugType.NONE) return;
			if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
				form.console.Text += s + "\r\n";
			}
		}
		public bool GetButtonDown(Button b) {
			return buttons_down[(int)b];
		}
		public bool GetButton(Button b) {
			return buttons[(int)b];
		}
		public bool GetButtonUp(Button b) {
			return buttons_up[(int)b];
		}
		public float[] GetStick() {
			return stick;
		}
		public float[] GetStick2() {
			return stick2;
		}
		public Vector3 GetGyro() {
			return gyr_g;
		}
		public Vector3 GetAccel() {
			return acc_g;
		}
		public int Attach(byte leds_ = 0x0) {
			state = state_.ATTACHED;

			// Make sure command is received
			HIDapi.hid_set_nonblocking(handle, 0);

			byte[] a = { 0x0 };

			// Connect
			if (!isUSB) {
				// Input report mode
				Subcommand(0x03, new byte[] { 0x30 }, 1, false);
                
				a[0] = 0x1;
				dump_calibration_data();
            } else {
				Subcommand(0x03, new byte[] { 0x3f }, 1, false);

				a = Enumerable.Repeat((byte)0, 64).ToArray();
				form.AppendTextBox("Using USB.\r\n");

				a[0] = 0x80;
				a[1] = 0x01;
				HIDapi.hid_write(handle, a, new UIntPtr(2));
				HIDapi.hid_read(handle, a, new UIntPtr(64));

				if (a[2] != 0x3) {
					PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
				}

				// USB Pairing
				a = Enumerable.Repeat((byte)0, 64).ToArray();
				a[0] = 0x80; a[1] = 0x02; // Handshake
				HIDapi.hid_write(handle, a, new UIntPtr(2));

				a[0] = 0x80; a[1] = 0x03; // 3Mbit baud rate
				HIDapi.hid_write(handle, a, new UIntPtr(2));

				a[0] = 0x80; a[1] = 0x02; // Handshake at new baud rate
				HIDapi.hid_write(handle, a, new UIntPtr(2));

				a[0] = 0x80; a[1] = 0x04; // Prevent HID timeout
				HIDapi.hid_write(handle, a, new UIntPtr(2));

				dump_calibration_data();
			}

            BlinkLED();

            a[0] = leds_;
			Subcommand(0x30, a, 1);
			Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1, true);
			Subcommand(0x3, new byte[] { 0x30 }, 1, true);
			Subcommand(0x48, new byte[] { 0x01 }, 1, true);
			
			Subcommand(0x41, new byte[] { 0x03, 0x00, 0x00, 0x01 }, 4, false); // higher gyro performance rate

			DebugPrint("Done with init.", DebugType.COMMS);

			HIDapi.hid_set_nonblocking(handle, 1);

			return 0;
		}

        public void SetLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkLED() { // do not call after initial setup
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray(); // LED ring
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25, false);
        }

        private void BatteryChanged() { // battery changed level
            foreach (var v in form.con) {
                if (v.Tag == this) {
                    switch (battery) {
                        case 4:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                            break;
                        case 3:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                            break;
                        case 2:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.GreenYellow);
                            break;
                        case 1:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Orange);
                            break;
                        default:
                            v.BackColor = System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
                            break;
                    }
                }
            }
        }

		public void SetFilterCoeff(float a) {
			filterweight = a;
		}

		public void Detach() {
			stop_polling = true;

            if (xin != null) {
                xin.Disconnect(); xin.Dispose();
            }

			if (state > state_.NO_JOYCONS) {
                HIDapi.hid_set_nonblocking(handle, 0);

                Subcommand(0x40, new byte[] { 0x0 }, 1);
				//Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

				if (isUSB) {
					byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
					a[0] = 0x80; a[1] = 0x05; // Allow device to talk to BT again
					HIDapi.hid_write(handle, a, new UIntPtr(2));
                    a[0] = 0x80; a[1] = 0x06; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                }
			}
			if (state > state_.DROPPED) {
				HIDapi.hid_close(handle);
			}
			state = state_.NOT_ATTACHED;
		}

		private byte ts_en;
		private int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;
            HIDapi.hid_set_nonblocking(handle, 1);
			byte[] raw_buf = new byte[report_len];
			int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5000);
			if (ret > 0) {
                // Process packets as soon as they come
                for (int n = 0; n < 3; n++) {
					ExtractIMUValues(raw_buf, n);

					byte lag = (byte) Math.Max(0, raw_buf[1] - ts_en - 3);
					if (n == 0) {
						Timestamp += (ulong)lag * 5000; // add lag once
						ProcessButtonsAndStick(raw_buf);

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                            BatteryChanged();
					}
					Timestamp += 5000; // 5ms difference

					packetCounter++;
					if (Program.server != null)
						Program.server.NewReportIncoming(this);

					if (xin != null)
						xin.SendReport(report);
				}

				if (ts_en == raw_buf[1]) {
					form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
					DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);
				}
				ts_en = raw_buf[1];
				DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", ret, raw_buf[1]), DebugType.THREADING);
			}
			return ret;
		}

		private Thread PollThreadObj; // pro times out over time randomly if it was USB and then bluetooth??
		private void Poll() {
			int attempts = 0;
			Stopwatch watch = new Stopwatch();
			watch.Start();
			while (!stop_polling & state > state_.NO_JOYCONS) {
                if (isUSB || rumble_obj.t > 0)
                    SendRumble(rumble_obj.GetData());
                else if (watch.ElapsedMilliseconds >= 1000) {
					// Send a no-op operation as heartbeat to keep connection alive.
					// Do not send this too frequently, otherwise I/O would be too heavy and cause lag.
					// Needed for both BLUETOOTH and USB to not time out. Never remove pls
					SendRumble(rumble_obj.GetData());
					watch.Restart();
				}
                int a = ReceiveRaw();

                if (a > 0) {
                    state = state_.IMU_DATA_OK;
					attempts = 0;
				} else if (attempts > 240) {
					state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
					break;
				} else if (a < 0) {
					// An error on read.
					//form.AppendTextBox("Pause 5ms");
					Thread.Sleep((Int32)5);
					++attempts;
				} else if (a == 0) {
					// The non-blocking read timed out. No need to sleep.
					// No need to increase attempts because it's not an error.
				}
            }
		}

		public void Update() {
            if (state > state_.NO_JOYCONS) {	
				if (rumble_obj.timed_rumble) {
					if (rumble_obj.t < 0) {
						rumble_obj.set_vals(lowFreq, highFreq, 0, 0);
					} else {
						rumble_obj.t -= (1 / timing);
                        //rumble_obj.amp = (float) Math.Sin(((timing - rumble_obj.t * 1000f) / timing) * Math.PI) * rumble_obj.fullamp;
					}
				}
			}
		}

		public float[] otherStick = { 0, 0 };

		bool swapButtons = Boolean.Parse(ConfigurationManager.AppSettings["SwapButtons"]);
		private int ProcessButtonsAndStick(byte[] report_buf) {
			if (report_buf[0] == 0x00) return -1;

			stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
			stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
			stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

			if (isPro) {
				stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
				stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
				stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
			}

			stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
			stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));

			stick = CenterSticks(stick_precal, stick_cal, deadzone);

			if (isPro) {
				stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
				stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
				stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2);
			}

			// Read other Joycon's sticks
			if (isLeft && other != null) {
				stick2 = otherStick;
				other.otherStick = stick;
			}

			if (!isLeft && other != null) {
				Array.Copy(stick, stick2, 2);
				stick = otherStick;
				other.otherStick = stick2;
			}
			//

            // Set button states both for server and ViGEm
			lock (buttons) {
				lock (down_) {
					for (int i = 0; i < buttons.Length; ++i) {
						down_[i] = buttons[i];
					}
				}
				buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
				buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
				buttons[(int)Button.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
				buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
				buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
				buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
				buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
				buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
				buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
				buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
				buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
				buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

				if (isPro && xin != null) {
					buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
					buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
					buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
					buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

					buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
					buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
					buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;

					report.SetButtonState(Xbox360Buttons.A, buttons[(int)(!swapButtons ? Button.B : Button.A)]);
					report.SetButtonState(Xbox360Buttons.B, buttons[(int)(!swapButtons ? Button.A : Button.B)]);
					report.SetButtonState(Xbox360Buttons.Y, buttons[(int)(!swapButtons ? Button.X : Button.Y)]);
					report.SetButtonState(Xbox360Buttons.X, buttons[(int)(!swapButtons ? Button.Y : Button.X)]);
					report.SetButtonState(Xbox360Buttons.Up, buttons[(int)Button.DPAD_UP]);
					report.SetButtonState(Xbox360Buttons.Down, buttons[(int)Button.DPAD_DOWN]);
					report.SetButtonState(Xbox360Buttons.Left, buttons[(int)Button.DPAD_LEFT]);
					report.SetButtonState(Xbox360Buttons.Right, buttons[(int)Button.DPAD_RIGHT]);
					report.SetButtonState(Xbox360Buttons.Back, buttons[(int)Button.MINUS]);
					report.SetButtonState(Xbox360Buttons.Start, buttons[(int)Button.PLUS]);
					report.SetButtonState(Xbox360Buttons.Guide, buttons[(int)Button.HOME]);
					report.SetButtonState(Xbox360Buttons.LeftShoulder, buttons[(int)Button.SHOULDER_1]);
					report.SetButtonState(Xbox360Buttons.RightShoulder, buttons[(int)Button.SHOULDER2_1]);
					report.SetButtonState(Xbox360Buttons.LeftThumb, buttons[(int)Button.STICK]);
					report.SetButtonState(Xbox360Buttons.RightThumb, buttons[(int)Button.STICK2]);
				}

				if (other != null) {
					buttons[(int)(Button.B)] = other.buttons[(int)Button.DPAD_DOWN];
					buttons[(int)(Button.A)] = other.buttons[(int)Button.DPAD_RIGHT];
					buttons[(int)(Button.X)] = other.buttons[(int)Button.DPAD_UP];
					buttons[(int)(Button.Y)] = other.buttons[(int)Button.DPAD_LEFT];

					buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
					buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1];
					buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2];
				}

				if (isLeft && other != null) {
					buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
					buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
				}

				if (!isLeft && other != null) {
					buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
				}

				if (!isPro && xin != null) {
                    if (other != null) {
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.A : Xbox360Buttons.B, buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.B : Xbox360Buttons.A, buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.Y : Xbox360Buttons.X, buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.X : Xbox360Buttons.Y, buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)]);
                        report.SetButtonState(Xbox360Buttons.Up, buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)]);
                        report.SetButtonState(Xbox360Buttons.Down, buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)]);
                        report.SetButtonState(Xbox360Buttons.Left, buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)]);
                        report.SetButtonState(Xbox360Buttons.Right, buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)]);
                        report.SetButtonState(Xbox360Buttons.Back, buttons[(int)Button.MINUS]);
                        report.SetButtonState(Xbox360Buttons.Start, buttons[(int)Button.PLUS]);
                        report.SetButtonState(Xbox360Buttons.Guide, buttons[(int)Button.HOME]);
                        report.SetButtonState(Xbox360Buttons.LeftShoulder, buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)]);
                        report.SetButtonState(Xbox360Buttons.RightShoulder, buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)]);
                        report.SetButtonState(Xbox360Buttons.LeftThumb, buttons[(int)(isLeft ? Button.STICK : Button.STICK2)]);
                        report.SetButtonState(Xbox360Buttons.RightThumb, buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)]);
                    } else { // single joycon mode
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.A : Xbox360Buttons.B, buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.B : Xbox360Buttons.A, buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.Y : Xbox360Buttons.X, buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)]);
                        report.SetButtonState(!swapButtons ? Xbox360Buttons.X : Xbox360Buttons.Y, buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)]);
                        report.SetButtonState(Xbox360Buttons.Back, buttons[(int)Button.MINUS] | buttons[(int)Button.HOME]);
                        report.SetButtonState(Xbox360Buttons.Start, buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE]);

                        report.SetButtonState(Xbox360Buttons.LeftShoulder, buttons[(int)Button.SL]);
                        report.SetButtonState(Xbox360Buttons.RightShoulder, buttons[(int)Button.SR]);

                        report.SetButtonState(Xbox360Buttons.LeftThumb, buttons[(int)Button.STICK]);
                    }
				}

				lock (buttons_up) {
					lock (buttons_down) {
						for (int i = 0; i < buttons.Length; ++i) {
							buttons_up[i] = (down_[i] & !buttons[i]);
							buttons_down[i] = (!down_[i] & buttons[i]);
						}
					}
				}
			}

			if (xin != null) {
                if (other != null || isPro) {
                    report.SetAxis(Xbox360Axes.LeftThumbX, CastStickValue(stick[0]));
                    report.SetAxis(Xbox360Axes.LeftThumbY, CastStickValue(stick[1]));
                    report.SetAxis(Xbox360Axes.RightThumbX, CastStickValue(stick2[0]));
                    report.SetAxis(Xbox360Axes.RightThumbY, CastStickValue(stick2[1]));
                } else { // single joycon mode
                    report.SetAxis(Xbox360Axes.LeftThumbY, CastStickValue((isLeft ? 1 : -1) * stick[0]));
                    report.SetAxis(Xbox360Axes.LeftThumbX, CastStickValue((isLeft ? -1 : 1) * stick[1]));
                }
                report.SetAxis(Xbox360Axes.LeftTrigger, (short)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? Int16.MaxValue : 0));
				report.SetAxis(Xbox360Axes.RightTrigger, (short)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? Int16.MaxValue : 0));
			}

			return 0;
		}

		private void ExtractIMUValues(byte[] report_buf, int n = 0) {
			gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
			gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
			gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
			acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
			acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
			acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

			Int16[] offset;
			if (isPro)
				offset = pro_hor_offset;
			else if (isLeft)
				offset = left_hor_offset;
			else
				offset = right_hor_offset;

			for (int i = 0; i < 3; ++i) {
				switch (i) {
					case 0:
						acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
						gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

						break;
					case 1:
						acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
						gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

						break;
					case 2:
						acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                        gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

						break;
				}
			}

            if (other == null && !isPro) { // single joycon mode; Z do not swap, rest do
                if (isLeft) {
                    acc_g.X = -acc_g.X;
                    gyr_g.X = -gyr_g.X;
                } else {
                    gyr_g.Y = -gyr_g.Y;
                }
                
                float temp = acc_g.X; acc_g.X = acc_g.Y; acc_g.Y = temp;
                temp = gyr_g.X; gyr_g.X = gyr_g.Y; gyr_g.Y = temp;
            }
		}

		public void Begin() {
			if (PollThreadObj == null) {
				PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
				PollThreadObj.Start();

				form.AppendTextBox("Starting poll thread.\r\n");
			} else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
		}

		public void Recenter() {
			first_imu_packet = true;
		}

        // Should really be called calculating stick data
		private float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz) {
			ushort[] t = cal;

			float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);
			return s;
		}

        private short CastStickValue(float stick_value)
        {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

		public void SetRumble(float low_freq, float high_freq, float amp, int time = 0) {
			if (state <= Joycon.state_.ATTACHED) return;
			//if (rumble_obj.timed_rumble == false || rumble_obj.t < 0) {
				rumble_obj = new Rumble(low_freq, high_freq, amp, time);
            //}
        }

		private void SendRumble(byte[] buf) {
			byte[] buf_ = new byte[report_len];
			buf_[0] = 0x10;
			buf_[1] = global_count;
			if (global_count == 0xf) global_count = 0;
			else ++global_count;
			Array.Copy(buf, 0, buf_, 2, 8);
			PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
			HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
		}

		private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
			byte[] buf_ = new byte[report_len];
			byte[] response = new byte[report_len];
			Array.Copy(default_buf, 0, buf_, 2, 8);
			Array.Copy(buf, 0, buf_, 11, len);
			buf_[10] = sc;
			buf_[1] = global_count;
			buf_[0] = 0x1;
			if (global_count == 0xf) global_count = 0;
			else ++global_count;
			if (print) { PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}"); };
			HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
			int res = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 50);
			if (res < 1) DebugPrint("No response.", DebugType.COMMS);
			else if (print) { PrintArray(response, DebugType.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}"); }
			return response;
		}

		private void dump_calibration_data() {
			byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
			bool found = false;
			for (int i = 0; i < 9; ++i) {
				if (buf_[i] != 0xff) {
					form.AppendTextBox("Using user stick calibration data.\r\n");
					found = true;
					break;
				}
			}
			if (!found) {
				form.AppendTextBox("Using factory stick calibration data.\r\n");
				buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
			}
			stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
			stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
			stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
			stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
			stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
			stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

			PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

			if (isPro) {
				buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
				found = false;
				for (int i = 0; i < 9; ++i) {
					if (buf_[i] != 0xff) {
						form.AppendTextBox("Using user stick calibration data.\r\n");
						found = true;
						break;
					}
				}
				if (!found) {
					form.AppendTextBox("Using factory stick calibration data.\r\n");
					buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
				}
				stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
				stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
				stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
				stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
				stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
				stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

				PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

				buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
				deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
			}

			buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
			deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

			buf_ = ReadSPI(0x80, 0x28, 10);
			acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
			acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
			acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

			buf_ = ReadSPI(0x80, 0x2E, 10);
			acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
			acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
			acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

			buf_ = ReadSPI(0x80, 0x34, 10);
			gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
			gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
			gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

			buf_ = ReadSPI(0x80, 0x3A, 10);
			gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
			gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
			gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

			PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

			// This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
			if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
				buf_ = ReadSPI(0x60, 0x20, 10);
				acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
				acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
				acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

				buf_ = ReadSPI(0x60, 0x26, 10);
				acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
				acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
				acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

				buf_ = ReadSPI(0x60, 0x2C, 10);
				gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
				gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
				gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

				buf_ = ReadSPI(0x60, 0x32, 10);
				gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
				gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
				gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

				PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
			}
		}

		private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
			byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
			byte[] read_buf = new byte[len];
			byte[] buf_ = new byte[len + 20];

			for (int i = 0; i < 100; ++i) {
				buf_ = Subcommand(0x10, buf, 5, false);
				if (buf_[15] == addr2 && buf_[16] == addr1) {
					break;
				}
			}
			Array.Copy(buf_, 20, read_buf, 0, len);
			if (print) PrintArray(read_buf, DebugType.COMMS, len);
			return read_buf;
		}

		private void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
			if (d != debug_type && debug_type != DebugType.ALL) return;
			if (len == 0) len = (uint)arr.Length;
			string tostr = "";
			for (int i = 0; i < len; ++i) {
				tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
			}
			DebugPrint(string.Format(format, tostr), d);
		}
	}
}
