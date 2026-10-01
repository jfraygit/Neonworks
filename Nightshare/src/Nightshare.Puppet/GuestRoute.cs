using System;

namespace Nightshare.Puppet
{
    /// <summary>
    /// A circle walked around a known point, at a constant speed.
    /// <para>
    /// The centre is either typed in, or taken once from the host's first position. It is
    /// not updated after that. Chasing the host would turn a test walk into a conga line,
    /// and the point of the puppet is a body whose path is known in advance.
    /// </para>
    /// <para>
    /// Yaw is the direction of travel. At angle 0 the body stands at +X from the centre
    /// and faces +Z, which is yaw 0 in Unity. A positive angle turns it toward +Z and then
    /// toward -X.
    /// </para>
    /// </summary>
    public sealed class GuestRoute
    {
        public float CenterX { get; private set; }
        public float CenterY { get; private set; }
        public float CenterZ { get; private set; }

        /// <summary>Metres out from the centre. Below five centimetres the puppet stands still.</summary>
        public float Radius = 4f;

        /// <summary>Metres per second along the circle. The plugin's walk blend is full at 3.</summary>
        public float Speed = 3f;

        public bool HasCenter { get; private set; }

        public void SetCenter(float x, float y, float z)
        {
            CenterX = x;
            CenterY = y;
            CenterZ = z;
            HasCenter = true;
        }

        public void Sample(float elapsedSeconds, out float x, out float y, out float z, out float yaw, out bool moving)
        {
            if (!HasCenter)
                throw new InvalidOperationException("The route has no centre yet.");

            y = CenterY;

            if (Radius < 0.05f || Speed <= 0f)
            {
                x = CenterX;
                z = CenterZ;
                yaw = 0f;
                moving = false;
                return;
            }

            var angle = elapsedSeconds * (Speed / Radius);
            x = CenterX + Radius * MathF.Cos(angle);
            z = CenterZ + Radius * MathF.Sin(angle);

            // Tangent is (-sin, cos). atan2(x, z) is Unity yaw: 0 faces +Z, +90 faces +X.
            yaw = MathF.Atan2(-MathF.Sin(angle), MathF.Cos(angle)) * (180f / MathF.PI);
            moving = true;
        }
    }
}
