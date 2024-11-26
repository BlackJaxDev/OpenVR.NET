using System.Numerics;
using Valve.VR;

namespace OpenVR.NET;

public static class Extensions
{
    public static Matrix4x4 ToMatrix4x4(this HmdMatrix33_t mat)
    {
        return new Matrix4x4(
            mat.m0, mat.m3, mat.m6, 0,
            mat.m1, mat.m4, mat.m7, 0,
            mat.m2, mat.m5, mat.m8, 0,
            0, 0, 0, 1
        );
    }

    public static HmdMatrix33_t ToHmdMatrix33(this Matrix4x4 mat)
    {
        return new HmdMatrix33_t()
        {
            m0 = mat.M11,
            m1 = mat.M21,
            m2 = mat.M31,
            m3 = mat.M12,
            m4 = mat.M22,
            m5 = mat.M32,
            m6 = mat.M13,
            m7 = mat.M23,
            m8 = mat.M33
        };
    }

    public static Matrix4x4 ToMatrix4x4(this HmdMatrix34_t mat)
    {
        return new Matrix4x4(
            mat.m0, mat.m4, mat.m8, 0,
            mat.m1, mat.m5, mat.m9, 0,
            mat.m2, mat.m6, mat.m10, 0,
            mat.m3, mat.m7, mat.m11, 1
        );
    }

    public static Matrix4x4 ToMatrix4x4(this HmdMatrix44_t mat)
    {
        return new Matrix4x4(
            mat.m0, mat.m4, mat.m8, mat.m12,
            mat.m1, mat.m5, mat.m9, mat.m13,
            mat.m2, mat.m6, mat.m10, mat.m14,
            mat.m3, mat.m7, mat.m11, mat.m15
        );
    }

    public static HmdMatrix34_t ToHmdMatrix34(this Matrix4x4 mat)
    {
        return new HmdMatrix34_t()
        {
            m0 = mat.M11,
            m1 = mat.M21,
            m2 = mat.M31,
            m3 = mat.M41,
            m4 = mat.M12,
            m5 = mat.M22,
            m6 = mat.M32,
            m7 = mat.M42,
            m8 = mat.M13,
            m9 = mat.M23,
            m10 = mat.M33,
            m11 = mat.M43
        };
    }

    public static HmdMatrix44_t ToHmdMatrix44(this Matrix4x4 mat)
    {
        return new HmdMatrix44_t()
        {
            m0 = mat.M11,
            m1 = mat.M21,
            m2 = mat.M31,
            m3 = mat.M41,
            m4 = mat.M12,
            m5 = mat.M22,
            m6 = mat.M32,
            m7 = mat.M42,
            m8 = mat.M13,
            m9 = mat.M23,
            m10 = mat.M33,
            m11 = mat.M43,
            m12 = mat.M14,
            m13 = mat.M24,
            m14 = mat.M34,
            m15 = mat.M44
        };
    }

    public static Vector3 ToVector3(this HmdVector3_t vec)
    {
        return new Vector3(vec.v0, vec.v1, vec.v2);
    }

    public static Quaternion ToQuaternion(this HmdQuaternion_t quat)
    {
        return new Quaternion()
        {
            X = (float)quat.x,
            Y = (float)quat.y,
            Z = (float)quat.z,
            W = (float)quat.w
        };
    }

    public static HmdQuaternion_t ToHmdQuaternion(this Quaternion quat)
    {
        return new HmdQuaternion_t()
        {
            x = quat.X,
            y = quat.Y,
            z = quat.Z,
            w = quat.W
        };
    }

    public static HmdVector3_t ToHmdVector3(this Vector3 vec)
    {
        return new HmdVector3_t()
        {
            v0 = vec.X,
            v1 = vec.Y,
            v2 = vec.Z
        };
    }

    public static HmdVector3_t ToHmdVector3(this Vector2 vec)
    {
        return new HmdVector3_t()
        {
            v0 = vec.X,
            v1 = vec.Y,
            v2 = 0
        };
    }

    public static HmdVector3_t ToHmdVector3(this Vector4 vec)
    {
        return new HmdVector3_t()
        {
            v0 = vec.X,
            v1 = vec.Y,
            v2 = vec.Z
        };
    }

    public static HmdVector2_t ToHmdVector2(this Vector2 vec)
    {
        return new HmdVector2_t()
        {
            v0 = vec.X,
            v1 = vec.Y
        };
    }

    public static Vector2 ToVector2(this HmdVector2_t vec)
    {
        return new Vector2(vec.v0, vec.v1);
    }
}
