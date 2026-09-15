using System;
using System.Runtime.InteropServices;

namespace BetterJoyForCemu {
    public enum ResampleQuality {
        SincBest = 0,
        SincMedium = 1,
        SincFastest = 2,
        ZeroOrderHold = 3,
        Linear = 4,
    }

    // Minimal P/Invoke binding for libsamplerate (https://github.com/libsndfile/libsamplerate,
    // BSD-2-Clause), a native audio resampler. Ported from nefarius/SharpSampleRate's
    // SampleRate.cs (part of nefarius/DS4AudioStreamer, itself unlicensed/MIT-adjacent per that
    // repo) - trimmed to only the entry points actually needed and, unlike the upstream wrapper,
    // kept pointer-free (pinned GCHandles instead of float*) to match this project's existing
    // native bindings (HIDapi.cs, SbcEncoder.cs).
    //
    // Replaces an earlier attempt at using NAudio's own MediaFoundationResampler for this step.
    // On real hardware that dropped a fixed ~19% of every capture callback's audio - confirmed via
    // debug logging (BluetoothAudioCapture.cs), not a guess: consistently 480 stereo frames
    // captured (10ms @ 48kHz) in, only 259 resampled frames out, forever, not a one-time startup
    // effect. MediaFoundationResampler is well suited to resampling a whole file at once; driving
    // it with small, frequent pushes from a live capture callback is a known rough edge. libsamplerate's
    // src_process API is built for exactly this streaming, arbitrary-chunk-size use case (it's
    // what the reference project actually uses), so this is a straight substitution, not a
    // simplification.
    internal static class SampleRateNative {
        const string dll = "samplerate.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct SRC_DATA {
            public IntPtr data_in;
            public IntPtr data_out;
            public int input_frames;
            public int output_frames;
            public int input_frames_used;
            public int output_frames_gen;
            public int end_of_input;
            public double src_ratio;
        }

        [DllImport(dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr src_new(int converter_type, int channels, out int error);

        [DllImport(dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr src_delete(IntPtr state);

        [DllImport(dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int src_process(IntPtr state, ref SRC_DATA data);

        [DllImport(dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "src_strerror")]
        private static extern IntPtr src_strerror_native(int error);

        public static string src_strerror(int error) => Marshal.PtrToStringAnsi(src_strerror_native(error));
    }

    // One instance per continuous stream - libsamplerate keeps interpolation history across
    // Process() calls, so reusing one instance for the whole capture session (not one per
    // callback) is what keeps the output continuous across callback boundaries instead of
    // producing an audible seam at every single one.
    public sealed class SampleRateResampler : IDisposable {
        private IntPtr state;

        // Settable, not just constructor-fixed: a caller bridging two independently-clocked
        // audio devices (see UsbAudioLoopback.cs) needs to nudge this by a tiny amount over time
        // to track real clock drift between them, or the output buffer slowly drains/fills until
        // it audibly under/overruns. libsamplerate documents src_ratio as safe to change between
        // Process() calls - it interpolates smoothly rather than producing a seam, as long as the
        // change per call is small (a caller doing gentle proportional correction, not a jump).
        public double Ratio { get; set; }

        public SampleRateResampler(ResampleQuality quality, int channels, double ratio) {
            state = SampleRateNative.src_new((int)quality, channels, out int error);
            if (state == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Could not initialize libsamplerate (samplerate.dll): " + SampleRateNative.src_strerror(error));
            Ratio = ratio;
        }

        // src/dst are interleaved float sample arrays (frames * channels floats each). dst must be
        // sized for the worst case the caller expects to need. Returns the number of input frames
        // actually consumed and output frames actually generated - libsamplerate does not
        // guarantee consuming everything in one call, so the caller must be prepared to carry any
        // unused input over to the next call.
        public (int framesUsed, int framesGenerated) Process(
            float[] src, int inFrames, float[] dst, int outFrames, bool endOfInput) {
            GCHandle inHandle = GCHandle.Alloc(src, GCHandleType.Pinned);
            GCHandle outHandle = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try {
                var data = new SampleRateNative.SRC_DATA {
                    data_in = inHandle.AddrOfPinnedObject(),
                    data_out = outHandle.AddrOfPinnedObject(),
                    input_frames = inFrames,
                    output_frames = outFrames,
                    src_ratio = Ratio,
                    end_of_input = endOfInput ? 1 : 0,
                };

                int result = SampleRateNative.src_process(state, ref data);
                if (result != 0)
                    throw new InvalidOperationException(
                        "libsamplerate error: " + SampleRateNative.src_strerror(result));

                return (data.input_frames_used, data.output_frames_gen);
            } finally {
                inHandle.Free();
                outHandle.Free();
            }
        }

        public void Dispose() {
            if (state != IntPtr.Zero) {
                SampleRateNative.src_delete(state);
                state = IntPtr.Zero;
            }
        }
    }
}
