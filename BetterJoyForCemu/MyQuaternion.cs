using System;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using System.Numerics;

/// <summary>
/// Quaternions are used to represent rotations.
/// A custom completely managed implementation of UnityEngine.Quaternion
/// Base is decompiled UnityEngine.Quaternion
/// Doesn't implement methods marked Obsolete
/// Does implicit coversions to and from UnityEngine.Quaternion
///
/// Uses code from:
/// https://raw.githubusercontent.com/mono/opentk/master/Source/OpenTK/Math/Quaternion.cs
/// http://answers.unity3d.com/questions/467614/what-is-the-source-code-of-quaternionlookrotation.html
/// http://stackoverflow.com/questions/12088610/conversion-between-euler-quaternion-like-in-unity3d-engine
/// http://stackoverflow.com/questions/11492299/quaternion-to-euler-angles-algorithm-how-to-convert-to-y-up-and-between-ha
///
/// Version: aeroson 2017-07-11 (author yyyy-MM-dd)
/// License: ODC Public Domain Dedication & License 1.0 (PDDL-1.0) https://tldrlegal.com/license/odc-public-domain-dedication-&-license-1.0-(pddl-1.0)
/// </summary>
[Serializable]
public struct MyQuaternion : IEquatable<MyQuaternion> {
	const float radToDeg = (float)(180.0 / Math.PI);
	const float degToRad = (float)(Math.PI / 180.0);

	public const float kEpsilon = 1E-06f; // should probably be used in the 0 tests in LookRotation or Slerp

	[XmlIgnore]
	public Vector3 xyz {
		set {
			x = value.X;
			y = value.Y;
			z = value.Z;
		}
		get {
			return new Vector3(x, y, z);
		}
	}
	/// <summary>
	///   <para>X component of the Quaternion. Don't modify this directly unless you know quaternions inside out.</para>
	/// </summary>
	public float x;
	/// <summary>
	///   <para>Y component of the Quaternion. Don't modify this directly unless you know quaternions inside out.</para>
	/// </summary>
	public float y;
	/// <summary>
	///   <para>Z component of the Quaternion. Don't modify this directly unless you know quaternions inside out.</para>
	/// </summary>
	public float z;
	/// <summary>
	///   <para>W component of the Quaternion. Don't modify this directly unless you know quaternions inside out.</para>
	/// </summary>
	public float w;

	[XmlIgnore]
	public float this[int index] {
		get {
			switch (index) {
				case 0:
				return this.x;
				case 1:
				return this.y;
				case 2:
				return this.z;
				case 3:
				return this.w;
				default:
				throw new IndexOutOfRangeException("Invalid Quaternion index: " + index + ", can use only 0,1,2,3");
			}
		}
		set {
			switch (index) {
				case 0:
				this.x = value;
				break;
				case 1:
				this.y = value;
				break;
				case 2:
				this.z = value;
				break;
				case 3:
				this.w = value;
				break;
				default:
				throw new IndexOutOfRangeException("Invalid Quaternion index: " + index + ", can use only 0,1,2,3");
			}
		}
	}
	/// <summary>
	///   <para>The identity rotation (RO).</para>
	/// </summary>
	[XmlIgnore]
	public static MyQuaternion identity {
		get {
			return new MyQuaternion(0f, 0f, 0f, 1f);
		}
	}
	/// <summary>
	///   <para>Returns the euler angle representation of the rotation.</para>
	/// </summary>
	[XmlIgnore]
	public Vector3 eulerAngles {
		get {
			return MyQuaternion.ToEulerRad(this) * radToDeg;
		}
		set {
			this = MyQuaternion.FromEulerRad(value * degToRad);
		}
	}
	/// <summary>
	/// Gets the length (magnitude) of the quaternion.
	/// </summary>
	/// <seealso cref="LengthSquared"/>
	[XmlIgnore]
	public float Length {
		get {
			return (float)System.Math.Sqrt(x * x + y * y + z * z + w * w);
		}
	}

