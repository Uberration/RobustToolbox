using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Prometheus;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects
{
    public delegate void EntityUidQueryCallback(EntityUid uid);

    public delegate void ComponentQueryCallback<T>(EntityUid uid, T component) where T : IComponent;

    /// <inheritdoc />
    [Virtual]
    public abstract partial class EntityManager : IEntityManager
    {
        #region Dependencies

        [IoC.Dependency] protected readonly IPrototypeManager PrototypeManager = default!;
        [IoC.Dependency] protected readonly ILogManager LogManager = default!;
        [IoC.Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
        [IoC.Dependency] private readonly IMapManager _mapManager = default!;
        [IoC.Dependency] private readonly IGameTiming _gameTiming = default!;
        [IoC.Dependency] private readonly ISerializationManager _serManager = default!;
        [IoC.Dependency] private readonly ProfManager _prof = default!;
        [IoC.Dependency] private readonly INetManager _netMan = default!;
        [IoC.Dependency] private readonly IReflectionManager _reflection = default!;
        [IoC.Dependency] private readonly EntityConsoleHost _entityConsoleHost = default!;

        // System dependencies are resolved during Startup.
        protected SharedTransformSystem _xforms = default!;
        private SharedContainerSystem _containers = default!;
        private SharedMapSystem _mapSystem = default!;

        // High-performance query accessors for common components.
        public EntityQuery<MetaDataComponent> MetaQuery;
        public EntityQuery<TransformComponent> TransformQuery;
        private EntityQuery<PhysicsComponent> _physicsQuery;
        private EntityQuery<ActorComponent> _actorQuery;

        #endregion

        #region Public Properties and Events

        /// <inheritdoc />
        public GameTick CurrentTick => _gameTiming.CurTick;

        /// <inheritdoc />
        public IEntitySystemManager EntitySysManager => _entitySystemManager;

        /// <inheritdoc />
        public abstract IEntityNetworkManager EntityNetManager { get; }

        /// <inheritdoc />
        public IEventBus EventBus => _eventBus;

        /// <inheritdoc />
        public int EntityCount => _entities.Count;

        public bool Started { get; protected set; }
        public bool ShuttingDown { get; protected set; }
        public bool Initialized { get; protected set; }

        public static readonly MapInitEvent MapInitEventInstance = new();

        public event Action<Entity<MetaDataComponent>>? EntityAdded;
        public event Action<Entity<MetaDataComponent>>? EntityInitialized;
        public event Action<Entity<MetaDataComponent>>? EntityDeleted;
        public event Action? BeforeEntityFlush;
        public event Action? AfterEntityFlush;
        public event Action<EntityUid>? EntityQueueDeleted;
        public event Action<Entity<MetaDataComponent>>? EntityDirtied;

        /// <summary>
        /// Internal termination event handlers. This is mainly for exception tolerance, ensuring
        /// that PVS and other important engine systems can get updated before content code throws an exception.
        /// </summary>
        internal event TerminatingEventHandler? BeforeEntityTerminating;
        public delegate void TerminatingEventHandler(ref EntityTerminatingEvent ev);

        #endregion

        #region Internal State

        private readonly Queue<EntityUid> _queuedDeletions = new();
        private readonly HashSet<EntityUid> _queuedDeletionsSet = new();
        private readonly EntityDiffContext _context = new();

        /// <summary>
        ///     All entities currently stored in the manager.
        /// </summary>
        private readonly HashSet<EntityUid> _entities = new();

        private EntityEventBus _eventBus = null!;
        protected int NextEntityUid = (int)EntityUid.FirstUid;
        protected int NextNetworkId = (int)NetEntity.First;

        private ComponentRegistration _metaReg = default!;
        private ComponentRegistration _xformReg = default!;

        private ISawmill _sawmill = default!;
        private ISawmill _resolveSawmill = default!;

#if DEBUG
        private int _mainThreadId;
#endif

        #endregion

        #region Initialization and Lifecycle

        /// <summary>
        /// Constructs a new instance of <see cref="EntityManager"/>.
        /// </summary>
        public EntityManager()
        {
        }

        public virtual void Initialize()
        {
            if (Initialized)
                throw new InvalidOperationException("Initialize() called multiple times");

            _eventBus = new EntityEventBus(this, _reflection);

            InitializeComponents(); // This now calls the method in the other partial class.
            _metaReg = ComponentFactory.GetRegistration(typeof(MetaDataComponent));
            _xformReg = ComponentFactory.GetRegistration(typeof(TransformComponent));
            _sawmill = LogManager.GetSawmill("entity");
            _resolveSawmill = LogManager.GetSawmill("resolve");

            MetaQuery = GetEntityQuery<MetaDataComponent>();
            TransformQuery = GetEntityQuery<TransformComponent>();
            _physicsQuery = GetEntityQuery<PhysicsComponent>();
            _actorQuery = GetEntityQuery<ActorComponent>();

#if DEBUG
            _mainThreadId = Environment.CurrentManagedThreadId;
#endif

            Initialized = true;
        }

        public virtual void Startup()
        {
            if (!Initialized)
                throw new InvalidOperationException("Startup() called without Initialized");
            if (Started)
                throw new InvalidOperationException("Startup() called multiple times");

            _entitySystemManager.Initialize();
            Started = true;
            _eventBus.LockSubscriptions();

            // Resolve system dependencies
            _mapSystem = System<SharedMapSystem>();
            _xforms = System<SharedTransformSystem>();
            _containers = System<SharedContainerSystem>();

            _entityConsoleHost.Startup();
        }

        public virtual void Shutdown()
        {
            ShuttingDown = true;
            FlushEntities();
            _eventBus.ClearSubscriptions();
            _entitySystemManager.Shutdown();
            ClearComponents();
            ShuttingDown = false;
            Started = false;
            _entityConsoleHost.Shutdown();
        }

        public virtual void Cleanup()
        {
            ComponentFactory.ComponentsAdded -= OnComponentsAdded;
            ShuttingDown = true;
            FlushEntities();
            _entitySystemManager.Clear();
            _eventBus.Dispose();
            _eventBus = null!;
            ClearComponents();

            ShuttingDown = false;
            Initialized = false;
            Started = false;
        }

        #endregion

        #region Game Loop Methods

        public virtual void TickUpdate(float frameTime, bool noPredictions, Histogram? histogram)
        {
            using (histogram?.WithLabels("EntitySystems").NewTimer())
            using (_prof.Group("Systems"))
            {
                _entitySystemManager.TickUpdate(frameTime, noPredictions);
            }

            using (histogram?.WithLabels("EntityEventBus").NewTimer())
            using (_prof.Group("Events"))
            {
                _eventBus.ProcessEventQueue();
            }

            using (histogram?.WithLabels("QueuedDeletion").NewTimer())
            using (_prof.Group("QueueDel"))
            {
                while (_queuedDeletions.TryDequeue(out var uid))
                {
                    DeleteEntity(uid);
                }

                _queuedDeletionsSet.Clear();
            }

            using (histogram?.WithLabels("ComponentCull").NewTimer())
            using (_prof.Group("ComponentCull"))
            {
                CullRemovedComponents();
            }
        }

        public virtual void FrameUpdate(float frameTime)
        {
            _entitySystemManager.FrameUpdate(frameTime);
        }

        #endregion

        #region Entity Creation

        /// <inheritdoc />
        public EntityUid CreateEntityUninitialized(string? prototypeName, EntityUid euid, ComponentRegistry? overrides = null)
        {
            // The euid parameter is not used in the base implementation.
            return CreateEntity(prototypeName, out _, overrides);
        }

        /// <inheritdoc />
        public EntityUid CreateEntityUninitialized(string? prototypeName, ComponentRegistry? overrides = null)
        {
            return CreateEntity(prototypeName, out _, overrides);
        }

        public EntityUid CreateEntityUninitialized(string? prototypeName, out MetaDataComponent meta, ComponentRegistry? overrides = null)
        {
            return CreateEntity(prototypeName, out meta, overrides);
        }

        /// <inheritdoc />
        public virtual EntityUid CreateEntityUninitialized(string? prototypeName, EntityCoordinates coordinates, ComponentRegistry? overrides = null, Angle rotation = default)
        {
            var newEntity = CreateEntity(prototypeName, out _, overrides);

            var xformComp = TransformQuery.GetComponent(newEntity);
            _xforms.SetCoordinates(newEntity, xformComp, coordinates, rotation: rotation, unanchor: false);
            return newEntity;
        }

        /// <inheritdoc />
        public virtual EntityUid CreateEntityUninitialized(string? prototypeName, MapCoordinates coordinates, ComponentRegistry? overrides = null, Angle rotation = default)
        {
            var newEntity = CreateEntity(prototypeName, out _, overrides);
            var transform = TransformQuery.GetComponent(newEntity);

            if (coordinates.MapId == MapId.Nullspace)
            {
                transform._parent = EntityUid.Invalid;
                _xforms.Unanchor(newEntity, transform);
                return newEntity;
            }

            var mapEnt = _mapSystem.GetMap(coordinates.MapId);
            if (!TryGetComponent(mapEnt, out TransformComponent? mapXform))
                throw new ArgumentException($"Attempted to spawn entity on an invalid map. Coordinates: {coordinates}");

            if (_mapManager.TryFindGridAt(coordinates, out var gridUid, out var grid) &&
                MetaQuery.TryGetComponentInternal(gridUid, out var meta) &&
                meta.EntityLifeStage < EntityLifeStage.Terminating)
            {
                var gridCoords = new EntityCoordinates(gridUid, _mapSystem.WorldToLocal(gridUid, grid, coordinates.Position));
                _xforms.SetCoordinates(newEntity, transform, gridCoords, rotation, unanchor: false);
            }
            else
            {
                var mapCoords = new EntityCoordinates(mapEnt, coordinates.Position);
                _xforms.SetCoordinates(newEntity, transform, mapCoords, rotation, newParent: mapXform);
            }

            return newEntity;
        }

        #endregion

        #region Entity Deletion

        /// <summary>
        /// Shuts down and removes a given Entity and all of its children. This is also broadcast to all clients.
        /// </summary>
        public virtual void DeleteEntity(EntityUid? uid)
        {
            if (uid == null)
                return;

            // This can be called when UIs are disposed after the EntityManager has already shut down.
            if (!Started)
                return;

            if (MetaQuery.TryGetComponent(uid.Value, out var meta))
                DeleteEntity(uid.Value, meta, TransformQuery.GetComponent(uid.Value));
        }

        /// <summary>
        /// Internal implementation of entity deletion logic.
        /// </summary>

        public void DeleteEntity(EntityUid e, MetaDataComponent meta, TransformComponent xform)
        {
            if (!Started || meta.EntityLifeStage >= EntityLifeStage.Deleted)
                return;

            ThreadCheck();

            if (meta.EntityLifeStage == EntityLifeStage.Terminating)
            {
                var msg = $"Called Delete on an entity already being deleted. Entity: {ToPrettyString(e)}";
#if !EXCEPTION_TOLERANCE
                throw new InvalidOperationException(msg);
#else
                _sawmill.Error($"{msg}. Trace: {Environment.StackTrace}");
                return;
#endif
            }

            RecursiveFlagEntityTermination(e, meta, xform);

            TransformComponent? parentXform = null;
            if (xform.ParentUid.IsValid() && xform.LifeStage >= ComponentLifeStage.Initialized)
            {
                // Use resolve for automatic error logging if parent is missing.
                TransformQuery.Resolve(xform.ParentUid, ref parentXform);
            }

            RecursiveDeleteEntity(e, meta, xform, parentXform);
        }

        private void RecursiveFlagEntityTermination(EntityUid uid, MetaDataComponent metadata, TransformComponent xform)
        {
            DebugTools.Assert(metadata.EntityLifeStage < EntityLifeStage.Terminating);
            SetLifeStage(metadata, EntityLifeStage.Terminating);

            try
            {
                var ev = new EntityTerminatingEvent((uid, metadata));
                BeforeEntityTerminating?.Invoke(ref ev);
                EventBus.RaiseLocalEvent(uid, ref ev, true);
            }
            catch (Exception e)
            {
                _sawmill.Error($"Caught exception while raising {nameof(EntityTerminatingEvent)} on entity {ToPrettyString(uid, metadata)}\n{e}");
            }

            // Recursively flag children for termination.
            foreach (var child in xform._children.ToArray()) // ToArray to prevent collection modification issues
            {
                if (!MetaQuery.TryGetComponent(child, out var childMeta) || childMeta.EntityDeleted)
                {
                    _sawmill.Error($"A deleted entity was still the transform child of another entity. Parent: {ToPrettyString(uid, metadata)}.");
                    xform._children.Remove(child);
                    continue;
                }

                RecursiveFlagEntityTermination(child, childMeta, TransformQuery.GetComponent(child));
            }
        }

        private void RecursiveDeleteEntity(EntityUid uid, MetaDataComponent metadata, TransformComponent transform, TransformComponent? parentXform)
        {
            // Detach from parent first to prevent cascading updates during child deletion.
            _xforms.DetachEntity(uid, transform, metadata, parentXform, true);

            // Recursively delete all children.
            foreach (var child in transform._children.ToArray()) // ToArray to prevent collection modification issues
            {
                try
                {
                    var childMeta = MetaQuery.GetComponent(child);
                    var childXform = TransformQuery.GetComponent(child);
                    RecursiveDeleteEntity(child, childMeta, childXform, transform);
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Caught exception while recursively deleting child entity '{ToPrettyString(child)}' of '{ToPrettyString(uid, metadata)}'\n{e}");
                }
            }

            if (transform._children.Count != 0)
                _sawmill.Error($"Failed to delete all children of entity: {ToPrettyString(uid)}");

            // Shut down and dispose all components.
            DisposeComponents(uid, metadata);
            SetLifeStage(metadata, EntityLifeStage.Deleted);

            try
            {
                EntityDeleted?.Invoke((uid, metadata));
            }
            catch (Exception e)
            {
                _sawmill.Error($"Caught exception while invoking {nameof(EntityDeleted)} on '{ToPrettyString(uid, metadata)}'\n{e}");
            }

            _eventBus.OnEntityDeleted(uid);
            _entities.Remove(uid);
            NetEntityLookup.Remove(metadata.NetEntity);
        }


        public virtual void QueueDeleteEntity(EntityUid? uid)
        {
            if (uid == null)
                return;

            if (!_queuedDeletionsSet.Add(uid.Value))
                return;

            _queuedDeletions.Enqueue(uid.Value);
            EntityQueueDeleted?.Invoke(uid.Value);
        }

        public bool TryQueueDeleteEntity(EntityUid? uid)
        {
            if (uid == null || Deleted(uid.Value) || _queuedDeletionsSet.Contains(uid.Value))
                return false;

            QueueDeleteEntity(uid);
            return true;
        }

        public virtual bool IsQueuedForDeletion(EntityUid uid) => _queuedDeletionsSet.Contains(uid);

        /// <inheritdoc />
        public void RemoveComponents(EntityUid target, EntityPrototype prototype)
        {
            RemoveComponents(target, prototype.Components);
        }

        /// <inheritdoc />
        public void RemoveComponents(EntityUid target, ComponentRegistry registry)
        {
            if (registry.Count == 0)
                return;

            var metadata = MetaQuery.GetComponent(target);

            foreach (var entry in registry.Values)
            {
                RemoveComponent(target, entry.Component.GetType(), metadata);
            }
        }

        #endregion

        #region Entity State and Queries

        /// <inheritdoc />
        public IEnumerable<EntityUid> GetEntities() => _entities;

        public bool EntityExists(EntityUid uid)
        {
            return MetaQuery.HasComponentInternal(uid);
        }

        public bool EntityExists(EntityUid? uid)
        {
            return uid.HasValue && EntityExists(uid.Value);
        }

        /// <inheritdoc />
        public bool IsPaused(EntityUid? uid, MetaDataComponent? metadata = null)
        {
            if (uid == null)
                return false;

            return MetaQuery.Resolve(uid.Value, ref metadata) && metadata.EntityPaused;
        }

        public bool Deleted(EntityUid uid)
        {
            return !MetaQuery.TryGetComponentInternal(uid, out var comp) || comp.EntityDeleted;
        }

        public bool Deleted([NotNullWhen(false)] EntityUid? uid)
        {
            return !uid.HasValue || Deleted(uid.Value);
        }

        /// <summary>
        /// Returns true if the entity's data (apart from transform) is default.
        /// </summary>
        public bool IsDefault(EntityUid uid, ICollection<string>? ignoredComps = null)
        {
            if (!MetaQuery.TryGetComponent(uid, out var metadata) || metadata.EntityPrototype == null)
                return false;

            var prototype = metadata.EntityPrototype;

            if (metadata.EntityName != prototype.Name || metadata.EntityDescription != prototype.Description)
                return false;

            var protoData = PrototypeManager.GetPrototypeData(prototype);
            var comps = GetComponentsInternal(uid);

            if (protoData.Count + 2 != comps.Count) // +2 for MetaData and Transform
                return false;

            foreach (var component in comps)
            {
                var compType = component.GetType();
                if (compType == typeof(TransformComponent) || compType == typeof(MetaDataComponent))
                    continue;

                var compName = ComponentFactory.GetComponentName(compType);
                if (ignoredComps?.Contains(compName) == true)
                    continue;

                if (!protoData.TryGetValue(compName, out var protoMapping))
                    return false; // Component exists on entity but not prototype.

                MappingDataNode compMapping;
                try
                {
                    compMapping = _serManager.WriteValueAs<MappingDataNode>(compType, component, alwaysWrite: true, context: _context);
                }
                catch (Exception e)
                {
                    _sawmill.Error($"Failed to serialize {compName} of entity prototype {prototype.ID}. Exception: {e.Message}");
                    return false;
                }

                if (compMapping.AnyExcept(protoMapping))
                    return false;
            }

            return true;
        }

        #endregion

        #region Entity Dirtying

        /// <inheritdoc />
        public virtual void DirtyEntity(EntityUid uid, MetaDataComponent? metadata = null)
        {
            if (!MetaQuery.ResolveInternal(uid, ref metadata))
                return;

            if (metadata.EntityLastModifiedTick == _gameTiming.CurTick)
                return;

            metadata.EntityLastModifiedTick = _gameTiming.CurTick;

            if (metadata.EntityLifeStage > EntityLifeStage.Initializing)
            {
                EntityDirtied?.Invoke((uid, metadata));
            }
        }

        /// <inheritdoc />
        [Obsolete("use override with an EntityUid or Entity<T>")]
        public void Dirty(IComponent component, MetaDataComponent? meta = null)
        {
            // This overload is obsolete because component.Owner is obsolete.
            // It is kept for backward compatibility.
#pragma warning disable CS0618
            Dirty(component.Owner, component, meta);
#pragma warning restore CS0618
        }

        /// <inheritdoc />
        public virtual void Dirty(EntityUid uid, IComponent component, MetaDataComponent? meta = null)
        {
            DebugTools.Assert(component.GetType().HasCustomAttribute<NetworkedComponentAttribute>(),
                $"Attempted to dirty a non-networked component: {component.GetType()}");
            DebugTools.AssertOwner(uid, component);

            if (component.LifeStage >= ComponentLifeStage.Removing || !component.NetSyncEnabled)
                return;

            if (component.LastModifiedTick == CurrentTick)
                return;

            DirtyEntity(uid, meta);
            component.LastModifiedTick = CurrentTick;
        }

        // Generic overloads for convenience and performance.
        public virtual void Dirty<T>(Entity<T> ent, MetaDataComponent? meta = null) where T : IComponent
        {
            Dirty(ent.Owner, ent.Comp, meta);
        }

        public virtual void Dirty<T1, T2>(Entity<T1, T2> ent, MetaDataComponent? meta = null)
            where T1 : IComponent where T2 : IComponent
        {
            if (ent.Comp1.LastModifiedTick != CurrentTick || ent.Comp2.LastModifiedTick != CurrentTick)
                DirtyEntity(ent.Owner, meta);

            ent.Comp1.LastModifiedTick = CurrentTick;
            ent.Comp2.LastModifiedTick = CurrentTick;
        }

        public virtual void Dirty<T1, T2, T3>(Entity<T1, T2, T3> ent, MetaDataComponent? meta = null)
            where T1 : IComponent where T2 : IComponent where T3 : IComponent
        {
            if (ent.Comp1.LastModifiedTick != CurrentTick || ent.Comp2.LastModifiedTick != CurrentTick || ent.Comp3.LastModifiedTick != CurrentTick)
                DirtyEntity(ent.Owner, meta);

            ent.Comp1.LastModifiedTick = CurrentTick;
            ent.Comp2.LastModifiedTick = CurrentTick;
            ent.Comp3.LastModifiedTick = CurrentTick;
        }

        public virtual void Dirty<T1, T2, T3, T4>(Entity<T1, T2, T3, T4> ent, MetaDataComponent? meta = null)
            where T1 : IComponent where T2 : IComponent where T3 : IComponent where T4 : IComponent
        {
            if (ent.Comp1.LastModifiedTick != CurrentTick || ent.Comp2.LastModifiedTick != CurrentTick || ent.Comp3.LastModifiedTick != CurrentTick || ent.Comp4.LastModifiedTick != CurrentTick)
                DirtyEntity(ent.Owner, meta);

            ent.Comp1.LastModifiedTick = CurrentTick;
            ent.Comp2.LastModifiedTick = CurrentTick;
            ent.Comp3.LastModifiedTick = CurrentTick;
            ent.Comp4.LastModifiedTick = CurrentTick;
        }

        #endregion

        #region Networking and Prediction

        public virtual void PredictedDeleteEntity(Entity<MetaDataComponent?, TransformComponent?> ent)
        {
            DeleteEntity(ent.Owner);
        }

        public void PredictedDeleteEntity(Entity<MetaDataComponent?, TransformComponent?>? ent)
        {
            if (ent != null)
                PredictedDeleteEntity(ent.Value);
        }

        public virtual void PredictedQueueDeleteEntity(Entity<MetaDataComponent?> ent)
        {
            QueueDeleteEntity(ent);
        }

        public void PredictedQueueDeleteEntity(Entity<MetaDataComponent?>? ent)
        {
            if (ent != null)
                PredictedQueueDeleteEntity(ent.Value);
        }

        [Obsolete("use variant without TransformComponent")]
        public virtual void PredictedQueueDeleteEntity(Entity<MetaDataComponent?, TransformComponent?> ent)
            => PredictedQueueDeleteEntity(new Entity<MetaDataComponent?>(ent.Owner, ent.Comp1));

        [Obsolete("use variant without TransformComponent")]
        public void PredictedQueueDeleteEntity(Entity<MetaDataComponent?, TransformComponent?>? ent)
        {
            if (ent != null)
                PredictedQueueDeleteEntity(ent.Value);
        }

        public void PredictedQueueDeleteEntity(EntityUid uid)
            => PredictedQueueDeleteEntity(new Entity<MetaDataComponent?>(uid, null));

        public void PredictedQueueDeleteEntity(EntityUid? uid)
        {
            if (uid != null)
                PredictedQueueDeleteEntity(uid.Value);
        }

        public virtual void RaisePredictiveEvent<T>(T msg) where T : EntityEventArgs
        {
            // This is part of the shared IEntityManager interface, but the server should never raise predictive events.
            DebugTools.Assert("Why are you raising predictive events on the server?");
        }

        public abstract void RaiseSharedEvent<T>(T message, EntityUid? user = null) where T : EntityEventArgs;
        public abstract void RaiseSharedEvent<T>(T message, ICommonSession? user = null) where T : EntityEventArgs;

        #endregion

        #region Internal Implementation and Helpers

        /// <summary>
        ///     Disposes all entities and clears all lists.
        /// </summary>
        public virtual void FlushEntities()
        {
            _sawmill.Info($"Flushing entities. Entity count: {_entities.Count}");
            BeforeEntityFlush?.Invoke();
            FlushEntitiesInternal();

            if (_entities.Count != 0)
            {
                _sawmill.Error($"Failed to flush all entities. Entity count: {_entities.Count}");
                foreach (var uid in _entities.Take(512))
                {
                    _sawmill.Error($"Entity exists after flush: {ToPrettyString(uid)}");
                }
#if EXCEPTION_TOLERANCE
                // Attempt to flush entities a second time, just in case something caused an entity to be spawned
                // while flushing entities
                FlushEntitiesInternal();
#endif
            }

            if (_entities.Count != 0)
                throw new Exception($"Failed to flush all entities. Entity count: {_entities.Count}");

            AfterEntityFlush?.Invoke();
        }

        private void FlushEntitiesInternal()
        {
            _queuedDeletions.Clear();
            _queuedDeletionsSet.Clear();

            // First, delete all map entities. This will recursively delete most other entities efficiently.
            var maps = AllEntityUids<MapComponent>().ToArray();
            foreach (var map in maps)
            {
                try
                {
                    DeleteEntity(map);
                }
                catch (Exception e)
                {
                    _sawmill.Log(LogLevel.Error, e, $"Caught exception while trying to delete map entity {ToPrettyString(map)}.");
                }
            }

            // Then delete any remaining entities that were not parented to a map.
            var remainingEnts = _entities.ToArray();
            foreach (var uid in remainingEnts)
            {
                try
                {
                    DeleteEntity(uid);
                }
                catch (Exception e)
                {
                    _sawmill.Log(LogLevel.Error, e, $"Caught exception while trying to delete entity {ToPrettyString(uid)}.");
                }
            }
        }
        internal EntityUid AllocEntity(out MetaDataComponent metadata)
        {
            ThreadCheck();

            var uid = GenerateEntityUid();

        #if DEBUG
            if (EntityExists(uid))
            {
                throw new InvalidOperationException($"UID already taken: {uid}");
            }
        #endif

            metadata = new MetaDataComponent
            {
                EntityLastModifiedTick = _gameTiming.CurTick
            };

            // FIX: Set the Owner property immediately after creation.
        #pragma warning disable CS0618 // Owner is obsolete, but this is the one place we must set it.
            metadata.Owner = uid;
        #pragma warning restore CS0618

            var netEntity = GenerateNetEntity();
            SetNetEntity(uid, netEntity, metadata);

            // This event will now succeed because metadata.Owner == uid.
            EntityAdded?.Invoke((uid, metadata));
            _eventBus.OnEntityAdded(uid);

            _entities.Add(uid);
            AddComponentInternal(uid, metadata, _metaReg, false, true, metadata);

            // While we're here, fix the same bug for TransformComponent.
            var xformComp = (TransformComponent)ComponentFactory.GetComponent(_xformReg);
        #pragma warning disable CS0618
            xformComp.Owner = uid;
        #pragma warning restore CS0618
            AddComponentInternal(uid, xformComp, _xformReg, false, true, metadata);

            return uid;
        }

        internal virtual EntityUid CreateEntity(string? prototypeName, out MetaDataComponent metadata, IEntityLoadContext? context = null)
        {
            if (prototypeName == null)
                return AllocEntity(out metadata);

            if (!PrototypeManager.TryIndex<EntityPrototype>(prototypeName, out var prototype))
                throw new EntityCreationException($"Attempted to spawn an entity with an invalid prototype: {prototypeName}");

            var entity = AllocEntity(out metadata);
            metadata._entityPrototype = prototype;
            try
            {
                EntityPrototype.LoadEntity((entity, metadata), ComponentFactory, this, _serManager, context);
                return entity;
            }
            catch (Exception e)
            {
                DeleteEntity(entity);
                throw new EntityCreationException($"Exception inside CreateEntity with prototype {prototype.ID}", e);
            }
        }

        public void InitializeAndStartEntity(EntityUid entity, MapId? mapId = null)
        {
            var doMapInit = mapId.HasValue && _mapSystem.IsInitialized(mapId.Value);
            InitializeAndStartEntity((entity, null), doMapInit);
        }

        public void InitializeAndStartEntity(Entity<MetaDataComponent?> entity, bool doMapInit)
        {
            if (!MetaQuery.Resolve(entity.Owner, ref entity.Comp))
                return;

            try
            {
                InitializeEntity(entity.Owner, entity.Comp);
                StartEntity(entity.Owner);

                if (doMapInit)
                    RunMapInit(entity.Owner, entity.Comp);
            }
            catch (Exception e)
            {
                DeleteEntity(entity);
                throw new EntityCreationException("Exception inside InitializeAndStartEntity", e);
            }
        }

        public void InitializeEntity(EntityUid entity, MetaDataComponent? meta = null)
        {
            DebugTools.AssertOwner(entity, meta);
            meta ??= GetComponent<MetaDataComponent>(entity);
#pragma warning disable CS0618 // Type or member is obsolete
            InitializeComponents(entity, meta);
#pragma warning restore CS0618 // Type or member is obsolete
            EntityInitialized?.Invoke((entity, meta));
        }

        public void StartEntity(EntityUid entity)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            StartComponents(entity);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        public void RunMapInit(EntityUid entity, MetaDataComponent meta)
        {
            if (meta.EntityLifeStage == EntityLifeStage.MapInitialized)
                return;

            DebugTools.Assert(meta.EntityLifeStage == EntityLifeStage.Initialized, $"Expected entity {ToPrettyString(entity)} to be initialized, was {meta.EntityLifeStage}");
            SetLifeStage(meta, EntityLifeStage.MapInitialized);

            EventBus.RaiseLocalEvent(entity, MapInitEventInstance);
        }

        [return: NotNullIfNotNull("uid")]
        public EntityStringRepresentation? ToPrettyString(EntityUid? uid, MetaDataComponent? metadata = null)
            => uid == null ? null : ToPrettyString(uid.Value, metadata);

        public EntityStringRepresentation ToPrettyString(EntityUid uid, MetaDataComponent? metadata)
            => ToPrettyString((uid, metadata));

        public EntityStringRepresentation ToPrettyString(Entity<MetaDataComponent?> entity)
        {
            if (entity.Comp == null && !MetaQuery.Resolve(entity.Owner, ref entity.Comp, false))
                return new EntityStringRepresentation(entity.Owner, default, true);

            return new EntityStringRepresentation(entity.Owner, entity.Comp, _actorQuery.CompOrNull(entity));
        }

        [return: NotNullIfNotNull("netEntity")]
        public EntityStringRepresentation? ToPrettyString(NetEntity? netEntity)
            => netEntity == null ? null : ToPrettyString(netEntity.Value);

        public EntityStringRepresentation ToPrettyString(NetEntity netEntity)
        {
            if (!TryGetEntityData(netEntity, out var uid, out var meta))
                return new EntityStringRepresentation(EntityUid.Invalid, netEntity, true);

            return ToPrettyString(uid.Value, meta);
        }

        internal EntityUid GenerateEntityUid() => new(NextEntityUid++);

        protected virtual NetEntity GenerateNetEntity() => new(NextNetworkId++);

        [Conditional("DEBUG")]
        protected void ThreadCheck()
        {
#if DEBUG
            DebugTools.Assert(
                Environment.CurrentManagedThreadId == _mainThreadId,
                "Attempted to use the EntityManager from a thread other than the main thread.");
#endif
        }

        #endregion

    }

}
