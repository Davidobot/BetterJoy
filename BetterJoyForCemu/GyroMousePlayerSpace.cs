using System;
using System.Numerics;

namespace BetterJoyForCemu {
    // Gravity tracking and world-space gyro mapping adapted from Julian "Jibb" Smart's
    // MIT-licensed GamepadMotionHelpers reference implementation:
    // https://github.com/JibbSmart/GamepadMotionHelpers
    //
    // The important separation is that accelerometer fusion estimates which way is down, while
    // only calibrated gyro angular velocity creates pointer motion. Gravity defines which gyro
    // rotations are horizontal and vertical in the player's frame; it is never added to cursor
    // velocity. An imperfect gravity estimate can therefore change the mapping, but cannot
    // manufacture mouse movement while the gyro is stationary.
    internal sealed class GyroMousePlayerSpace {
        private const float DegreesToRadians = (float)(Math.PI / 180.0);
        // GamepadMotionHelpers gravity-correction defaults (version 10).
        private const float ShakinessMinThreshold = 0.01f;
        private const float ShakinessMaxThreshold = 0.4f;
        private const float StillCorrectionSpeed = 1.0f;
        private const float ShakyCorrectionSpeed = 0.1f;
        private const float GyroCorrectionFactor = 0.1f;
        private const float GyroCorrectionMinThreshold = 0.05f;
        private const float GyroCorrectionMaxThreshold = 0.25f;
        private const float MinimumCorrectionSpeed = 0.01f;
        private const float ShortSteadinessHalfTime = 0.25f;
        // GamepadMotionHelpers' default guard around the world-space pitch singularity. When the
        // controller's local pitch axis is nearly vertical, projecting it onto the horizontal
        // plane becomes ill-conditioned, so its contribution is faded instead of amplified.
        private const float WorldPitchSideReductionThreshold = 0.125f;
        // Accelerometer direction is a useful gravity reference at rest, but sustained rotation
        // around an off-centre IMU produces centripetal acceleration proportional to rotation
        // speed squared, regardless of which axis is spinning. That makes the apparent tilt point
        // the same way for both signs of that rotation, matching the observed one-way crawl on
        // the OTHER output axis during sustained yaw.
        // Reduce accelerometer influence whenever motion is both sustained-speed and strongly
        // dominated by a single axis, whichever axis that is. Keep a non-zero correction floor:
        // fully removing the gravity anchor lets small gyro/reference-axis disagreement turn into
        // a large periodic oscillation over repeated rotations.
        private const float TrustReductionStartRate = 2.0f; // degrees/sec
        private const float TrustReductionFullRate = 10.0f; // degrees/sec
        private const float DominanceStart = 0.80f;
        private const float DominanceFull = 0.95f;
        private const float MinimumGravityTrust = 0.25f;
        private const float TrustReductionHalfTime = 0.05f;
        private const float TrustRecoveryHalfTime = 0.35f;
        // Per-axis dominance is computed from the SAME gravityDirection estimate the trust
        // reduction exists to protect - during a fast, transient flick (confirmed on DualSense: a
        // roll spiking to 170+deg/s and back within ~250ms) the true rotation axis is itself noisy
        // sample to sample before gravity has had a chance to track it, so dominance can bounce
        // between axes (or land on the wrong one) throughout the whole event and never cleanly
        // cross DominanceStart for long enough to fully engage. This second, dominance-independent
        // term is a safety net keyed on total angular speed alone regardless of axis: any
        // sufficiently fast rotation is centripetal-artifact-prone whether or not per-axis
        // classification currently agrees on which axis it's happening around. Thresholds sit well
        // above normal gyro-mouse aiming speeds specifically to avoid damping legitimate fast
        // diagonal flicks, which this cannot distinguish from a fast roll by design.
        private const float OverallSpeedTrustReductionStart = 60.0f; // degrees/sec
        private const float OverallSpeedTrustReductionFull = 160.0f; // degrees/sec
        // Yaw's speed/dominance thresholds above were tuned for exactly one trigger (2027652,
        // Joy-Con, yaw only). Reusing those same low thresholds for pitch and roll turns this into
        // an OR of three independent triggers instead of one - confirmed on real DualSense
        // hardware: median gravity trust across a normal, non-aggressive handling capture was
        // 0.469 (a quarter of all samples at the floor), because ordinary in-hand movement crosses
        // 2deg/s and 80% single-axis purity on SOME axis often enough that at least one of the
        // three almost always fires. Pitch and roll need meaningfully stricter bars than yaw's
        // proven single-trigger tuning - genuinely fast AND genuinely pure rotation, not the
        // everyday threshold yaw alone could safely use.
        private const float ExtendedTrustReductionStartRate = 15.0f; // degrees/sec
        private const float ExtendedTrustReductionFullRate = 60.0f; // degrees/sec
        private const float ExtendedDominanceStart = 0.90f;
        private const float ExtendedDominanceFull = 0.98f;
        // The narrow purity gate keeps ordinary diagonals and corner-to-corner gestures out of
        // the cross-axis learners; the cap is a safety bound, not a controller-specific value.
        // Yaw->pitch is an even-order leak and uses |yaw|. Real DualSense table captures show
        // pitch->yaw is instead a signed projection and therefore uses pitch without rectifying it.
        private const float EvenLeakPurityFull = 0.08f;
        private const float EvenLeakPurityZero = 0.20f;
        private const float MaximumEvenLeakRatio = 0.15f;
        private const float EvenLeakAttackHalfTime = 0.04f;
        private const float EvenLeakReleaseHalfTime = 0.50f;

