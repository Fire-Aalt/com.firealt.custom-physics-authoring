using Unity.Mathematics;

namespace Unity.Physics.Authoring
{
    /// <summary>
    /// Bakes Rigidbody-style rotation freezes into <see cref="PhysicsMass"/> by zeroing the inverse inertia about the
    /// frozen axes, so the solver cannot rotate the body about them and no joint is needed.
    /// </summary>
    /// <remarks>
    /// The inverse inertia is diagonal in motion space, so a frozen axis is locked relative to the body. Freezing all
    /// three axes, or two axes when the body only turns about the remaining world axis, matches PhysX exactly. A
    /// single frozen axis tilts once the body rotates about one of the two free axes.
    /// </remarks>
    static class FrozenRotationUtility
    {
        // Stands in for the infinite inertia of an axis that is already locked, which would produce NaN in products.
        const float INFINITE_INERTIA = 1e30f;

        /// <summary> Applies <paramref name="frozen"/> to the body's mass and its motion-space angular velocity. </summary>
        /// <param name="mass"> The dynamic body's mass properties. </param>
        /// <param name="angularVelocity"> The body's angular velocity in the motion space of <paramref name="mass"/>. </param>
        /// <param name="frozen"> The frozen world axes. </param>
        /// <param name="bodyRotation"> The body's world rotation, which maps the world axes into body space. </param>
        internal static void Apply(ref PhysicsMass mass, ref float3 angularVelocity, RotationAxes frozen,
            quaternion bodyRotation)
        {
            frozen &= RotationAxes.All;
            var frozenCount = math.countbits((int)frozen);
            if (frozenCount == 0)
                return;

            if (frozenCount == 3)
            {
                mass.InverseInertia = float3.zero;
                angularVelocity = float3.zero;
                return;
            }

            // Two frozen axes leave one free axis, and one frozen axis leaves one locked axis. Either way, the
            // constraint is described by that single axis.
            var single = frozenCount == 2 ? ~frozen & RotationAxes.All : frozen;
            var worldAxis = single == RotationAxes.X ? math.right() : single == RotationAxes.Y ? math.up() : math.forward();
            var bodyAxis = math.rotate(math.inverse(bodyRotation), worldAxis);

            var oldOrientation = mass.InertiaOrientation;
            var oldAxes = new float3x3(oldOrientation);
            var inertia = math.select(math.rcp(mass.InverseInertia), INFINITE_INERTIA, mass.InverseInertia == 0f);
            var bodyAngularVelocity = math.rotate(oldOrientation, angularVelocity);

            // Turn the motion axis closest to the single axis onto it with the smallest rotation, so bodies whose
            // inertia is already aligned keep their inertia orientation and motion-space velocity layout.
            var alignment = math.abs(math.mul(bodyAxis, oldAxes));
            var index = alignment.x >= alignment.y && alignment.x >= alignment.z ? 0 : alignment.y >= alignment.z ? 1 : 2;
            var motionAxis = oldAxes[index];
            var target = math.dot(motionAxis, bodyAxis) < 0f ? -bodyAxis : bodyAxis;
            var orientation = math.normalize(math.mul(FromToRotation(motionAxis, target), oldOrientation));

            var inverseInertia = float3.zero;
            if (frozenCount == 2)
            {
                // Only rotation about the free axis remains, which resists with the inertia about that axis.
                inverseInertia[index] = math.rcp(Inertia(oldAxes, inertia, target, target));
            }
            else
            {
                // Rotation is limited to the plane perpendicular to the locked axis. Its two remaining motion axes
                // must be the principal axes of the inertia restricted to that plane.
                var axes = new float3x3(orientation);
                var u = axes[(index + 1) % 3];
                var v = axes[(index + 2) % 3];
                var uu = Inertia(oldAxes, inertia, u, u);
                var vv = Inertia(oldAxes, inertia, v, v);
                var uv = Inertia(oldAxes, inertia, u, v);

                // Rotating u and v by theta about the locked axis diagonalizes the 2x2 inertia. Any multiple of 90
                // degrees only swaps them, so the smallest rotation is used.
                var theta = 0.5f * math.atan2(2f * uv, uu - vv);
                if (theta > math.PI / 4f)
                    theta -= math.PI / 2f;
                else if (theta < -math.PI / 4f)
                    theta += math.PI / 2f;

                orientation = math.normalize(math.mul(quaternion.AxisAngle(target, theta), orientation));
                axes = new float3x3(orientation);
                u = axes[(index + 1) % 3];
                v = axes[(index + 2) % 3];
                inverseInertia[(index + 1) % 3] = math.rcp(Inertia(oldAxes, inertia, u, u));
                inverseInertia[(index + 2) % 3] = math.rcp(Inertia(oldAxes, inertia, v, v));
            }

            mass.InertiaOrientation = orientation;
            mass.InverseInertia = inverseInertia;
            angularVelocity = math.select(float3.zero, math.rotate(math.inverse(orientation), bodyAngularVelocity),
                inverseInertia != 0f);
        }

        /// <summary> Returns <c>xᵀ I y</c> for the inertia tensor I with principal axes <paramref name="axes"/>. </summary>
        static float Inertia(float3x3 axes, float3 principal, float3 x, float3 y) =>
            math.csum(principal * math.mul(x, axes) * math.mul(y, axes));

        /// <summary> The shortest rotation from <paramref name="from"/> to <paramref name="to"/>, which are unit
        /// vectors at most 90 degrees apart. </summary>
        static quaternion FromToRotation(float3 from, float3 to) =>
            math.normalize(new quaternion(new float4(math.cross(from, to), 1f + math.dot(from, to))));
    }
}
