using System;

namespace BetterJoyForCemu {
    // SnesController : NintendoController - step 5 sub-step D1 of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. Single physical unit, no sticks at all, no gyro/accel hardware.
    //
    // KNOWN ISSUE, deliberately not fixed here (see clever-wiggling-rocket.md's "Known issue this
    // step deliberately does NOT fix"): HasDualSticks/HasGyro below restate today's exact
    // (wrong-for-SNES) answers rather than correcting them - a SNES controller has zero physical
    // sticks and no gyro/accel sensor at all, but historically inherited "true" for both via the
    // isPro superset flag, and nothing external currently trusts these two properties in a way
    // that produces an observable bug. HasSticks=false/HasImuHardware=false (the real, currently-
    // exercised gates, set via the base isSnes flag below) already correctly skip stick/gyro
    // processing for this type - only the PUBLIC capability contract is left inaccurate.
    public class SnesController : NintendoController {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;   // known issue - see class comment above
        public override bool HasGyro => true;          // known issue - see class comment above
        public override bool HasAnalogTriggers => false;
        public override bool UsesNintendoProtocol => true;
        public override ControllerKind Kind => ControllerKind.Snes;

        // isLeft is hardcoded true (not a caller parameter) - not semantic for a SNES controller,
        // only there so NintendoController's shared report-parsing code (which still branches on
        // isLeft for byte-offset selection) picks the correct half of the report layout.
        public SnesController(IntPtr handle_, bool imu, bool localize, float alpha,
                string path, string serialNum, int id = 0, bool thirdParty = false,
                bool? isUsbOverride = null)
            : base(handle_, imu, localize, alpha, true, path, serialNum, id,
                  isPro: false, isSnes: true, is64: false, thirdParty: thirdParty,
                  isUsbOverride: isUsbOverride) {
        }
    }
}
