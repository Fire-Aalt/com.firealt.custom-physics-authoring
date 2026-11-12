# FireAlt Custom Physics Authoring

Fork of the Custom Physics Authoring sample from Unity Physics (`com.unity.physics` 6.7.0).
Licensed under the Unity Companion License, see `LICENSE.md`.

## Changes

- Removed the sample's `Unity.Physics.CommonScripts`.
- Shape handles resize like built-in PhysX colliders: dragging a handle moves only that side.
  Hold Alt to resize symmetrically. Applies to Box, Capsule, Sphere, Cylinder and Plane shapes.
- Added `Tools > Unity Physics > Replace PhysX Authoring with ECS Physics Authoring`.
  It replaces Rigidbodies, colliders and joints in prefabs and scenes, matching the PhysX bakers and keeping
  prefab instance overrides. Lossy mappings are listed in the console.
  Rigidbody `Freeze Rotation` constraints become the Physics Body's `Freeze Rotation`, unlike the PhysX bakers, which
  ignore them. `Freeze Position` constraints are still dropped; use a Limit DOF Joint for those.
- The Physics Body has a `Constraints` foldout with `Freeze Rotation` X/Y/Z toggles for dynamic bodies, like the
  Rigidbody's. The world axes are baked into `PhysicsMass` by zeroing the inverse inertia about them, so no joint
  entity is created and the solver cannot drift. Frozen axes are converted to body space using the baked rotation:
  - Three axes match PhysX exactly.
  - Two axes match PhysX if the body only spawns rotated about the free axis. Other spawn rotations break the freeze.
  - One axis is locked relative to the body, not the world. It tilts once the body rotates about a free axis.

  The inspector warns about the one- and two-axis cases. Initial angular velocity about frozen axes is removed.
  Keep `PhysicsStep` gyroscopic torque disabled, as it is by default: it divides by the inverse inertia and turns
  frozen axes into NaN.
- Colliders, Rigidbodies and joints have `Convert to Physics Shape`, `Convert to Physics Body` and
  `Convert to ECS Joint` in their component context menu. The replacement takes the component's place, with the same
  mappings as the bulk tool, as one undo step. A Rigidbody is converted together with its joints and every Rigidbody
  connected to it through joints, because the joints reference each other's bodies.
- Searching the Add Component window for `Rigidbody`, `Box Collider`, `Sphere Collider`, `Capsule Collider` or
  `Mesh Collider` also lists the Physics Body and Physics Shape, preset to the matching shape type.
- Baked capsules keep their authored rotation order, which avoids precision loss near gimbal lock.
- Added EditMode tests that bake PhysX and converted authoring and compare the results.
- Added `PhysicsMaterialTemplateExtensions` with extensions like `ToCollisionFilter()` and `ToMaterial()`.
- The Physics Shape inspector follows the PhysX Colliders inspector:
  - An `Edit Collider` button toggles a scene tool that hides the transform gizmo and enables the shape handles.
    Without it, primitive shapes only draw their wireframe.
  - `Fit to Self` and `Fit to Children` buttons, with PhysX's tooltips, replace `Fit to Enabled Render Meshes`
    on primitive shapes. `Fit to Children` skips children that have their own shape. The minimum skinned vertex
    weight popup only opens when skinned geometry is involved.
  - Size, Center and Orientation show their X/Y/Z labels and line up with the other shape fields.
