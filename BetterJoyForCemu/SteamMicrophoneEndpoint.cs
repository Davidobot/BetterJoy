using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using static BetterJoyForCemu.SteamMicrophoneInstaller;

namespace BetterJoyForCemu {
    // Alternative to ViiperMicrophoneEndpoint that needs nothing bundled beyond a driver Valve
    // already ships and Microsoft has already attestation-signed - renders decoded DualSense
    // microphone PCM into a render endpoint, which that driver internally loops through to its
    // own paired capture endpoint (the same virtual-cable pattern VB-CABLE/Voicemeeter use). The
    // bundled INF/CAT are Valve's own, byte-for-byte unmodified - the CAT's signature covers the
    // INF's own hash, so editing its hardware ID or strings (tried once) breaks that hash and
    // Windows refuses to load it (ERROR_FILE_HASH_NOT_IN_CATALOG), right back to the "needs
    // test-signing mode" problem this driver was chosen specifically to avoid. So this targets
    // Steam's own hardware ID, but always BetterJoy's own separate device instance (see
    // BetterJoy.iss's install step) - never an instance Steam itself created for its own Remote
    // Play/Link voice forwarding, even though both would share the same hardware ID and, until
    // renamed, the same default name. IsOwnedByBetterJoy below is what tells them apart.
    // This also applies a distinguishing friendly-name override to the endpoint at runtime,
    // re-applied on every Open() so it self-heals if Steam recreates ITS OWN device later
    // (confirmed on real hardware that reconnecting a controller through Steam does exactly that
    // to ITS instance - BetterJoy's own separate instance is never touched by that). This targets
    // each endpoint's own SWD\MMDEVAPI PnP node under HKLM\SYSTEM\CurrentControlSet\Enum, not the
    // audio subsystem's PKEY_Device_FriendlyName - that property is documented read-only for any
    // client app (E_ACCESSDENIED via IPropertyStore::SetValue, confirmed even elevated), and the
    // underlying MMDevices\Audio property-store registry keys are ACL-locked against admin writes
    // too. The SWD\MMDEVAPI PnP node's plain FriendlyName is a different, normally-writable
    // location that Windows composes the endpoint's displayed name from - confirmed on real
    // hardware, along with the fact that its PnP parent is always the underlying
    // ROOT\SteamStreamingMicrophone\NNNN devnode directly, no intermediate node in between.
    internal sealed class SteamMicrophoneEndpoint : IMicrophoneEndpoint {
        public const string EndpointNameHint = "Steam Streaming Microphone";
        private const string RenderDisplayName = "Speakers (BetterJoy)";
        private const string CaptureDisplayName = "Microphone (BetterJoy)";

        private const int SourceSampleRate = 48000;
        private const int SourceChannels = 2;
        private const int SourceBytesPerFrame = SourceChannels * sizeof(short);

        private readonly object writeLock = new object();
        private readonly WasapiOut output;
        private readonly BufferedWaveProvider outputBuffer;
        private readonly SampleRateResampler resampler; // null when the target is already 48 kHz
        private readonly double resampleRatio;
        private int disposed;

        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

        // Walks from the audio endpoint's own SWD\MMDEVAPI PnP node up to its parent devnode
        // (confirmed on real hardware to be the ROOT\SteamStreamingMicrophone\NNNN device
        // directly) and checks that devnode's FriendlyName for BetterJoy's own ownership marker -
        // the only way to tell "the instance BetterJoy's installer created" apart from "an
        // instance Steam created for its own Remote Play voice forwarding" when both share the
        // exact same hardware ID and, until renamed, the exact same default name.
        private static bool IsOwnedByBetterJoy(MMDevice device) {
            IntPtr devInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (devInfoSet == IntPtr.Zero)
                return false;

            try {
                var endpointInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(devInfoSet, @"SWD\MMDEVAPI\" + device.ID, IntPtr.Zero, 0,
                        ref endpointInfo))
                    return false;

                if (CM_Get_Parent(out uint parentDevInst, endpointInfo.devInst, 0) != 0)
                    return false;

                var parentId = new StringBuilder(512);
                if (CM_Get_Device_IDW(parentDevInst, parentId, parentId.Capacity, 0) != 0)
                    return false;

                var parentInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(devInfoSet, parentId.ToString(), IntPtr.Zero, 0, ref parentInfo))
                    return false;

                var nameBuffer = new byte[512];
                if (!SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref parentInfo, SPDRP_FRIENDLYNAME,
                        out _, nameBuffer, (uint)nameBuffer.Length, out uint requiredSize) || requiredSize < 2)
                    return false;

