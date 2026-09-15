using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoyForCemu {
    public sealed class ControllerAudioEndpoint {
        public string Id { get; set; }
        public string Name { get; set; }

        public override string ToString() => Name;
    }

    // Windows owns wired Sony-controller audio as an ordinary USB Audio Class render endpoint.
    // This edge deliberately contains no controller protocol: DualSense/DualShock4 prepare their
    // own hardware, while this class only enumerates endpoints and renders channel-safe PCM.
    public static class ControllerAudio {
        public static List<ControllerAudioEndpoint> GetRenderEndpoints() {
            using (var enumerator = new MMDeviceEnumerator()) {
                MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render, DeviceState.Active);
                return devices.Select(device => new ControllerAudioEndpoint {
                    Id = device.ID,
                    Name = device.FriendlyName,
                }).OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public static async Task PlayTestToneAsync(string endpointId, int volumePercent) {
            if (String.IsNullOrEmpty(endpointId))
                throw new InvalidOperationException("No controller audio endpoint is selected.");

            using (var enumerator = new MMDeviceEnumerator())
            using (MMDevice endpoint = enumerator.GetDevice(endpointId))
            using (var output = new WasapiOut(
                endpoint, AudioClientShareMode.Shared, false, 40)) {
                WaveFormat mix = endpoint.AudioClient.MixFormat;
                var tone = new ControllerSpeakerTone(
                    mix.SampleRate, mix.Channels, volumePercent, TimeSpan.FromMilliseconds(650));
                var completion = new TaskCompletionSource<bool>();
                output.PlaybackStopped += (sender, args) => {
                    if (args.Exception != null)
                        completion.TrySetException(args.Exception);
                    else
                        completion.TrySetResult(true);
                };
                output.Init(tone);
                output.Play();
                await completion.Task;
            }
        }

        private sealed class ControllerSpeakerTone : ISampleProvider {
            private readonly int channels;
            private readonly int totalFrames;
            private readonly int fadeFrames;
            private readonly float amplitude;
            private int frame;
            private double phase;

            public WaveFormat WaveFormat { get; }

            public ControllerSpeakerTone(int sampleRate, int channels, int volumePercent,
                                         TimeSpan duration) {
                this.channels = Math.Max(1, channels);
                totalFrames = Math.Max(1, (int)(sampleRate * duration.TotalSeconds));
                fadeFrames = Math.Max(1, sampleRate / 50);
                amplitude = 0.18f * Math.Max(0, Math.Min(100, volumePercent)) / 100.0f;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, this.channels);
            }

            public int Read(float[] buffer, int offset, int count) {
                int requestedFrames = count / channels;
                int frames = Math.Min(requestedFrames, totalFrames - frame);
                for (int i = 0; i < frames; i++, frame++) {
                    int remaining = totalFrames - frame;
                    float fade = Math.Min(1.0f,
                        Math.Min(frame / (float)fadeFrames, remaining / (float)fadeFrames));
                    float sample = (float)Math.Sin(phase) * amplitude * fade;
                    phase += 2.0 * Math.PI * 880.0 / WaveFormat.SampleRate;
                    if (phase >= 2.0 * Math.PI)
                        phase -= 2.0 * Math.PI;

                    int sampleOffset = offset + i * channels;
                    for (int channel = 0; channel < channels; channel++)
                        buffer[sampleOffset + channel] = channel < 2 ? sample : 0.0f;
                }
                return frames * channels;
            }
        }
    }
}
