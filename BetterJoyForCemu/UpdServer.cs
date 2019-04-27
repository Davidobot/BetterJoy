using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Force.Crc32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Configuration;

namespace BetterJoyForCemu {
	class UdpServer {
		private Socket udpSock;
		private uint serverId;
		private bool running;
		private byte[] recvBuffer = new byte[1024];

		List<Joycon> controllers;

		public MainForm form;

		public UdpServer(List<Joycon> p) {
			controllers = p;
		}

		enum MessageType {
			DSUC_VersionReq = 0x100000,
			DSUS_VersionRsp = 0x100000,
			DSUC_ListPorts = 0x100001,
			DSUS_PortInfo = 0x100001,
			DSUC_PadDataReq = 0x100002,
			DSUS_PadDataRsp = 0x100002,
		};

		private const ushort MaxProtocolVersion = 1001;

		class ClientRequestTimes {
			DateTime allPads;
			DateTime[] padIds;
			Dictionary<PhysicalAddress, DateTime> padMacs;

			public DateTime AllPadsTime { get { return allPads; } }
			public DateTime[] PadIdsTime { get { return padIds; } }
			public Dictionary<PhysicalAddress, DateTime> PadMacsTime { get { return padMacs; } }

			public ClientRequestTimes() {
				allPads = DateTime.MinValue;
				padIds = new DateTime[4];

				for (int i = 0; i < padIds.Length; i++)
					padIds[i] = DateTime.MinValue;

				padMacs = new Dictionary<PhysicalAddress, DateTime>();
			}

			public void RequestPadInfo(byte regFlags, byte idToReg, PhysicalAddress macToReg) {
				if (regFlags == 0)
					allPads = DateTime.UtcNow;
				else {
					if ((regFlags & 0x01) != 0) //id valid
					{
						if (idToReg < padIds.Length)
							padIds[idToReg] = DateTime.UtcNow;
					}
					if ((regFlags & 0x02) != 0) //mac valid
					{
						padMacs[macToReg] = DateTime.UtcNow;
					}
				}
			}
		}

		private Dictionary<IPEndPoint, ClientRequestTimes> clients = new Dictionary<IPEndPoint, ClientRequestTimes>();

		private int BeginPacket(byte[] packetBuf, ushort reqProtocolVersion = MaxProtocolVersion) {
			int currIdx = 0;
			packetBuf[currIdx++] = (byte)'D';
			packetBuf[currIdx++] = (byte)'S';
			packetBuf[currIdx++] = (byte)'U';
			packetBuf[currIdx++] = (byte)'S';

			Array.Copy(BitConverter.GetBytes((ushort)reqProtocolVersion), 0, packetBuf, currIdx, 2);
			currIdx += 2;

			Array.Copy(BitConverter.GetBytes((ushort)packetBuf.Length - 16), 0, packetBuf, currIdx, 2);
			currIdx += 2;

			Array.Clear(packetBuf, currIdx, 4); //place for crc
			currIdx += 4;

			Array.Copy(BitConverter.GetBytes((uint)serverId), 0, packetBuf, currIdx, 4);
			currIdx += 4;

			return currIdx;
		}

		private void FinishPacket(byte[] packetBuf) {
			Array.Clear(packetBuf, 8, 4);

			uint crcCalc = Crc32Algorithm.Compute(packetBuf);
			Array.Copy(BitConverter.GetBytes((uint)crcCalc), 0, packetBuf, 8, 4);
		}

		private void SendPacket(IPEndPoint clientEP, byte[] usefulData, ushort reqProtocolVersion = MaxProtocolVersion) {
			byte[] packetData = new byte[usefulData.Length + 16];
			int currIdx = BeginPacket(packetData, reqProtocolVersion);
			Array.Copy(usefulData, 0, packetData, currIdx, usefulData.Length);
			FinishPacket(packetData);

			try { udpSock.SendTo(packetData, clientEP); } catch (Exception e) { }
		}

