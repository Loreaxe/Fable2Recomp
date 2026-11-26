using Godot;

namespace Refii.Camera;

/// <summary>
/// THE GUEST'S ROTATION ARITHMETIC, as the camera component uses it: quaternions (x, y, z, w),
/// Hamilton products, a vector turned as q·v·q*, rows the images of the axes (X right, Y forward,
/// Z up), every operation in single precision like the VMX unit's.
/// </summary>
/// <remarks>
/// Read from the Fable 2 TU1 binary (the disc and both Fable 3 builds call the same shapes; the
/// addresses are in docs-side NOTES): the product at 0x822A4DBC..0x822A4E14, the turn sub_821F59E0
/// (q ⊗ (v,0) ⊗ q*), rows to quaternion sub_8223B9A0 (trace and largest-diagonal paths), the axis
/// angle sub_8223A070 (half angle), the slerp sub_8220CF20 (shortest arc, lerp below a sine of
/// 1e-4), the direction basis of SetDirection and sub_82278948, the angles of sub_82A23A60, and the
/// normalise Math_Vec3NormalizeInPlaceEps (zero below the epsilon).
/// </remarks>
public readonly record struct Engine_CameraQuat(float X, float Y, float Z, float W)
{
	public const float Epsilon = 0.000099999997f;

	public static readonly Engine_CameraQuat Identity = new(0f, 0f, 0f, 1f);
	public static readonly Vector3 AxisX = new(1f, 0f, 0f);
	public static readonly Vector3 AxisY = new(0f, 1f, 0f);
	public static readonly Vector3 AxisZ = new(0f, 0f, 1f);

	public static Engine_CameraQuat operator *(Engine_CameraQuat a, Engine_CameraQuat b) => new(
		a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
		a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
		a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
		a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

	public Engine_CameraQuat Conjugate => new(-X, -Y, -Z, W);

	public float Dot(Engine_CameraQuat other) => X * other.X + Y * other.Y + Z * other.Z + W * other.W;

	/// <summary>The vector turned by this quaternion (sub_821F59E0).</summary>
	public Vector3 Rotate(Vector3 v)
	{
		Engine_CameraQuat turned = this * new Engine_CameraQuat(v.X, v.Y, v.Z, 0f) * Conjugate;
		return new Vector3(turned.X, turned.Y, turned.Z);
	}

	/// <summary>The forward and up rows of this orientation, the block's +0x20 and +0x10.</summary>
	public Vector3 Forward => Rotate(AxisY);

	public Vector3 Up => Rotate(AxisZ);

	/// <summary>A turn of <paramref name="angle"/> radians about <paramref name="axis"/> (sub_8223A070).</summary>
	public static Engine_CameraQuat AxisAngle(Vector3 axis, float angle)
	{
		float half = angle * 0.5f;
		float s = (float)System.Math.Sin(half);
		float c = (float)System.Math.Cos(half);
		return new Engine_CameraQuat(axis.X * s, axis.Y * s, axis.Z * s, c);
	}

	/// <summary>Math_Vec3NormalizeInPlaceEps: unit length, or zero below the epsilon.</summary>
	public static Vector3 Normalise(Vector3 v)
	{
		float length = System.MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
		if (length < Epsilon)
		{
			return Vector3.Zero;
		}

		float inverse = 1f / length;
		return new Vector3(v.X * inverse, v.Y * inverse, v.Z * inverse);
	}

	public static Vector3 Cross(Vector3 a, Vector3 b) => new(
		a.Y * b.Z - a.Z * b.Y,
		a.Z * b.X - a.X * b.Z,
		a.X * b.Y - a.Y * b.X);

	/// <summary>The rows (right, forward, up) as a quaternion (sub_8223B9A0).</summary>
	public static Engine_CameraQuat FromRows(Vector3 right, Vector3 forward, Vector3 up)
	{
		float[,] m =
		{
			{ right.X, right.Y, right.Z },
			{ forward.X, forward.Y, forward.Z },
			{ up.X, up.Y, up.Z },
		};
		float trace = m[0, 0] + m[1, 1] + m[2, 2];
		if (trace > 0f)
		{
			float s = System.MathF.Sqrt(trace + 1f);
			float t = 0.5f / s;
			return new Engine_CameraQuat(
				(m[1, 2] - m[2, 1]) * t,
				(m[2, 0] - m[0, 2]) * t,
				(m[0, 1] - m[1, 0]) * t,
				s * 0.5f);
		}

		int i = 0;
		if (m[1, 1] > m[0, 0])
		{
			i = 1;
		}

		if (m[2, 2] > m[i, i])
		{
			i = 2;
		}

		int j = (i + 1) % 3;
		int k = (j + 1) % 3;
		float root = System.MathF.Sqrt(m[i, i] - (m[j, j] + m[k, k]) + 1f);
		float scale = 0.5f / root;
		float[] q = new float[3];
		q[i] = root * 0.5f;
		q[j] = (m[i, j] + m[j, i]) * scale;
		q[k] = (m[i, k] + m[k, i]) * scale;
		return new Engine_CameraQuat(q[0], q[1], q[2], (m[j, k] - m[k, j]) * scale);
	}

	/// <summary>
	/// sub_82278948 then sub_8223B9A0: up and forward normalised, right = forward × up.
	/// </summary>
	public static Engine_CameraQuat FromUpForward(Vector3 up, Vector3 forward)
	{
		Vector3 u = Normalise(up);
		Vector3 f = Normalise(forward);
		return FromRows(Cross(f, u), f, u);
	}

	/// <summary>
	/// sub_821B0068 (the easing step): the up and forward rows as they stand, right = forward × up,
	/// no normalising.
	/// </summary>
	public static Engine_CameraQuat FromStepRows(Vector3 up, Vector3 forward) =>
		FromRows(Cross(forward, up), forward, up);

	/// <summary>The target orientation SetDirection makes of a direction (world up Z).</summary>
	public static Engine_CameraQuat FromDirection(Vector3 direction)
	{
		Vector3 forward = Normalise(direction);
		Vector3 right = Normalise(Cross(forward, AxisZ));
		Vector3 up = Cross(right, forward);
		return FromUpForward(up, forward);
	}

	/// <summary>
	/// sub_82A23A60: the up and forward of yaw, pitch and roll in radians — forward
	/// (sin yaw·cos pitch, cos yaw·cos pitch, sin pitch), up the pitched Z, then up turned about
	/// forward by the roll.
	/// </summary>
	public static (Vector3 Up, Vector3 Forward) FromAngles(float yaw, float pitch, float roll)
	{
		float s1 = (float)System.Math.Sin(pitch);
		float c1 = (float)System.Math.Cos(pitch);
		float s0 = (float)System.Math.Sin(yaw);
		float c0 = (float)System.Math.Cos(yaw);
		var forward = new Vector3(s0 * c1, c0 * c1, s1);
		var up = new Vector3(-(s0 * s1), -(c0 * s1), c1);
		return (AxisAngle(forward, roll).Rotate(up), forward);
	}

	/// <summary>sub_8220CF20: shortest-arc slerp, a straight lerp where the arc's sine is below 1e-4.</summary>
	public static Engine_CameraQuat Slerp(Engine_CameraQuat from, Engine_CameraQuat to, float t)
	{
		float cos = from.Dot(to);
		float sign = 1f;
		if (cos < 0f)
		{
			cos = -cos;
			sign = -1f;
		}

		cos = System.Math.Clamp(cos, -1f, 1f);
		float omega = System.MathF.Acos(cos);
		float sin = System.MathF.Sin(omega);
		float a;
		float b;
		if (System.MathF.Abs(sin) >= Epsilon)
		{
			a = System.MathF.Sin((1f - t) * omega) / sin;
			b = System.MathF.Sin(t * omega) / sin * sign;
		}
		else
		{
			a = 1f - t;
			b = t * sign;
		}

		return new Engine_CameraQuat(
			from.X * a + to.X * b,
			from.Y * a + to.Y * b,
			from.Z * a + to.Z * b,
			from.W * a + to.W * b);
	}
}
