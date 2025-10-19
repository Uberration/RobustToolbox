// Filename: Filter.cs

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;

#nullable enable

namespace Robust.Shared.Player
{
    /// <summary>
    /// Contains a set of player sessions to which a networked message or event will be sent.
    /// This class provides a fluent interface for building recipient lists.
    /// </summary>
    [PublicAPI]
    public sealed class Filter
    {
        #region Internal State and Properties

        private HashSet<ICommonSession> _recipients;

        /// <summary>
        /// If true, the event will be sent reliably. Defaults to false (unreliable).
        /// </summary>
        public bool SendReliable { get; private set; }

        /// <summary>
        /// If true (default), the event will be suppressed on clients during re-prediction ticks.
        /// </summary>
        public bool CheckPrediction { get; private set; } = true;

        /// <summary>
        /// The number of player sessions currently in this filter.
        /// </summary>
        public int Count => _recipients.Count;

        /// <summary>
        /// An enumerable collection of the player sessions in this filter.
        /// </summary>
        public IEnumerable<ICommonSession> Recipients => _recipients;

        private Filter()
        {
            _recipients = new HashSet<ICommonSession>();
        }

        #endregion

        #region Factory Methods

        /// <summary>
        /// Creates a new, empty filter.
        /// </summary>
        public static Filter Empty()
        {
            return new();
        }

        /// <summary>
        /// Creates a new filter containing a single player session.
        /// </summary>
        public static Filter SinglePlayer(ICommonSession player)
        {
            return Empty().AddPlayer(player);
        }

        /// <summary>
        /// Creates a new filter containing all currently connected player sessions.
        /// </summary>
        public static Filter Broadcast(ISharedPlayerManager? playerManager = null)
        {
            return Empty().AddAllPlayers(playerManager);
        }

        /// <summary>
        /// Creates a new filter containing all players whose attached entities are on a specific grid.
        /// </summary>
        public static Filter BroadcastGrid(EntityUid grid)
        {
            return Empty().AddInGrid(grid);
        }

        /// <summary>
        /// Creates a new filter containing all players whose attached entities are on a specific map.
        /// </summary>
        public static Filter BroadcastMap(MapId map)
        {
            return Empty().AddInMap(map);
        }

        /// <summary>
        /// Creates a new filter containing all players within the PVS (Potentially Visible Set) of an entity.
        /// </summary>
        public static Filter Pvs(EntityUid origin, float rangeMultiplier = 2f, IEntityManager? entityManager = null, ISharedPlayerManager? playerManager = null, IConfigurationManager? cfgManager = null)
        {
            return Empty().AddPlayersByPvs(origin, rangeMultiplier, entityManager, playerManager, cfgManager);
        }

        /// <summary>
        /// Creates a new filter containing all players within the PVS (Potentially Visible Set) of a specific coordinate.
        /// </summary>
        public static Filter Pvs(EntityCoordinates origin, float rangeMultiplier = 2f, IEntityManager? entityMan = null, ISharedPlayerManager? playerMan = null)
        {
            return Empty().AddPlayersByPvs(origin, rangeMultiplier, entityMan, playerMan);
        }

        /// <summary>
        /// Creates a new filter containing all players within the PVS (Potentially Visible Set) of a specific map coordinate.
        /// </summary>
        public static Filter Pvs(MapCoordinates origin, float rangeMultiplier = 2f)
        {
            return Empty().AddPlayersByPvs(origin, rangeMultiplier);
        }

        /// <summary>
        /// Creates a new filter containing all players within the PVS of an entity, excluding the player attached to that entity.
        /// </summary>
        public static Filter PvsExcept(EntityUid origin, float rangeMultiplier = 2f, IEntityManager? entityManager = null)
        {
            return Pvs(origin, rangeMultiplier, entityManager).RemovePlayerByAttachedEntity(origin);
        }

        /// <summary>
        /// Creates a new filter containing all players attached to the specified entities.
        /// </summary>
        public Filter FromEntities(params EntityUid[] entities)
        {
            // The modern, recommended way to get a system.
            var entityMan = IoCManager.Resolve<IEntityManager>();
            var filterSystem = entityMan.System<SharedFilterSystem>();
            return filterSystem.FromEntities(this, entities);
        }

        /// <summary>
        /// Creates a new filter that only targets the local player. On the server, this is an empty filter.
        /// </summary>
        public static Filter Local()
        {
            // On the server, there is no "local" player, so this is always empty.
            // The client-side implementation of networking systems interprets this correctly.
            return Empty();
        }

        /// <summary>
        /// Creates a new filter containing all players attached to the specified entities.
        /// </summary>
        public static Filter Entities(params EntityUid[] entities)
        {
            return Empty().FromEntities(entities);
        }

        /// <summary>
        /// A static helper to get all currently connected player sessions.
        /// Equivalent to resolving ISharedPlayerManager and accessing NetworkedSessions.
        /// </summary>
        public static IEnumerable<ICommonSession> GetAllPlayers(ISharedPlayerManager? playerManager = null)
        {
            IoCManager.Resolve(ref playerManager);
            return playerManager!.NetworkedSessions;
        }