		private void ProcessIncoming(byte[] localMsg, IPEndPoint clientEP) {
			try {
				int currIdx = 0;
				if (localMsg[0] != 'D' || localMsg[1] != 'S' || localMsg[2] != 'U' || localMsg[3] != 'C')
					return;
				else
					currIdx += 4;

				uint protocolVer = BitConverter.ToUInt16(localMsg, currIdx);
				currIdx += 2;

				if (protocolVer > MaxProtocolVersion)
					return;

				uint packetSize = BitConverter.ToUInt16(localMsg, currIdx);
				currIdx += 2;

				if (packetSize < 0)
					return;

				packetSize += 16; //size of header
				if (packetSize > localMsg.Length)
					return;
				else if (packetSize < localMsg.Length) {
					byte[] newMsg = new byte[packetSize];
					Array.Copy(localMsg, newMsg, packetSize);
					localMsg = newMsg;
				}

				uint crcValue = BitConverter.ToUInt32(localMsg, currIdx);
				//zero out the crc32 in the packet once we got it since that's whats needed for calculation
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;

				uint crcCalc = Crc32Algorithm.Compute(localMsg);
				if (crcValue != crcCalc)
					return;

				uint clientId = BitConverter.ToUInt32(localMsg, currIdx);
				currIdx += 4;

				uint messageType = BitConverter.ToUInt32(localMsg, currIdx);
				currIdx += 4;

				if (messageType == (uint)MessageType.DSUC_VersionReq) {
					byte[] outputData = new byte[8];
					int outIdx = 0;
					Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_VersionRsp), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes((ushort)MaxProtocolVersion), 0, outputData, outIdx, 2);
					outIdx += 2;
					outputData[outIdx++] = 0;
					outputData[outIdx++] = 0;

					SendPacket(clientEP, outputData, 1001);
				} else if (messageType == (uint)MessageType.DSUC_ListPorts) {
					// Requested information on gamepads - return MAC address
					int numPadRequests = BitConverter.ToInt32(localMsg, currIdx);
					currIdx += 4;
					if (numPadRequests < 0 || numPadRequests > 4)
						return;

					int requestsIdx = currIdx;
					for (int i = 0; i < numPadRequests; i++) {
						byte currRequest = localMsg[requestsIdx + i];
						if (currRequest < 0 || currRequest > 4)
							return;
					}

					byte[] outputData = new byte[16];
					for (byte i = 0; i < numPadRequests; i++) {
						byte currRequest = localMsg[requestsIdx + i];
						var padData = controllers[i];//controllers[currRequest];

						int outIdx = 0;
						Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_PortInfo), 0, outputData, outIdx, 4);
						outIdx += 4;

						outputData[outIdx++] = (byte)padData.PadId;
						outputData[outIdx++] = (byte)padData.constate;
						outputData[outIdx++] = (byte)padData.model;
						outputData[outIdx++] = (byte)padData.connection;

						var addressBytes = padData.PadMacAddress.GetAddressBytes();
						if (addressBytes.Length == 6) {
							outputData[outIdx++] = addressBytes[0];
							outputData[outIdx++] = addressBytes[1];
							outputData[outIdx++] = addressBytes[2];
							outputData[outIdx++] = addressBytes[3];
							outputData[outIdx++] = addressBytes[4];
							outputData[outIdx++] = addressBytes[5];
						} else {
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
						}

						outputData[outIdx++] = (byte)padData.battery;//(byte)padData.BatteryStatus;
						outputData[outIdx++] = 0;

