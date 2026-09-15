using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BetterJoyForCemu {
    // Opt-in alternative to the ordinary "set the controller as your Windows default playback
    // device" USB audio flow (PrepareUsbAudio/ControllerAudio.cs). Unlike Bluetooth, USB already
    // gives the controller a real Windows audio device - no HID report smuggling needed - so this
    // is a pure same-machine audio router: capture the system's current default playback device
    // via WASAPI loopback (same technique BluetoothAudioCapture.cs uses) and render it into the
    // controller's own USB Audio Class endpoint via WasapiOut. Runs entirely inside the
    // per-session input-helper process, like BluetoothAudioCapture - both capture and render need
    // an interactive desktop Session 0 doesn't have.
    internal sealed class UsbAudioLoopback : IDisposable {
        private const int TargetChannels = 2;

        // The source and target render devices each run off their own independent hardware
        // clock. A resample ratio fixed at the two devices' nominal sample rates is only exactly
        // right if those clocks are perfectly matched, which real hardware crystals never are -
        // the tiny leftover drift accumulates until the output buffer either empties (audible
        // gap) or fills past capacity (DiscardOnBufferOverflow drops audio) roughly
        // periodically, matching the ~2000ms blip reported on real hardware. Nudging the ratio by
        // a few hundred parts-per-million based on how full the buffer actually is - the same
        // kind of clock-drift servo VIIPER's own telemetry (microphoneServoRatePPM, seen while
        // investigating the DualSense mic feature) confirms Sony's own official audio bridging
        // needs for this exact class of problem - keeps the buffer level stable indefinitely
        // instead of slowly drifting to one extreme.
        private const double TargetBufferedMs = 250.0;
        private const double ServoGainPpmPerMs = 5.0;
        private const double MaxCorrectionPpm = 2000.0;

        private readonly object lifecycleLock = new object();
        private EventSyncedLoopbackCapture capture;
        private WasapiOut output;
        private BufferedWaveProvider outputBuffer;
        private SampleRateResampler resampler;
        private VolumeSampleProvider volumeProvider;
        private double nominalRatio;
        private float[] sourceCarry = new float[0]; // unconsumed stereo float frames, interleaved
        private string currentSourceEndpointId;
        private string currentTargetEndpointId;
        private bool running;

        // Program.cs's profile-reconciliation timer calls this every ~2 seconds regardless of
        // whether anything actually changed (same as it does for every other per-profile audio
        // setting) - Start used to unconditionally tear down and rebuild the whole capture/render
        // pipeline on every single call, producing an audible blip on that exact cadence
        // (confirmed on real hardware: "every 2000ms"). Only source/target actually changing
        // needs a real restart; a volume-only change is applied live via the existing
        // VolumeSampleProvider instead.
        public void Start(string sourceEndpointId, string targetEndpointId, string targetNameHint,
            int volumePercent) {
            sourceEndpointId = sourceEndpointId ?? String.Empty;
            lock (lifecycleLock) {
                if (running &&
                    String.Equals(currentSourceEndpointId, sourceEndpointId, StringComparison.Ordinal) &&
                    String.Equals(currentTargetEndpointId, targetEndpointId, StringComparison.Ordinal)) {
                    if (volumeProvider != null)
                        volumeProvider.Volume = Math.Max(0, Math.Min(100, volumePercent)) / 100f;
                    return;
                }
            }

            Stop();
            try {
                StartInner(sourceEndpointId, targetEndpointId, targetNameHint, volumePercent);
            } catch {
                Stop(); // tear down whatever partially came up before the throw
            }
        }

        private void StartInner(string sourceEndpointId, string targetEndpointId,
            string targetNameHint, int volumePercent) {
            MMDevice sourceDevice = null;
            MMDevice targetDevice = null;
            using (var enumerator = new MMDeviceEnumerator()) {
                if (!String.IsNullOrEmpty(sourceEndpointId)) {
                    try { sourceDevice = enumerator.GetDevice(sourceEndpointId); } catch { sourceDevice = null; }
                }
                if (sourceDevice == null)
                    sourceDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                if (!String.IsNullOrEmpty(targetEndpointId)) {
                    try { targetDevice = enumerator.GetDevice(targetEndpointId); } catch { targetDevice = null; }
                }
                // "Default" (no explicit selection) for the target means "the controller's own
                // endpoint", not the system default - rendering desktop audio into itself would
                // be a feedback loop, and the whole point of this feature is routing to the
                // controller specifically. Same substring match Reassign.cs's endpoint dropdown
                // already uses to auto-suggest a device for the UI, just applied here so it also
                // resolves for real at the point actual audio routing happens, not just display.
                if (targetDevice == null && !String.IsNullOrEmpty(targetNameHint)) {
                    foreach (MMDevice candidate in enumerator.EnumerateAudioEndPoints(
                            DataFlow.Render, DeviceState.Active)) {
                        if (candidate.FriendlyName.IndexOf(targetNameHint,
                                StringComparison.OrdinalIgnoreCase) >= 0) {
                            targetDevice = candidate;
                            break;
                        }
                        candidate.Dispose();
                    }
                }
                if (targetDevice == null)
                    throw new InvalidOperationException(
                        "No controller audio endpoint is selected, and none could be found automatically.");
            }

            var newCapture = new EventSyncedLoopbackCapture(sourceDevice);
            WaveFormat targetFormat = targetDevice.AudioClient.MixFormat;
            double ratio = (double)targetFormat.SampleRate / newCapture.WaveFormat.SampleRate;
            var newResampler = new SampleRateResampler(ResampleQuality.SincBest, TargetChannels, ratio);

            var newOutputBuffer = new BufferedWaveProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(targetFormat.SampleRate, TargetChannels)) {
                // A few hundred ms of slack absorbs capture-side jitter without ever blocking the
                // capture callback (AddSamples drops the newest audio instead of growing forever
                // once full - matching every other capture path in this codebase's own "best
                // effort, never let audio plumbing stall real work" convention).
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };

            var newOutput = new WasapiOut(targetDevice, AudioClientShareMode.Shared, false, 100);
            var newVolumeProvider = new VolumeSampleProvider(newOutputBuffer.ToSampleProvider()) {
                Volume = Math.Max(0, Math.Min(100, volumePercent)) / 100f,
            };
            newOutput.Init(newVolumeProvider);

            lock (lifecycleLock) {
                capture = newCapture;
                resampler = newResampler;
                nominalRatio = ratio;
                output = newOutput;
                outputBuffer = newOutputBuffer;
                volumeProvider = newVolumeProvider;
                sourceCarry = new float[0];
                currentSourceEndpointId = sourceEndpointId;
                currentTargetEndpointId = targetEndpointId;
                running = true;
            }

            newCapture.DataAvailable += OnDataAvailable;
            newCapture.StartRecording();
            newOutput.Play();
            AudioDebugLog.Write("UsbLoopback", "Start source=" + sourceDevice.FriendlyName +
                " target=" + targetDevice.FriendlyName +
                " sourceRate=" + newCapture.WaveFormat.SampleRate +
                " targetRate=" + targetFormat.SampleRate);
        }

        public void Stop() {
            EventSyncedLoopbackCapture captureToStop;
            WasapiOut outputToStop;
            SampleRateResampler resamplerToDispose;
            lock (lifecycleLock) {
                captureToStop = capture;
                outputToStop = output;
                resamplerToDispose = resampler;
                capture = null;
                output = null;
                outputBuffer = null;
                resampler = null;
                volumeProvider = null;
                sourceCarry = new float[0];
                currentSourceEndpointId = null;
                currentTargetEndpointId = null;
                running = false;
            }

            if (captureToStop != null) {
                captureToStop.DataAvailable -= OnDataAvailable;
                try { captureToStop.StopRecording(); } catch { }
                captureToStop.Dispose();
            }
            if (outputToStop != null) {
                try { outputToStop.Stop(); } catch { }
                outputToStop.Dispose();
                AudioDebugLog.Write("UsbLoopback", "Stop");
            }
            resamplerToDispose?.Dispose();
        }

        // Runs on a WASAPI-internal callback thread - matches BluetoothAudioCapture's own
        // never-let-an-exception-escape reasoning (this thread also owns the capture device's
        // teardown path).
        private void OnDataAvailable(object sender, WaveInEventArgs e) {
            try {
                OnDataAvailableLocked(e);
            } catch {
            }
        }

        private void OnDataAvailableLocked(WaveInEventArgs e) {
            lock (lifecycleLock) {
                if (capture == null || outputBuffer == null)
                    return; // stopped concurrently with a capture callback already in flight

                int sourceChannels = capture.WaveFormat.Channels;
                int floatCount = e.BytesRecorded / 4;
                var samples = new float[floatCount];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, floatCount * 4);

                if (sourceChannels < 2)
                    return; // mono/invalid loopback format - nothing sensible to downmix

                int frames = floatCount / sourceChannels;
                var stereo = new float[frames * 2];
                Downmix(samples, stereo, frames, sourceChannels);

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

                // Buffer trending full (positive error) means production is outrunning the
                // render device's real drain rate - ease the ratio down slightly so future output
                // is a hair smaller than nominal; trending empty does the opposite. The clamp
                // keeps this well below anything perceptible as a pitch shift even at the extremes.
                double bufferedMs = outputBuffer.BufferedDuration.TotalMilliseconds;
                double correctionPpm = Math.Max(-MaxCorrectionPpm, Math.Min(MaxCorrectionPpm,
                    -(bufferedMs - TargetBufferedMs) * ServoGainPpmPerMs));
                resampler.Ratio = nominalRatio * (1.0 + correctionPpm / 1_000_000.0);

                int maxOutFrames = (int)Math.Ceiling(inputFrames * resampler.Ratio) + 16;
                var resampled = new float[maxOutFrames * 2];
                (int used, int generated) = resampler.Process(input, inputFrames, resampled, maxOutFrames, false);

                int unusedFrames = inputFrames - used;
                if (unusedFrames > 0) {
                    sourceCarry = new float[unusedFrames * 2];
                    Buffer.BlockCopy(input, used * 2 * 4, sourceCarry, 0, sourceCarry.Length * 4);
                } else {
                    sourceCarry = new float[0];
                }

                if (generated > 0) {
                    var bytes = new byte[generated * TargetChannels * 4];
                    Buffer.BlockCopy(resampled, 0, bytes, 0, bytes.Length);
                    outputBuffer.AddSamples(bytes, 0, bytes.Length);
                }
            }
        }

        // Ported from BluetoothAudioCapture.cs's own Downmix (same nefarius/DS4AudioStreamer
        // origin, MIT) - kept as a private copy rather than a shared helper since the two files'
        // lifecycle/locking shapes differ enough that sharing would need its own abstraction for
        // no real benefit at this size.
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
