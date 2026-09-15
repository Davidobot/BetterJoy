using System;
using System.Runtime.InteropServices;

namespace BetterJoyForCemu {
    public enum SbcSubBandCount : byte { Sb4 = 0x00, Sb8 = 0x01 }
    public enum SbcChannelMode : byte { Mono = 0x00, DualChannel = 0x01, Stereo = 0x02, JointStereo = 0x03 }
    public enum SbcAllocationMode : byte { Loudness = 0x00, Snr = 0x01 }
    public enum SbcBlockCount : byte { Blk4 = 0x00, Blk8 = 0x01, Blk12 = 0x02, Blk16 = 0x03 }

    // Minimal P/Invoke binding for libsbc (https://github.com/nefarius/libsbc, GPL-2.0), a native
    // Bluetooth SBC (Subband Codec) encoder/decoder. Ported from nefarius/SharpSBC's Native.cs
    // (MIT), part of nefarius/DS4AudioStreamer - trimmed to only the entry points DS4's Bluetooth
    // audio streaming actually needs (encode-only; decode/parse/msbc/a2dp variants dropped) and
    // consolidated into one file to match this project's HIDapi.cs convention. libsbc.dll itself
    // stays GPL-2.0 even though the rest of this project is MIT - see README's credits section.
    internal static class SbcNative {
        const string dll = "libsbc.dll";

        public const int SBC_FREQ_16000 = 0x00;
        public const int SBC_FREQ_32000 = 0x01;
        public const int SBC_FREQ_44100 = 0x02;
        public const int SBC_FREQ_48000 = 0x03;
        public const int SBC_LE = 0x00;

        [StructLayout(LayoutKind.Sequential)]
        public struct sbc_t {
            public uint flags;
            public byte frequency;
            public byte blocks;
            public byte subbands;
            public byte mode;
            public byte allocation;
            public byte bitpool;
            public byte endian;
            public IntPtr priv;
            public IntPtr priv_alloc_base;
        }

        [DllImport(dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sbc_init(ref sbc_t sbc, uint flags);

        [DllImport(dll, CallingConvention = CallingConvention.StdCall)]
        public static extern ulong sbc_get_codesize(ref sbc_t sbc);

        [DllImport(dll, CallingConvention = CallingConvention.StdCall)]
        public static extern ulong sbc_get_frame_length(ref sbc_t sbc);

        [DllImport(dll, CallingConvention = CallingConvention.StdCall)]
        public static extern long sbc_encode(ref sbc_t sbc, byte[] input, ulong input_len,
            byte[] output, ulong output_len, out ulong written);

        [DllImport(dll, CallingConvention = CallingConvention.StdCall)]
        public static extern void sbc_finish(ref sbc_t sbc);
    }

    // Thin managed wrapper around SbcNative, same shape as SharpSBC's SbcEncoder.cs. CodeSize is
    // the raw PCM input block length in bytes this encoder expects per Encode() call; FrameSize is
    // the compressed output block length it produces.
    public sealed class SbcEncoder : IDisposable {
        private SbcNative.sbc_t sbc;

        public int CodeSize { get; }
        public int FrameSize { get; }

        public SbcEncoder(int sampleRate, SbcSubBandCount subBands, int bitPool,
                           SbcChannelMode channelMode, SbcAllocationMode allocation, SbcBlockCount blocks) {
            sbc = new SbcNative.sbc_t();
            if (SbcNative.sbc_init(ref sbc, 0) < 0)
                throw new InvalidOperationException("Could not initialize the SBC encoder (libsbc.dll).");

            int frequency;
            switch (sampleRate) {
                case 16000: frequency = SbcNative.SBC_FREQ_16000; break;
                case 32000: frequency = SbcNative.SBC_FREQ_32000; break;
                case 44100: frequency = SbcNative.SBC_FREQ_44100; break;
                case 48000: frequency = SbcNative.SBC_FREQ_48000; break;
                default: throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }
            sbc.frequency = (byte)frequency;
            sbc.subbands = (byte)subBands;
            sbc.mode = (byte)channelMode;
            sbc.endian = SbcNative.SBC_LE;
            sbc.bitpool = (byte)bitPool;
            sbc.allocation = (byte)allocation;
            sbc.blocks = (byte)blocks;

            CodeSize = (int)SbcNative.sbc_get_codesize(ref sbc);
            FrameSize = (int)SbcNative.sbc_get_frame_length(ref sbc);
        }

        // src must be exactly CodeSize bytes; dst must be at least FrameSize bytes. Returns the
        // number of encoded bytes written to dst (0 on failure).
        public int Encode(byte[] src, byte[] dst) {
            long consumed = SbcNative.sbc_encode(ref sbc, src, (ulong)CodeSize, dst, (ulong)dst.Length,
                out ulong written);
            return consumed < 0 ? 0 : (int)written;
        }

        public void Dispose() => SbcNative.sbc_finish(ref sbc);
    }
}