						SendPacket(clientEP, outputData, 1001);
					}
				} else if (messageType == (uint)MessageType.DSUC_PadDataReq) {
					byte regFlags = localMsg[currIdx++];
					byte idToReg = localMsg[currIdx++];
					PhysicalAddress macToReg = null;
					{
						byte[] macBytes = new byte[6];
						Array.Copy(localMsg, currIdx, macBytes, 0, macBytes.Length);
						currIdx += macBytes.Length;
						macToReg = new PhysicalAddress(macBytes);
					}

					lock (clients) {
						if (clients.ContainsKey(clientEP))
							clients[clientEP].RequestPadInfo(regFlags, idToReg, macToReg);
						else {
							var clientTimes = new ClientRequestTimes();
							clientTimes.RequestPadInfo(regFlags, idToReg, macToReg);
							clients[clientEP] = clientTimes;
						}
					}
				}
			} catch (Exception e) { }
		}

		private void ReceiveCallback(IAsyncResult iar) {
			byte[] localMsg = null;
			EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);

			try {
				//Get the received message.
				Socket recvSock = (Socket)iar.AsyncState;
				int msgLen = recvSock.EndReceiveFrom(iar, ref clientEP);

				localMsg = new byte[msgLen];
				Array.Copy(recvBuffer, localMsg, msgLen);
			} catch (Exception e) { }

			//Start another receive as soon as we copied the data
			StartReceive();

			//Process the data if its valid
			if (localMsg != null) {
				ProcessIncoming(localMsg, (IPEndPoint)clientEP);
			}
		}
		private void StartReceive() {
			try {
				if (running) {
					//Start listening for a new message.
					EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
					udpSock.BeginReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref newClientEP, ReceiveCallback, udpSock);
				}
			} catch (SocketException ex) {
				uint IOC_IN = 0x80000000;
				uint IOC_VENDOR = 0x18000000;
				uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
				udpSock.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);

				StartReceive();
			}
		}

		public void Start(IPAddress ip, int port = 26760) {
			if (running) {
				if (udpSock != null) {
					udpSock.Close();
					udpSock = null;
				}
				running = false;
			}

			udpSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			try { udpSock.Bind(new IPEndPoint(ip, port)); } catch (SocketException ex) {
				udpSock.Close();
				udpSock = null;

				form.console.Text += "Could not start server. Make sure that only one instance of the program is running at a time and no other CemuHook applications are running.\r\n";
				return;
			}

			byte[] randomBuf = new byte[4];
			new Random().NextBytes(randomBuf);
			serverId = BitConverter.ToUInt32(randomBuf, 0);

			running = true;
			form.console.Text += String.Format("Starting server on {0}:{1}\r\n", ip.ToString(), port);
			StartReceive();
		}

		public void Stop() {
			running = false;
			if (udpSock != null) {
				udpSock.Close();
				udpSock = null;
			}
		}

		bool swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
		bool swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);
		private bool ReportToBuffer(Joycon hidReport, byte[] outputData, ref int outIdx) {
			outputData[outIdx] = 0;

			bool isLeft = hidReport.isLeft;

			if (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_LEFT : Joycon.Button.Y)) outputData[outIdx] |= 0x80;
			if (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_DOWN : Joycon.Button.B)) outputData[outIdx] |= 0x40;
			if (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_RIGHT : Joycon.Button.A)) outputData[outIdx] |= 0x20;
			if (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_UP : Joycon.Button.X)) outputData[outIdx] |= 0x10;

			if (hidReport.GetButton(Joycon.Button.PLUS)) outputData[outIdx] |= 0x08;
			if (hidReport.GetButton(isLeft ? Joycon.Button.STICK2 : Joycon.Button.STICK)) outputData[outIdx] |= 0x04;
			if (hidReport.GetButton(isLeft ? Joycon.Button.STICK : Joycon.Button.STICK2)) outputData[outIdx] |= 0x02;
			if (hidReport.GetButton(Joycon.Button.MINUS)) outputData[outIdx] |= 0x01;

			outputData[++outIdx] = 0;

			if (hidReport.GetButton(!swapXY ? (isLeft ? Joycon.Button.Y : Joycon.Button.DPAD_LEFT) : (isLeft ? Joycon.Button.X : Joycon.Button.DPAD_UP))) outputData[outIdx] |= 0x80;
			if (hidReport.GetButton(!swapAB ? (isLeft ? Joycon.Button.B : Joycon.Button.DPAD_DOWN) : (isLeft ? Joycon.Button.A : Joycon.Button.DPAD_RIGHT))) outputData[outIdx] |= 0x40;
			if (hidReport.GetButton(!swapAB ? (isLeft ? Joycon.Button.A : Joycon.Button.DPAD_RIGHT) : (isLeft ? Joycon.Button.B : Joycon.Button.DPAD_DOWN))) outputData[outIdx] |= 0x20;
			if (hidReport.GetButton(!swapXY ? (isLeft ? Joycon.Button.X : Joycon.Button.DPAD_UP) : (isLeft ? Joycon.Button.Y : Joycon.Button.DPAD_LEFT))) outputData[outIdx] |= 0x10;

			if (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER2_1 : Joycon.Button.SHOULDER_1)) outputData[outIdx] |= 0x08;
			if (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER_1 : Joycon.Button.SHOULDER2_1)) outputData[outIdx] |= 0x04;
			if (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER2_2 : Joycon.Button.SHOULDER_2)) outputData[outIdx] |= 0x02;
			if (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER_2 : Joycon.Button.SHOULDER2_2)) outputData[outIdx] |= 0x01;

			outputData[++outIdx] = (hidReport.GetButton(Joycon.Button.HOME)) ? (byte)1 : (byte)0;
			outputData[++outIdx] = 0; // no touch pad

			float[] leftStick = hidReport.GetStick(); // 127 is 0
			outputData[++outIdx] = (byte)(Math.Max(0, Math.Min(255, 127 + leftStick[0] * 127)));
			outputData[++outIdx] = (byte)(Math.Max(0, Math.Min(255, 127 + leftStick[1] * 127)));

			float[] rightStick = hidReport.GetStick2(); // 127 is 0
			outputData[++outIdx] = (byte)(Math.Max(0, Math.Min(255, 127 + rightStick[0] * 127)));
			outputData[++outIdx] = (byte)(Math.Max(0, Math.Min(255, 127 + rightStick[1] * 127)));

			//we don't have analog buttons so just use the Button enums (which give either 0 or 0xFF)
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_LEFT : Joycon.Button.Y)) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_DOWN : Joycon.Button.B)) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_RIGHT : Joycon.Button.A)) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.DPAD_UP : Joycon.Button.X)) ? (byte)0xFF : (byte)0;

			outputData[++outIdx] = (hidReport.GetButton(!swapXY ? (isLeft ? Joycon.Button.Y : Joycon.Button.DPAD_LEFT) : (isLeft ? Joycon.Button.X : Joycon.Button.DPAD_UP))) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(!swapAB ? (isLeft ? Joycon.Button.B : Joycon.Button.DPAD_DOWN) : (isLeft ? Joycon.Button.A : Joycon.Button.DPAD_RIGHT))) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(!swapAB ? (isLeft ? Joycon.Button.A : Joycon.Button.DPAD_RIGHT) : (isLeft ? Joycon.Button.B : Joycon.Button.DPAD_DOWN))) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(!swapXY ? (isLeft ? Joycon.Button.X : Joycon.Button.DPAD_UP) : (isLeft ? Joycon.Button.Y : Joycon.Button.DPAD_LEFT))) ? (byte)0xFF : (byte)0;

			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER2_1 : Joycon.Button.SHOULDER_1)) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER_1 : Joycon.Button.SHOULDER2_1)) ? (byte)0xFF : (byte)0;

			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER2_2 : Joycon.Button.SHOULDER_2)) ? (byte)0xFF : (byte)0;
			outputData[++outIdx] = (hidReport.GetButton(isLeft ? Joycon.Button.SHOULDER_2 : Joycon.Button.SHOULDER2_2)) ? (byte)0xFF : (byte)0;

			outIdx++;

			//DS4 only: touchpad points
			for (int i = 0; i < 2; i++) {
				outIdx += 6;
			}

			//motion timestamp
			Array.Copy(BitConverter.GetBytes(hidReport.Timestamp), 0, outputData, outIdx, 8);
			outIdx += 8;

			//accelerometer
			{
				var accel = hidReport.GetAccel();
				if (accel != null) {
					Array.Copy(BitConverter.GetBytes(accel.Y), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes(-accel.Z), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes(accel.X), 0, outputData, outIdx, 4);
					outIdx += 4;
				} else {
					outIdx += 12;
					Console.WriteLine("No accelerometer reported.");
				}
			}

			//gyroscope
			{
				var gyro = hidReport.GetGyro();
				if (gyro != null) {
					Array.Copy(BitConverter.GetBytes(gyro.Y), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes(gyro.Z), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes(gyro.X), 0, outputData, outIdx, 4);
					outIdx += 4;
				} else {
					outIdx += 12;
					Console.WriteLine("No gyroscope reported.");
				}
			}

			return true;
		}

		public void NewReportIncoming(Joycon hidReport) {
			if (!running)
				return;

			var clientsList = new List<IPEndPoint>();
			var now = DateTime.UtcNow;
			lock (clients) {
				var clientsToDelete = new List<IPEndPoint>();

				foreach (var cl in clients) {
					const double TimeoutLimit = 5;

					if ((now - cl.Value.AllPadsTime).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else if ((hidReport.PadId >= 0 && hidReport.PadId <= 3) &&
							 (now - cl.Value.PadIdsTime[(byte)hidReport.PadId]).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else if (cl.Value.PadMacsTime.ContainsKey(hidReport.PadMacAddress) &&
							 (now - cl.Value.PadMacsTime[hidReport.PadMacAddress]).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else //check if this client is totally dead, and remove it if so
					{
						bool clientOk = false;
						for (int i = 0; i < cl.Value.PadIdsTime.Length; i++) {
							var dur = (now - cl.Value.PadIdsTime[i]).TotalSeconds;
							if (dur < TimeoutLimit) {
								clientOk = true;
								break;
							}
						}
						if (!clientOk) {
							foreach (var dict in cl.Value.PadMacsTime) {
								var dur = (now - dict.Value).TotalSeconds;
								if (dur < TimeoutLimit) {
									clientOk = true;
									break;
								}
							}

							if (!clientOk)
								clientsToDelete.Add(cl.Key);
						}
					}
				}

				foreach (var delCl in clientsToDelete) {
					clients.Remove(delCl);
				}
				clientsToDelete.Clear();
				clientsToDelete = null;
			}

			if (clientsList.Count <= 0)
				return;

			byte[] outputData = new byte[100];
			int outIdx = BeginPacket(outputData, 1001);
			Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_PadDataRsp), 0, outputData, outIdx, 4);
			outIdx += 4;

			outputData[outIdx++] = (byte)hidReport.PadId;
			outputData[outIdx++] = (byte)hidReport.constate;
			outputData[outIdx++] = (byte)hidReport.model;
			outputData[outIdx++] = (byte)hidReport.connection;
			{
				byte[] padMac = hidReport.PadMacAddress.GetAddressBytes();
				outputData[outIdx++] = padMac[0];
				outputData[outIdx++] = padMac[1];
				outputData[outIdx++] = padMac[2];
				outputData[outIdx++] = padMac[3];
				outputData[outIdx++] = padMac[4];
				outputData[outIdx++] = padMac[5];
			}

			outputData[outIdx++] = (byte)hidReport.battery;
			outputData[outIdx++] = 1;

			Array.Copy(BitConverter.GetBytes(hidReport.packetCounter), 0, outputData, outIdx, 4);
			outIdx += 4;

			if (!ReportToBuffer(hidReport, outputData, ref outIdx))
				return;
			else
				FinishPacket(outputData);

			foreach (var cl in clientsList) {
				try { udpSock.SendTo(outputData, cl); } catch (SocketException ex) { }
			}
			clientsList.Clear();
			clientsList = null;
		}
	}
}
