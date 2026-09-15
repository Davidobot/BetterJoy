using System;

namespace BetterJoyForCemu {
    // Common shape shared by every backend that can present decoded controller microphone PCM
    // as a Windows recording device - ViiperMicrophoneEndpoint (VIIPER + usbip-win2, emulates a
    // real USB audio device) and VadMicrophoneEndpoint (renders into an already-installed
    // Virtual Audio Driver by MTT endpoint instead). BluetoothMicrophoneWorker (DualSense.cs)
    // only ever talks to this interface, so which backend is actually in use is decided entirely
    // by MicrophoneEndpointFactory.Open.
    internal interface IMicrophoneEndpoint : IDisposable {
        void WriteMicrophonePcm(byte[] stereoPcm);
        bool IsMicrophoneInterfaceActive();
    }
}