                string friendlyName = Encoding.Unicode.GetString(nameBuffer, 0, (int)requiredSize).TrimEnd('\0');
                return string.Equals(friendlyName, OwnerMarker, StringComparison.OrdinalIgnoreCase);
            } finally {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        // Ownership (IsOwnedByBetterJoy) is the sole, authoritative check - it doesn't depend on
        // the endpoint's display name at all. A name-substring pre-filter used to gate it too, but
        // that broke discovery outright: the devnode's own OwnerMarker leaks into the endpoint's
        // *default* composed name as Windows' fallback (confirmed on real hardware - before the
        // runtime rename below ever runs, the endpoint already shows e.g. "Speakers (<marker>)"),
        // so any OwnerMarker value not itself containing EndpointNameHint made the device
        // permanently unfindable, independent of whether it was actually installed correctly.
        private static bool TryFindDevice(DataFlow flow, out MMDevice device) {
            device = null;
            using (var enumerator = new MMDeviceEnumerator()) {
                foreach (MMDevice candidate in enumerator.EnumerateAudioEndPoints(
                        flow, DeviceState.Active)) {
                    if (IsOwnedByBetterJoy(candidate)) {
                        device = candidate;
                        return true;
                    }
                    candidate.Dispose();
                }
            }
            return false;
        }

        // Cosmetic only - must never take down the actual microphone feature if it fails (e.g.
        // running unelevated, the registry key not existing yet). Idempotent: safe to call on
        // every Open(), which is what makes it self-heal after Steam recreates the device.
        private static void TryRenameEndpoint(MMDevice device, string displayName) {
            try {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\" + device.ID, writable: true))
                    key?.SetValue("FriendlyName", displayName, RegistryValueKind.String);
            } catch {
                // Best-effort - the device still works fine under whatever name Windows already
                // has for it.
            }
        }

        // The dedicated, purpose-built IPolicyConfig method for exactly this - not the
        // IPropertyStore.SetValue(PKEY_AudioEngine_DeviceFormat) path (E_ACCESSDENIED, confirmed
        // even elevated), and not a direct registry write either (tried against the runtime
        // MMDevices\Audio\...\Properties key first - the write went through and read back
        // correctly, but never visibly changed the Advanced tab, meaning that key isn't actually
        // what this driver/Windows reads the Default Format from). SoundVolumeView's own
        // /SetDefaultFormat switch almost certainly calls this same method. GUIDs/vtable order
        // confirmed against three independent published ports (Sunshine's PolicyConfig.h,
        // tartakynov/audioswitch, ThiefMaster/coreaudio-dotnet) - Windows 7+ IPolicyConfig/
        // CPolicyConfigClient, not the older Vista-only IPolicyConfigVista pair. Fixes a real bug
        // found on real hardware: Windows' own cached Default Format for this capture endpoint was
        // stuck at 1 channel/44100 Hz, not matching what actually flows through the driver's
        // render->capture loopback - anything listening on the capture side heard complete audio,
        // no dropouts, just audibly slowed/deepened, exactly what a receiver assuming the wrong
        // sample rate for otherwise-correct data sounds like. Manually changing the Advanced tab
        // dropdown to 48000 Hz confirmed this is the right lever. Deliberately does NOT also touch
        // endpoint visibility/enable state on this or the paired render endpoint - see git history
        // for why (broke mic capture entirely on real hardware, reverted).
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig {
            [PreserveSig] int GetMixFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
            [PreserveSig] int GetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool defaultFormat,
                out IntPtr format);
            [PreserveSig] int ResetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat,
                IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool defaultPeriod,
                out long defaultDevicePeriod, out long minimumDevicePeriod);
            [PreserveSig] int SetProcessingPeriod(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long devicePeriod);
            [PreserveSig] int GetShareMode(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
            [PreserveSig] int SetShareMode(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr shareMode);
            [PreserveSig] int GetPropertyValue(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool fxStore, IntPtr key,
                IntPtr value);
            [PreserveSig] int SetPropertyValue(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool fxStore, IntPtr key,
                IntPtr value);
            [PreserveSig] int SetDefaultEndpoint(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                [MarshalAs(UnmanagedType.Bool)] bool visible);
        }

        private static readonly Guid KsDataFormatSubTypePcm =
            new Guid("00000001-0000-0010-8000-00aa00389b71");

        private static void TrySetCaptureDeviceFormat(MMDevice device) {
            byte[] format = BuildWaveFormatExtensibleBytes();
            IntPtr formatPtr = IntPtr.Zero;
            object instance = null;
            try {
                formatPtr = Marshal.AllocHGlobal(format.Length);
                Marshal.Copy(format, 0, formatPtr, format.Length);

                instance = Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")));
                int hr = ((IPolicyConfig)instance).SetDeviceFormat(device.ID, formatPtr, formatPtr);
                AudioDebugLog.Write("SteamMic",
                    "SetCaptureDeviceFormat: SetDeviceFormat(" + device.ID + ") hr=0x" +
                    hr.ToString("X8"));
            } catch (Exception ex) {
                AudioDebugLog.Write("SteamMic",
                    "SetCaptureDeviceFormat failed for " + device.ID + ": " + ex);
            } finally {
                if (instance != null)
                    Marshal.ReleaseComObject(instance);
                if (formatPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(formatPtr);
            }
        }

        // Raw WAVEFORMATEXTENSIBLE (40 bytes, no PROPVARIANT/registry-blob framing - SetDeviceFormat
        // takes a bare PWAVEFORMATEX pointer) for 2 channel/48000 Hz/32-bit integer PCM - "2
        // channel, 32 bit, 48000 Hz (Studio Quality)" in the Sound Control Panel's own
        // Advanced-tab wording (confirmed that dropdown's 32-bit options are integer PCM, not IEEE
        // float, unlike the WASAPI float stream this class's own WriteMicrophonePcm produces for
        // its render output - those are two independent format contracts, not required to match).
        private static byte[] BuildWaveFormatExtensibleBytes() {
            const short channels = 2;
            const int sampleRate = 48000;
            const short bitsPerSample = 32;
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int avgBytesPerSec = sampleRate * blockAlign;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream)) {
                // WAVEFORMATEX (18 bytes)
                writer.Write(unchecked((short)0xFFFE)); // WAVE_FORMAT_EXTENSIBLE
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(avgBytesPerSec);
                writer.Write(blockAlign);
                writer.Write(bitsPerSample);
                writer.Write((short)22); // cbSize - the WAVEFORMATEXTENSIBLE tail below
                // WAVEFORMATEXTENSIBLE tail (22 bytes)
                writer.Write(bitsPerSample); // wValidBitsPerSample
                writer.Write(3); // dwChannelMask: SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
                writer.Write(KsDataFormatSubTypePcm.ToByteArray()); // SubFormat
                return stream.ToArray();
            }
        }

        public static SteamMicrophoneEndpoint Open() {
            if (!TryFindDevice(DataFlow.Render, out MMDevice device))
                throw new InvalidOperationException(
                    "\"" + EndpointNameHint + "\" was not found or not installed.");

            TryRenameEndpoint(device, RenderDisplayName);
            if (TryFindDevice(DataFlow.Capture, out MMDevice captureDevice)) {
                using (captureDevice) {
                    TryRenameEndpoint(captureDevice, CaptureDisplayName);
                    TrySetCaptureDeviceFormat(captureDevice);
                }
            }

            try {
                return new SteamMicrophoneEndpoint(device);
            } catch {
                device.Dispose();
                throw;
            }
        }

        private SteamMicrophoneEndpoint(MMDevice device) {
            using (device) {
                WaveFormat targetFormat = device.AudioClient.MixFormat;
                resampleRatio = (double)targetFormat.SampleRate / SourceSampleRate;
                if (targetFormat.SampleRate != SourceSampleRate)
                    resampler = new SampleRateResampler(ResampleQuality.SincBest, SourceChannels,
                        resampleRatio);

                outputBuffer = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(targetFormat.SampleRate, SourceChannels)) {
                    // A few call's worth of slack (~10ms arrives at a time) - large enough to
                    // absorb ordinary scheduling jitter, small enough that a stale backlog after
                    // a stall doesn't turn into a noticeable delay.
                    BufferDuration = TimeSpan.FromMilliseconds(300),
                    DiscardOnBufferOverflow = true,
                };

                output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
                output.Init(outputBuffer);
                output.Play();
            }
        }

        public void WriteMicrophonePcm(byte[] stereoPcm) {
            if (stereoPcm == null || stereoPcm.Length % SourceBytesPerFrame != 0)
                throw new ArgumentException("Malformed stereo S16LE PCM.", nameof(stereoPcm));
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(SteamMicrophoneEndpoint));

            int frames = stereoPcm.Length / SourceBytesPerFrame;
            var floatSamples = new float[frames * SourceChannels];
            for (int i = 0; i < floatSamples.Length; i++) {
                short sample = (short)(stereoPcm[i * 2] | (stereoPcm[i * 2 + 1] << 8));
                floatSamples[i] = sample / (float)Int16.MaxValue;
            }

            lock (writeLock) {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(SteamMicrophoneEndpoint));

                if (resampler == null) {
                    WriteFloats(floatSamples, floatSamples.Length / SourceChannels);
                    return;
                }

                int maxOutFrames = (int)Math.Ceiling(frames * resampleRatio) + 4;
                var resampled = new float[maxOutFrames * SourceChannels];
                (int used, int generated) = resampler.Process(
                    floatSamples, frames, resampled, maxOutFrames, false);
                // Fixed-size 10ms pushes with no continuous carry-over buffer here (unlike
                // BluetoothAudioCapture/UsbAudioLoopback's live capture streams) - libsamplerate
                // not fully draining one call is rare enough for small, uniform chunks like this
                // one that dropping the last few unconsumed frames is an acceptable simplification
                // rather than adding a carry buffer for what would be a handful of samples.
                _ = used;
                WriteFloats(resampled, generated);
            }
        }

        private void WriteFloats(float[] samples, int frames) {
            var bytes = new byte[frames * SourceChannels * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            outputBuffer.AddSamples(bytes, 0, bytes.Length);
        }

        // No API to ask the driver whether anything has actually opened the mic - unlike VIIPER,
        // this is a plain WDM driver with no companion server to query. Always active once
        // selected: BluetoothMicrophoneWorker keeps decoding/rendering for as long as the feature
        // is enabled, rather than only while some app happens to have the endpoint open.
        public bool IsMicrophoneInterfaceActive() => Volatile.Read(ref disposed) == 0;

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            lock (writeLock) {
                try { output.Stop(); } catch { }
                output.Dispose();
            }
        }
    }
}
