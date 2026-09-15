using System;

namespace BetterJoyForCemu {
    // JoyconController : NintendoController - step 5 sub-step D1 of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. The only Nintendo-family type that pairs (SupportsPairing) - two physical
    // units combine into one logical controller, each with a single real stick (HasDualSticks is
    // false; the pair's second stick comes from its partner, not from this unit itself).
    public class JoyconController : NintendoController {
        public override bool SupportsPairing => true;
        public override bool HasDualSticks => false;
        public override bool HasGyro => true;
        public override bool HasAnalogTriggers => false;
        public override bool UsesNintendoProtocol => true;
        public override ControllerKind Kind => isLeft ? ControllerKind.Left : ControllerKind.Right;

        // isLeft is a real, caller-supplied identity here (unlike Pro/SNES/N64, where it's always
        // true by convention) - see NintendoController's base constructor for isPro/isSnes/is64,
        // which this passes as all-false to match a plain Joy-Con's identity.
        public JoyconController(IntPtr handle_, bool imu, bool localize, float alpha,
                bool left, string path, string serialNum, int id = 0,
                bool thirdParty = false, bool? isUsbOverride = null)
            : base(handle_, imu, localize, alpha, left, path, serialNum, id,
                  isPro: false, isSnes: false, is64: false, thirdParty: thirdParty,
                  isUsbOverride: isUsbOverride) {
        }
    }
}
