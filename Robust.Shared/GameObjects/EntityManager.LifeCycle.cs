// Filename: EntityManager.Lifecycle.cs

using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects
{
    /// <summary>
    /// This partial class of EntityManager contains the core logic for managing the
    /// lifecycle state machines of both entities and their components.
    /// </summary>
    public partial class EntityManager
    {
        #region Pre-allocated Lifecycle Events

        private static readonly ComponentAdd CompAddInstance = new();
        private static readonly ComponentInit CompInitInstance = new();
        private static readonly ComponentStartup CompStartupInstance = new();
        private static readonly ComponentShutdown CompShutdownInstance = new();
        private static readonly ComponentRemove CompRemoveInstance = new();

        #endregion

        #region Component Lifecycle Management

        /// <summary>
        /// Transitions a component from <see cref="ComponentLifeStage.PreAdd"/> to <see cref="ComponentLifeStage.Added"/>,
        /// raising a <see cref="ComponentAdd"/> event.
        /// </summary>
        internal void LifeAddToEntity(EntityUid uid, IComponent component, CompIdx idx)
        {
            DebugTools.Assert(!_deleteSet.Contains(component));
            DebugTools.Assert(component.LifeStage == ComponentLifeStage.PreAdd);

        #pragma warning disable CS0618 // LifeStage is managed internally here.
            component.LifeStage = ComponentLifeStage.Adding;
            component.CreationTick = CurrentTick;
            // Networked components are assumed to be dirty when added to entities.
            component.LastModifiedTick = CurrentTick;
            EventBus.RaiseComponentEvent(uid, component, idx, CompAddInstance);
            component.LifeStage = ComponentLifeStage.Added;
        #pragma warning restore CS0618
        }

        /// <summary>
        /// Transitions a component from <see cref="ComponentLifeStage.Added"/> to <see cref="ComponentLifeStage.Initialized"/>,
        /// raising a <see cref="ComponentInit"/> event.
        /// </summary>
        internal void LifeInitialize(EntityUid uid, IComponent component, CompIdx idx)
        {
            DebugTools.Assert(!_deleteSet.Contains(component));
            DebugTools.Assert(component.LifeStage == ComponentLifeStage.Added);

        #pragma warning disable CS0618 // LifeStage is managed internally here.
            component.LifeStage = ComponentLifeStage.Initializing;
            EventBus.RaiseComponentEvent(uid, component, idx, CompInitInstance);
            component.LifeStage = ComponentLifeStage.Initialized;
        #pragma warning restore CS0618
        }

        /// <summary>
        /// Transitions a component from <see cref="ComponentLifeStage.Initialized"/> to <see cref="ComponentLifeStage.Running"/>,
        /// raising a <see cref="ComponentStartup"/> event.
        /// </summary>
        internal void LifeStartup(EntityUid uid, IComponent component, CompIdx idx)
        {
            DebugTools.Assert(!_deleteSet.Contains(component));
            DebugTools.Assert(component.LifeStage == ComponentLifeStage.Initialized);

        #pragma warning disable CS0618 // LifeStage is managed internally here.
            component.LifeStage = ComponentLifeStage.Starting;
            EventBus.RaiseComponentEvent(uid, component, idx, CompStartupInstance);
            component.LifeStage = ComponentLifeStage.Running;
        #pragma warning restore CS0618
        }

        /// <summary>
        /// Transitions a component from its current running state to <see cref="ComponentLifeStage.Stopped"/>,
        /// raising a <see cref="ComponentShutdown"/> event if it was running.
        /// </summary>
        internal void LifeShutdown(EntityUid uid, IComponent component, CompIdx idx)
        {
            DebugTools.Assert(component.LifeStage is >= ComponentLifeStage.Initializing and < ComponentLifeStage.Stopping);

        #pragma warning disable CS0618 // LifeStage is managed internally here.
            // If the component was never started, no shutdown logic is necessary.
            if (component.LifeStage <= ComponentLifeStage.Initialized)
            {
                component.LifeStage = ComponentLifeStage.Stopped;
                return;
            }

            component.LifeStage = ComponentLifeStage.Stopping;
            EventBus.RaiseComponentEvent(uid, component, idx, CompShutdownInstance);
            component.LifeStage = ComponentLifeStage.Stopped;
        #pragma warning restore CS0618
        }

        /// <summary>
        /// Transitions a component to <see cref="ComponentLifeStage.Deleted"/>, raising a <see cref="ComponentRemove"/> event.
        /// </summary>
        internal void LifeRemoveFromEntity(EntityUid uid, IComponent component, CompIdx idx)
        {
            // Can be called at any time after PreAdd, including inside other life stage events.
            DebugTools.Assert(component.LifeStage != ComponentLifeStage.PreAdd);

    #pragma warning disable CS0618 // LifeStage is managed internally here.
            component.LifeStage = ComponentLifeStage.Removing;
            EventBus.RaiseComponentEvent(uid, component, idx, CompRemoveInstance);
            component.LifeStage = ComponentLifeStage.Deleted;
    #pragma warning restore CS0618
        }

    #endregion

    #region Entity Lifecycle State Management

    internal virtual void SetLifeStage(MetaDataComponent meta, EntityLifeStage stage)
    {
        // The entity lifecycle should only ever move forward, except when being deleted.
        DebugTools.Assert(stage > meta.EntityLifeStage || stage >= EntityLifeStage.Terminating);
        meta.EntityLifeStage = stage;
    }

    #endregion
    }
}
