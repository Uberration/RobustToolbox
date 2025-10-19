// Filename: Robust.Server/Physics/BroadphaseSystem.cs

namespace Robust.Server.Physics
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
