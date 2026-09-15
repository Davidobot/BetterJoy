using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // Tells a locally running OpenRGB instance to rescan for devices, so it picks BetterJoy's
    // exposed HID controller back up without the user manually clicking Rescan in OpenRGB's own
    // UI. Needed because OpenRGB's device list only reflects HID visibility as of its own last
    // scan, and BetterJoy's HidHide state can change afterward (a fresh connection, Passthrough
    // being toggled) - see Program.cs's two call sites (on Attach, and on a real HidHide
    // hidden-to-visible transition), both gated on Lighting Mode: OpenRGB.
    //
    // Talks to OpenRGB's SDK server directly over its documented TCP protocol (127.0.0.1:6742) -
    // no OpenRGB plugin or extra dependency needed. Packet IDs confirmed against OpenRGB's own
    // NetworkProtocol.h/NetworkServer.cpp source (CalcProgrammer1/OpenRGB), not just carried over
    // from the user's original openrgb-rescan-safe.ps1 unverified.
    //
    // A device newly appearing in OpenRGB's list (exactly what this rescan causes) has been
    // confirmed to corrupt other devices' lighting in Artemis 2 specifically - a downstream SDK
    // client's bug, not OpenRGB's or this code's. Artemis 2 only handles a device that was
    // already present when Artemis 2 itself started; one showing up mid-session breaks it. If
    // this symptom (other RGB devices glitching right after a controller connects) comes up
    // again, check which lighting app layer is actually consuming OpenRGB's feed before
    // re-investigating this class - WaitForRescanCompletion below is a real, worthwhile fix
    // (closing the connection before OpenRGB's synchronous rescan handler actually returns was a
    // genuine bug), but it was already confirmed not to be the cause of that report.
    internal static class OpenRgbRescan {
        private const string Host = "127.0.0.1";
        private const int Port = 6742;
        private const uint PacketIdSetClientName = 50;
        private const uint PacketIdDeviceListUpdated = 100;
        private const uint PacketIdDetectionComplete = 103;
        private const uint PacketIdRequestRescan = 140;
        private const string ClientName = "BetterJoy2";

        // How long to wait for OpenRGB to actually finish before giving up and closing anyway -
        // generous because real device detection (I2C/SMBus bus scanning especially) can
        // legitimately take a while, and closing too early is the confirmed bug this replaces.
        private const int MaxRescanWaitMs = 60000;

        private static int inFlight;

        // Fire-and-forget, single-flight like the original script's named mutex - a rescan
        // already in progress makes a second request redundant rather than additive, so this
        // drops it instead of queuing (matches the script's WaitOne(0) exiting immediately when
        // the mutex is already held).
        public static void RequestRescan() {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
                return;

            new Thread(RescanWorker) { IsBackground = true, Name = "OpenRgbRescan" }.Start();
        }

        // No settle delay - real-hardware testing (debug.log) showed the rescan completing
        // cleanly in well under a second, DEVICE_LIST_UPDATED received before this class ever
        // closed the connection, and the reported lighting breakage still happened anyway. That
        // rules out both the original premature-disconnect theory (WaitForRescanCompletion below
        // already fixed that, confirmed working) and simple timing/settle races - so there's
        // nothing left here worth waiting for. Whatever's actually breaking is on OpenRGB's side
        // (or another SDK client's reaction to its broadcast) after it already reports success,
        // not anything observable from this end of the socket.
        private const int SettleDelayMs = 0;

        private static void RescanWorker() {
            try {
                DebugLog.Write("OpenRgbRescan: settling for " + SettleDelayMs + "ms before connecting");
                Thread.Sleep(SettleDelayMs);

                using (var client = new TcpClient()) {
                    client.SendTimeout = 5000;
                    client.ReceiveTimeout = 5000;
                    client.Connect(Host, Port);
                    DebugLog.Write("OpenRgbRescan: connected, sending client name + rescan request");

                    using (NetworkStream stream = client.GetStream()) {
                        byte[] nameBytes = Encoding.ASCII.GetBytes(ClientName + "\0");
                        SendPacket(stream, PacketIdSetClientName, nameBytes);
                        Thread.Sleep(500);
                        SendPacket(stream, PacketIdRequestRescan, Array.Empty<byte>());
                        WaitForRescanCompletion(stream);
                    }
                }
                DebugLog.Write("OpenRgbRescan: done");
            } catch (Exception ex) {
                // OpenRGB not running/reachable is an expected common case (not everyone with
                // Lighting Mode: OpenRGB has it open right now) - log only, nothing to surface to
                // the user beyond the log.
                DebugLog.Write("OpenRgbRescan: rescan failed - " + ex.Message);
            } finally {
                Volatile.Write(ref inFlight, 0);
            }
        }

        // Reads and discards packets from the server until DEVICE_LIST_UPDATED or
        // DETECTION_COMPLETE actually arrives (confirmed via NetworkServer.cpp: both are
        // broadcast to every connected client, including this one, once RescanDevices() finishes)
        // - only then is it safe to close without cutting the server off mid-scan. Gives up after
        // MaxRescanWaitMs (logged, not thrown) rather than hanging forever if something about this
        // OpenRGB version/setup never sends either packet - a slow rescan closed a bit early is
        // still a lot better than the previous fixed-2-second guess.
        private static void WaitForRescanCompletion(NetworkStream stream) {
            byte[] header = new byte[16];
            var elapsed = Stopwatch.StartNew();

            while (elapsed.ElapsedMilliseconds < MaxRescanWaitMs) {
                int remaining = (int)(MaxRescanWaitMs - elapsed.ElapsedMilliseconds);
                if (remaining <= 0)
                    break;
                stream.ReadTimeout = Math.Max(1, remaining);

                if (!ReadExact(stream, header, 16)) {
                    DebugLog.Write("OpenRgbRescan: connection closed while waiting for completion");
                    return;
                }

                uint packetId = BitConverter.ToUInt32(header, 8);
                uint payloadLength = BitConverter.ToUInt32(header, 12);
                if (payloadLength > 0 && !ReadExact(stream, new byte[payloadLength], (int)payloadLength)) {
                    DebugLog.Write("OpenRgbRescan: connection closed mid-payload while waiting for completion");
                    return;
                }

                if (packetId == PacketIdDeviceListUpdated || packetId == PacketIdDetectionComplete) {
                    DebugLog.Write("OpenRgbRescan: rescan completion signal received (packet " + packetId + ")");
                    return;
                }
            }

            DebugLog.Write("OpenRgbRescan: gave up waiting for completion after " + MaxRescanWaitMs + "ms");
        }

        // NetworkStream.Read can return fewer bytes than requested on a single call - loop until
        // the full count is in hand or the connection is gone (0-byte read = orderly close).
        private static bool ReadExact(NetworkStream stream, byte[] buffer, int count) {
            int offset = 0;
            while (offset < count) {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    return false;
                offset += read;
            }
            return true;
        }

        // OpenRGB SDK wire format: 4-byte "ORGB" magic, 4-byte device index (0 - both packets
        // used here are server-wide, not addressed to a specific device), 4-byte packet ID,
        // 4-byte payload length, then the payload itself.
        private static void SendPacket(NetworkStream stream, uint packetId, byte[] payload) {
            byte[] header = new byte[16];
            header[0] = (byte)'O';
            header[1] = (byte)'R';
            header[2] = (byte)'G';
            header[3] = (byte)'B';
            Array.Copy(BitConverter.GetBytes(packetId), 0, header, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, header, 12, 4);

            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
