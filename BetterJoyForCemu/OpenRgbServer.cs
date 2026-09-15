using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BetterJoyForCemu.Collections;

namespace BetterJoyForCemu {
    // Loopback-only OpenRGB SDK protocol SERVER - the other half of OpenRgbRescan.cs's client
    // role. Exposes exactly one fixed "BetterJoy2" device, always present for as long as this
    // server is running, so a server OpenRGB is configured to connect to (its own Settings > SDK
    // Client tab) has it from OpenRGB's own startup instead of it appearing/disappearing
    // mid-session - confirmed against OpenRGB's own ResourceManager.cpp that a saved SDK Client
    // entry auto-reconnects on every OpenRGB launch, before any downstream consumer (Artemis 2,
    // etc.) ever queries OpenRGB's device list. That's what actually fixes third-party OpenRGB
    // clients that mishandle a device appearing out of nowhere mid-session.
    //
    // Deliberately ONE fixed device, not one per connected controller: an early version exposed
    // controllers individually, which meant the device count itself changed on every
    // connect/disconnect - the exact same "device appears mid-session" problem this class exists
    // to avoid, just moved one layer down. A color write applies to whichever controller(s) are
    // currently connected with Lighting Mode: OpenRGB (zero, one, or more); the color itself is
    // cached and reapplied to a controller as soon as it becomes eligible, so connecting later
    // doesn't lose whatever OpenRGB last set.
    //
    // Deliberately global, not per-profile: gated behind the "OpenRGB SDK server" Global mode
    // dropdown (Reassign.cs, see Modes below), independent of - and coexisting with -
    // OpenRgbRescan.cs's existing client-side rescan-nudge, which stays exactly as-is for anyone
    // who'd rather just expose the raw HID device to OpenRGB directly instead.
    //
    // Hard requirement: listens on 127.0.0.1 ONLY, never any other interface - every socket
    // operation here goes through that one guarantee (see Start() below).
    //
    // Wire format confirmed directly against OpenRGB's own source (CalcProgrammer1/OpenRGB:
    // NetworkProtocol.h, NetworkServer.cpp, RGBController.cpp, RGBControllerInterface.h), not
    // guessed - see each serialization method's own comment for the function it mirrors.
    // Targets protocol version 3: the vendor field (v1) plus per-mode brightness (v3, needed for
    // Battery's brightness slider - see ModeFlagHasBrightness). Fields introduced later still
    // (segments, flags, display names, device-specific configuration, v4-6) remain absent - every
    // descriptor and mode-update parsing still honor an older client's negotiated version, so
    // v0-v2 clients receive their older field layout without brightness. The server version is
    // always answered explicitly; leaving the request unanswered keeps current OpenRGB clients
    // in an incomplete handshake rather than reliably falling back.
    internal static class OpenRgbServer {
        private const int Port = 6743; // Deliberately not 6742 (OpenRGB's own default) - this is
                                        // a second, independent server the user points OpenRGB at.
        private const uint DeclaredProtocolVersion = 3;

        // Global Options' "OpenRGB SDK server" dropdown (Reassign.cs) - single source of truth
        // for both the stored OpenRgbServerMode value and the dropdown's own Items, same pattern
        // as ControllerMappings.LightingModes. EnabledWithCache additionally persists the last
        // color to OpenRgbServerCachedColor (see SyncEnabledState) - real hardware remembers its
        // last color on its own even across a restart; this virtual device otherwise can't, since
        // cachedColor below is normally just in-process memory.
        public const string ModeDisabled = "Disabled";
        public const string ModeEnabled = "Enabled";
        public const string ModeEnabledWithCache = "EnabledWithCache";
        public static readonly (string Value, string Label)[] Modes = {
            (ModeDisabled, "Disabled"), (ModeEnabled, "Enabled"),
            (ModeEnabledWithCache, "Enabled with cache"),
        };

        // Confirmed against RGBControllerInterface.h.
        private const int DeviceTypeGamepad = 10;
        private const uint ZoneTypeSingle = 0;
        private const uint ModeFlagHasPerLedColor = 1u << 5;
        private const uint ModeColorsPerLed = 1;
        private const uint ModeFlagHasSpeed = 1u << 0;
        private const uint ModeFlagHasBrightness = 1u << 4;
        private const uint ModeFlagHasModeSpecificColor = 1u << 6;
        private const uint ModeColorsNone = 0;
        private const uint ModeColorsModeSpecific = 2;
        private const uint BatteryBrightnessMin = 0;
        private const uint BatteryBrightnessMax = 100;

        private const uint PacketIdRequestControllerCount = 0;
        private const uint PacketIdRequestControllerData = 1;
        private const uint PacketIdRequestProtocolVersion = 40;
        private const uint PacketIdRgbControllerUpdateLeds = 1050;
        private const uint PacketIdRgbControllerUpdateZoneLeds = 1051;
        private const uint PacketIdRgbControllerUpdateSingleLed = 1052;
        private const uint PacketIdRgbControllerUpdateMode = 1101;
        private const uint PacketIdRgbControllerSaveMode = 1102;

        // The four modes BetterJoy2 advertises - Direct (index 0, unchanged), Rainbow Puke
        // (index 1, a discrete step-and-hold hue cycle: rainbowColorCount evenly-spaced
        // full-saturation hues, holding at each for 1/rainbowColorCount of a full cycle before
        // jumping to the next - not a smooth sweep, so "colors" visibly changes how chunky the
        // cycle looks, not just cosmetic default swatches), Color Shift (index 2, a smooth
        // crossfade through whatever colors OpenRGB's mode editor actually has picked - the
        // opposite of Rainbow Puke, which ignores picked colors entirely in favor of synthesized
        // hues), and Battery (index 3, a continuous red-to-yellow-to-green gradient by charge -
        // unlike the other two, no speed/colors params, and unlike every other mode here, each
        // eligible controller gets its OWN color instead of one shared value, since each may be
        // at a different charge). CycleSpeedMin/Max and CycleColorsMin/Max are shared by the two
        // cycling modes - same speed slider and color-count bounds, just applied differently.
        private const int ModeIndexDirect = 0;
        private const int ModeIndexRainbow = 1;
        private const int ModeIndexColorShift = 2;
        private const int ModeIndexBattery = 3;
        private const uint CycleSpeedMin = 1;
        private const uint CycleSpeedMax = 100;
        private const uint CycleColorsMin = 2;
        private const uint CycleColorsMax = 8;

