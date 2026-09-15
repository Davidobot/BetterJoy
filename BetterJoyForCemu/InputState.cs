using System.Collections.Generic;

namespace BetterJoyForCemu {
    // Tracks which keyboard keys and mouse buttons are CURRENTLY held, fed by Program's
    // OnKeyDown/OnKeyUp/OnMouseButtonDown/OnMouseButtonUp - the same unified entry points that
    // already work identically in GUI mode (direct WindowsInput.Capture.Global hook) and
    // service mode (forwarded from the session-launched input helper over a pipe).
    //
    // Exists so a bind can be a COMBINATION of inputs (for example a gyro activation mapping's
    // "joy_4+key_65"), not
    // just one - checking "is this whole combo held right now" needs to know the current held/
    // released state of every key and mouse button, not just react to the one that just changed.
    // Controller buttons don't need an entry here: each Joycon already exposes its own current
    // state directly via GetButton, checked per-instance where a combo is actually evaluated.
    public static class InputState {
        private static readonly HashSet<int> heldKeys = new HashSet<int>();
        private static readonly HashSet<int> heldMouseButtons = new HashSet<int>();
        private static readonly object stateLock = new object();

        public static void KeyDown(int keyCode) {
            lock (stateLock) { heldKeys.Add(keyCode); }
        }

        public static void KeyUp(int keyCode) {
            lock (stateLock) { heldKeys.Remove(keyCode); }
        }

        public static void MouseDown(int buttonCode) {
            lock (stateLock) { heldMouseButtons.Add(buttonCode); }
        }

        public static void MouseUp(int buttonCode) {
            lock (stateLock) { heldMouseButtons.Remove(buttonCode); }
        }

        public static bool IsKeyHeld(int keyCode) {
            lock (stateLock) { return heldKeys.Contains(keyCode); }
        }

        public static bool IsMouseButtonHeld(int buttonCode) {
            lock (stateLock) { return heldMouseButtons.Contains(buttonCode); }
        }
    }
}
