using System;
using System.Configuration;
using System.Diagnostics;

namespace BetterJoyForCemu {
    // N64Controller : NintendoController - step 5 sub-step D1 of DOCS/CONTROLLERS-REFACTOR.md's
    // migration order. Single physical unit with one real analog stick (goes through the shared
    // stick-parsing path like Joy-Con/Pro - HasSticks is true, set via the base isSnes=false flag
    // below), then remapped through this class's own Getn64StickValues: tracks the observed
    // min/max range live (a Joy-Con's stick can't physically match a real N64 controller's stick
    // 1:1) and, when N64Range is enabled, rescales output into the tighter ±0.79 range that
    // approximates a real N64 controller's octagonal gate - the physical stopper around the stick
    // that clips diagonal deflection short of a full circle - so games see input shaped like the
    // real hardware, not a full circular Joy-Con range.
    //
    // KNOWN ISSUE, deliberately not fixed here (see clever-wiggling-rocket.md's "Known issue this
    // step deliberately does NOT fix"): HasDualSticks/HasGyro below restate today's exact
    // (wrong-for-N64) answers rather than correcting them - N64 has no gyro/accel sensor at all
    // (HasImuHardware, the real currently-exercised gate, is correctly false via the base is64
    // flag), and HasDualSticks=true is preserved even though N64 doesn't have a second physical
    // stick, because Controller.MapToXbox360Input's paired-button-population block (the "else if
    // (hasDualSticks)"-adjacent code inside NintendoController.ProcessButtonsAndStick) is what
    // actually populates the SHOULDER2_2/Y/MINUS button bits N64's own is64-specific output
    // mapping reads - flipping this to false would silently break real N64 button output, not
    // just correct a naming inaccuracy. Left exactly as today pending a closer look at that
    // coupling, not casually "fixed" here.
    public class N64Controller : NintendoController {
        public override bool SupportsPairing => false;
        public override bool HasDualSticks => true;   // known issue - see class comment above
        public override bool HasGyro => true;          // known issue - see class comment above
        public override bool HasAnalogTriggers => false;
        public override bool UsesNintendoProtocol => true;
        public override ControllerKind Kind => ControllerKind.N64;

        // isLeft is hardcoded true (not a caller parameter) - not semantic for an N64 controller,
        // only there so NintendoController's shared report-parsing code (which still branches on
        // isLeft for byte-offset selection) picks the correct half of the report layout.
        public N64Controller(IntPtr handle_, bool imu, bool localize, float alpha,
                string path, string serialNum, int id = 0, bool thirdParty = false,
                bool? isUsbOverride = null)
            : base(handle_, imu, localize, alpha, true, path, serialNum, id,
                  isPro: false, isSnes: false, is64: true, thirdParty: thirdParty,
                  isUsbOverride: isUsbOverride) {
        }

        // Observed live stick range (min/max seen so far) that Getn64StickValues rescales against
        // - moved verbatim from Joycon.cs (step 5 sub-step D1), genuinely N64-only, never shared
        // with the rest of the Nintendo family.
        private float maxX = 0.5f;
        private float minX = -0.5f;
        private float maxY = 0.5f;
        private float minY = -0.5f;

        private bool realn64Range = Boolean.Parse(ConfigurationManager.AppSettings["N64Range"]);

        private static float GetNormalizedValue(float value, float rawMin, float rawMax, float normalizedMin, float normalizedMax)
        {
            return (value - rawMin) / (rawMax - rawMin) * (normalizedMax - normalizedMin) + normalizedMin;
        }

        // internal, not private: called from Controller.MapToXbox360Input via an explicit
        // N64Controller cast (is64 being true already guarantees input actually is one - no other
        // Kind ever reports N64).
        internal static float[] Getn64StickValues(N64Controller input)
        {
            var isLeft = input.isLeft;
            var other = input.other;
            var stick = input.stick;
            var stick2 = input.stick2;
            var stick_correction = new float[] { 0f, 0f};

            // other is Joycon-typed (retyping to JoyconController is a later sub-step - see
            // DOCS/CONTROLLERS-REFACTOR.md step 5); N64 never pairs (SupportsPairing is false), so
            // this reference comparison is always false in practice, same as before this move -
            // the object cast is only here to satisfy the compiler across the two unrelated types.
            var xAxis = ((object)other == (object)input && !isLeft) ? stick2[0] : stick[0];
            var yAxis = ((object)other == (object)input && !isLeft) ? stick2[1] : stick[1];


            if (xAxis < input.minX)
            {
                input.minX = xAxis;
            }

            if (xAxis > input.maxX)
            {
                input.maxX = xAxis;
            }

            if (yAxis < input.minY)
            {
                input.minY = yAxis;
            }

            if (yAxis > input.maxY)
            {
                input.maxY = yAxis;
            }

            var middleX = (input.minX + (input.maxX - input.minX)/2);
            var middleY = (input.minY + (input.maxY - input.minY)/2);
            #if DEBUG
            var desc = "";
            desc += "x: "+xAxis+"; y: "+yAxis;
            desc += "\n X: ["+input.minX+", "+input.maxX+"]; Y: ["+input.minY+", "+input.maxY+"] ";
            desc += "; middle ["+middleX+", "+middleY+"]";

            Debug.WriteLine(desc);
            #endif

            var negative_normalized = new float[] {-1, 0};
            var positive_normalized = new float[] {0, 1};

            var xRange = new float[] {-1f, 1f};
            var yRange = new float[] {-1f, 1f};

            if (input.realn64Range)
            {
                xRange = new float[] {-0.79f, 0.79f};
                yRange = new float[] {-0.79f, 0.79f};
            }


            if (xAxis < (middleX - middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, input.minX, (middleX - middleX), xRange[0], 0f);
            }

            if (xAxis > (middleX+middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, (middleX+middleX), input.maxX, 0f, xRange[1]);
            }

            if (yAxis < (middleY-middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, input.minY, (middleY-middleY), yRange[0], 0f);
            }

            if (yAxis > (middleY+middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, (middleY+middleY), input.maxY, 0f, yRange[1]);
            }


            return stick_correction;
        }
    }
}
