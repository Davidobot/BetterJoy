namespace BetterJoyForCemu {
    // VIIPER remains the default backend (self-contained: BetterJoy already knows how to start
    // it, no separate install decision for the user beyond the one installer checkbox). Steam
    // Streaming Microphone (SteamMicrophoneEndpoint.cs) is the opt-out fallback - selected when
    // the global "Use VIIPER for DualSense microphone" setting is off, or when VIIPER itself
    // turns out not to be available (its own exe missing, its API never coming up, etc.) despite
    // being preferred. Unlike the Virtual Audio Driver by MTT this replaced, Steam's driver is
    // properly Microsoft attestation-signed (confirmed by inspecting its actual certificate
    // chain) - no test-signing mode required - so BetterJoy bundles and installs these same
    // three files (SteamStreamingMicrophone.sys/.inf/.cat) itself instead of depending on the
    // user having triggered Steam's own first-run install of it.
    internal static class MicrophoneEndpointFactory {
        public static IMicrophoneEndpoint Open() {
            if (ApplicationSettings.BoolValue("UseViiperForDualSenseMicrophone")) {
                try {
                    return ViiperMicrophoneEndpoint.Open();
                } catch {
                    // Preferred but absent/disabled right now - fall through to the other backend
                    // rather than failing the whole feature outright.
                }
            }

            return SteamMicrophoneEndpoint.Open();
        }
    }
}
