using System;
using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoyForCemu {
    // Runs inside the per-session input-helper process (InputHelper.cs) - WASAPI loopback capture
    // needs an interactive desktop, which Session 0 (BetterJoyService) doesn't have. See
    // IJoyconHost.StartBluetoothAudioCapture for why this lives here instead of in the service.
    //
    // Pipeline: EventSyncedLoopbackCapture (below, built on NAudio's WasapiCapture - already a
    // project dependency) on the selected/default render endpoint -> downmix to stereo float
    // (matches nefarius/DS4AudioStreamer's Downmixer.cs, MIT) -> SampleRateResampler
    // (SampleRateResampler.cs, libsamplerate) -> the controller-selected encoder: DS4 uses 16 kHz
    // PCM into SBC while DualSense uses 48 kHz float into fixed 200-byte Opus frames. Controller
    // report construction stays in DualShock4.cs/DualSense.cs. An earlier attempt used NAudio's
    // own MediaFoundationResampler here to
    // avoid a second native dependency; on real hardware that measurably dropped a fixed ~19% of
    // every callback's audio (confirmed via this file's own debug logging), so this now matches
    // the reference project's actual resampler choice instead.
    // NAudio's own WasapiLoopbackCapture only exposes a parameterless/default-device constructor
    // pairing with the default (non-event-synced, ~100ms-buffered) WasapiCapture base behavior.
    // Ported from nefarius/DS4AudioStreamer's BufferedLoopbackCapture (MIT): event-synced with the
    // minimum buffer the audio engine allows, delivering audio in small, steady chunks instead of
    // occasional ~100ms bursts - the bursty version measurably produced periodic audio skips on
    // real hardware, since DualShock4Controller's stream worker expects roughly steady arrival to
    // pace its own real-time output against.
    //
    // A DiscontinuityAwareLoopbackCapture variant (driving AudioClient/AudioCaptureClient
    // directly, to surface WASAPI's AudioClientBufferFlags.DataDiscontinuity - which
    // WasapiCapture's DataAvailable never exposes) was tried here and reverted the same day: on
    // real hardware it produced a genuine 14.2-SECOND capture-thread stall (confirmed via this
    // file's own callbackIntervalMs logging), far worse than anything seen before it, most likely
    // a bug in that from-scratch capture loop rather than proof of a real engine-level problem.
    // WasapiCapture's own internal loop is the proven, working implementation - stick with it
    // rather than re-attempting the from-scratch replacement without a way to test it directly
    // against real hardware first.
    internal sealed class EventSyncedLoopbackCapture : WasapiCapture {
        public EventSyncedLoopbackCapture(MMDevice captureDevice) : base(captureDevice, true, 0) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    internal sealed class BluetoothAudioCapture : IDisposable {
        private const int TargetChannels = 2;
        // DS4Windows' current loopback transport uses 16 kHz SBC so one 109-byte frame represents
        // 8 ms of audio. That permits the controller's reliable one-frame 0x12 report lane at
        // half the HID transaction rate of the old 32 kHz/4 ms form.
        private const int DualShock4SampleRate = 16000;
        private const int DualSenseOpusSampleRate = 48000;
        // Sony's Bluetooth media clock presents one 480-sample Opus packet every 10.667 ms,
        // consuming 512 frames from a normal 48 kHz render stream. A continuous 45 kHz resample
        // performs that same 512-to-480 conversion without allowing capture to outrun transport.
        private const int DualSenseCaptureOutputRate = 45000;
        private const int DualSenseOpusFrameSamples = 480;
        private const int DualSenseOpusFrameBytes = 200;

        private readonly Action<byte[]> onFrame;
        private EventSyncedLoopbackCapture capture;
        private SampleRateResampler resampler;
        private SbcEncoder sbcEncoder;
        private IOpusEncoder opusEncoder;
        private BluetoothAudioCodec codec;
        private float[] sourceCarry = new float[0]; // unconsumed stereo float frames, interleaved
        private byte[] pcmCarry = new byte[0]; // resampled 16-bit PCM bytes not yet a full SBC block
        private float[] opusCarry = new float[0]; // resampled floats not yet a 480-sample Opus frame
        private readonly object lifecycleLock = new object();
        private Stopwatch debugStopwatch;
        private double debugLastCallbackMs;
        private double debugLastSummaryMs;
        private double debugMaximumCallbackIntervalMs;
        private int debugCallbacksSinceSummary;
        private int debugFramesEncodedSinceSummary;

        public BluetoothAudioCapture(Action<byte[]> onFrame) {
            this.onFrame = onFrame;
        }

        // Never lets an exception escape: this runs on the input helper's single shared pipe
        // read-loop thread, which also drives keyboard/mouse remap - an unhandled throw here
        // (a missing/exclusive-mode audio device, a resampler init failure, ...) would otherwise
        // kill that entire loop and take the whole helper connection down with it, not just audio.
        //
        // Construction happens entirely outside lifecycleLock, only assigning the finished objects
        // to fields under a brief lock, and Stop (below) never calls a capture/resampler/encoder
        // method while holding that same lock either - WasapiCapture.StopRecording can block
        // waiting for its own callback thread to finish, and that callback thread
        // (OnDataAvailable) also takes lifecycleLock, so calling StopRecording from inside the
        // lock would risk a real deadlock between the two threads.
        public void Start(string endpointId, BluetoothAudioCodec requestedCodec) {
            Stop();
            try {
                StartInner(endpointId, requestedCodec);
            } catch {
                Stop(); // tear down whatever partially came up before the throw
            }
        }

        private void StartInner(string endpointId, BluetoothAudioCodec requestedCodec) {
            MMDevice device = null;
            using (var enumerator = new MMDeviceEnumerator()) {
                if (!String.IsNullOrEmpty(endpointId)) {
                    try { device = enumerator.GetDevice(endpointId); } catch { device = null; }
                }
                if (device == null)
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            var newCapture = new EventSyncedLoopbackCapture(device);
            int targetSampleRate = requestedCodec == BluetoothAudioCodec.DualSenseOpus
                ? DualSenseCaptureOutputRate
                : DualShock4SampleRate;
            double ratio = (double)targetSampleRate / newCapture.WaveFormat.SampleRate;
            var newResampler = new SampleRateResampler(ResampleQuality.SincBest, TargetChannels, ratio);
            SbcEncoder newSbcEncoder = null;
            IOpusEncoder newOpusEncoder = null;
            if (requestedCodec == BluetoothAudioCodec.DualSenseOpus) {
                newOpusEncoder = OpusCodecFactory.CreateEncoder(DualSenseOpusSampleRate,
                    TargetChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
                newOpusEncoder.Bitrate = 160000;
                newOpusEncoder.UseVBR = false;
                newOpusEncoder.Complexity = 0;
            } else {
                newSbcEncoder = new SbcEncoder(DualShock4SampleRate, SbcSubBandCount.Sb8, 48,
                    SbcChannelMode.JointStereo, SbcAllocationMode.Snr, SbcBlockCount.Blk16);
            }

            lock (lifecycleLock) {
                capture = newCapture;
                resampler = newResampler;
                sbcEncoder = newSbcEncoder;
                opusEncoder = newOpusEncoder;
                codec = requestedCodec;
                sourceCarry = new float[0];
                pcmCarry = new byte[0];
                opusCarry = new float[0];
                debugStopwatch = Stopwatch.StartNew();
                debugLastCallbackMs = 0;
                debugLastSummaryMs = 0;
                debugMaximumCallbackIntervalMs = 0;
                debugCallbacksSinceSummary = 0;
                debugFramesEncodedSinceSummary = 0;
            }

            newCapture.DataAvailable += OnDataAvailable;
            newCapture.StartRecording();
            AudioDebugLog.Write("Capture", "Start device=" + device.FriendlyName +
                " codec=" + requestedCodec +
                " sourceRate=" + newCapture.WaveFormat.SampleRate +
                " sourceChannels=" + newCapture.WaveFormat.Channels +
                " targetRate=" + targetSampleRate +
                (newSbcEncoder == null ? " opusFrameBytes=" + DualSenseOpusFrameBytes :
                    " codeSize=" + newSbcEncoder.CodeSize + " frameSize=" + newSbcEncoder.FrameSize));
        }

        public void Stop() {
            EventSyncedLoopbackCapture captureToStop;
            SampleRateResampler resamplerToDispose;
            SbcEncoder encoderToDispose;
            lock (lifecycleLock) {
                captureToStop = capture;
                resamplerToDispose = resampler;
                encoderToDispose = sbcEncoder;
                capture = null;
                resampler = null;
                sbcEncoder = null;
                opusEncoder = null;
                sourceCarry = new float[0];
                pcmCarry = new byte[0];
                opusCarry = new float[0];
            }

            if (captureToStop != null) {
                captureToStop.DataAvailable -= OnDataAvailable;
                try { captureToStop.StopRecording(); } catch { }
                captureToStop.Dispose();
                AudioDebugLog.Write("Capture", "Stop");
            }
            resamplerToDispose?.Dispose();
            encoderToDispose?.Dispose();
        }

        // Runs on a WASAPI-internal callback thread - an unhandled exception here would take
        // capture down (or worse) with no path back for this session until the helper relaunches.
        // Best-effort: drop this one buffer's worth of audio rather than the whole stream.
        private void OnDataAvailable(object sender, WaveInEventArgs e) {
            try {
                OnDataAvailableLocked(e);
            } catch {
            }
        }

        private void OnDataAvailableLocked(WaveInEventArgs e) {
            lock (lifecycleLock) {
                if (capture == null)
                    return; // stopped concurrently with a capture callback already in flight

                int sourceChannels = capture.WaveFormat.Channels;
                int floatCount = e.BytesRecorded / 4;
                var samples = new float[floatCount];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, floatCount * 4);

                if (sourceChannels < 2)
                    return; // mono/invalid loopback format - nothing sensible to downmix

                int frames = floatCount / sourceChannels;
                // Always routed through Downmix, even when sourceChannels is already 2 - it isn't
                // just a channel-count reducer, it also applies the reference's 0.5x headroom
                // scale unconditionally. An earlier "already stereo, skip it" fast path silently
                // dropped that scaling for the common case (a plain stereo default device),
                // sending full-scale audio into the encoder - real hardware showed that as
                // clipping (muffled/distorted audio), not a channel-mixing problem.
                var stereo = new float[frames * 2];
                Downmix(samples, stereo, frames, sourceChannels);

                // Diagnostic only - reports how different L and R actually are right after
                // downmix, to localize a reported "right channel only" symptom: if this is
                // already ~0 here, the source capture/downmix itself has no stereo content to
                // lose; if it's clearly nonzero here but ~0 after resampling below, the resampler
                // is the culprit; if it stays nonzero all the way through, the loss (if any) is
                // downstream of this file entirely (SBC encode or the controller's own hardware).
                float preResampleMaxDiff = MaxChannelDiff(stereo, frames);

                // Prepend whatever libsamplerate didn't consume last callback - it doesn't
                // guarantee draining everything given to it in one Process() call.
                float[] input;
                int inputFrames;
                int carryFrames = sourceCarry.Length / 2;
                if (carryFrames == 0) {
                    input = stereo;
                    inputFrames = frames;
                } else {
                    input = new float[sourceCarry.Length + stereo.Length];
                    Buffer.BlockCopy(sourceCarry, 0, input, 0, sourceCarry.Length * 4);
                    Buffer.BlockCopy(stereo, 0, input, sourceCarry.Length * 4, stereo.Length * 4);
                    inputFrames = carryFrames + frames;
                }

                int maxOutFrames = (int)Math.Ceiling(inputFrames * resampler.Ratio) + 16;
                var resampled = new float[maxOutFrames * 2];
                (int used, int generated) = resampler.Process(input, inputFrames, resampled, maxOutFrames, false);
                float postResampleMaxDiff = MaxChannelDiff(resampled, generated);

                int unusedFrames = inputFrames - used;
                if (unusedFrames > 0) {
                    sourceCarry = new float[unusedFrames * 2];
                    Buffer.BlockCopy(input, used * 2 * 4, sourceCarry, 0, sourceCarry.Length * 4);
                } else {
                    sourceCarry = new float[0];
                }

                int framesEncoded;
                if (codec == BluetoothAudioCodec.DualSenseOpus) {
                    framesEncoded = generated > 0 ? ConsumeResampledOpus(resampled, generated) : 0;
                } else {
                    var pcm = new byte[generated * TargetChannels * 2];
                    for (int i = 0; i < generated * TargetChannels; i++) {
                        float sample = Math.Max(-1f, Math.Min(1f, resampled[i]));
                        short pcmSample = (short)Math.Round(sample * short.MaxValue);
                        pcm[i * 2] = (byte)pcmSample;
                        pcm[i * 2 + 1] = (byte)(pcmSample >> 8);
                    }
                    framesEncoded = pcm.Length > 0 ? ConsumeResampledPcm(pcm, pcm.Length) : 0;
                }

                double nowMs = debugStopwatch.Elapsed.TotalMilliseconds;
                double intervalMs = nowMs - debugLastCallbackMs;
                debugLastCallbackMs = nowMs;
                debugMaximumCallbackIntervalMs = Math.Max(
                    debugMaximumCallbackIntervalMs, intervalMs);
                debugCallbacksSinceSummary++;
                debugFramesEncodedSinceSummary += framesEncoded;
                if (intervalMs > 15.0 || e.BytesRecorded == 0 ||
                    nowMs - debugLastSummaryMs >= 1000.0) {
                    debugLastSummaryMs = nowMs;
                    AudioDebugLog.Write("Capture", "callbackIntervalMs=" + intervalMs.ToString("F1") +
                        " maxCallbackIntervalMs=" + debugMaximumCallbackIntervalMs.ToString("F1") +
                        " callbacks=" + debugCallbacksSinceSummary +
                        " bytesRecorded=" + e.BytesRecorded + " inputFrames=" + inputFrames +
                        " used=" + used + " generated=" + generated +
                        " framesEncoded=" + debugFramesEncodedSinceSummary +
                        " preResampleMaxLRDiff=" + preResampleMaxDiff.ToString("F4") +
                        " postResampleMaxLRDiff=" + postResampleMaxDiff.ToString("F4"));
                    debugMaximumCallbackIntervalMs = 0;
                    debugCallbacksSinceSummary = 0;
                    debugFramesEncodedSinceSummary = 0;
                }
            }
        }

        private int ConsumeResampledPcm(byte[] data, int length) {
            byte[] pcm;
            if (pcmCarry.Length == 0) {
                pcm = data;
            } else {
                pcm = new byte[pcmCarry.Length + length];
                Buffer.BlockCopy(pcmCarry, 0, pcm, 0, pcmCarry.Length);
                Buffer.BlockCopy(data, 0, pcm, pcmCarry.Length, length);
                length = pcm.Length;
            }

            int codeSize = sbcEncoder.CodeSize;
            int offset = 0;
            int framesEncoded = 0;
            byte[] pcmBlock = new byte[codeSize];
            byte[] sbcBlock = new byte[sbcEncoder.FrameSize];
            while (length - offset >= codeSize) {
                Buffer.BlockCopy(pcm, offset, pcmBlock, 0, codeSize);
                offset += codeSize;

                int encoded = sbcEncoder.Encode(pcmBlock, sbcBlock);
                if (encoded <= 0)
                    continue;

                byte[] frame = new byte[encoded];
                Buffer.BlockCopy(sbcBlock, 0, frame, 0, encoded);
                onFrame(frame);
                framesEncoded++;
            }

            int remaining = length - offset;
            pcmCarry = new byte[remaining];
            if (remaining > 0)
                Buffer.BlockCopy(pcm, offset, pcmCarry, 0, remaining);

            return framesEncoded;
        }

        private int ConsumeResampledOpus(float[] data, int frameCount) {
            int length = frameCount * TargetChannels;
            float[] samples;
            if (opusCarry.Length == 0) {
                samples = data;
            } else {
                samples = new float[opusCarry.Length + length];
                Buffer.BlockCopy(opusCarry, 0, samples, 0, opusCarry.Length * 4);
                Buffer.BlockCopy(data, 0, samples, opusCarry.Length * 4, length * 4);
                length = samples.Length;
            }

            int samplesPerFrame = DualSenseOpusFrameSamples * TargetChannels;
            int offset = 0;
            int framesEncoded = 0;
            byte[] encodedFrame = new byte[DualSenseOpusFrameBytes];
            while (length - offset >= samplesPerFrame) {
                int encoded = opusEncoder.Encode(
                    new ReadOnlySpan<float>(samples, offset, samplesPerFrame),
                    DualSenseOpusFrameSamples, new Span<byte>(encodedFrame),
                    encodedFrame.Length);
                offset += samplesPerFrame;

                // The controller's speaker TLV is fixed at exactly 200 bytes. With 160 kbps CBR,
                // one 10 ms stereo frame is exactly that size. A shorter/error frame cannot be
                // padded without changing the Opus packet and is therefore dropped.
                if (encoded != DualSenseOpusFrameBytes)
                    continue;

                byte[] frame = new byte[DualSenseOpusFrameBytes];
                Buffer.BlockCopy(encodedFrame, 0, frame, 0, frame.Length);
                onFrame(frame);
                framesEncoded++;
            }

            int remaining = length - offset;
            opusCarry = new float[remaining];
            if (remaining > 0)
                Buffer.BlockCopy(samples, offset * 4, opusCarry, 0, remaining * 4);

            return framesEncoded;
        }

        // Diagnostic only - largest |L-R| across an interleaved stereo buffer's first frameCount
        // frames. Near 0 means the two channels are effectively identical at this point.
        private static float MaxChannelDiff(float[] stereoInterleaved, int frameCount) {
            float maxDiff = 0f;
            for (int i = 0; i < frameCount; i++) {
                float diff = Math.Abs(stereoInterleaved[i * 2] - stereoInterleaved[i * 2 + 1]);
                if (diff > maxDiff)
                    maxDiff = diff;
            }
            return maxDiff;
        }

        // Ported from nefarius/DS4AudioStreamer's Downmixer.DownmixToStereo (MIT). Always called,
        // even for an already-2-channel source (see the caller's comment) - the unconditional
        // 0.5x scale on every output sample is the reference's own headroom choice, kept faithful
        // rather than "corrected" without a reason to.
        private static void Downmix(float[] input, float[] output, int frames, int sourceChannels) {
            int inIdx = 0, outIdx = 0;
            for (int i = 0; i < frames; i++) {
                float left = input[inIdx];
                float right = input[inIdx + 1];

                if (sourceChannels > 2) {
                    left += input[inIdx + 2] * 0.7f;
                    right += input[inIdx + 2] * 0.7f;
                }
                if (sourceChannels > 3) {
                    left += input[inIdx + 3] * 0.5f;
                    right += input[inIdx + 3] * 0.5f;
                }
                if (sourceChannels > 4)
                    left += input[inIdx + 4] * 0.7f;
                if (sourceChannels > 5)
                    right += input[inIdx + 5] * 0.7f;
                if (sourceChannels > 6)
                    left += input[inIdx + 6] * 0.7f;
                if (sourceChannels > 7)
                    right += input[inIdx + 7] * 0.7f;

                output[outIdx] = left * 0.5f;
                output[outIdx + 1] = right * 0.5f;

                inIdx += sourceChannels;
                outIdx += 2;
            }
        }

        public void Dispose() => Stop();
    }
}