        private Vector3 gravity;
        private Vector3 smoothedAccel;
        private float shakiness;
        private bool gravityInitialized;
        private float gravityCorrectionTrust = 1.0f;
        private float yawDominance;
        private float pitchDominance;
        private float rollDominance;
        private float gravityErrorDegrees;
        private float evenYawLeakRatio;
        private float evenYawLeakCorrection;
        private float signedPitchLeakRatio;
        private float signedPitchLeakCorrection;

        // Gates every generalization beyond 2027652's original yaw-dominant-only formula: pitch
        // dominance and roll dominance both folded into gravity-trust reduction, and pitch->yaw
        // leak correction. Off by default so every existing caller - specifically Joy-Con's
        // gyroMousePlayerSpace/gyroStickPlayerSpace instances, already tuned and trusted via
        // 2027652 - gets bit-identical behavior to before any of this existed. Confirmed needed on
        // DualSense hardware via two separate real tests (pure pitch drifted left/right; rolling
        // the wrist produced a corkscrew cursor path - both are the same centripetal-acceleration
        // mechanism 2027652 already fixed for yaw, just on axes that formula never covered). The
        // user explicitly has no such problem on Joy-Con doing the same motions, so this stays
        // opt-in rather than assumed to generalize - set true only from DualSenseController.
        public bool EnableExtendedAxisCorrection;

        public Vector3 Gravity => gravity;
        public float GravityCorrectionTrust => gravityCorrectionTrust;
        public float YawDominance => yawDominance;
        public float PitchDominance => pitchDominance;
        public float RollDominance => rollDominance;
        public float GravityErrorDegrees => gravityErrorDegrees;
        public float EvenYawLeakRatio => evenYawLeakRatio;
        public float EvenYawLeakCorrection => evenYawLeakCorrection;
        public float SignedPitchLeakRatio => signedPitchLeakRatio;
        public float SignedPitchLeakCorrection => signedPitchLeakCorrection;

        public void Reset() {
            gravity = Vector3.Zero;
            smoothedAccel = Vector3.Zero;
            shakiness = 0.0f;
            gravityInitialized = false;
            gravityCorrectionTrust = 1.0f;
            yawDominance = 0.0f;
            pitchDominance = 0.0f;
            rollDominance = 0.0f;
            gravityErrorDegrees = 0.0f;
            evenYawLeakRatio = 0.0f;
            evenYawLeakCorrection = 0.0f;
            signedPitchLeakRatio = 0.0f;
            signedPitchLeakCorrection = 0.0f;
        }

