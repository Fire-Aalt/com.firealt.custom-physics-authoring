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
