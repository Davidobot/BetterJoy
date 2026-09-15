using System;

namespace BetterJoyForCemu {
    // Integrates the complete controller orientation from body-space gyro rates, then exposes
    // incremental yaw/pitch in the orientation frame that was current at Reset().  Unlike adding
    // local Y/Z rates independently, these deltas form one coherent 3D rotation: if the controller
    // returns to its starting orientation, their accumulated yaw/pitch return to the start too
    // (apart from real sensor/integration error).  No accelerometer data is used here, so linear
    // hand acceleration cannot tilt the mapping during circles or figure-eights.
    internal sealed class GyroMouseOrientation {
        private const double DegreesToRadians = Math.PI / 180.0;

        // Unit quaternion in w,x,y,z order.  It represents controller orientation relative to the
        // pose at the most recent Reset().
        private double qw = 1.0;
        private double qx;
        private double qy;
        private double qz;

        private double previousYaw;
        private double previousPitch;

        public void Reset() {
            qw = 1.0;
            qx = qy = qz = 0.0;
            previousYaw = previousPitch = 0.0;
        }

        public void MapSample(float gyroXDegPerSec, float gyroYDegPerSec, float gyroZDegPerSec,
                              float samplePeriodSeconds, out float deltaYawRadians,
                              out float deltaPitchRadians, out float rollRadians) {
            double wx = gyroXDegPerSec * DegreesToRadians;
            double wy = gyroYDegPerSec * DegreesToRadians;
            double wz = gyroZDegPerSec * DegreesToRadians;
            double angularSpeed = Math.Sqrt(wx * wx + wy * wy + wz * wz);

            // Apply the exact quaternion exponential for a constant-rate 5ms sample instead of
            // a first-order qDot Euler step. This remains accurate near the controller's maximum
            // angular rate and makes a sample followed by its exact inverse an exact rotation
            // inverse apart from floating-point roundoff.
            if (angularSpeed > 1e-12) {
                double halfAngle = 0.5 * angularSpeed * samplePeriodSeconds;
                double vectorScale = Math.Sin(halfAngle) / angularSpeed;
                double deltaW = Math.Cos(halfAngle);
                double deltaX = wx * vectorScale;
                double deltaY = wy * vectorScale;
                double deltaZ = wz * vectorScale;

                double oldW = qw;
                double oldX = qx;
                double oldY = qy;
                double oldZ = qz;

                // Body-space sensor rates post-multiply the current body-to-reference pose.
                qw = oldW * deltaW - oldX * deltaX - oldY * deltaY - oldZ * deltaZ;
                qx = oldW * deltaX + oldX * deltaW + oldY * deltaZ - oldZ * deltaY;
                qy = oldW * deltaY - oldX * deltaZ + oldY * deltaW + oldZ * deltaX;
                qz = oldW * deltaZ + oldX * deltaY - oldY * deltaX + oldZ * deltaW;
            }

            double normSquared = qw * qw + qx * qx + qy * qy + qz * qz;
            if (normSquared < 1e-12 || double.IsNaN(normSquared) || double.IsInfinity(normSquared)) {
                Reset();
                deltaYawRadians = deltaPitchRadians = rollRadians = 0.0f;
                return;
            }

            double inverseNorm = 1.0 / Math.Sqrt(normSquared);
            qw *= inverseNorm;
            qx *= inverseNorm;
            qy *= inverseNorm;
            qz *= inverseNorm;

            // ZYX yaw/pitch/roll decomposition, matching MadgwickAHRS.GetEulerAngles().  At the
            // reset pose, a small +Z gyro sample is +yaw and a small +Y sample is +pitch, so this
            // preserves BetterJoy's established cursor directions and sensitivity units.
            double sinPitch = 2.0 * (qw * qy - qz * qx);
            sinPitch = Math.Max(-1.0, Math.Min(1.0, sinPitch));
            double pitch = Math.Asin(sinPitch);
            double yaw = Math.Atan2(2.0 * (qw * qz + qx * qy),
                                    1.0 - 2.0 * (qy * qy + qz * qz));
            double roll = Math.Atan2(2.0 * (qw * qx + qy * qz),
                                     1.0 - 2.0 * (qx * qx + qy * qy));

            deltaYawRadians = (float)WrapAngle(yaw - previousYaw);
            deltaPitchRadians = (float)(pitch - previousPitch);
            rollRadians = (float)roll;

            previousYaw = yaw;
            previousPitch = pitch;
        }

        private static double WrapAngle(double angle) {
            while (angle > Math.PI)
                angle -= 2.0 * Math.PI;
            while (angle < -Math.PI)
                angle += 2.0 * Math.PI;
            return angle;
        }
    }
}