        public void Update(Vector3 gyroDegPerSec, Vector3 accel, float deltaTime) {
            float accelMagnitude = accel.Length();
            Vector3 rotationRadians = gyroDegPerSec * DegreesToRadians;
            float angularSpeed = rotationRadians.Length();

            // BetterJoy has already transformed and calibrated this sample into the controller's
            // active coordinate basis. Seed gravity from it immediately instead of spending the
            // reference implementation's first second easing a zero vector toward "down". This
            // removes the slow correction users could feel after attach or a Joy-Con layout
            // change; the guarded fusion below still handles subsequent accumulated error.
            if (!gravityInitialized && accelMagnitude > 0.0f) {
                gravity = -accel / accelMagnitude;
                smoothedAccel = accel;
                shakiness = 0.0f;
                gravityInitialized = true;
                gravityCorrectionTrust = 1.0f;
                yawDominance = 0.0f;
                pitchDominance = 0.0f;
                rollDominance = 0.0f;
                gravityErrorDegrees = 0.0f;
                evenYawLeakCorrection = 0.0f;
                signedPitchLeakCorrection = 0.0f;
                return;
            }

            // Gravity is world-fixed, so in controller-local coordinates it rotates opposite the
            // controller. Doing this from gyro preserves immediate response while accelerometer
            // correction below only reins in accumulated tilt error.
            gravity = RotateByInverseLocalMotion(gravity, rotationRadians, deltaTime);

            if (accelMagnitude <= 0.0f)
                return;

            smoothedAccel = RotateByInverseLocalMotion(smoothedAccel, rotationRadians, deltaTime);
            float smoothFactor = (float)Math.Pow(2.0, -deltaTime / ShortSteadinessHalfTime);
            shakiness *= smoothFactor;
            shakiness = Math.Max(shakiness, (accel - smoothedAccel).Length());
            smoothedAccel = Vector3.Lerp(accel, smoothedAccel, smoothFactor);

            // BetterJoy's accelerometer reports apparent gravity upward at rest, so the physical
            // down/gravity vector is its negation. Its calibrated nominal magnitude is 1 g.
            Vector3 targetGravity = -Vector3.Normalize(accel);
            Vector3 gravityError = targetGravity - gravity;
            float errorLength = gravityError.Length();
            Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
                ? Vector3.Normalize(gravity)
                : targetGravity;
            float angularSpeedDegrees = gyroDegPerSec.Length();
            float yawSpeed = Math.Abs(Vector3.Dot(gravityDirection, gyroDegPerSec));
            yawDominance = angularSpeedDegrees > 1e-5f
                ? Clamp01(yawSpeed / angularSpeedDegrees)
                : 0.0f;

            Vector3 worldPitchAxisForDominance = ComputeWorldPitchAxis(gravityDirection,
                                                                        out float pitchSideReduction);
            float pitchSpeed = worldPitchAxisForDominance.LengthSquared() > 0.0f
                ? Math.Abs(Vector3.Dot(worldPitchAxisForDominance, gyroDegPerSec)) *
                  pitchSideReduction
                : 0.0f;
            pitchDominance = angularSpeedDegrees > 1e-5f
                ? Clamp01(pitchSpeed / angularSpeedDegrees)
                : 0.0f;

            // The third axis completing the yaw/pitch/roll triad - rotation around the
            // controller's own pointing axis, which by construction is orthogonal to both
            // gravityDirection (yaw's axis) and worldPitchAxisForDominance (pitch's axis). Pure
            // roll drives neither yawRate nor pitchRate, so it was invisible to both dominance
            // checks above - but it produces the exact same centripetal accelerometer corruption
            // during a fast wrist roll, just with nowhere for the existing formula to detect it.
            // Left uncorrected, that corrupted accelerometer reading gets full trust throughout
            // the roll and drags the tracked gravity vector away from truth, which then reads out
            // as a corkscrew/spiral cursor path once the (temporarily wrong) gravity reference
            // feeds back into yawRate/pitchRate.
            Vector3 rollAxis = Vector3.Cross(gravityDirection, worldPitchAxisForDominance);
            float rollSpeed = rollAxis.LengthSquared() > 0.0f
                ? Math.Abs(Vector3.Dot(Vector3.Normalize(rollAxis), gyroDegPerSec))
                : 0.0f;
            rollDominance = angularSpeedDegrees > 1e-5f
                ? Clamp01(rollSpeed / angularSpeedDegrees)
                : 0.0f;

            // Both factors use magnitudes deliberately. A centripetal/rectification error is
            // even in the dominant axis's direction, so turning/tilting either way along it must
            // produce identical trust. Whichever axis - yaw or pitch - is actually dominant right
            // now drives the reduction; a controller resting flat while being pitched dominates on
            // pitch, one held on its side while being yawed dominates on yaw, and either can
            // produce the same kind of gravity-reference error.
            float yawSpeedConfidence = SmoothStep(Clamp01(
                (yawSpeed - TrustReductionStartRate) /
                (TrustReductionFullRate - TrustReductionStartRate)));
            float yawDominanceConfidence = SmoothStep(Clamp01(
                (yawDominance - DominanceStart) /
                (DominanceFull - DominanceStart)));
            float pitchSpeedConfidence = SmoothStep(Clamp01(
                (pitchSpeed - ExtendedTrustReductionStartRate) /
                (ExtendedTrustReductionFullRate - ExtendedTrustReductionStartRate)));
            float pitchDominanceConfidence = SmoothStep(Clamp01(
                (pitchDominance - ExtendedDominanceStart) /
                (ExtendedDominanceFull - ExtendedDominanceStart)));
            float rollSpeedConfidence = SmoothStep(Clamp01(
                (rollSpeed - ExtendedTrustReductionStartRate) /
                (ExtendedTrustReductionFullRate - ExtendedTrustReductionStartRate)));
            float rollDominanceConfidence = SmoothStep(Clamp01(
                (rollDominance - ExtendedDominanceStart) /
                (ExtendedDominanceFull - ExtendedDominanceStart)));
            // yawSpeedConfidence/yawDominanceConfidence alone reproduces the pre-existing,
            // already-trusted 2027652 formula exactly - EnableExtendedAxisCorrection off (the
            // default) folds in nothing extra, so callers that never opt in see the identical
            // trust value they always did.
            float dominantAxisTrustReduction = yawSpeedConfidence * yawDominanceConfidence;
            if (EnableExtendedAxisCorrection) {
                dominantAxisTrustReduction = Math.Max(dominantAxisTrustReduction,
                    pitchSpeedConfidence * pitchDominanceConfidence);
                dominantAxisTrustReduction = Math.Max(dominantAxisTrustReduction,
                    rollSpeedConfidence * rollDominanceConfidence);
                // Dominance-independent safety net - see OverallSpeedTrustReductionStart/Full's
                // field comment. No dominance factor: engages purely on total speed.
                float overallSpeedConfidence = SmoothStep(Clamp01(
                    (angularSpeedDegrees - OverallSpeedTrustReductionStart) /
                    (OverallSpeedTrustReductionFull - OverallSpeedTrustReductionStart)));
                dominantAxisTrustReduction = Math.Max(dominantAxisTrustReduction,
                    overallSpeedConfidence);
            }
            float targetTrust = 1.0f - dominantAxisTrustReduction * (1.0f - MinimumGravityTrust);
            float trustHalfTime = targetTrust < gravityCorrectionTrust
                ? TrustReductionHalfTime
                : TrustRecoveryHalfTime;
            float trustBlend = 1.0f - (float)Math.Pow(2.0, -deltaTime / trustHalfTime);
            gravityCorrectionTrust += (targetTrust - gravityCorrectionTrust) * trustBlend;

            float gravityDot = Clamp(Vector3.Dot(gravityDirection, targetGravity), -1.0f, 1.0f);
            gravityErrorDegrees = (float)(Math.Acos(gravityDot) / DegreesToRadians);
            if (errorLength <= 0.0f)
                return;

            float correctionSpeed = StillCorrectionSpeed +
                (ShakyCorrectionSpeed - StillCorrectionSpeed) *
                Clamp01((shakiness - ShakinessMinThreshold) /
                        (ShakinessMaxThreshold - ShakinessMinThreshold));

            float gyroCorrectionLimit = Math.Max(angularSpeed * GyroCorrectionFactor,
                                                  MinimumCorrectionSpeed);
            if (correctionSpeed > gyroCorrectionLimit) {
                float closeEnoughFactor = Clamp01((errorLength - GyroCorrectionMinThreshold) /
                                                  (GyroCorrectionMaxThreshold -
                                                   GyroCorrectionMinThreshold));
                correctionSpeed = gyroCorrectionLimit +
                    (correctionSpeed - gyroCorrectionLimit) * closeEnoughFactor;
            }

            correctionSpeed *= gravityCorrectionTrust;
            if (correctionSpeed <= 0.0f)
                return;

            Vector3 correction = gravityError / errorLength * correctionSpeed * deltaTime;
            gravity = correction.LengthSquared() < gravityError.LengthSquared()
                ? gravity + correction
                : targetGravity;
        }

