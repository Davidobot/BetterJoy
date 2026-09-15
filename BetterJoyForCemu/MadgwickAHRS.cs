using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// source: https://github.com/xioTechnologies/Open-Source-AHRS-With-x-IMU/blob/master/x-IMU%20IMU%20and%20AHRS%20Algorithms/x-IMU%20IMU%20and%20AHRS%20Algorithms/AHRS/MadgwickAHRS.cs

namespace BetterJoyForCemu {
    /// <summary>
    /// MadgwickAHRS class. Implementation of Madgwick's IMU and AHRS algorithms.
    /// </summary>
    /// <remarks>
    /// See: http://www.x-io.co.uk/node/8#open_source_ahrs_and_imu_algorithms
    /// </remarks>
    public class MadgwickAHRS {
        /// <summary>
        /// Gets or sets the sample period.
        /// </summary>
        public float SamplePeriod { get; set; }

        /// <summary>
        /// Gets or sets the algorithm gain beta.
        /// </summary>
        public float Beta { get; set; }

        /// <summary>
        /// Gets or sets the Quaternion output.
        /// </summary>
        public float[] Quaternion { get; set; }

        public float[] old_pitchYawRoll { get; set; }

        // Quaternion pose captured by Recenter(). GetEulerAngles reports orientation relative to
        // this pose, so "Re-Centre Gyro" can declare the way the controller is being held right
        // now to be neutral without changing any sensor calibration/bias values.
        private float[] referenceQuaternion;