        #endregion

        #region Fluent "Add" Methods

        /// <summary>
        /// Adds a single player session to the filter.
        /// </summary>
        public Filter AddPlayer(ICommonSession player)
        {
            _recipients.Add(player);
            return this;
        }

        /// <summary>
        /// Adds a collection of player sessions to the filter.
        /// </summary>
        public Filter AddPlayers(IEnumerable<ICommonSession> players)
        {
            _recipients.UnionWith(players);
            return this;
        }

        /// <summary>
        /// Adds all currently connected player sessions to the filter.
        /// </summary>
        public Filter AddAllPlayers(ISharedPlayerManager? playerMan = null)
        {
            IoCManager.Resolve(ref playerMan);
            return AddPlayers(playerMan.NetworkedSessions);
        }

        /// <summary>
        /// Adds all player sessions that match a given predicate.
        /// </summary>
        public Filter AddWhere(Predicate<ICommonSession> predicate, ISharedPlayerManager? playerMan = null)
        {
            IoCManager.Resolve(ref playerMan);
            foreach (var player in playerMan.NetworkedSessions)
            {
                if (predicate(player))
                {
                    AddPlayer(player);
                }
            }
            return this;
        }

        /// <summary>
        /// Adds all players whose attached entity matches a predicate.
        /// Players without an attached entity are ignored.
        /// </summary>
        public Filter AddWhereAttachedEntity(Predicate<EntityUid> predicate)
        {
            return AddWhere(session => session.AttachedEntity is { } uid && predicate(uid));
        }

        /// <summary>
        /// Adds all players within the Potentially Visible Set (PVS) of an entity.
        /// </summary>
        public Filter AddPlayersByPvs(EntityUid origin, float rangeMultiplier = 2f, IEntityManager? entityManager = null, ISharedPlayerManager? playerMan = null, IConfigurationManager? cfgMan = null)
        {
            IoCManager.Resolve(ref entityManager, ref playerMan, ref cfgMan);

            // CRITICAL FIX: Use TryGetComponent to safely handle cases where the origin entity has been deleted.
            // This prevents a KeyNotFoundException if an event handler deletes an entity before
            // another handler tries to get its position.
            if (!entityManager.TryGetComponent<TransformComponent>(origin, out var xform))
            {
                // The entity doesn't exist anymore, so no players can be in its PVS.
                return this;
            }

            var transformSystem = entityManager.System<SharedTransformSystem>();
            return AddPlayersByPvs(transformSystem.GetMapCoordinates(xform), rangeMultiplier, entityManager, playerMan, cfgMan);
        }

        /// <summary>
        /// Adds all players within the PVS of a specific coordinate.
        /// </summary>
        public Filter AddPlayersByPvs(EntityCoordinates origin, float rangeMultiplier = 2f, IEntityManager? entityMan = null, ISharedPlayerManager? playerMan = null, IConfigurationManager? cfgMan = null)
        {
            IoCManager.Resolve(ref entityMan, ref playerMan, ref cfgMan);
            var system = entityMan.System<SharedTransformSystem>();
            return AddPlayersByPvs(system.ToMapCoordinates(origin), rangeMultiplier, entityMan, playerMan, cfgMan);
        }

        /// <summary>
        /// Adds all players within the PVS of a specific map coordinate.
        /// </summary>
        public Filter AddPlayersByPvs(MapCoordinates origin, float rangeMultiplier = 2f, IEntityManager? entManager = null, ISharedPlayerManager? playerMan = null, IConfigurationManager? cfgMan = null)
        {
            IoCManager.Resolve(ref entManager, ref playerMan, ref cfgMan);

            // If PVS is disabled, we simply add all players.
            if (!cfgMan.GetCVar(CVars.NetPVS))
                return AddAllPlayers(playerMan);

            var pvsRange = cfgMan.GetCVar(CVars.NetMaxUpdateRange) * rangeMultiplier;
            return AddInRange(origin, pvsRange, playerMan, entManager);
        }

        /// <summary>
        /// Adds all players whose attached entities are within a specified range of a map coordinate.
        /// </summary>
        public Filter AddInRange(MapCoordinates position, float range, ISharedPlayerManager? playerMan = null, IEntityManager? entMan = null)
        {
            IoCManager.Resolve(ref playerMan, ref entMan);
            var xformQuery = entMan.GetEntityQuery<TransformComponent>();
            var xformSystem = entMan.System<SharedTransformSystem>();
            var rangeSquared = range * range;

            return AddWhere(session =>
                session.AttachedEntity != null &&
                xformQuery.TryGetComponent(session.AttachedEntity.Value, out var xform) &&
                xform.MapID == position.MapId &&
                (xformSystem.GetWorldPosition(xform) - position.Position).LengthSquared() < rangeSquared, playerMan);
        }

