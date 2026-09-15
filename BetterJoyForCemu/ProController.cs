using System;

namespace BetterJoyForCemu {
    // ProController : NintendoController - step 5 sub-step D1 of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. Single physical unit, two real sticks, never pairs.
    public class ProController : NintendoController {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;
        public override bool HasGyro => true;
        public override bool HasAnalogTriggers => false;
        public override bool UsesNintendoProtocol => true;
        public override ControllerKind Kind => ControllerKind.Pro;

        // isLeft is hardcoded true (not a caller parameter) - it's not semantic for a Pro
        // Controller, only there so NintendoController's shared report-parsing code (which still
        // branches on isLeft for byte-offset selection) picks the correct half of the report
        // layout, matching every prior connect-site convention for this device type.
        public ProController(IntPtr handle_, bool imu, bool localize, float alpha,
                string path, string serialNum, int id = 0, bool thirdParty = false,
                bool? isUsbOverride = null)
            : base(handle_, imu, localize, alpha, true, path, serialNum, id,
                  isPro: true, isSnes: false, is64: false, thirdParty: thirdParty,
                  isUsbOverride: isUsbOverride) {
        }
    }
}
