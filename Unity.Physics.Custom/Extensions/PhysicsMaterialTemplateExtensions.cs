namespace Unity.Physics.Authoring
{
    public static class PhysicsMaterialTemplateExtensions
    {
        public static CollisionFilter ToCollisionFilter(this PhysicsMaterialTemplate template)
        {
            return new CollisionFilter
            {
                BelongsTo = template.BelongsTo.Value,
                CollidesWith = template.CollidesWith.Value,
                GroupIndex = 0
            };
        }

        public static Material ToMaterial(this PhysicsMaterialTemplate template)
        {
            return new Material
            {
                CollisionResponse = template.CollisionResponse,
                Friction = template.Friction.Value,
                FrictionCombinePolicy = template.Friction.CombineMode,
                Restitution = template.Restitution.Value,
                RestitutionCombinePolicy = template.Restitution.CombineMode,
                CustomTags = template.CustomTags.Value,
                EnableDetailedStaticMeshCollision = template.DetailedStaticMeshCollision.Value,
            };
        }
    }
}