        /// <summary>
        /// Adds all players whose attached entity is on a certain grid.
        /// </summary>
        public Filter AddInGrid(EntityUid uid, IEntityManager? entMan = null)
        {
            IoCManager.Resolve(ref entMan);
            var xformQuery = entMan.GetEntityQuery<TransformComponent>();
            return AddWhereAttachedEntity(entity => xformQuery.TryGetComponent(entity, out var xform) && xform.GridUid == uid);
        }

        /// <summary>
        /// Adds all players whose attached entity is on a certain map.
        /// </summary>
        public Filter AddInMap(MapId mapId, IEntityManager? entMan = null)
        {
            IoCManager.Resolve(ref entMan);
            var xformQuery = entMan.GetEntityQuery<TransformComponent>();
            return AddWhereAttachedEntity(entity => xformQuery.TryGetComponent(entity, out var xform) && xform.MapID == mapId);
        }

        #endregion

        #region Fluent "Remove" Methods

        /// <summary>
        /// Removes a single player session from the filter.
        /// </summary>
        public Filter RemovePlayer(ICommonSession player)
        {
            _recipients.Remove(player);
            return this;
        }

        /// <summary>
        /// Removes a collection of player sessions from the filter.
        /// </summary>
        public Filter RemovePlayers(IEnumerable<ICommonSession> players)
        {
            foreach (var player in players)
                _recipients.Remove(player);
            return this;
        }

        /// <summary>
        /// Removes a collection of player sessions from the filter.
        /// </summary>
        public Filter RemovePlayers(params ICommonSession[] players) => RemovePlayers((IEnumerable<ICommonSession>)players);

        /// <summary>
        /// Removes all player sessions from the filter that match a predicate.
        /// </summary>
        public Filter RemoveWhere(Predicate<ICommonSession> predicate)
        {
            _recipients.RemoveWhere(predicate);
            return this;
        }

        /// <summary>
        /// Removes all players whose attached entity matches a predicate.
        /// </summary>
        public Filter RemoveWhereAttachedEntity(Predicate<EntityUid> predicate)
        {
            return RemoveWhere(session => session.AttachedEntity is { } uid && predicate(uid));
        }

        /// <summary>
        /// Removes a single player from the filter, specified by the entity to which they are attached.
        /// </summary>
        public Filter RemovePlayerByAttachedEntity(EntityUid uid)
        {
            return RemoveWhereAttachedEntity(e => e == uid);
        }

        /// <summary>
        /// Removes all players attached to the specified entities.
        /// </summary>
        public Filter RemovePlayersByAttachedEntity(IEnumerable<EntityUid> uids)
        {
            var uidSet = uids.ToHashSet();
            return RemoveWhereAttachedEntity(uidSet.Contains);
        }

        /// <summary>
        /// Removes all players attached to the specified entities.
        /// </summary>
        public Filter RemovePlayersByAttachedEntity(params EntityUid[] uids) => RemovePlayersByAttachedEntity((IEnumerable<EntityUid>)uids);

        /// <summary>
        /// Removes all players whose attached entities are within a specified range of a map coordinate.
        /// </summary>
        public Filter RemoveInRange(MapCoordinates position, float range, IEntityManager? entMan = null)
        {
            IoCManager.Resolve(ref entMan);
            var xformQuery = entMan.GetEntityQuery<TransformComponent>();
            var xformSystem = entMan.System<SharedTransformSystem>();
            var rangeSquared = range * range;

            return RemoveWhere(session =>
                session.AttachedEntity != null &&
                xformQuery.TryGetComponent(session.AttachedEntity.Value, out var xform) &&
                xform.MapID == position.MapId &&
                (xformSystem.GetWorldPosition(xform) - position.Position).LengthSquared() < rangeSquared);
        }

        /// <summary>
        /// Removes all players who do not have the specified visibility flag in their attached entity's <see cref="EyeComponent"/>.
        /// </summary>
        public Filter RemoveByVisibility(uint flag, IEntityManager? entMan = null)
        {
            IoCManager.Resolve(ref entMan);
            var eyeQuery = entMan.GetEntityQuery<EyeComponent>();

            return RemoveWhere(session =>
                session.AttachedEntity == null
                || !eyeQuery.TryGetComponent(session.AttachedEntity, out var eye)
                || (eye.VisibilityMask & flag) == 0);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Adds all players from another filter into this one.
        /// </summary>
        public Filter Merge(Filter other)
        {
            return AddPlayers(other.Recipients);
        }

        /// <summary>
        /// Returns a new filter with an identical set of recipients and configuration.
        /// </summary>
        public Filter Clone()
        {
            return new()
            {
                _recipients = new HashSet<ICommonSession>(_recipients),
                SendReliable = SendReliable,
                CheckPrediction = CheckPrediction,
            };
        }

        /// <summary>
        /// Configures the filter to send its message reliably.
        /// </summary>
        public Filter SendReliably()
        {
            SendReliable = true;
            return this;
        }

        /// <summary>
        /// Disables prediction checking for this filter. The event will be raised on clients
        /// on every tick, including re-prediction ticks. Use this sparingly.
        /// </summary>
        public Filter Unpredicted()
        {
            CheckPrediction = false;
            return this;
        }

        #endregion
    }
}