        public void Map(Vector3 gyroDegPerSec, float deltaTime, out float yawRate,
                        out float pitchRate, out float rollRadians) {
            // Normalize here as JoyShockMapper does. Fusion deliberately lets the gravity vector
            // converge smoothly, so its length is not guaranteed to remain exactly one; using
            // the unnormalized vector would make cursor gain vary during that convergence.
            Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
                ? Vector3.Normalize(gravity)
                : new Vector3(0.0f, -1.0f, 0.0f);

            // Match GamepadMotionHelpers::CalculateWorldSpaceGyro. Horizontal motion is rotation
            // around gravity. Vertical motion uses the controller's LOCAL pitch axis (+X)
            // projected onto the plane perpendicular to gravity. This projection is important:
            // choosing an arbitrary horizontal axis such as Z x gravity follows wrist roll past
            // the side-on pose and can redirect compound roll/yaw motion into one-way mouse Y.
            yawRate = Vector3.Dot(gravityDirection, gyroDegPerSec);

            Vector3 worldPitchAxis = ComputeWorldPitchAxis(gravityDirection, out float sideReduction);
            pitchRate = worldPitchAxis.LengthSquared() > 0.0f
                ? sideReduction * Vector3.Dot(worldPitchAxis, gyroDegPerSec)
                // Local pitch is exactly parallel to gravity, so world-relative pitch is
                // undefined. The side reduction above smoothly approaches this zero.
                : 0.0f;

            float originalYawRate = yawRate;
            float originalPitchRate = pitchRate;
            ApplyEvenYawLeakCorrection(ref pitchRate, originalYawRate, deltaTime);
            // Opt-in - see EnableExtendedAxisCorrection's field comment. Skipped entirely (not just
            // zeroed) when off, so yawRate is left exactly as Map() originally computed it, same
            // as every caller got before this correction existed.
            if (EnableExtendedAxisCorrection)
                ApplySignedPitchLeakCorrection(ref yawRate, originalPitchRate, deltaTime);

            // Diagnostic only: zero while the canonical controller frame is flat.
            rollRadians = (float)Math.Atan2(gravityDirection.X, -gravityDirection.Y);
        }

