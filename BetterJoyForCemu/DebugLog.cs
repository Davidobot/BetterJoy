using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BetterJoyForCemu {
    // General-purpose file logger for diagnosing controller-state bugs (PadId assignment,
    // join/split, UI-slot rendering) - gated behind DebugLogging (see App.config, default off),
    // not a build-time distinction: every build (Debug and Release both) ships with this, it's
    // just off until a user/tester turns it on. Writes to debug.log in the data folder, never
    // the in-app console/textbox - see LogDualSenseRawDump (Joycon.cs) for the same pattern
    // applied to DualSense-specific raw report dumps.
    internal static class DebugLog {
        private static readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        private static int writerStarted = 0;

        internal static bool Enabled {
            get {
                try {
                    return Boolean.Parse(ConfigurationManager.AppSettings["DebugLogging"]);
                } catch {
                    return false;
                }
            }
        }

        public static void Write(string message) {
            if (!Enabled)
                return;

            if (Interlocked.CompareExchange(ref writerStarted, 1, 0) == 0) {
                new Thread(WriterLoop) { IsBackground = true, Name = "DebugLogWriter" }.Start();
            }
            queue.Enqueue(string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff} {1}\r\n", DateTime.Now, message));
        }

        private static void WriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "debug.log");
            while (true) {
                Thread.Sleep(250);
                if (queue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (queue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch { }
            }
        }
    }
}
