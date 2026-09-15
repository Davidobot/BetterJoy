namespace BetterJoyForCemu.VirtualOutput {
    // Common surface both Xbox 360 output backends (ViGEmBus-backed OutputControllerXbox360 and
    // VIIPER-backed OutputControllerXbox360Viiper) expose, so Controller.out_xbox and every call
    // site that feeds it input/rumble state doesn't need to know or care which one a profile
    // actually picked. Reuses ViGEm's own Xbox360FeedbackReceivedEventArgs/EventHandler for
    // rumble feedback rather than inventing a parallel type - it's just (largeMotor, smallMotor,
    // ledNumber) with a public constructor, and every existing consumer (Controller.ReceiveRumble)
    // already reads it that shape regardless of which backend produced it.
    public interface IOutputControllerXbox360 {
        event OutputControllerXbox360.Xbox360FeedbackReceivedEventHandler FeedbackReceived;

        bool UpdateInput(OutputControllerXbox360InputState newState);
        void Connect();
        void Disconnect();
        int UserIndex { get; }
    }
}