        /// <summary>
        /// Initializes a new instance of the <see cref="MadgwickAHRS"/> class.
        /// </summary>
        /// <param name="samplePeriod">
        /// Sample period.
        /// </param>
        public MadgwickAHRS(float samplePeriod)
            : this(samplePeriod, 1f) {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MadgwickAHRS"/> class.
        /// </summary>
        /// <param name="samplePeriod">
        /// Sample period.
        /// </param>
        /// <param name="beta">
        /// Algorithm gain beta.
        /// </param>
        public MadgwickAHRS(float samplePeriod, float beta) {
            SamplePeriod = samplePeriod;
            Beta = beta;
            Reset();
        }

        // Throws away an orientation whose sensor coordinate basis is no longer valid (for
        // example, when a Joycon changes between solo/sideways and joined/vertical layout).
        public void Reset() {
            Quaternion = new float[] { 1f, 0f, 0f, 0f };
            referenceQuaternion = new float[] { 1f, 0f, 0f, 0f };
            old_pitchYawRoll = new float[] { 0f, 0f, 0f };
        }

        // Keeps the live fused orientation, but makes its current pose the new output origin.
        // This is an orientation operation, not calibration: gyro/accelerometer offsets and
        // sensitivities are deliberately untouched.
        public void Recenter() {
            float q0 = Quaternion[0], q1 = Quaternion[1], q2 = Quaternion[2], q3 = Quaternion[3];
            float norm = (float)Math.Sqrt(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3);
            if (norm <= 0f || float.IsNaN(norm) || float.IsInfinity(norm)) {
                Reset();
                return;
            }

            float inverseNorm = 1.0f / norm;
            referenceQuaternion[0] = q0 * inverseNorm;
            referenceQuaternion[1] = q1 * inverseNorm;
            referenceQuaternion[2] = q2 * inverseNorm;
            referenceQuaternion[3] = q3 * inverseNorm;
            old_pitchYawRoll = new float[] { 0f, 0f, 0f };
        }

        /// <summary>
        /// Algorithm IMU update method. Requires only gyroscope and accelerometer data.
        /// </summary>
        /// <param name="gx">
        /// Gyroscope x axis measurement in radians/s.
        /// </param>
        /// <param name="gy">
        /// Gyroscope y axis measurement in radians/s.
        /// </param>
        /// <param name="gz">
        /// Gyroscope z axis measurement in radians/s.
        /// </param>
        /// <param name="ax">
        /// Accelerometer x axis measurement in any calibrated units.
        /// </param>
        /// <param name="ay">
        /// Accelerometer y axis measurement in any calibrated units.
        /// </param>
        /// <param name="az">
        /// Accelerometer z axis measurement in any calibrated units.
        /// </param>
        /// <remarks>
        /// Optimised for minimal arithmetic.
        /// Total ±: 45
        /// Total *: 85
        /// Total /: 3
        /// Total sqrt: 3
        /// </remarks>
        public void Update(float gx, float gy, float gz, float ax, float ay, float az) {
            float q1 = Quaternion[0], q2 = Quaternion[1], q3 = Quaternion[2], q4 = Quaternion[3];   // short name local variable for readability
            float norm;
            float s1, s2, s3, s4;
            float qDot1, qDot2, qDot3, qDot4;

            // Auxiliary variables to avoid repeated arithmetic
            float _2q1 = 2f * q1;
            float _2q2 = 2f * q2;
            float _2q3 = 2f * q3;
            float _2q4 = 2f * q4;
            float _4q1 = 4f * q1;
            float _4q2 = 4f * q2;
            float _4q3 = 4f * q3;
            float _8q2 = 8f * q2;
            float _8q3 = 8f * q3;
            float q1q1 = q1 * q1;
            float q2q2 = q2 * q2;
            float q3q3 = q3 * q3;
            float q4q4 = q4 * q4;

            // Normalise accelerometer measurement
            norm = (float)Math.Sqrt(ax * ax + ay * ay + az * az);
            if (norm == 0f) return; // handle NaN
            norm = 1 / norm;        // use reciprocal for division
            ax *= norm;
            ay *= norm;
            az *= norm;

            // Gradient decent algorithm corrective step
            s1 = _4q1 * q3q3 + _2q3 * ax + _4q1 * q2q2 - _2q2 * ay;
            s2 = _4q2 * q4q4 - _2q4 * ax + 4f * q1q1 * q2 - _2q1 * ay - _4q2 + _8q2 * q2q2 + _8q2 * q3q3 + _4q2 * az;
            s3 = 4f * q1q1 * q3 + _2q1 * ax + _4q3 * q4q4 - _2q4 * ay - _4q3 + _8q3 * q2q2 + _8q3 * q3q3 + _4q3 * az;
            s4 = 4f * q2q2 * q4 - _2q2 * ax + 4f * q3q3 * q4 - _2q3 * ay;
            float stepNormSquared = s1 * s1 + s2 * s2 + s3 * s3 + s4 * s4;
            if (stepNormSquared > 0f) {
                norm = 1f / (float)Math.Sqrt(stepNormSquared);    // normalise step magnitude
                s1 *= norm;
                s2 *= norm;
                s3 *= norm;
                s4 *= norm;
            } else {
                // A perfectly aligned stationary sample has no corrective gradient. Treat that
                // as zero correction; 0 * (1 / sqrt(0)) would otherwise poison the quaternion
                // with NaNs precisely when the controller is resting flat or was just reset.
                s1 = s2 = s3 = s4 = 0f;
            }

            // Compute rate of change of quaternion
            qDot1 = 0.5f * (-q2 * gx - q3 * gy - q4 * gz) - Beta * s1;
            qDot2 = 0.5f * (q1 * gx + q3 * gz - q4 * gy) - Beta * s2;
            qDot3 = 0.5f * (q1 * gy - q2 * gz + q4 * gx) - Beta * s3;
            qDot4 = 0.5f * (q1 * gz + q2 * gy - q3 * gx) - Beta * s4;

            // Integrate to yield quaternion
            q1 += qDot1 * SamplePeriod;
            q2 += qDot2 * SamplePeriod;
            q3 += qDot3 * SamplePeriod;
            q4 += qDot4 * SamplePeriod;
            norm = 1f / (float)Math.Sqrt(q1 * q1 + q2 * q2 + q3 * q3 + q4 * q4);    // normalise quaternion
            Quaternion[0] = q1 * norm;
            Quaternion[1] = q2 * norm;
            Quaternion[2] = q3 * norm;
            Quaternion[3] = q4 * norm;
        }

        public float[] GetEulerAngles() {
            float[] pitchYawRoll = new float[3];
            float r0 = referenceQuaternion[0], r1 = referenceQuaternion[1], r2 = referenceQuaternion[2], r3 = referenceQuaternion[3];
            float c0 = Quaternion[0], c1 = Quaternion[1], c2 = Quaternion[2], c3 = Quaternion[3];

            // relative = conjugate(reference) * current. At the instant Recenter() captures the
            // reference this is identity, regardless of the controller's physical tilt.
            float q0 = r0 * c0 + r1 * c1 + r2 * c2 + r3 * c3;
            float q1 = r0 * c1 - r1 * c0 - r2 * c3 + r3 * c2;
            float q2 = r0 * c2 + r1 * c3 - r2 * c0 - r3 * c1;
            float q3 = r0 * c3 - r1 * c2 + r2 * c1 - r3 * c0;
            float sq1 = q1 * q1, sq2 = q2 * q2, sq3 = q3 * q3;          
            float sinPitch = Math.Max(-1f, Math.Min(1f, 2f * (q0 * q2 - q3 * q1)));
            pitchYawRoll[0] = (float)Math.Asin(sinPitch);                                             // Pitch
            pitchYawRoll[1] = (float)Math.Atan2(2f * (q0 * q3 + q1 * q2), 1 - 2f * (sq2 + sq3));     // Yaw
            pitchYawRoll[2] = (float)Math.Atan2(2f * (q0 * q1 + q2 * q3), 1 - 2f * (sq1 + sq2));     // Roll 

            float[] returnAngles = new float[6];
            Array.Copy(pitchYawRoll, returnAngles, 3);
            Array.Copy(old_pitchYawRoll, 0, returnAngles, 3, 3);
            old_pitchYawRoll = pitchYawRoll;

            return returnAngles;
        }
    }
}