        // Returns the controller-local rotation axis that should drive screen vertical movement.
        // The legacy GamepadMotionHelpers projection is retained for existing callers. Its fixed
        // local +X source fades to zero when +X aligns with gravity, however, so at a 90-degree
        // wrist roll it loses one pointer axis instead of exchanging local pitch and yaw. The
        // DualSense opt-in path derives screen pitch from forward x gravity: flat it is local +X;
        // as the wrist rolls it rotates continuously into +/-Y; only pointing the controller's
        // forward axis vertically is genuinely singular. Update() and Map() share this function
        // so dominance classification and output always use the same basis.
        private Vector3 ComputeWorldPitchAxis(Vector3 gravityDirection,
                                               out float sideReduction) {
            if (EnableExtendedAxisCorrection) {
                Vector3 dynamicAxis = Vector3.Cross(new Vector3(0.0f, 0.0f, 1.0f),
                                                    gravityDirection);
                float length = dynamicAxis.Length();
                if (length <= 0.0f) {
                    sideReduction = 0.0f;
                    return Vector3.Zero;
                }

                dynamicAxis /= length;
                sideReduction = Clamp01(length / WorldPitchSideReductionThreshold);
                return dynamicAxis;
            }

            float gravityAlongLocalPitch = gravityDirection.X;
            Vector3 axis = new Vector3(1.0f, 0.0f, 0.0f) - gravityDirection * gravityAlongLocalPitch;
            float lengthSquared = axis.LengthSquared();
            if (lengthSquared <= 0.0f) {
                sideReduction = 0.0f;
                return Vector3.Zero;
            }

            axis /= (float)Math.Sqrt(lengthSquared);
            float flatness = Math.Abs(gravityDirection.Y);
            float upness = Math.Abs(gravityDirection.Z);
            sideReduction = Clamp01(
                (Math.Max(flatness, upness) - WorldPitchSideReductionThreshold) /
                WorldPitchSideReductionThreshold);
            return axis;
        }

