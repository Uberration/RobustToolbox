// Filename: Robust.Client/Physics/BroadphaseSystem.cs

using Robust.Shared.Physics.Systems;

#nullable enable

namespace Robust.Client.Physics
{
    public sealed class BroadphaseSystem : SharedBroadphaseSystem
    {
        public override void Initialize()
        {
            base.Initialize();
            // Ensure broadphase runs before the main physics step.
            UpdatesBefore.Add(typeof(PhysicsSystem));
        }
    }
}
