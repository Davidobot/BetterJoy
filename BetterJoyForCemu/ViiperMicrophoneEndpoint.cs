using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BetterJoyForCemu {
    // Presents decoded controller microphone PCM through VIIPER's audio-only DualSense USB
    // device. VIIPER and usbip-win2 remain separate, independently licensed programs; BetterJoy
    // speaks only the public localhost API and V5 framed stream documented by VIIPER.
    internal sealed class ViiperMicrophoneEndpoint : IMicrophoneEndpoint {
        private const string Host = "127.0.0.1";
        private const int ApiPort = 3242;
        private const byte StreamVersion = 0x05;
        private const byte MicrophonePcmFrameType = 0x02;
        private const int HeaderLength = 16;
        private const int MicrophonePcmLength = 1920;

        private readonly object writeLock = new object();
        private readonly TcpClient streamClient;
        private readonly NetworkStream stream;
        private readonly uint busId;
        private readonly string deviceId;
        private readonly byte[] writeBuffer = new byte[HeaderLength + MicrophonePcmLength];
        private readonly Thread drainThread;
        private uint sequence;
        private int disposed;

        private ViiperMicrophoneEndpoint(TcpClient streamClient, uint busId, string deviceId) {
            this.streamClient = streamClient;
            stream = streamClient.GetStream();
            this.busId = busId;
            this.deviceId = deviceId;

            drainThread = new Thread(DrainFeedback) {
                IsBackground = true,
                Name = "BetterJoy2ViiperFeedback"
            };
            drainThread.Start();
        }

        public static ViiperMicrophoneEndpoint Open() {
            ViiperServer.Acquire();
            uint busId = 0;
            string deviceId = null;
            try {
                Dictionary<string, object> bus = SendRequest("bus/create", "0");
                busId = Convert.ToUInt32(Required(bus, "busId"));

                var request = new Dictionary<string, object> {
                    { "type", "dualsenseaudioonlyduplexv5" }
                };
                string payload = new JavaScriptSerializer().Serialize(request);
                Dictionary<string, object> device = SendRequest(
                    "bus/" + busId + "/add", payload);
                deviceId = Convert.ToString(Required(device, "devId"));
                int usbipPort = Convert.ToInt32(Required(device, "usbipPort"));
                if (usbipPort < 0)
                    throw new IOException("VIIPER did not attach the virtual audio device to usbip-win2.");

                TcpClient streamClient = Connect(0);
                byte[] handshake = Encoding.UTF8.GetBytes(
                    "bus/" + busId + "/" + deviceId + "\0");
                streamClient.GetStream().Write(handshake, 0, handshake.Length);
                return new ViiperMicrophoneEndpoint(streamClient, busId, deviceId);
            } catch {
                if (!String.IsNullOrEmpty(deviceId))
                    TrySendRequest("bus/" + busId + "/remove", deviceId);
                if (busId != 0)
                    TrySendRequest("bus/remove", busId.ToString());
                ViiperServer.Release();
                throw;
            }
        }

        public void WriteMicrophonePcm(byte[] stereoPcm) {
            if (stereoPcm == null || stereoPcm.Length != MicrophonePcmLength)
                throw new ArgumentException("DualSense microphone PCM must contain 480 stereo S16LE frames.",
                    nameof(stereoPcm));
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ViiperMicrophoneEndpoint));

            lock (writeLock) {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(ViiperMicrophoneEndpoint));

                byte[] frame = writeBuffer;
                frame[0] = (byte)'V';
                frame[1] = (byte)'P';
                frame[2] = (byte)'C';
                frame[3] = (byte)'M';
                frame[4] = StreamVersion;
                frame[5] = MicrophonePcmFrameType;
                frame[6] = (byte)(MicrophonePcmLength & 0xFF);
                frame[7] = (byte)(MicrophonePcmLength >> 8);
                uint currentSequence = sequence++;
                frame[8] = (byte)currentSequence;
                frame[9] = (byte)(currentSequence >> 8);
                frame[10] = (byte)(currentSequence >> 16);
                frame[11] = (byte)(currentSequence >> 24);
                Buffer.BlockCopy(stereoPcm, 0, frame, HeaderLength, stereoPcm.Length);
                uint crc = FramedCrc32(frame, frame.Length);
                frame[12] = (byte)crc;
                frame[13] = (byte)(crc >> 8);
                frame[14] = (byte)(crc >> 16);
                frame[15] = (byte)(crc >> 24);
                stream.Write(frame, 0, frame.Length);
            }
        }

        public bool IsMicrophoneInterfaceActive() {
            Dictionary<string, object> response = SendRequest(
                "bus/" + busId + "/list");
            // JavaScriptSerializer deserializes a JSON array as System.Collections.ArrayList, not
            // object[] - the previous "as object[]" cast failed on every call, immediately after
            // every successful connection (confirmed via the Windows Event Log: "is available"
            // followed by this exact error, every ~12 seconds), which is what actually made VIIPER
            // appear to "never start" - it was crash-looping past its first status check.
            System.Collections.ArrayList devices =
                Required(response, "devices") as System.Collections.ArrayList;
            if (devices == null)
                throw new IOException("VIIPER returned an invalid device list.");

            foreach (object item in devices) {
                var device = item as Dictionary<string, object>;
                if (device == null || !String.Equals(Convert.ToString(
                        Required(device, "devId")), deviceId,
                        StringComparison.Ordinal))
                    continue;

                var details = Required(device, "deviceSpecific") as
                    Dictionary<string, object>;
                if (details == null)
                    throw new IOException("VIIPER omitted microphone interface state.");
                return Convert.ToBoolean(Required(details,
                    "microphoneInterfaceActive"));
            }

            throw new IOException("The VIIPER microphone device disappeared.");
        }

        private void DrainFeedback() {
            byte[] header = new byte[HeaderLength];
            byte[] payload = new byte[4096];
            try {
                while (Volatile.Read(ref disposed) == 0) {
                    ReadExactly(stream, header, 0, header.Length);
                    if (header[0] != (byte)'V' || header[1] != (byte)'P' ||
                        header[2] != (byte)'C' || header[3] != (byte)'M' ||
                        header[4] != StreamVersion)
                        throw new IOException("VIIPER returned an invalid V5 stream frame.");
                    int length = header[6] | (header[7] << 8);
                    while (length > 0) {
                        int chunk = Math.Min(length, payload.Length);
                        ReadExactly(stream, payload, 0, chunk);
                        length -= chunk;
                    }
                }
            } catch {
                // The microphone writer owns user-visible failure reporting. Closing or removing
                // the virtual device also interrupts this blocking read as part of normal stop.
            }
        }

        private static void ReadExactly(Stream source, byte[] buffer, int offset, int count) {
            while (count > 0) {
                int read = source.Read(buffer, offset, count);
                if (read <= 0)
                    throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private static uint FramedCrc32(byte[] frame, int length) {
            uint crc = 0xFFFFFFFFu;
            for (int index = 4; index < 12; index++)
                crc = UpdateCrc32(crc, frame[index]);
            for (int index = HeaderLength; index < length; index++)
                crc = UpdateCrc32(crc, frame[index]);
            return ~crc;
        }

        private static uint UpdateCrc32(uint crc, byte value) {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            return crc;
        }

        private static Dictionary<string, object> SendRequest(string path, string payload = null) {
            string response = SendRequestRaw(path, payload);
            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> result = serializer.Deserialize<Dictionary<string, object>>(response);
            if (result == null)
                throw new IOException("VIIPER returned an empty response.");
            if (result.ContainsKey("status")) {
                string title = result.ContainsKey("title") ? Convert.ToString(result["title"]) : "error";
                string detail = result.ContainsKey("detail") ? Convert.ToString(result["detail"]) : response;
                throw new IOException("VIIPER API " + title + ": " + detail);
            }
            return result;
        }

        private static object Required(Dictionary<string, object> response, string key) {
            object value;
            if (!response.TryGetValue(key, out value) || value == null)
                throw new IOException("VIIPER omitted " + key + " from its response.");
            return value;
        }

        private static string SendRequestRaw(string path, string payload = null) {
            using (TcpClient client = Connect(5000)) {
                NetworkStream network = client.GetStream();
                string request = String.IsNullOrEmpty(payload) ? path : path + " " + payload;
                byte[] bytes = Encoding.UTF8.GetBytes(request + "\0");
                network.Write(bytes, 0, bytes.Length);
                using (var response = new MemoryStream()) {
                    byte[] buffer = new byte[1024];
                    int read;
                    while ((read = network.Read(buffer, 0, buffer.Length)) > 0)
                        response.Write(buffer, 0, read);
                    return Encoding.UTF8.GetString(response.ToArray()).TrimEnd('\r', '\n');
                }
            }
        }

        private static void TrySendRequest(string path, string payload) {
            try { SendRequestRaw(path, payload); } catch { }
        }

        private static TcpClient Connect(int receiveTimeout,
            int connectTimeout = 2000) {
            var client = new TcpClient {
                NoDelay = true,
                SendTimeout = 2000,
                ReceiveTimeout = receiveTimeout
            };
            IAsyncResult pending = client.BeginConnect(Host, ApiPort, null, null);
            try {
                if (!pending.AsyncWaitHandle.WaitOne(connectTimeout))
                    throw new IOException("Timed out connecting to the VIIPER audio backend.");
                client.EndConnect(pending);
                return client;
            } catch (Exception ex) {
                client.Close();
                if (ex is IOException)
                    throw;
                throw new IOException("Could not connect to the VIIPER audio backend: " + ex.Message, ex);
            } finally {
                pending.AsyncWaitHandle.Close();
            }
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try { streamClient.Close(); } catch { }
            if (drainThread != null && drainThread.IsAlive &&
                Thread.CurrentThread.ManagedThreadId != drainThread.ManagedThreadId)
                drainThread.Join(500);
            TrySendRequest("bus/" + busId + "/remove", deviceId);
            TrySendRequest("bus/remove", busId.ToString());
            ViiperServer.Release();
        }

        private static class ViiperServer {
            private static readonly object Sync = new object();
            private static Process ownedProcess;
            private static int users;

            public static void Acquire() {
                lock (Sync) {
                    if (!CanConnect()) {
                        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "Drivers", "VIIPER-0.1.0-x64.exe");
                        if (!File.Exists(executable))
                            throw new IOException("The optional VIIPER microphone backend is not installed.");

                        ownedProcess = StartServer(executable);
                        var timeout = Stopwatch.StartNew();
                        while (timeout.ElapsedMilliseconds < 5000 && !CanConnect())
                            Thread.Sleep(100);
                        if (!CanConnect()) {
                            StopOwnedProcess();
                            throw new IOException("VIIPER started but its localhost API did not become ready. " +
                                "Install the optional Bluetooth microphone backend and restart Windows if requested.");
                        }
                    }
                    users++;
                }
            }

            public static void Release() {
                lock (Sync) {
                    if (users > 0)
                        users--;
                    if (users == 0)
                        StopOwnedProcess();
                }
            }

            private static bool CanConnect() {
                try {
                    using (TcpClient probe = Connect(250, 250)) { }
                    return true;
                } catch {
                    return false;
                }
            }

            private static Process StartServer(string executable) {
                // BetterJoy normally owns controllers from its LocalSystem service. Audio
                // endpoints, however, belong to the signed-in Windows session. Use the same
                // proven service-to-session launcher as the input helper so VIIPER and its UAC
                // device enumerate where recording applications can open them. Direct GUI mode
                // lacks the service privileges required by that launcher and falls back normally.
                string commandLine = "\"" + executable + "\" server";
                if (SessionLauncher.TryLaunchInActiveSession(commandLine,
                        out int processId)) {
                    try {
                        return Process.GetProcessById(processId);
                    } catch { }
                }

                return Process.Start(new ProcessStartInfo {
                    FileName = executable,
                    Arguments = "server",
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }

            private static void StopOwnedProcess() {
                Process process = ownedProcess;
                ownedProcess = null;
                if (process == null)
                    return;
                try {
                    if (!process.HasExited)
                        process.Kill();
                } catch { }
                process.Dispose();
            }
        }
    }
}