        private void ApplyEvenYawLeakCorrection(ref float pitchRate, float yawRate,
                                                float deltaTime) {
            float absoluteYaw = Math.Abs(yawRate);
            float absolutePitch = Math.Abs(pitchRate);
            float yawSpeedConfidence = SmoothStep(Clamp01(
                (absoluteYaw - TrustReductionStartRate) /
                (TrustReductionFullRate - TrustReductionStartRate)));
            float yawDominanceConfidence = SmoothStep(Clamp01(
                (yawDominance - DominanceStart) /
                (DominanceFull - DominanceStart)));
            float pitchToYaw = absoluteYaw > 1e-5f
                ? absolutePitch / absoluteYaw
                : float.MaxValue;
            float purityConfidence = 1.0f - SmoothStep(Clamp01(
                (pitchToYaw - EvenLeakPurityFull) /
                (EvenLeakPurityZero - EvenLeakPurityFull)));
            float learningConfidence = yawSpeedConfidence * yawDominanceConfidence *
                                       purityConfidence;

            if (learningConfidence > 0.0f && absoluteYaw > 1e-5f) {
                // |yaw| is deliberate: a real even-order leak retains its pitch sign when the
                // user reverses yaw. A signed divisor was the reason the first adaptive attempt
                // learned mutually incompatible coefficients for left and right turns.
                float observedRatio = Clamp(pitchRate / absoluteYaw,
                                            -MaximumEvenLeakRatio,
                                            MaximumEvenLeakRatio);
                float attackBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenLeakAttackHalfTime);
                evenYawLeakRatio += (observedRatio - evenYawLeakRatio) *
                                    attackBlend * learningConfidence;
            } else {
                float releaseBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenLeakReleaseHalfTime);
                evenYawLeakRatio += (0.0f - evenYawLeakRatio) * releaseBlend;
            }

            evenYawLeakCorrection = evenYawLeakRatio * absoluteYaw * learningConfidence;
            pitchRate -= evenYawLeakCorrection;
        }

        // DualSense table captures show a small signed projection from pitch into yaw: reversing
        // pitch also reverses the unwanted yaw. Learn yaw/pitch so both directions reinforce the
        // same coefficient. Using |pitch| here made the learner chase opposite coefficients after
        // every reversal, leaving a visible horizontal transient in otherwise vertical motion.
        private void ApplySignedPitchLeakCorrection(ref float yawRate, float pitchRate,
                                                    float deltaTime) {
            float absolutePitch = Math.Abs(pitchRate);
            float absoluteYaw = Math.Abs(yawRate);
            float pitchSpeedConfidence = SmoothStep(Clamp01(
                (absolutePitch - TrustReductionStartRate) /
                (TrustReductionFullRate - TrustReductionStartRate)));
            float pitchDominanceConfidence = SmoothStep(Clamp01(
                (pitchDominance - DominanceStart) /
                (DominanceFull - DominanceStart)));
            float yawToPitch = absolutePitch > 1e-5f
                ? absoluteYaw / absolutePitch
                : float.MaxValue;
            float purityConfidence = 1.0f - SmoothStep(Clamp01(
                (yawToPitch - EvenLeakPurityFull) /
                (EvenLeakPurityZero - EvenLeakPurityFull)));
            float learningConfidence = pitchSpeedConfidence * pitchDominanceConfidence *
                                       purityConfidence;

            if (learningConfidence > 0.0f && absolutePitch > 1e-5f) {
                float observedRatio = Clamp(yawRate / pitchRate,
                                            -MaximumEvenLeakRatio,
                                            MaximumEvenLeakRatio);
                float attackBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenLeakAttackHalfTime);
                signedPitchLeakRatio += (observedRatio - signedPitchLeakRatio) *
                                        attackBlend * learningConfidence;
            } else {
                float releaseBlend = 1.0f -
                    (float)Math.Pow(2.0, -deltaTime / EvenLeakReleaseHalfTime);
                signedPitchLeakRatio += (0.0f - signedPitchLeakRatio) * releaseBlend;
            }

            signedPitchLeakCorrection = signedPitchLeakRatio * pitchRate * learningConfidence;
            yawRate -= signedPitchLeakCorrection;
        }

        private static Vector3 RotateByInverseLocalMotion(Vector3 value,
                                                           Vector3 rotationRadiansPerSecond,
                                                           float deltaTime) {
            float angularSpeed = rotationRadiansPerSecond.Length();
            if (angularSpeed <= 1e-8f || value.LengthSquared() <= 0.0f)
                return value;

            Vector3 axis = rotationRadiansPerSecond / angularSpeed;
            float angle = angularSpeed * deltaTime;
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return value * cos - Vector3.Cross(axis, value) * sin +
                   axis * Vector3.Dot(axis, value) * (1.0f - cos);
        }

        private static float Clamp01(float value) {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }

        private static float Clamp(float value, float minimum, float maximum) {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static float SmoothStep(float value) {
            return value * value * (3.0f - 2.0f * value);
        }
    }
}