	/// <summary>
	/// Gets the square of the quaternion length (magnitude).
	/// </summary>
	[XmlIgnore]
	public float LengthSquared {
		get {
			return x * x + y * y + z * z + w * w;
		}
	}
	/// <summary>
	///   <para>Constructs new MyQuaternion with given x,y,z,w components.</para>
	/// </summary>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="z"></param>
	/// <param name="w"></param>
	public MyQuaternion(float x, float y, float z, float w) {
		this.x = x;
		this.y = y;
		this.z = z;
		this.w = w;
	}
	/// <summary>
	/// Construct a new MyQuaternion from vector and w components
	/// </summary>
	/// <param name="v">The vector part</param>
	/// <param name="w">The w part</param>
	public MyQuaternion(Vector3 v, float w) {
		this.x = v.X;
		this.y = v.Y;
		this.z = v.Z;
		this.w = w;
	}
	/// <summary>
	///   <para>Set x, y, z and w components of an existing MyQuaternion.</para>
	/// </summary>
	/// <param name="new_x"></param>
	/// <param name="new_y"></param>
	/// <param name="new_z"></param>
	/// <param name="new_w"></param>
	public void Set(float new_x, float new_y, float new_z, float new_w) {
		this.x = new_x;
		this.y = new_y;
		this.z = new_z;
		this.w = new_w;
	}
	/// <summary>
	/// Scales the MyQuaternion to unit length.
	/// </summary>
	public void Normalize() {
		float scale = 1.0f / this.Length;
		xyz *= scale;
		w *= scale;
	}
	/// <summary>
	/// Scale the given quaternion to unit length
	/// </summary>
	/// <param name="q">The quaternion to normalize</param>
	/// <returns>The normalized quaternion</returns>
	public static MyQuaternion Normalize(MyQuaternion q) {
		MyQuaternion result;
		Normalize(ref q, out result);
		return result;
	}
	/// <summary>
	/// Scale the given quaternion to unit length
	/// </summary>
	/// <param name="q">The quaternion to normalize</param>
	/// <param name="result">The normalized quaternion</param>
	public static void Normalize(ref MyQuaternion q, out MyQuaternion result) {
		float scale = 1.0f / q.Length;
		result = new MyQuaternion(q.xyz * scale, q.w * scale);
	}
	/// <summary>
	///   <para>The dot product between two rotations.</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	public static float Dot(MyQuaternion a, MyQuaternion b) {
		return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
	}
	/// <summary>
	///   <para>Creates a rotation which rotates /angle/ degrees around /axis/.</para>
	/// </summary>
	/// <param name="angle"></param>
	/// <param name="axis"></param>
	public static MyQuaternion AngleAxis(float angle, Vector3 axis) {
		return MyQuaternion.AngleAxis(angle, ref axis);
	}
	private static MyQuaternion AngleAxis(float degress, ref Vector3 axis) {
		if (axis.LengthSquared() == 0.0f)
			return identity;

		MyQuaternion result = identity;
		var radians = degress * degToRad;
		radians *= 0.5f;
		axis = Vector3.Normalize(axis);
		axis = axis * (float)System.Math.Sin(radians);
		result.x = axis.X;
		result.y = axis.Y;
		result.z = axis.Z;
		result.w = (float)System.Math.Cos(radians);

		return Normalize(result);
	}
	public void ToAngleAxis(out float angle, out Vector3 axis) {
		MyQuaternion.ToAxisAngleRad(this, out axis, out angle);
		angle *= radToDeg;
	}
	/// <summary>
	///   <para>Creates a rotation which rotates from /fromDirection/ to /toDirection/.</para>
	/// </summary>
	/// <param name="fromDirection"></param>
	/// <param name="toDirection"></param>
	public static MyQuaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection) {
		return RotateTowards(LookRotation(fromDirection), LookRotation(toDirection), float.MaxValue);
	}
	/// <summary>
	///   <para>Creates a rotation which rotates from /fromDirection/ to /toDirection/.</para>
	/// </summary>
	/// <param name="fromDirection"></param>
	/// <param name="toDirection"></param>
	public void SetFromToRotation(Vector3 fromDirection, Vector3 toDirection) {
		this = MyQuaternion.FromToRotation(fromDirection, toDirection);
	}
	/// <summary>
	///   <para>Creates a rotation with the specified /forward/ and /upwards/ directions.</para>
	/// </summary>
	/// <param name="forward">The direction to look in.</param>
	/// <param name="upwards">The vector that defines in which direction up is.</param>
	public static MyQuaternion LookRotation(Vector3 forward, Vector3 upwards) {
		return MyQuaternion.LookRotation(ref forward, ref upwards);
	}
	public static MyQuaternion LookRotation(Vector3 forward) {
		Vector3 up = new Vector3(0, 0, 1);
		return MyQuaternion.LookRotation(ref forward, ref up);
	}
	// from http://answers.unity3d.com/questions/467614/what-is-the-source-code-of-quaternionlookrotation.html
	private static MyQuaternion LookRotation(ref Vector3 forward, ref Vector3 up) {

		forward = Vector3.Normalize(forward);
		Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
		up = Vector3.Cross(forward, right);
		var m00 = right.X;
		var m01 = right.Y;
		var m02 = right.Z;
		var m10 = up.X;
		var m11 = up.Y;
		var m12 = up.Z;
		var m20 = forward.X;
		var m21 = forward.Y;
		var m22 = forward.Z;


		float num8 = (m00 + m11) + m22;
		var quaternion = new MyQuaternion();
		if (num8 > 0f) {
			var num = (float)System.Math.Sqrt(num8 + 1f);
			quaternion.w = num * 0.5f;
			num = 0.5f / num;
			quaternion.x = (m12 - m21) * num;
			quaternion.y = (m20 - m02) * num;
			quaternion.z = (m01 - m10) * num;
			return quaternion;
		}
		if ((m00 >= m11) && (m00 >= m22)) {
			var num7 = (float)System.Math.Sqrt(((1f + m00) - m11) - m22);
			var num4 = 0.5f / num7;
			quaternion.x = 0.5f * num7;
			quaternion.y = (m01 + m10) * num4;
			quaternion.z = (m02 + m20) * num4;
			quaternion.w = (m12 - m21) * num4;
			return quaternion;
		}
		if (m11 > m22) {
			var num6 = (float)System.Math.Sqrt(((1f + m11) - m00) - m22);
			var num3 = 0.5f / num6;
			quaternion.x = (m10 + m01) * num3;
			quaternion.y = 0.5f * num6;
			quaternion.z = (m21 + m12) * num3;
			quaternion.w = (m20 - m02) * num3;
			return quaternion;
		}
		var num5 = (float)System.Math.Sqrt(((1f + m22) - m00) - m11);
		var num2 = 0.5f / num5;
		quaternion.x = (m20 + m02) * num2;
		quaternion.y = (m21 + m12) * num2;
		quaternion.z = 0.5f * num5;
		quaternion.w = (m01 - m10) * num2;
		return quaternion;
	}
	public void SetLookRotation(Vector3 view) {
		Vector3 up = new Vector3(0, 0, 1);
		this.SetLookRotation(view, up);
	}
	/// <summary>
	///   <para>Creates a rotation with the specified /forward/ and /upwards/ directions.</para>
	/// </summary>
	/// <param name="view">The direction to look in.</param>
	/// <param name="up">The vector that defines in which direction up is.</param>
	public void SetLookRotation(Vector3 view, Vector3 up) {
		this = MyQuaternion.LookRotation(view, up);
	}
	/// <summary>
	///   <para>Spherically interpolates between /a/ and /b/ by t. The parameter /t/ is clamped to the range [0, 1].</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	/// <param name="t"></param>
	public static MyQuaternion Slerp(MyQuaternion a, MyQuaternion b, float t) {
		return MyQuaternion.Slerp(ref a, ref b, t);
	}
	private static MyQuaternion Slerp(ref MyQuaternion a, ref MyQuaternion b, float t) {
		if (t > 1) t = 1;
		if (t < 0) t = 0;
		return SlerpUnclamped(ref a, ref b, t);
	}
	/// <summary>
	///   <para>Spherically interpolates between /a/ and /b/ by t. The parameter /t/ is not clamped.</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	/// <param name="t"></param>
	public static MyQuaternion SlerpUnclamped(MyQuaternion a, MyQuaternion b, float t) {
		return MyQuaternion.SlerpUnclamped(ref a, ref b, t);
	}
	private static MyQuaternion SlerpUnclamped(ref MyQuaternion a, ref MyQuaternion b, float t) {
		// if either input is zero, return the other.
		if (a.LengthSquared == 0.0f) {
			if (b.LengthSquared == 0.0f) {
				return identity;
			}
			return b;
		} else if (b.LengthSquared == 0.0f) {
			return a;
		}


		float cosHalfAngle = a.w * b.w + Vector3.Dot(a.xyz, b.xyz);

		if (cosHalfAngle >= 1.0f || cosHalfAngle <= -1.0f) {
			// angle = 0.0f, so just return one input.
			return a;
		} else if (cosHalfAngle < 0.0f) {
			b.xyz = -b.xyz;
			b.w = -b.w;
			cosHalfAngle = -cosHalfAngle;
		}

		float blendA;
		float blendB;
		if (cosHalfAngle < 0.99f) {
			// do proper slerp for big angles
			float halfAngle = (float)System.Math.Acos(cosHalfAngle);
			float sinHalfAngle = (float)System.Math.Sin(halfAngle);
			float oneOverSinHalfAngle = 1.0f / sinHalfAngle;
			blendA = (float)System.Math.Sin(halfAngle * (1.0f - t)) * oneOverSinHalfAngle;
			blendB = (float)System.Math.Sin(halfAngle * t) * oneOverSinHalfAngle;
		} else {
			// do lerp if angle is really small.
			blendA = 1.0f - t;
			blendB = t;
		}

		MyQuaternion result = new MyQuaternion(blendA * a.xyz + blendB * b.xyz, blendA * a.w + blendB * b.w);
		if (result.LengthSquared > 0.0f)
			return Normalize(result);
		else
			return identity;
	}
	/// <summary>
	///   <para>Interpolates between /a/ and /b/ by /t/ and normalizes the result afterwards. The parameter /t/ is clamped to the range [0, 1].</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	/// <param name="t"></param>
	public static MyQuaternion Lerp(MyQuaternion a, MyQuaternion b, float t) {
		if (t > 1) t = 1;
		if (t < 0) t = 0;
		return Slerp(ref a, ref b, t); // TODO: use lerp not slerp, "Because quaternion works in 4D. Rotation in 4D are linear" ???
	}
	/// <summary>
	///   <para>Interpolates between /a/ and /b/ by /t/ and normalizes the result afterwards. The parameter /t/ is not clamped.</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	/// <param name="t"></param>
	public static MyQuaternion LerpUnclamped(MyQuaternion a, MyQuaternion b, float t) {
		return Slerp(ref a, ref b, t);
	}
	/// <summary>
	///   <para>Rotates a rotation /from/ towards /to/.</para>
	/// </summary>
	/// <param name="from"></param>
	/// <param name="to"></param>
	/// <param name="maxDegreesDelta"></param>
	public static MyQuaternion RotateTowards(MyQuaternion from, MyQuaternion to, float maxDegreesDelta) {
		float num = MyQuaternion.Angle(from, to);
		if (num == 0f) {
			return to;
		}
		float t = Math.Min(1f, maxDegreesDelta / num);
		return MyQuaternion.SlerpUnclamped(from, to, t);
	}
	/// <summary>
	///   <para>Returns the Inverse of /rotation/.</para>
	/// </summary>
	/// <param name="rotation"></param>
	public static MyQuaternion Inverse(MyQuaternion rotation) {
		float lengthSq = rotation.LengthSquared;
		if (lengthSq != 0.0) {
			float i = 1.0f / lengthSq;
			return new MyQuaternion(rotation.xyz * -i, rotation.w * i);
		}
		return rotation;
	}
	/// <summary>
	///   <para>Returns a nicely formatted string of the MyQuaternion.</para>
	/// </summary>
	/// <param name="format"></param>
	public override string ToString() {
		return string.Format("({0:F1}, {1:F1}, {2:F1}, {3:F1})", this.x, this.y, this.z, this.w);
	}
	/// <summary>
	///   <para>Returns a nicely formatted string of the MyQuaternion.</para>
	/// </summary>
	/// <param name="format"></param>
	public string ToString(string format) {
		return string.Format("({0}, {1}, {2}, {3})", this.x.ToString(format), this.y.ToString(format), this.z.ToString(format), this.w.ToString(format));
	}
	/// <summary>
	///   <para>Returns the angle in degrees between two rotations /a/ and /b/.</para>
	/// </summary>
	/// <param name="a"></param>
	/// <param name="b"></param>
	public static float Angle(MyQuaternion a, MyQuaternion b) {
		float f = MyQuaternion.Dot(a, b);
		return (float)Math.Acos(Math.Min(  Math.Abs( f ), 1f) ) * 2f * radToDeg;
	}
	/// <summary>
	///   <para>Returns a rotation that rotates z degrees around the z axis, x degrees around the x axis, and y degrees around the y axis (in that order).</para>
	/// </summary>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="z"></param>
	public static MyQuaternion Euler(float x, float y, float z) {
		return MyQuaternion.FromEulerRad(new Vector3((float)x, (float)y, (float)z) * degToRad);
	}
	/// <summary>
	///   <para>Returns a rotation that rotates z degrees around the z axis, x degrees around the x axis, and y degrees around the y axis (in that order).</para>
	/// </summary>
	/// <param name="euler"></param>
	public static MyQuaternion Euler(Vector3 euler) {
		return MyQuaternion.FromEulerRad(euler * degToRad);
	}
	// from http://stackoverflow.com/questions/12088610/conversion-between-euler-quaternion-like-in-unity3d-engine
	private static Vector3 ToEulerRad(MyQuaternion rotation) {
		float sqw = rotation.w * rotation.w;
		float sqx = rotation.x * rotation.x;
		float sqy = rotation.y * rotation.y;
		float sqz = rotation.z * rotation.z;
		float unit = sqx + sqy + sqz + sqw; // if normalised is one, otherwise is correction factor
		float test = rotation.x * rotation.w - rotation.y * rotation.z;
		Vector3 v;

		if (test > 0.4995f * unit) { // singularity at north pole
			v.Y = 2.0f * (float)Math.Atan2(rotation.y, rotation.x);
			v.X = (float) Math.PI / 2;
			v.Z = 0;
			return NormalizeAngles(v);
		}
		if (test < -0.4995f * unit) { // singularity at south pole
			v.Y = -2f * (float) Math.Atan2(rotation.y, rotation.x);
			v.X = -(float)Math.PI / 2;
			v.Z = 0;
			return NormalizeAngles(v);
		}
		MyQuaternion q = new MyQuaternion(rotation.w, rotation.z, rotation.x, rotation.y);
		v.Y = (float)System.Math.Atan2(2f * q.x * q.w + 2f * q.y * q.z, 1 - 2f * (q.z * q.z + q.w * q.w));     // Yaw
		v.X = (float)System.Math.Asin(2f * (q.x * q.z - q.w * q.y));                             // Pitch
		v.Z = (float)System.Math.Atan2(2f * q.x * q.y + 2f * q.z * q.w, 1 - 2f * (q.y * q.y + q.z * q.z));      // Roll
		return NormalizeAngles(v);
	}
	private static Vector3 NormalizeAngles(Vector3 angles) {
		angles.X = NormalizeAngle(angles.X);
		angles.Y = NormalizeAngle(angles.Y);
		angles.Z = NormalizeAngle(angles.Z);
		return angles;
	}
	private static float NormalizeAngle(float angle) {
		while (angle > 360)
			angle -= 360;
		while (angle < 0)
			angle += 360;
		return angle;
	}
	// from http://stackoverflow.com/questions/11492299/quaternion-to-euler-angles-algorithm-how-to-convert-to-y-up-and-between-ha
	private static MyQuaternion FromEulerRad(Vector3 euler) {
		var yaw = euler.X;
		var pitch = euler.Y;
		var roll = euler.Z;
		float rollOver2 = roll * 0.5f;
		float sinRollOver2 = (float)System.Math.Sin((float)rollOver2);
		float cosRollOver2 = (float)System.Math.Cos((float)rollOver2);
		float pitchOver2 = pitch * 0.5f;
		float sinPitchOver2 = (float)System.Math.Sin((float)pitchOver2);
		float cosPitchOver2 = (float)System.Math.Cos((float)pitchOver2);
		float yawOver2 = yaw * 0.5f;
		float sinYawOver2 = (float)System.Math.Sin((float)yawOver2);
		float cosYawOver2 = (float)System.Math.Cos((float)yawOver2);
		MyQuaternion result;
		result.x = cosYawOver2 * cosPitchOver2 * cosRollOver2 + sinYawOver2 * sinPitchOver2 * sinRollOver2;
		result.y = cosYawOver2 * cosPitchOver2 * sinRollOver2 - sinYawOver2 * sinPitchOver2 * cosRollOver2;
		result.z = cosYawOver2 * sinPitchOver2 * cosRollOver2 + sinYawOver2 * cosPitchOver2 * sinRollOver2;
		result.w = sinYawOver2 * cosPitchOver2 * cosRollOver2 - cosYawOver2 * sinPitchOver2 * sinRollOver2;
		return result;

	}
	private static void ToAxisAngleRad(MyQuaternion q, out Vector3 axis, out float angle) {
		if (System.Math.Abs(q.w) > 1.0f)
			q.Normalize();
		angle = 2.0f * (float)System.Math.Acos(q.w); // angle
		float den = (float)System.Math.Sqrt(1.0 - q.w * q.w);
		if (den > 0.0001f) {
			axis = q.xyz / den;
		} else {
			// This occurs when the angle is zero. 
			// Not a problem: just set an arbitrary normalized axis.
			axis = new Vector3(1, 0, 0);
		}
	}
	#region Obsolete methods
	/*
	[Obsolete("Use MyQuaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public static MyQuaternion EulerRotation(float x, float y, float z)
	{
		return MyQuaternion.Internal_FromEulerRad(new Vector3(x, y, z));
	}
	[Obsolete("Use MyQuaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public static MyQuaternion EulerRotation(Vector3 euler)
	{
		return MyQuaternion.Internal_FromEulerRad(euler);
	}
	[Obsolete("Use MyQuaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public void SetEulerRotation(float x, float y, float z)
	{
		this = Quaternion.Internal_FromEulerRad(new Vector3(x, y, z));
	}
	[Obsolete("Use Quaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public void SetEulerRotation(Vector3 euler)
	{
		this = Quaternion.Internal_FromEulerRad(euler);
	}
	[Obsolete("Use Quaternion.eulerAngles instead. This function was deprecated because it uses radians instead of degrees")]
	public Vector3 ToEuler()
	{
		return Quaternion.Internal_ToEulerRad(this);
	}
	[Obsolete("Use Quaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public static Quaternion EulerAngles(float x, float y, float z)
	{
		return Quaternion.Internal_FromEulerRad(new Vector3(x, y, z));
	}
	[Obsolete("Use Quaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public static Quaternion EulerAngles(Vector3 euler)
	{
		return Quaternion.Internal_FromEulerRad(euler);
	}
	[Obsolete("Use Quaternion.ToAngleAxis instead. This function was deprecated because it uses radians instead of degrees")]
	public void ToAxisAngle(out Vector3 axis, out float angle)
	{
		Quaternion.Internal_ToAxisAngleRad(this, out axis, out angle);
	}
	[Obsolete("Use Quaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public void SetEulerAngles(float x, float y, float z)
	{
		this.SetEulerRotation(new Vector3(x, y, z));
	}
	[Obsolete("Use Quaternion.Euler instead. This function was deprecated because it uses radians instead of degrees")]
	public void SetEulerAngles(Vector3 euler)
	{
		this = Quaternion.EulerRotation(euler);
	}
	[Obsolete("Use Quaternion.eulerAngles instead. This function was deprecated because it uses radians instead of degrees")]
	public static Vector3 ToEulerAngles(Quaternion rotation)
	{
		return Quaternion.Internal_ToEulerRad(rotation);
	}
	[Obsolete("Use Quaternion.eulerAngles instead. This function was deprecated because it uses radians instead of degrees")]
	public Vector3 ToEulerAngles()
	{
		return Quaternion.Internal_ToEulerRad(this);
	}
	[Obsolete("Use Quaternion.AngleAxis instead. This function was deprecated because it uses radians instead of degrees")]
	public static Quaternion AxisAngle(Vector3 axis, float angle)
	{
		return Quaternion.INTERNAL_CALL_AxisAngle(ref axis, angle);
	}

	private static Quaternion INTERNAL_CALL_AxisAngle(ref Vector3 axis, float angle)
	{

	}
	[Obsolete("Use Quaternion.AngleAxis instead. This function was deprecated because it uses radians instead of degrees")]
	public void SetAxisAngle(Vector3 axis, float angle)
	{
		this = Quaternion.AxisAngle(axis, angle);
	}
	*/
	#endregion
	public override int GetHashCode() {
		return this.x.GetHashCode() ^ this.y.GetHashCode() << 2 ^ this.z.GetHashCode() >> 2 ^ this.w.GetHashCode() >> 1;
	}
	public override bool Equals(object other) {
		if (!(other is MyQuaternion)) {
			return false;
		}
		MyQuaternion quaternion = (MyQuaternion)other;
		return this.x.Equals(quaternion.x) && this.y.Equals(quaternion.y) && this.z.Equals(quaternion.z) && this.w.Equals(quaternion.w);
	}
	public bool Equals(MyQuaternion other) {
		return this.x.Equals(other.x) && this.y.Equals(other.y) && this.z.Equals(other.z) && this.w.Equals(other.w);
	}
	public static MyQuaternion operator *(MyQuaternion lhs, MyQuaternion rhs) {
		return new MyQuaternion(lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y, lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z, lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x, lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
	}
	public static Vector3 operator *(MyQuaternion rotation, Vector3 point) {
		float num = rotation.x * 2f;
		float num2 = rotation.y * 2f;
		float num3 = rotation.z * 2f;
		float num4 = rotation.x * num;
		float num5 = rotation.y * num2;
		float num6 = rotation.z * num3;
		float num7 = rotation.x * num2;
		float num8 = rotation.x * num3;
		float num9 = rotation.y * num3;
		float num10 = rotation.w * num;
		float num11 = rotation.w * num2;
		float num12 = rotation.w * num3;
		Vector3 result;
		result.X = (1f - (num5 + num6)) * point.X + (num7 - num12) * point.Y + (num8 + num11) * point.Z;
		result.Y = (num7 + num12) * point.X + (1f - (num4 + num6)) * point.Y + (num9 - num10) * point.Z;
		result.Z = (num8 - num11) * point.X + (num9 + num10) * point.Y + (1f - (num4 + num5)) * point.Z;
		return result;
	}
	public static bool operator ==(MyQuaternion lhs, MyQuaternion rhs) {
		return MyQuaternion.Dot(lhs, rhs) > 0.999999f;
	}
	public static bool operator !=(MyQuaternion lhs, MyQuaternion rhs) {
		return MyQuaternion.Dot(lhs, rhs) <= 0.999999f;
	}
}