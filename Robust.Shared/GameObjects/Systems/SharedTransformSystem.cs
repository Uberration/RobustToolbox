// Filename: Robust.Shared/GameObjects/SharedTransformSystem.cs

using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

#nullable enable

namespace Robust.Shared.GameObjects
{
    /// <summary>
    /// This system is responsible for managing the position, rotation, and parenting of all entities.
    /// It is the heart of the scene graph and spatial relationships in the game world.
    /// </summary>
    public abstract partial class SharedTransformSystem : EntitySystem
    {
        #region Dependencies and State

        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly EntityLookupSystem _lookup = default!;
        [Dependency] private readonly SharedMapSystem _map = default!;
        [Dependency] private readonly MetaDataSystem _metaData = default!;
        [Dependency] private readonly SharedPhysicsSystem _physics = default!;
        [Dependency] private readonly INetManager _netMan = default!;
        [Dependency] private readonly SharedContainerSystem _container = default!;
        [Dependency] private readonly SharedGridTraversalSystem _traversal = default!;

        private EntityQuery<MapComponent> _mapQuery;
        private EntityQuery<MapGridComponent> _gridQuery;
        private EntityQuery<MetaDataComponent> _metaQuery;
        protected EntityQuery<TransformComponent> XformQuery;

        public delegate void MoveEventHandler(ref MoveEvent ev);

        /// <summary>
        /// Invoked whenever any entity's transform changes. This is a global, potentially expensive event.
        /// High-performance systems should prefer directed subscriptions to <see cref="MoveEvent"/>.
        /// </summary>
        public event MoveEventHandler? OnGlobalMoveEvent;

        /// <summary>
        /// Internal move event for engine systems. This is invoked before other move events to ensure
        /// critical systems like PVS and Physics are updated first.
        /// </summary>
        internal event MoveEventHandler? OnBeforeMoveEvent;

        #endregion

        #region Initialization and Event Subscriptions

        public override void Initialize()
        {
            base.Initialize();

            UpdatesOutsidePrediction = true;

            _mapQuery = GetEntityQuery<MapComponent>();
            _gridQuery = GetEntityQuery<MapGridComponent>();
            _metaQuery = GetEntityQuery<MetaDataComponent>();
            XformQuery = GetEntityQuery<TransformComponent>();

            // Broadcast event, passed by ref
            SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);

            // Directed entity events, passed by ref.
            // Using EntityEventRefHandler matches the (Entity<T>, ref TEvent) signature.
            SubscribeLocalEvent(new EntityEventRefHandler<TransformComponent, ComponentInit>(OnCompInit));
            SubscribeLocalEvent(new EntityEventRefHandler<TransformComponent, ComponentStartup>(OnCompStartup));
            SubscribeLocalEvent(new EntityEventRefHandler<TransformComponent, ComponentGetState>(OnGetState));
            SubscribeLocalEvent(new EntityEventRefHandler<TransformComponent, ComponentHandleState>(OnHandleState));
            SubscribeLocalEvent(new EntityEventRefHandler<TransformComponent, GridAddEvent>(OnGridAdd));
        }

        private void OnTileChanged(ref TileChangedEvent e)
        {
            foreach (var change in e.Changes)
            {
                if (change.NewTile != Tile.Empty)
                    continue;

                // When a tile is removed (e.g., by an explosion), we need to de-parent any entities
                // that were anchored to it.
                DeparentAllEntsOnTile(e.Entity, change.GridIndices);
            }
        }

        #endregion

        #region Coordinate and Position Helpers

        /// <summary>
        /// Gets the coordinates used by the mover systems, which may be relative to a grid, map, or another entity.
        /// </summary>
        public EntityCoordinates GetMoverCoordinates(EntityUid uid, TransformComponent? xform = null)
        {
            if (!Resolve(uid, ref xform))
                return EntityCoordinates.Invalid;

            // Nullspace, map, or grid-parented entities are already in mover-compatible coordinates.
            if (!xform.ParentUid.IsValid() || xform._gridInitialized && xform.GridUid == xform.ParentUid)
                return xform.Coordinates;

            if (!xform._gridInitialized)
                InitializeGridUid(uid, xform);

            // The entity is parented to something else. We need to convert its position
            // into the coordinate system of its parent grid (or map if not on a grid).
            var worldPos = GetWorldPosition(xform, XformQuery);
            if (xform.GridUid is not { } gridUid)
                return new EntityCoordinates(xform.MapUid ?? xform.ParentUid, worldPos);

            var gridXform = XformQuery.GetComponent(gridUid);
            var localPos = Vector2.Transform(worldPos, gridXform.InvLocalMatrix);
            return new EntityCoordinates(gridUid, localPos);
        }