        private sealed class ConnectedClient {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            // Version this client can actually parse. Protocol 0 is the safe legacy default;
            // negotiation/request payloads raise it, capped to the server's own maximum.
            public uint ProtocolVersion;
        }

        private static readonly object lifecycleLock = new object();
        private static TcpListener listener;
        private static readonly List<ConnectedClient> clients = new List<ConnectedClient>();
        private static readonly object clientsLock = new object();

        // The fixed device's app-level color, packed 0x00BBGGRR. -1 means OpenRGB has not sent a
        // color yet, so merely enabling the server never paints a controller black. Once set, it
        // is reapplied whenever a controller becomes eligible.
        private static int cachedColor = -1;

        // Mirrors OpenRgbServerMode == ModeEnabledWithCache - whether SetCachedColor should also
        // persist to OpenRgbServerCachedColor. A plain field, not read under lifecycleLock:
        // SetCachedColor runs on a client socket thread and only needs the latest value, not
        // exclusion with Start/Stop.
        private static volatile bool cachingEnabled;
        private const int PersistenceDebounceMs = 1000;
        private static readonly object persistenceLock = new object();
        private static readonly Timer persistenceTimer = new Timer(
            _ => FlushPersistence(), null, Timeout.Infinite, Timeout.Infinite);
        private static bool persistencePending;

        // Which advertised mode is active, and each cycling mode's own parameters. Persisted to
        // OpenRgbServerCachedModeState alongside cachedColor when caching is enabled (see
        // LoadOrPersistModeState) - OpenRGB has no memory of its own for a virtual device like
        // this one, so without it every restart would silently fall back to Direct, mode
        // parameters and all, the same "forgets like real hardware wouldn't" gap cachedColor
        // exists to close for a plain static color. All read/written via Volatile.Read/Write,
        // matching cachedColor's own convention - colorShiftColors is the one exception, guarded
        // by colorShiftColorsLock instead since it's a whole array, not a single primitive.
        private static int activeMode = ModeIndexDirect;
        private static uint rainbowSpeed = 50;
        private static int rainbowColorCount = 6;
        private static uint colorShiftSpeed = 50;
        private static readonly object colorShiftColorsLock = new object();
        private static uint[] colorShiftColors = DefaultColorShiftColors();
        private static uint batteryBrightness = 100;

        // True once ApplyUpdateMode has run at least once this session - mirrors cachedColor's
        // own -1 "nothing set yet" sentinel, just as a separate flag since activeMode's own
        // default (Direct) is a real, validly-selectable value and can't double as "unset" too.
        private static bool modeStateDirty;

        private static Thread animationThread;
        // A generation token, rather than one shared boolean, prevents a stopped animation loop
        // from coming back to life if the server is disabled and re-enabled before its final
        // sleep returns. Stop invalidates the old generation and joins it before Start can make
        // another one.
        private static int animationGeneration;
        private static readonly Stopwatch animationClock = Stopwatch.StartNew();

        private static uint[] DefaultColorShiftColors() {
            return new[] {
                ToRgbColor(255, 0, 0), ToRgbColor(0, 255, 0), ToRgbColor(0, 0, 255),
            };
        }

        // Called at startup and after every shared-config reload (HeadlessJoyconHost's config
        // watcher) - starts or stops the listener, and turns caching on/off, to match the
        // current "OpenRGB SDK server" Global option.
        public static void SyncEnabledState() {
            string mode = ApplicationSettings.StringValue("OpenRgbServerMode", ModeDisabled);
            bool shouldRun = mode != ModeDisabled;
            bool shouldCache = mode == ModeEnabledWithCache;

            lock (lifecycleLock) {
                bool wasCaching = cachingEnabled;
                if (!shouldCache && wasCaching)
                    FlushPersistence();
                cachingEnabled = shouldCache;

                if (shouldCache && !wasCaching) {
                    LoadOrPersistCachedColor();
                    LoadOrPersistModeState();
                }

                if (shouldRun && listener == null)
                    Start();
                else if (!shouldRun && listener != null)
                    Stop();
            }
        }

