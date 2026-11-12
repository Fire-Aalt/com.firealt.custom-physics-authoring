using System;

namespace Unity.Physics.Authoring
{
    /// <summary> A set of world-space rotation axes. </summary>
    [Flags]
    public enum RotationAxes
    {
        /// <summary> No axis. </summary>
        None = 0,

        /// <summary> The world X axis. </summary>
        X = 1 << 0,

        /// <summary> The world Y axis. </summary>
        Y = 1 << 1,

        /// <summary> The world Z axis. </summary>
        Z = 1 << 2,

        /// <summary> All three axes. </summary>
        All = X | Y | Z,
    }
}