        /// <summary>
        /// Variant of <see cref="GetMoverCoordinates(EntityUid, TransformComponent?)"/> that uses an <see cref="EntityCoordinates"/>.
        /// </summary>
        public EntityCoordinates GetMoverCoordinates(EntityCoordinates coordinates)
        {
            var parentUid = coordinates.EntityId;
            if (!parentUid.IsValid() || !XformQuery.TryGetComponent(parentUid, out var parentXform))
                return coordinates;

            if (!parentXform._gridInitialized)
                InitializeGridUid(parentUid, parentXform);

            if (parentXform.GridUid == parentUid || parentXform.MapUid == parentUid)
                return coordinates;

            var worldPos = Vector2.Transform(coordinates.Position, GetWorldMatrix(parentXform, XformQuery));

            if (parentXform.GridUid is not { } gridUid)
                return new EntityCoordinates(parentXform.MapUid ?? parentUid, worldPos);

            var gridXform = XformQuery.GetComponent(gridUid);
            var localPos = Vector2.Transform(worldPos, gridXform.InvLocalMatrix);
            return new EntityCoordinates(gridUid, localPos);
        }

        /// <summary>
        /// Variant of <see cref="GetMoverCoordinates()"/> that also returns the entity's world rotation.
        /// </summary>
        public (EntityCoordinates Coords, Angle WorldRot) GetMoverCoordinateRotation(EntityUid uid, TransformComponent? xform = null)
        {
            if (!Resolve(uid, ref xform))
                return (EntityCoordinates.Invalid, Angle.Zero);

            return (GetMoverCoordinates(uid, xform), GetWorldRotation(xform));
        }

        /// <summary>
        /// Helper method that returns the integer coordinates of the grid or map tile an entity is on.
        /// </summary>
        public Vector2i GetGridOrMapTilePosition(EntityUid uid, TransformComponent? xform = null)
        {
            if (!Resolve(uid, ref xform, false))
                return Vector2i.Zero;

            if (xform.GridUid is not { } gridUid || !TryComp(gridUid, out MapGridComponent? grid))
                return GetWorldPosition(xform).Floored();

            return _map.CoordinatesToTile(gridUid, grid, xform.Coordinates);
        }

        /// <summary>
        /// Helper method that returns the grid tile an entity is on, or a default value if not on a grid.
        /// </summary>
        public Vector2i GetGridTilePositionOrDefault(Entity<TransformComponent?> entity, MapGridComponent? grid = null)
        {
            if (TryGetGridTilePosition(entity, out var indices, grid))
                return indices;
            return Vector2i.Zero;
        }

        /// <summary>
        /// Tries to get the grid tile an entity is on.
        /// </summary>
        public bool TryGetGridTilePosition(Entity<TransformComponent?> entity, out Vector2i indices, MapGridComponent? grid = null)
        {
            indices = default;
            if (!Resolve(entity.Owner, ref entity.Comp) || entity.Comp.GridUid is not { } gridUid)
                return false;

            if (!Resolve(gridUid, ref grid))
                return false;

            indices = _map.CoordinatesToTile(gridUid, grid, entity.Comp.Coordinates);
            return true;
        }

        #endregion

        #region Internal Logic and Event Raising

        /// <summary>
        /// This is the core method for raising move events. It constructs the event and dispatches it
        /// to internal, directed, and global subscribers in the correct order.
        /// </summary>
        internal void RaiseMoveEvent(
            Entity<TransformComponent, MetaDataComponent> ent,
            EntityUid oldParent,
            Vector2 oldPosition,
            Angle oldRotation,
            EntityUid? oldMap,
            bool checkTraversal = true)
        {
            var newParent = ent.Comp1.ParentUid;
            var newCoords = newParent.IsValid()
                ? new EntityCoordinates(newParent, ent.Comp1.LocalPosition)
                : default;

            var oldCoords = oldParent.IsValid()
                ? new EntityCoordinates(oldParent, oldPosition)
                : default;

            var moveEvent = new MoveEvent(ent, oldCoords, newCoords, oldRotation, ent.Comp1.LocalRotation);

            // 1. Raise internal engine events first to ensure physics and PVS are up-to-date.
            OnBeforeMoveEvent?.Invoke(ref moveEvent);

            // 2. If the parent changed, update physics and raise the specific parent changed event.
            if (oldParent != newParent)
            {
                _physics.OnParentChange(ent, oldParent, oldMap);
                var parentChangedEvent = new EntParentChangedMessage(moveEvent.Sender, oldParent, oldMap, moveEvent.Component);
                RaiseLocalEvent(moveEvent.Sender, ref parentChangedEvent, true);
            }

            // 3. Raise the directed MoveEvent on the entity itself.
            RaiseLocalEvent(moveEvent.Sender, ref moveEvent);

            // 4. Raise the global MoveEvent for any broadcast subscribers.
            OnGlobalMoveEvent?.Invoke(ref moveEvent);

            // 5. Finally, check for grid traversal after all other move logic has completed.
            if (checkTraversal)
            {
                _traversal.CheckTraverse(ent);
            }
        }

