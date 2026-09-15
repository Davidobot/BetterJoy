using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // Shared by both the service process (DualShock4.cs's Poll-thread audio sender) and the
    // per-session input helper process (BluetoothAudioCapture.cs) - same DualShock4DebugLogging
    // flag DualShock4.cs's raw HID dump already uses, reused here instead of a second config
    // flag. Async queue + background writer, same shape as
    // DualShock4Controller.LogDualShock4RawDump - neither the Poll thread nor the WASAPI capture
    // callback thread can block on file I/O.
    internal static class AudioDebugLog {
        private static readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        private static int writerStarted;

        public static void Write(string tag, string message) {
            if (!Boolean.Parse(ConfigurationManager.AppSettings["DualShock4DebugLogging"]))
                return;

            if (Interlocked.CompareExchange(ref writerStarted, 1, 0) == 0) {
                new Thread(WriterLoop) { IsBackground = true, Name = "AudioDebugLogWriter" }.Start();
            }
            queue.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0:HH:mm:ss.fff} [{1}] {2}\r\n", DateTime.Now, tag, message));
        }

        private static void WriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "bluetooth_audio_debug.log");
            while (true) {
                Thread.Sleep(250);
                if (queue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (queue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // Diagnostic only: never let an unavailable log path affect audio I/O.
                }
            }
        }
    }
}