        // Called once, right as caching turns on. If this session already has a color (OpenRGB
        // set one before the mode was switched to "Enabled with cache"), persist it immediately
        // rather than waiting for the next color write. Otherwise there's nothing from this
        // session yet, so restore whatever a previous session last persisted.
        private static void LoadOrPersistCachedColor() {
            int color = Volatile.Read(ref cachedColor);
            if (color >= 0) {
                QueuePersistence();
                return;
            }

            int persisted;
            if (Int32.TryParse(ApplicationSettings.StringValue("OpenRgbServerCachedColor", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out persisted) &&
                    persisted >= 0) {
                Volatile.Write(ref cachedColor, persisted);
                ApplyCachedColorToEligibleControllers();
            }
        }

        // Same "already touched this session? persist; otherwise restore" shape as
        // LoadOrPersistCachedColor, just keyed off modeStateDirty instead of a sentinel value.
        private static void LoadOrPersistModeState() {
            if (Volatile.Read(ref modeStateDirty)) {
                QueuePersistence();
                return;
            }

            string[] parts = ApplicationSettings.StringValue("OpenRgbServerCachedModeState", "")
                .Split('|');
            if (parts.Length != 6)
                return;

            int mode; uint rSpeed; int rColors; uint csSpeed; uint brightness;
            if (!Int32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode) ||
                    !UInt32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rSpeed) ||
                    !Int32.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out rColors) ||
                    !UInt32.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out csSpeed) ||
                    !UInt32.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out brightness))
                return;
            if (mode != ModeIndexDirect && mode != ModeIndexRainbow &&
                    mode != ModeIndexColorShift && mode != ModeIndexBattery)
                return;

            string[] colorParts = parts[4].Split(',');
            var colors = new uint[colorParts.Length];
            for (int i = 0; i < colorParts.Length; i++) {
                if (!UInt32.TryParse(colorParts[i], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out colors[i]))
                    return; // malformed - bail without applying a partially-parsed state
            }
            if (colors.Length < CycleColorsMin || colors.Length > CycleColorsMax)
                return;

            Volatile.Write(ref activeMode, mode);
            Volatile.Write(ref rainbowSpeed, Math.Min(CycleSpeedMax, Math.Max(CycleSpeedMin, rSpeed)));
            Volatile.Write(ref rainbowColorCount,
                (int)Math.Min(CycleColorsMax, Math.Max(CycleColorsMin, (uint)rColors)));
            Volatile.Write(ref colorShiftSpeed,
                Math.Min(CycleSpeedMax, Math.Max(CycleSpeedMin, csSpeed)));
            Volatile.Write(ref batteryBrightness,
                Math.Min(BatteryBrightnessMax, Math.Max(BatteryBrightnessMin, brightness)));
            lock (colorShiftColorsLock) colorShiftColors = colors;
        }

        private static string CurrentModeState() {
            uint[] colors;
            lock (colorShiftColorsLock) colors = colorShiftColors;
            var colorParts = new string[colors.Length];
            for (int i = 0; i < colors.Length; i++)
                colorParts[i] = colors[i].ToString(CultureInfo.InvariantCulture);

            return Volatile.Read(ref activeMode).ToString(CultureInfo.InvariantCulture) +
                "|" + Volatile.Read(ref rainbowSpeed).ToString(CultureInfo.InvariantCulture) +
                "|" + Volatile.Read(ref rainbowColorCount).ToString(CultureInfo.InvariantCulture) +
                "|" + Volatile.Read(ref colorShiftSpeed).ToString(CultureInfo.InvariantCulture) +
                "|" + String.Join(",", colorParts) +
                "|" + Volatile.Read(ref batteryBrightness).ToString(CultureInfo.InvariantCulture);
        }

        // Direct-mode SDK clients commonly stream dozens of colors per second. Resetting this
        // one-shot timer coalesces that whole burst into one save after activity settles instead
        // of rewriting the executable config (and waking its watcher) for every animation frame.
        private static void QueuePersistence() {
            if (!cachingEnabled)
                return;
            lock (persistenceLock) {
                persistencePending = true;
                persistenceTimer.Change(PersistenceDebounceMs, Timeout.Infinite);
            }
        }

        private static void FlushPersistence() {
            Dictionary<string, string> values;
            lock (persistenceLock) {
                persistenceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                if (!persistencePending)
                    return;
                persistencePending = false;

                values = new Dictionary<string, string>();
                int color = Volatile.Read(ref cachedColor);
                if (color >= 0)
                    values["OpenRgbServerCachedColor"] =
                        color.ToString(CultureInfo.InvariantCulture);
                if (Volatile.Read(ref modeStateDirty))
                    values["OpenRgbServerCachedModeState"] = CurrentModeState();
            }

            if (values.Count == 0)
                return;
            try {
                ApplicationSettings.SetValues(values);
            } catch (ConfigurationErrorsException ex) {
                // Put the write back on the queue; a transient sharing/config error should not
                // permanently discard the latest state.
                lock (persistenceLock) {
                    persistencePending = true;
                    if (cachingEnabled)
                        persistenceTimer.Change(PersistenceDebounceMs, Timeout.Infinite);
                }
                DebugLog.Write("OpenRgbServer: failed to persist cached state - " + ex.Message);
            }
        }

        private static void Start() {
            try {
                // IPAddress.Loopback, never IPAddress.Any - the one hard requirement this class
                // exists to satisfy. Deliberately not configurable.
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "OpenRgbServerAccept" }.Start();

                int generation = Interlocked.Increment(ref animationGeneration);
                animationThread = new Thread(() => AnimationLoop(generation)) {
                    IsBackground = true,
                    Name = "OpenRgbServerAnimation",
                };
                animationThread.Start();

                DebugLog.Write("OpenRgbServer: listening on 127.0.0.1:" + Port);
            } catch (Exception ex) {
                try { listener?.Stop(); } catch { }
                listener = null;
                DebugLog.Write("OpenRgbServer: failed to start - " + ex.Message);
            }
        }

        private static void Stop() {
            try {
                listener?.Stop();
            } catch { }
            listener = null;
            Interlocked.Increment(ref animationGeneration);
            Thread stoppedAnimation = animationThread;
            animationThread = null;
            if (stoppedAnimation != null && stoppedAnimation != Thread.CurrentThread)
                stoppedAnimation.Join(1500); // longest loop sleep is 500ms

            lock (clientsLock) {
                foreach (ConnectedClient c in clients) {
                    try { c.Client.Close(); } catch { }
                }
                clients.Clear();
            }
            DebugLog.Write("OpenRgbServer: stopped");
        }

        // Called once per JoyconManager.ApplyControllerProfileOptions reconciliation pass
        // (Program.cs, ~every 2s, and after every config reload) - reapplies the cached color to
        // every controller currently eligible (connected, Lighting Mode: OpenRGB) rather than
        // only the ones that happened to be eligible the moment OpenRGB last sent a color. Cheap:
        // Controller.SetLightColor already dedupes an unchanged color internally.
        public static void ApplyCachedColorToEligibleControllers() {
            if (Volatile.Read(ref activeMode) != ModeIndexDirect)
                return; // A cycling mode's own animation loop owns output while it's active.

            int color = Volatile.Read(ref cachedColor);
            if (color < 0)
                return;

            byte cachedRed = (byte)(color & 0xFF);
            byte cachedGreen = (byte)((color >> 8) & 0xFF);
            byte cachedBlue = (byte)((color >> 16) & 0xFF);
            List<Controller> eligible = GetEligibleControllers();
            DebugLog.Write("OpenRgbServer: applying " + cachedRed + "," + cachedGreen + "," +
                cachedBlue + " to " + eligible.Count + " eligible controller(s)");
            foreach (Controller c in eligible)
                c.SetOpenRgbLightColor(cachedRed, cachedGreen, cachedBlue);
        }

        private static void AcceptLoop() {
            TcpListener currentListener = listener;
            try {
                while (true) {
                    TcpClient tcpClient = currentListener.AcceptTcpClient();
                    var connected = new ConnectedClient {
                        Client = tcpClient,
                        Stream = tcpClient.GetStream(),
                    };
                    lock (clientsLock) clients.Add(connected);
                    new Thread(() => ClientLoop(connected)) {
                        IsBackground = true,
                        Name = "OpenRgbServerClient",
                    }.Start();
                }
            } catch {
                // listener.Stop() breaks AcceptTcpClient() out with an exception - expected
                // shutdown path, nothing to log.
            }
        }

        // Drives Rainbow Puke, Color Shift, and Battery while any is the active mode; a cheap
        // idle sleep loop otherwise. Re-derives the eligible controller list only twice a second
        // rather than every tick - GetEligibleControllers logs one line per controller, and this
        // loop ticks every 20-50ms for the two cycling modes, so calling it per-tick would flood
        // the log and redo the profile/lighting-mode lookup far more often than useful.
        private static void AnimationLoop(int generation) {
            int lastStepIndex = -1;
            int lastMode = -1;
            List<Controller> eligible = new List<Controller>();
            long nextEligibilityRefresh = 0;

            while (Volatile.Read(ref animationGeneration) == generation) {
                int mode = Volatile.Read(ref activeMode);
                if (mode != lastMode) {
                    // Force the newly-selected mode to publish immediately. In particular,
                    // Rainbow must not inherit a matching step index from an earlier Rainbow run
                    // after Color Shift or Battery left a completely different color showing.
                    lastMode = mode;
                    lastStepIndex = -1;
                    nextEligibilityRefresh = 0;
                }
                if (mode != ModeIndexRainbow && mode != ModeIndexColorShift &&
                        mode != ModeIndexBattery) {
                    Thread.Sleep(200);
                    continue;
                }

                long now = animationClock.ElapsedMilliseconds;
                if (now >= nextEligibilityRefresh) {
                    eligible = GetEligibleControllers();
                    nextEligibilityRefresh = now + 500;
                }

                if (mode == ModeIndexRainbow) {
                    int colorCount = Volatile.Read(ref rainbowColorCount);
                    long stepDurationMs = Math.Max(20,
                        CycleDurationMs(Volatile.Read(ref rainbowSpeed)) / colorCount);
                    int stepIndex = (int)((now / stepDurationMs) % colorCount);
                    if (stepIndex != lastStepIndex) {
                        lastStepIndex = stepIndex;
                        (byte r, byte g, byte b) = HsvToRgb(360f * stepIndex / colorCount);
                        foreach (Controller c in eligible)
                            c.SetOpenRgbLightColor(r, g, b);
                    }
                    Thread.Sleep(20);
                } else if (mode == ModeIndexColorShift) {
                    (byte r, byte g, byte b) = CurrentColorShiftColor(now);
                    foreach (Controller c in eligible)
                        c.SetOpenRgbLightColor(r, g, b);
                    // Smooth crossfade, so every tick's color differs slightly - a gentler 50ms
                    // (~20fps) is plenty smooth to the eye while keeping HID output traffic well
                    // below Rainbow Puke's already-sparse discrete steps.
                    Thread.Sleep(50);
                } else {
                    // Each controller gets its OWN gradient color from its OWN charge, unlike
                    // every other mode here which fans one shared color out to all of them -
                    // charge barely moves, so this only needs to run alongside the eligibility
                    // refresh above, not on a tight animation tick.
                    uint brightness = Volatile.Read(ref batteryBrightness);
                    foreach (Controller c in eligible) {
                        (byte r, byte g, byte b) =
                            BatteryGradientColor(c.batteryPercent, brightness);
                        c.SetOpenRgbLightColor(r, g, b);
                    }
                    Thread.Sleep(500);
                }
            }
        }

        // Continuous red -> yellow -> green gradient by charge, unlike the existing per-profile
        // Battery Lighting Mode's deliberately coarse 3-band indicator (Program.cs's
        // BatteryLightColor) - this is a proper RGB effect for an RGB app to show, not a subtle
        // glanceable one, so full saturation (before the brightness scale-down below) rather than
        // that one's dimmed luminosity cap. Unknown charge (-1, batteryPercent's own default
        // before a real reading arrives) shows as off rather than guessing. brightnessPercent is
        // OpenRGB's own Brightness slider for this mode (0-100, ModeFlagHasBrightness) - applied
        // as a uniform scale after the gradient itself, same as a real device's brightness
        // control would sit downstream of whatever color/effect it's already showing.
        private static (byte R, byte G, byte B) BatteryGradientColor(int percent,
                                                                      uint brightnessPercent) {
            if (percent < 0)
                return (0, 0, 0);

            int clamped = Math.Min(100, percent);
            byte r, g;
            if (clamped <= 50) {
                r = 255;
                g = (byte)(255 * clamped / 50);
            } else {
                r = (byte)(255 * (100 - clamped) / 50);
                g = 255;
            }

            uint scale = Math.Min(BatteryBrightnessMax, brightnessPercent);
            return ((byte)(r * scale / 100), (byte)(g * scale / 100), 0);
        }

        // Linearly crossfades through colorShiftColors in a loop - color[0] to color[1] to ... to
        // color[N-1] back to color[0] - each segment taking 1/N of the full cycle duration.
        private static (byte R, byte G, byte B) CurrentColorShiftColor(long nowMs) {
            uint[] colors;
            lock (colorShiftColorsLock) colors = colorShiftColors;

            long cycleDurationMs = CycleDurationMs(Volatile.Read(ref colorShiftSpeed));
            long segmentDurationMs = Math.Max(1, cycleDurationMs / colors.Length);
            long elapsed = nowMs % (segmentDurationMs * colors.Length);
            int segment = (int)(elapsed / segmentDurationMs);
            float t = (elapsed % segmentDurationMs) / (float)segmentDurationMs;

            uint from = colors[segment];
            uint to = colors[(segment + 1) % colors.Length];
            byte r = Lerp((byte)(from & 0xFF), (byte)(to & 0xFF), t);
            byte g = Lerp((byte)((from >> 8) & 0xFF), (byte)((to >> 8) & 0xFF), t);
            byte b = Lerp((byte)((from >> 16) & 0xFF), (byte)((to >> 16) & 0xFF), t);
            return (r, g, b);
        }

        private static byte Lerp(byte from, byte to, float t) {
            return (byte)(from + (to - from) * t);
        }

        // speed 1 (slowest) -> 10s per full cycle, speed 100 (fastest) -> 200ms per full cycle.
        // No real device convention to match here - Rainbow Puke is BetterJoy2's own mode, not
        // mirroring anything from OpenRGB's own source.
        private static long CycleDurationMs(uint speed) {
            uint clamped = Math.Min(CycleSpeedMax, Math.Max(CycleSpeedMin, speed));
            double t = (clamped - CycleSpeedMin) / (double)(CycleSpeedMax - CycleSpeedMin);
            return (long)(10000 - t * 9800);
        }

        // Standard full-saturation, full-value HSV-to-RGB conversion, hue in [0, 360).
        private static (byte R, byte G, byte B) HsvToRgb(float hue) {
            const float c = 255f;
            float x = c * (1 - Math.Abs(hue / 60f % 2 - 1));
            if (hue < 60) return ((byte)c, (byte)x, 0);
            if (hue < 120) return ((byte)x, (byte)c, 0);
            if (hue < 180) return (0, (byte)c, (byte)x);
            if (hue < 240) return (0, (byte)x, (byte)c);
            if (hue < 300) return ((byte)x, 0, (byte)c);
            return ((byte)c, 0, (byte)x);
        }

        private static void ClientLoop(ConnectedClient connected) {
            try {
                byte[] header = new byte[16];
                while (true) {
                    if (!ReadExact(connected.Stream, header, 16))
                        break;
                    if (header[0] != (byte)'O' || header[1] != (byte)'R' ||
                            header[2] != (byte)'G' || header[3] != (byte)'B')
                        break;

                    uint devId = BitConverter.ToUInt32(header, 4);
                    uint packetId = BitConverter.ToUInt32(header, 8);
                    uint payloadLength = BitConverter.ToUInt32(header, 12);

                    byte[] payload = payloadLength > 0 ? new byte[payloadLength] : Array.Empty<byte>();
                    if (payloadLength > 0 && !ReadExact(connected.Stream, payload, (int)payloadLength))
                        break;

                    HandlePacket(connected, devId, packetId, payload);
                }
            } catch (Exception ex) {
                DebugLog.Write("OpenRgbServer: client connection ended - " + ex.Message);
            } finally {
                lock (clientsLock) clients.Remove(connected);
                try { connected.Client.Close(); } catch { }
            }
        }

        private static void HandlePacket(ConnectedClient connected, uint devId, uint packetId,
                                         byte[] payload) {
            DebugLog.Write("OpenRgbServer: packet " + packetId + " devId=" + devId +
                " payloadLen=" + payload.Length);
            switch (packetId) {
                // Confirmed against NetworkServer.cpp: on receiving this packet the real server
                // both reads the client's declared version AND replies with its own - a client
                // that gets no reply here does not reliably fall back in practice
                // (real-hardware testing: OpenRGB reconnected in a tight loop instead). This
                // reply is the fix - a plain 4-byte DeclaredProtocolVersion. Retain the lower of
                // the two versions for descriptor and mode-update parsing; older SDK clients are
                // allowed to request their own wire shape even when this server supports v3.
                case PacketIdRequestProtocolVersion:
                    if (payload.Length == sizeof(uint))
                        connected.ProtocolVersion = Math.Min(DeclaredProtocolVersion,
                            BitConverter.ToUInt32(payload, 0));
                    lock (connected.WriteLock) {
                        SendPacket(connected.Stream, 0, PacketIdRequestProtocolVersion,
                            BitConverter.GetBytes(DeclaredProtocolVersion));
                    }
                    break;
                case PacketIdRequestControllerCount: {
                    byte[] reply = BitConverter.GetBytes((uint)1);
                    lock (connected.WriteLock)
                        SendPacket(connected.Stream, 0, PacketIdRequestControllerCount, reply);
                    break;
                }
                case PacketIdRequestControllerData: {
                    if (devId != 0)
                        break; // only one device, ID 0 - anything else is stale/invalid
                    uint protocolVersion = connected.ProtocolVersion;
                    if (payload.Length == sizeof(uint))
                        protocolVersion = Math.Min(DeclaredProtocolVersion,
                            BitConverter.ToUInt32(payload, 0));
                    connected.ProtocolVersion = protocolVersion;
                    byte[] descriptor = BuildDeviceDescriptor(protocolVersion);
                    byte[] reply = new byte[4 + descriptor.Length];
                    // Self-referential leading size field (own 4 bytes + descriptor) - confirmed
                    // against NetworkServer.cpp's SendReply_ControllerData, not just the
                    // descriptor-only size GetDeviceDescriptionSize itself returns.
                    Array.Copy(BitConverter.GetBytes((uint)reply.Length), 0, reply, 0, 4);
                    Array.Copy(descriptor, 0, reply, 4, descriptor.Length);
                    lock (connected.WriteLock)
                        SendPacket(connected.Stream, devId, PacketIdRequestControllerData, reply);
                    break;
                }
                case PacketIdRgbControllerUpdateLeds:
                    if (devId == 0)
                        ApplyUpdateLeds(payload);
                    break;
                case PacketIdRgbControllerUpdateZoneLeds:
                    if (devId == 0)
                        ApplyUpdateZoneLeds(payload);
                    break;
                case PacketIdRgbControllerUpdateSingleLed:
                    if (devId == 0)
                        ApplyUpdateSingleLed(payload);
                    break;
                // SAVEMODE and UPDATEMODE carry an identical payload (NetworkServer.cpp routes
                // both through the same handler, one flag apart) - this class has no real
                // persistent "saved mode slot" concept, so both just switch/apply the same way.
                case PacketIdRgbControllerUpdateMode:
                case PacketIdRgbControllerSaveMode:
                    if (devId == 0)
                        ApplyUpdateMode(payload, connected.ProtocolVersion);
                    break;
                default:
                    // SET_CLIENT_NAME and everything else this class doesn't implement: no
                    // response needed.
                    break;
            }
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateLEDs: payload
        // is a self-referential 4-byte size (must equal the packet's own payload length) followed
        // by num_colors(2) and that many 4-byte RGBColor values. Applies the first color to every
        // currently eligible controller - this class only ever advertises one LED on the one
        // fixed device (see BuildDeviceDescriptor), so there is only ever one meaningful color.
        private static void ApplyUpdateLeds(byte[] payload) {
            if (payload.Length < 6 + 4)
                return;

            ushort numColors = BitConverter.ToUInt16(payload, 4);
            if (numColors == 0)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 6);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateZoneLEDs:
        // payload is a self-referential 4-byte size, then zone_idx(4), num_colors(2), then that
        // many 4-byte RGBColor values. This is what OpenRGB sends when the user sets a color from
        // the per-zone color picker (as opposed to the whole-device one) - this class only ever
        // advertises zone 0, so zone_idx is ignored rather than validated.
        private static void ApplyUpdateZoneLeds(byte[] payload) {
            if (payload.Length < 4 + 4 + 2 + 4)
                return;

            ushort numColors = BitConverter.ToUInt16(payload, 8);
            if (numColors == 0)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 10);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateSingleLED:
        // unlike the other two UPDATE* packets, this one has NO leading self-referential size
        // field - payload is exactly led_idx(4) then RGBColor(4). This is what OpenRGB's "Toggle
        // LED view" sends when the user picks a color for one specific LED - this class only ever
        // advertises LED 0, so led_idx is ignored rather than validated.
        private static void ApplyUpdateSingleLed(byte[] payload) {
            if (payload.Length < 4 + 4)
                return;

            uint rgbColor = BitConverter.ToUInt32(payload, 4);
            SetCachedColor(rgbColor);
        }

        // Confirmed against NetworkServer.cpp's ProcessRequest_RGBController_UpdateSaveMode and
        // RGBController.cpp's SetModeDescription (protocol version 3): self-referential 4-byte
        // size, mode_idx(4), then the full mode description this class's own GetModeDescriptionData
        // equivalent (WriteDirectMode/WriteRainbowMode/WriteColorShiftMode/WriteBatteryMode)
        // writes - name, value, flags, speed_min, speed_max, brightness_min, brightness_max,
        // colors_min, colors_max, speed, brightness, direction, color_mode, num_colors, colors[].
        // name/flags/min/max/direction/color_mode are this class's own fixed shape per mode, not
        // something OpenRGB can redefine, so only mode_idx/speed/brightness/colors matter here.
        // The actual color VALUES are read but only used by Color Shift - Rainbow Puke derives
        // its own palette purely from colorCount (see HsvToRgb in AnimationLoop), never from
        // whatever OpenRGB's mode editor happened to show as swatches.
        private static void ApplyUpdateMode(byte[] payload, uint protocolVersion) {
            try {
                using (var stream = new MemoryStream(payload))
                using (var r = new BinaryReader(stream)) {
                    r.ReadUInt32();                  // self-referential size - unused
                    int modeIndex = r.ReadInt32();
                    if (modeIndex != ModeIndexDirect && modeIndex != ModeIndexRainbow &&
                            modeIndex != ModeIndexColorShift && modeIndex != ModeIndexBattery)
                        return;

                    ushort nameLen = r.ReadUInt16();
                    r.ReadBytes(nameLen);             // name - unused, this class names its own modes
                    r.ReadInt32();                    // mode value - unused, present for < v6
                    r.ReadUInt32();                   // flags - unused, fixed per mode
                    r.ReadUInt32();                   // speed_min - unused, fixed per mode
                    r.ReadUInt32();                   // speed_max - unused, fixed per mode
                    if (protocolVersion >= 3) {
                        r.ReadUInt32();               // brightness_min - unused, fixed per mode
                        r.ReadUInt32();               // brightness_max - unused, fixed per mode
                    }
                    r.ReadUInt32();                   // colors_min - unused, fixed per mode
                    r.ReadUInt32();                   // colors_max - unused, fixed per mode
                    uint speed = r.ReadUInt32();
                    uint brightness = Volatile.Read(ref batteryBrightness);
                    if (protocolVersion >= 3)
                        brightness = r.ReadUInt32();
                    r.ReadUInt32();                   // direction - unused
                    r.ReadUInt32();                   // color_mode - unused, fixed per mode
                    ushort numColors = r.ReadUInt16();
                    uint[] colors = new uint[numColors];
                    for (int i = 0; i < numColors; i++)
                        colors[i] = r.ReadUInt32();

                    Volatile.Write(ref activeMode, modeIndex);
                    if (modeIndex == ModeIndexRainbow) {
                        Volatile.Write(ref rainbowSpeed,
                            Math.Min(CycleSpeedMax, Math.Max(CycleSpeedMin, speed)));
                        Volatile.Write(ref rainbowColorCount, (int)Math.Min(CycleColorsMax,
                            Math.Max(CycleColorsMin, (uint)numColors)));
                        DebugLog.Write("OpenRgbServer: mode set to Rainbow Puke speed=" +
                            rainbowSpeed + " colors=" + rainbowColorCount);
                    } else if (modeIndex == ModeIndexColorShift) {
                        Volatile.Write(ref colorShiftSpeed,
                            Math.Min(CycleSpeedMax, Math.Max(CycleSpeedMin, speed)));
                        if (colors.Length >= CycleColorsMin && colors.Length <= CycleColorsMax)
                            lock (colorShiftColorsLock) colorShiftColors = colors;
                        DebugLog.Write("OpenRgbServer: mode set to Color Shift speed=" +
                            colorShiftSpeed + " colors=" + colors.Length);
                    } else if (modeIndex == ModeIndexBattery) {
                        Volatile.Write(ref batteryBrightness, Math.Min(BatteryBrightnessMax,
                            Math.Max(BatteryBrightnessMin, brightness)));
                        DebugLog.Write("OpenRgbServer: mode set to Battery brightness=" +
                            batteryBrightness);
                    } else {
                        DebugLog.Write("OpenRgbServer: mode set to Direct");
                    }

                    Volatile.Write(ref modeStateDirty, true);
                    if (cachingEnabled)
                        QueuePersistence();
                    if (modeIndex == ModeIndexDirect)
                        ApplyCachedColorToEligibleControllers();
                }
            } catch (EndOfStreamException) {
                // Malformed/truncated payload - ignore rather than crash the client thread.
            }
        }

        private static void SetCachedColor(uint rgbColor) {
            int color = (int)(rgbColor & 0x00FFFFFF);
            Volatile.Write(ref cachedColor, color);
            if (cachingEnabled)
                QueuePersistence();
            byte cachedRed = (byte)(color & 0xFF);
            byte cachedGreen = (byte)((color >> 8) & 0xFF);
            byte cachedBlue = (byte)((color >> 16) & 0xFF);
            DebugLog.Write("OpenRgbServer: cached color set to " +
                cachedRed + "," + cachedGreen + "," + cachedBlue);

            ApplyCachedColorToEligibleControllers();
        }

        // Every controller currently connected with Lighting Mode: OpenRGB - zero, one, or more.
        // Order doesn't matter here (unlike an earlier version of this class): there is only ever
        // one advertised device, so nothing depends on a stable per-controller index anymore.
        private static List<Controller> GetEligibleControllers() {
            var result = new List<Controller>();
            ConcurrentList<Controller> all = Program.mgr?.j;
            if (all == null)
                return result;

            foreach (Controller c in all) {
                string profileId = ControllerMappings.ProfileIdFor(c);
                string lightingMode = ControllerMappings.LightingMode(profileId);
                bool eligible = c.state > Controller.state_.DROPPED &&
                    lightingMode == ControllerMappings.LightingModeOpenRgb;
                DebugLog.Write("OpenRgbServer: controller state=" + c.state +
                    " profile=" + profileId + " lightingMode=" + lightingMode +
                    " eligible=" + eligible);
                if (eligible)
                    result.Add(c);
            }
            return result;
        }

        // Mirrors RGBController::GetDeviceDescriptionData for the client's negotiated protocol
        // version. v0 omits vendor; v0-v2 omit mode brightness fields; v3 includes them for the
        // Battery slider. The device identity and four modes otherwise stay stable.
        private static byte[] BuildDeviceDescriptor(uint protocolVersion) {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream)) {
                w.Write(DeviceTypeGamepad);
                WriteString(w, "BetterJoy2");
                if (protocolVersion >= 1)
                    WriteString(w, "BetterJoy2"); // vendor - introduced in protocol version 1
                WriteString(w, "Applies to whichever connected controller has Lighting Mode: " +
                    "OpenRGB");
                WriteString(w, "1");
                WriteString(w, "betterjoy2-openrgb-server");
                WriteString(w, "BetterJoy2");

                w.Write((ushort)4); // num_modes
                w.Write(Volatile.Read(ref activeMode));
                WriteDirectMode(w, protocolVersion);
                WriteRainbowMode(w, protocolVersion);
                WriteColorShiftMode(w, protocolVersion);
                WriteBatteryMode(w, protocolVersion);

                w.Write((ushort)1); // num_zones
                WriteSingleZone(w);

                w.Write((ushort)1); // num_leds
                WriteString(w, "Lightbar");
                w.Write(0u); // led.value - unused, present because protocol_version < 6

                w.Write((ushort)1); // num_colors (top-level)
                int color = Volatile.Read(ref cachedColor);
                w.Write(color < 0 ? 0u : (uint)color);

                w.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteDirectMode(BinaryWriter w, uint protocolVersion) {
            WriteString(w, "Direct");
            w.Write(0);                          // mode_value - unused, present for < v6
            w.Write(ModeFlagHasPerLedColor);      // mode_flags
            w.Write(0u);                          // speed_min
            w.Write(0u);                          // speed_max
            if (protocolVersion >= 3) {
                w.Write(0u);                      // brightness_min - unused, no HAS_BRIGHTNESS
                w.Write(0u);                      // brightness_max
            }
            w.Write(0u);                          // colors_min
            w.Write(0u);                          // colors_max
            w.Write(0u);                          // speed
            if (protocolVersion >= 3)
                w.Write(0u);                      // brightness
            w.Write(0u);                          // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsPerLed);            // color_mode
            w.Write((ushort)0);                   // mode_num_colors - colors live on the
                                                   // controller itself for MODE_COLORS_PER_LED
        }

        // Colors written here are just OpenRGB mode-editor preview swatches (evenly-spaced hues
        // matching what the cycle actually shows) - AnimationLoop derives its own palette from
        // rainbowColorCount alone at animation time, never reads these back.
        private static void WriteRainbowMode(BinaryWriter w, uint protocolVersion) {
            WriteString(w, "Rainbow Puke");
            w.Write(0);                                        // mode_value - unused, present for < v6
            w.Write(ModeFlagHasSpeed | ModeFlagHasModeSpecificColor); // mode_flags
            w.Write(CycleSpeedMin);
            w.Write(CycleSpeedMax);
            if (protocolVersion >= 3) {
                w.Write(0u);                                   // brightness_min - unused
                w.Write(0u);                                   // brightness_max
            }
            w.Write(CycleColorsMin);
            w.Write(CycleColorsMax);
            w.Write(Volatile.Read(ref rainbowSpeed));
            if (protocolVersion >= 3)
                w.Write(0u);                                   // brightness
            w.Write(0u);                                       // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsModeSpecific);                    // color_mode
            int colorCount = Volatile.Read(ref rainbowColorCount);
            w.Write((ushort)colorCount);                        // mode_num_colors
            for (int i = 0; i < colorCount; i++) {
                (byte r, byte g, byte b) = HsvToRgb(360f * i / colorCount);
                w.Write(ToRgbColor(r, g, b));
            }
        }

        // Unlike Rainbow Puke's synthesized swatches, these are the real, current colorShiftColors
        // - OpenRGB's mode editor shows and lets the user edit these exact swatches, and
        // CurrentColorShiftColor (AnimationLoop) crossfades through this exact same array.
        private static void WriteColorShiftMode(BinaryWriter w, uint protocolVersion) {
            WriteString(w, "Color Shift");
            w.Write(0);                                        // mode_value - unused, present for < v6
            w.Write(ModeFlagHasSpeed | ModeFlagHasModeSpecificColor); // mode_flags
            w.Write(CycleSpeedMin);
            w.Write(CycleSpeedMax);
            if (protocolVersion >= 3) {
                w.Write(0u);                                   // brightness_min - unused
                w.Write(0u);                                   // brightness_max
            }
            w.Write(CycleColorsMin);
            w.Write(CycleColorsMax);
            w.Write(Volatile.Read(ref colorShiftSpeed));
            if (protocolVersion >= 3)
                w.Write(0u);                                   // brightness
            w.Write(0u);                                       // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsModeSpecific);                    // color_mode
            uint[] colors;
            lock (colorShiftColorsLock) colors = colorShiftColors;
            w.Write((ushort)colors.Length);                     // mode_num_colors
            foreach (uint color in colors)
                w.Write(color);
        }

        // No speed, no editable colors - the gradient itself is fixed (red/yellow/green) and
        // driven by each controller's own actual charge (BatteryGradientColor in AnimationLoop).
        // Brightness IS editable though (ModeFlagHasBrightness, OpenRGB's own Brightness slider) -
        // applied as a uniform scale on top of the gradient, the one param that genuinely makes
        // sense for a charge-driven color to expose.
        private static void WriteBatteryMode(BinaryWriter w, uint protocolVersion) {
            WriteString(w, "Battery");
            w.Write(0);                       // mode_value - unused, present for < v6
            w.Write(ModeFlagHasBrightness);   // mode_flags
            w.Write(0u);                      // speed_min - unused, no HAS_SPEED
            w.Write(0u);                      // speed_max
            if (protocolVersion >= 3) {
                w.Write(BatteryBrightnessMin);
                w.Write(BatteryBrightnessMax);
            }
            w.Write(0u);                      // colors_min - unused, no HAS_MODE_SPECIFIC_COLOR
            w.Write(0u);                      // colors_max
            w.Write(0u);                      // speed
            if (protocolVersion >= 3)
                w.Write(Volatile.Read(ref batteryBrightness));
            w.Write(0u);                      // direction (MODE_DIRECTION_LEFT)
            w.Write(ModeColorsNone);          // color_mode
            w.Write((ushort)0);               // mode_num_colors
        }

        private static void WriteSingleZone(BinaryWriter w) {
            WriteString(w, "Lightbar");
            w.Write(ZoneTypeSingle);
            w.Write(1u); // leds_min
            w.Write(1u); // leds_max
            w.Write(1u); // leds_count
            w.Write((ushort)0); // matrix_map_size - always present, 0 for a non-matrix zone
                                 // (confirmed against GetMatrixMapDescriptionData: writes nothing
                                 // when matrix_map.Empty())
        }

        // 2-byte length prefix INCLUDING the null terminator, then the string bytes, then the
        // null terminator itself - confirmed against every *_len/strcpy pair in
        // RGBController.cpp's Get*DescriptionData methods. Only strings embedded in a structured
        // payload like this one use this convention - a flat single-string packet (SET_CLIENT_NAME)
        // does not, see OpenRgbRescan.cs's own SendPacket.
        private static void WriteString(BinaryWriter w, string value) {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? String.Empty);
            w.Write((ushort)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        // Confirmed against RGBControllerInterface.h: typedef unsigned int RGBColor; #define
        // ToRGBColor(r, g, b) ((RGBColor)((b << 16) | (g << 8) | (r))) - matches the packing
        // SetCachedColor/ApplyCachedColorToEligibleControllers already unpack cachedColor with.
        private static uint ToRgbColor(byte r, byte g, byte b) {
            return (uint)((b << 16) | (g << 8) | r);
        }

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

        // Same OpenRGB SDK wire format OpenRgbRescan.cs's client side already sends: 4-byte
        // "ORGB" magic, 4-byte device ID, 4-byte packet ID, 4-byte payload length, then payload.
        private static void SendPacket(NetworkStream stream, uint devId, uint packetId,
                                       byte[] payload) {
            byte[] header = new byte[16];
            header[0] = (byte)'O';
            header[1] = (byte)'R';
            header[2] = (byte)'G';
            header[3] = (byte)'B';
            Array.Copy(BitConverter.GetBytes(devId), 0, header, 4, 4);
            Array.Copy(BitConverter.GetBytes(packetId), 0, header, 8, 4);
            Array.Copy(BitConverter.GetBytes((uint)payload.Length), 0, header, 12, 4);

            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }
}