        /// <summary>
        /// De-parents and unanchors all entities on a grid tile that has been removed (set to space).
        /// </summary>
        private void DeparentAllEntsOnTile(EntityUid gridId, Vector2i tileIndices)
        {
            if (!TryComp(gridId, out BroadphaseComponent? lookup) || !TryComp<MapGridComponent>(gridId, out var grid))
                return;

            if (!XformQuery.TryGetComponent(gridId, out var gridXform) || gridXform.MapUid is not { } mapUid)
                return;

            if (!XformQuery.TryGetComponent(mapUid, out var mapTransform))
                return;

            var aabb = _lookup.GetLocalBounds(tileIndices, grid.TileSize);

            // Find all entities intersecting the bounds of the removed tile.
            foreach (var entity in _lookup.GetLocalEntitiesIntersecting(lookup, aabb, LookupFlags.Uncontained | LookupFlags.Approximate))
            {
                if (!XformQuery.TryGetComponent(entity, out var xform) || xform.ParentUid != gridId)
                    continue;

                // Ensure the entity is actually within the tile's AABB before deparenting.
                if (!aabb.Contains(xform.LocalPosition))
                    continue;

                // If an entity is already being deleted (e.g., by the same explosion that removed the tile),
                // just detach it to null-space to avoid unnecessary reparenting logic.
                if (EntityManager.IsQueuedForDeletion(entity))
                    // The MetaData() helper now exists on the base EntitySystem.
                    DetachEntity(entity, xform, MetaData(entity), gridXform);
                else
                    SetParent(entity, xform, gridXform.MapUid.Value, mapTransform);
            }
        }

        #endregion

        #region Component Lifecycle and Networking

        private void OnCompInit(Entity<TransformComponent> ent, ref ComponentInit args)
        {
            var xform = ent.Comp;
            if (xform.ParentUid.IsValid())
            {
                // This can happen if the entity is spawned and parented in the same tick before initialization.
                // We need to ensure it's correctly added to the parent's child list.
                if (XformQuery.TryGetComponent(xform.ParentUid, out var parentXform))
                {
                    parentXform._children.Add(ent.Owner);
                    Dirty(xform.ParentUid, parentXform);
                }
            }
        }

        private void OnCompStartup(Entity<TransformComponent> ent, ref ComponentStartup args)
        {
            // After startup, we can definitively determine the entity's map and grid UIDs.
            // This is deferred until startup because the entity's parent might not have been
            // fully initialized until this point.
            var xform = ent.Comp;
            if (!xform._mapIdInitialized)
                InitializeMapUid(ent.Owner, xform);
            if (!xform._gridInitialized)
                InitializeGridUid(ent.Owner, xform);

            var ev = new TransformStartupEvent(ent);
            RaiseLocalEvent(ent, ref ev);
        }

        private void OnGetState(Entity<TransformComponent> ent, ref ComponentGetState args)
        {
            var xform = ent.Comp;
            args.State = new TransformComponentState(
                xform.LocalPosition,
                xform.LocalRotation,
                GetNetEntity(xform.ParentUid),
                xform.NoLocalRotation,
                xform.Anchored);
        }

        private void OnHandleState(Entity<TransformComponent> ent, ref ComponentHandleState args)
        {
            if (args.Current is not TransformComponentState state)
                return;

            var xform = ent.Comp;
            if (xform.NoLocalRotation)
                SetLocalRotation(ent.Owner, state.Rotation, xform);

            // This is an optimization. The state handling for parenting and positioning is complex
            // and is handled by the ClientGame State Manager directly, which sorts entities by
            // their transform hierarchy before applying states. Setting them here would be redundant
            // and could cause issues with out-of-order application.
        }

        private void OnGridAdd(Entity<TransformComponent> ent, ref GridAddEvent args)
        {
            // When an entity becomes a grid, it must be detached from any parent and become a root object on the map.
            if (ent.Comp.ParentUid != ent.Comp.MapUid && TryComp(ent.Comp.MapUid, out TransformComponent? mapXform))
                SetParent(ent, ent.Comp, ent.Comp.MapUid.Value, mapXform);
        }

        #endregion
    }

    // --- Event and State Definitions ---

    [ByRefEvent]
    public readonly struct TransformStartupEvent
    {
        public readonly Entity<TransformComponent> Entity;
        public TransformComponent Component => Entity.Comp;

        public TransformStartupEvent(Entity<TransformComponent> entity)
        {
            Entity = entity;
        }
    }

    /// <summary>
    /// Networked state of a TransformComponent.
    /// </summary>
    [Serializable, NetSerializable]
    internal readonly record struct TransformComponentState(
        Vector2 LocalPosition,
        Angle Rotation,
        NetEntity ParentID,
        bool NoLocalRotation,
        bool Anchored) : IComponentState;
}
