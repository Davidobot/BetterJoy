using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int JoyConLeft = 1;
    private const int JoyConRight = 2;
    private const int ProController = 3;

    private static readonly object StatsLock = new();
    private static readonly Dictionary<int, DeviceStats> Stats = new();
    private static readonly Dictionary<int, string> DeviceNames = new();
    private static readonly Jsl.EventCallback Callback = OnControllerEvent;
    private static volatile bool stopping;

    private static int Main(string[] args)
    {
        int durationSeconds = 60;
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length)
                durationSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--output" && i + 1 < args.Length)
                outputPath = args[++i];
            else
            {
                Console.Error.WriteLine("Usage: JoyShockTiming [--seconds N] [--output PATH]");
                return 2;
            }
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping = true;
        };

        StreamWriter? log = outputPath == null ? null : new StreamWriter(outputPath, append: false)
        {
            AutoFlush = true
        };

        try
        {
            int reportedCount = Jsl.JslConnectDevices();
            int[] handles = new int[Math.Max(16, reportedCount)];
            int connectedCount = Jsl.JslGetConnectedDeviceHandles(handles, handles.Length);

            WriteLine(log, $"JoyShockLibrary reported {connectedCount} connected device(s).");
            if (connectedCount == 0)
            {
                WriteLine(log, "No controllers are visible to JoyShockLibrary.");
                return 3;
            }

            lock (StatsLock)
            {
                for (int i = 0; i < connectedCount; i++)
                {
                    int handle = handles[i];
                    string name = ControllerName(Jsl.JslGetControllerType(handle));
                    DeviceNames[handle] = name;
                    Stats[handle] = new DeviceStats();
                    WriteLine(log, $"handle={handle} type={name}");
                }
            }

            Jsl.JslSetCallback(Callback);
            WriteLine(log, $"Collecting callback cadence for {durationSeconds} seconds. Press Ctrl+C to stop.");
            WriteLine(log, "Each row is one second. wall=Stopwatch callback spacing; jsl=JSL-supplied deltaTime.");

            Stopwatch run = Stopwatch.StartNew();
            int previousSecond = -1;
            while (!stopping && run.Elapsed.TotalSeconds < durationSeconds)
            {
                Thread.Sleep(25);
                int second = (int)run.Elapsed.TotalSeconds;
                if (second == previousSecond)
                    continue;

                previousSecond = second;
                if (second == 0)
                    continue;

                foreach (Snapshot snapshot in TakeWindowSnapshots())
                    WriteLine(log, FormatSnapshot(DateTime.Now, snapshot, "window"));
            }

            WriteLine(log, "--- FINAL ---");
            foreach (Snapshot snapshot in TakeOverallSnapshots())
                WriteLine(log, FormatSnapshot(DateTime.Now, snapshot, "overall"));

            return 0;
        }
        finally
        {
            Jsl.JslDisconnectAndDisposeAll();
            log?.Dispose();
        }
    }

    private static void OnControllerEvent(int handle, Jsl.JoyShockState state,
        Jsl.JoyShockState lastState, Jsl.ImuState imuState,
        Jsl.ImuState lastImuState, float deltaTime)
    {
        long now = Stopwatch.GetTimestamp();
        lock (StatsLock)
        {
            if (!Stats.TryGetValue(handle, out DeviceStats? stats))
                return;

            stats.Record(now, deltaTime * 1000.0);
        }
    }

    private static List<Snapshot> TakeWindowSnapshots()
    {
        lock (StatsLock)
        {
            return Stats.Select(pair =>
            {
                Snapshot snapshot = pair.Value.Window.Snapshot(pair.Key, DeviceNames[pair.Key]);
                pair.Value.Window.Reset();
                return snapshot;
            }).ToList();
        }
    }

    private static List<Snapshot> TakeOverallSnapshots()
    {
        lock (StatsLock)
        {
            return Stats.Select(pair =>
                pair.Value.Overall.Snapshot(pair.Key, DeviceNames[pair.Key])).ToList();
        }
    }

    private static string FormatSnapshot(DateTime now, Snapshot value, string scope)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff} scope={1} handle={2} type={3} callbacks={4} " +
            "wall_ms avg={5:F3} min={6:F3} max={7:F3} ge30={8} ge45={9} ge75={10} ge100={11} " +
            "jsl_ms avg={12:F3} min={13:F3} max={14:F3} ge30={15} ge45={16} ge75={17} ge100={18}",
            now, scope, value.Handle, value.Name, value.Callbacks,
            value.WallAverage, value.WallMinimum, value.WallMaximum,
            value.WallGe30, value.WallGe45, value.WallGe75, value.WallGe100,
            value.JslAverage, value.JslMinimum, value.JslMaximum,
            value.JslGe30, value.JslGe45, value.JslGe75, value.JslGe100);
    }

    private static string ControllerName(int type) => type switch
    {
        JoyConLeft => "JoyCon-L",
        JoyConRight => "JoyCon-R",
        ProController => "Pro",
        4 => "DualShock4",
        5 => "DualSense",
        _ => $"Unknown-{type}"
    };

    private static void WriteLine(StreamWriter? log, string text)
    {
        Console.WriteLine(text);
        log?.WriteLine(text);
    }

    private sealed class DeviceStats
    {
        public long LastTimestamp;
        public readonly Aggregate Window = new();
        public readonly Aggregate Overall = new();

        public void Record(long now, double jslDeltaMs)
        {
            double? wallDeltaMs = LastTimestamp == 0
                ? null
                : (now - LastTimestamp) * 1000.0 / Stopwatch.Frequency;
            LastTimestamp = now;
            Window.Record(wallDeltaMs, jslDeltaMs);
            Overall.Record(wallDeltaMs, jslDeltaMs);
        }
    }

    private sealed class Aggregate
    {
        private long callbacks;
        private readonly Distribution wall = new();
        private readonly Distribution jsl = new();

        public void Record(double? wallDeltaMs, double jslDeltaMs)
        {
            callbacks++;
            if (wallDeltaMs.HasValue)
                wall.Record(wallDeltaMs.Value);
            jsl.Record(jslDeltaMs);
        }

        public Snapshot Snapshot(int handle, string name) => new(
            handle, name, callbacks,
            wall.Average, wall.Minimum, wall.Maximum,
            wall.Ge30, wall.Ge45, wall.Ge75, wall.Ge100,
            jsl.Average, jsl.Minimum, jsl.Maximum,
            jsl.Ge30, jsl.Ge45, jsl.Ge75, jsl.Ge100);

        public void Reset()
        {
            callbacks = 0;
            wall.Reset();
            jsl.Reset();
        }
    }

    private sealed class Distribution
    {
        private long count;
        private double sum;
        public double Minimum { get; private set; } = double.PositiveInfinity;
        public double Maximum { get; private set; } = double.NegativeInfinity;
        public long Ge30 { get; private set; }
        public long Ge45 { get; private set; }
        public long Ge75 { get; private set; }
        public long Ge100 { get; private set; }
        public double Average => count == 0 ? 0 : sum / count;

        public void Record(double value)
        {
            count++;
            sum += value;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
            if (value >= 30) Ge30++;
            if (value >= 45) Ge45++;
            if (value >= 75) Ge75++;
            if (value >= 100) Ge100++;
        }

        public void Reset()
        {
            count = 0;
            sum = 0;
            Minimum = double.PositiveInfinity;
            Maximum = double.NegativeInfinity;
            Ge30 = Ge45 = Ge75 = Ge100 = 0;
        }
    }

    private sealed record Snapshot(int Handle, string Name, long Callbacks,
        double WallAverage, double WallMinimum, double WallMaximum,
        long WallGe30, long WallGe45, long WallGe75, long WallGe100,
        double JslAverage, double JslMinimum, double JslMaximum,
        long JslGe30, long JslGe45, long JslGe75, long JslGe100);

    private static class Jsl
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct JoyShockState
        {
            public int Buttons;
            public float LeftTrigger;
            public float RightTrigger;
            public float LeftStickX;
            public float LeftStickY;
            public float RightStickX;
            public float RightStickY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ImuState
        {
            public float AccelX;
            public float AccelY;
            public float AccelZ;
            public float GyroX;
            public float GyroY;
            public float GyroZ;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(int handle, JoyShockState state,
            JoyShockState lastState, ImuState imuState, ImuState lastImuState,
            float deltaTime);

        [DllImport("JoyShockLibrary.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int JslConnectDevices();

        [DllImport("JoyShockLibrary.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int JslGetConnectedDeviceHandles([Out] int[] handles, int size);

        [DllImport("JoyShockLibrary.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void JslDisconnectAndDisposeAll();

        [DllImport("JoyShockLibrary.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void JslSetCallback(EventCallback callback);

        [DllImport("JoyShockLibrary.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int JslGetControllerType(int deviceId);
    }
}
