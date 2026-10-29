using System.Numerics;

namespace Shaft.Game.Rendering;

/// <summary>
/// The first-person camera. The matrix maths is the three.js
/// <c>PerspectiveCamera</c> with <c>rotation.order = 'YXZ'</c>, transposed into the
/// row-vector convention System.Numerics uses.
/// <para>
/// <c>main.js</c> sets <c>camera.position = player.pos + (0, eyeHeight, 0)</c> and
/// <c>camera.rotation = (pitch, yaw, 0)</c>. Its view matrix is the inverse of that
/// world matrix, which works out to
/// <c>T(-eye) * Ry(-yaw) * Rx(-pitch)</c>.
/// </para>
/// </summary>
public sealed class Camera
{
    /// <summary><c>new THREE.PerspectiveCamera(75, aspect, .1, 1000)</c> in core.js.</summary>
    public float FovDegrees = 75f;
    public float Near = 0.1f;
    public float Far = 1000f;

    public Vector3 Position;
    public float Yaw;
    public float Pitch;

    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;

    public float Aspect { get; private set; } = 16f / 9f;

    /// <summary>View direction: <c>R(yaw,pitch) * (0,0,-1)</c>.</summary>
    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            return new Vector3(-sy * cp, sp, -cy * cp);
        }
    }

    /// <summary>Camera right axis, used to billboard the sun/moon sprites.</summary>
    public Vector3 Right => new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

    /// <summary>Camera up axis in world space.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public void Update(float aspect)
    {
        Aspect = aspect <= 0f ? 1f : aspect;
        View = Matrix4x4.CreateTranslation(-Position)
             * Matrix4x4.CreateRotationY(-Yaw)
             * Matrix4x4.CreateRotationX(-Pitch);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(
            FovDegrees * MathF.PI / 180f, Aspect, Near, Far);
        ViewProjection = View * Projection;
    }

    /// <summary>
    /// Extracts the six frustum planes (pointing inward) from the combined matrix, in the
    /// form the Gribb-Hartmann method gives for a row-vector projection.
    /// </summary>
    public void GetFrustumPlanes(Span<Vector4> planes)
    {
        Matrix4x4 m = ViewProjection;
        planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41); // left
        planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
        planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42); // bottom
        planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top
        planes[4] = new Vector4(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43); // near
        planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43); // far

        for (int i = 0; i < 6; i++)
        {
            Vector4 p = planes[i];
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len > 1e-8f) planes[i] = p / len;
        }
    }

    /// <summary>AABB test against the planes from <see cref="GetFrustumPlanes"/>.</summary>
    public static bool BoxInFrustum(ReadOnlySpan<Vector4> planes, Vector3 min, Vector3 max)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            Vector4 p = planes[i];
            // The "positive vertex" test: if the furthest corner along the normal is
            // outside, the whole box is.
            float px = p.X >= 0f ? max.X : min.X;
            float py = p.Y >= 0f ? max.Y : min.Y;
            float pz = p.Z >= 0f ? max.Z : min.Z;
            if (px * p.X + py * p.Y + pz * p.Z + p.W < 0f) return false;
        }
        return true;
    }
}
