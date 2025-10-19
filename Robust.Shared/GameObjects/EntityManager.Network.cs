// Filename: EntityManager.Network.cs

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Utility;

#nullable enable

namespace Robust.Shared.GameObjects
{
    /// <summary>
    /// This partial class of EntityManager contains all logic related to networking,
    /// including the translation between EntityUid, NetEntity, and their coordinate counterparts.
    /// </summary>
    public partial class EntityManager
    {
        #region Internal Network State and Management

        /// <summary>
        /// Inverse lookup from a NetEntity to its corresponding EntityUid and MetaDataComponent.
        /// </summary>
        protected readonly Dictionary<NetEntity, (EntityUid, MetaDataComponent)> NetEntityLookup = new(EntityCapacity);

        /// <summary>
        /// Clears an old inverse lookup for a particular entity.
        /// Should only be called during entity deletion.
        /// </summary>
        internal void ClearNetEntity(NetEntity netEntity)
        {
            NetEntityLookup.Remove(netEntity);
        }

        /// <summary>
        /// Sets the inverse lookup for a newly created entity.
        /// </summary>
        internal void SetNetEntity(EntityUid uid, NetEntity netEntity, MetaDataComponent component)
        {
            DebugTools.Assert(component.NetEntity == NetEntity.Invalid || _netMan.IsClient);
            DebugTools.Assert(!NetEntityLookup.ContainsKey(netEntity));
            NetEntityLookup[netEntity] = (uid, component);
            component.NetEntity = netEntity;
        }

        /// <inheritdoc />
        public virtual bool IsClientSide(EntityUid uid, MetaDataComponent? metadata = null)
        {
            // This is false on the server. The client implementation overrides this to return true
            // for client-side entities.
            return false;
        }

        #endregion

        #region NetEntity <-> EntityUid Translation

        /// <inheritdoc />
        public bool TryParseNetEntity(string arg, [NotNullWhen(true)] out EntityUid? entity)
        {
            if (!NetEntity.TryParse(arg, out var netEntity) || !TryGetEntity(netEntity, out entity))
            {
                entity = null;
                return false;
            }
            return true;
        }

        /// <inheritdoc />
        public bool TryGetEntity(NetEntity nEntity, [NotNullWhen(true)] out EntityUid? entity)
        {
            if (NetEntityLookup.TryGetValue(nEntity, out var went))
            {
                entity = went.Item1;
                return true;
            }

            entity = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetEntityData(NetEntity nEntity, [NotNullWhen(true)] out EntityUid? entity, [NotNullWhen(true)] out MetaDataComponent? meta)
        {
            if (NetEntityLookup.TryGetValue(nEntity, out var went))
            {
                entity = went.Item1;
                meta = went.Item2;
                return true;
            }

            entity = null;
            meta = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetEntity(NetEntity? nEntity, [NotNullWhen(true)] out EntityUid? entity)
        {
            if (nEntity == null)
            {
                entity = null;
                return false;
            }
            return TryGetEntity(nEntity.Value, out entity);
        }

        /// <inheritdoc />
        public bool TryGetNetEntity(EntityUid uid, [NotNullWhen(true)] out NetEntity? netEntity, MetaDataComponent? metadata = null)
        {
            if (MetaQuery.TryGetComponent(uid, out metadata))
            {
                netEntity = metadata.NetEntity;
                return true;
            }

            netEntity = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetNetEntity(EntityUid? uid, [NotNullWhen(true)] out NetEntity? netEntity, MetaDataComponent? metadata = null)
        {
            if (uid == null)
            {
                netEntity = null;
                return false;
            }
            return TryGetNetEntity(uid.Value, out netEntity, metadata);
        }

        /// <inheritdoc />
        public EntityUid GetEntity(NetEntity nEntity)
        {
            if (NetEntityLookup.TryGetValue(nEntity, out var tuple))
                return tuple.Item1;

            return EntityUid.Invalid;
        }

        public (EntityUid, MetaDataComponent) GetEntityData(NetEntity nEntity) => NetEntityLookup[nEntity];

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityUid? GetEntity(NetEntity? nEntity)
        {
            if (nEntity == null)
                return null;

            return GetEntity(nEntity.Value);
        }

        /// <inheritdoc />
        public NetEntity GetNetEntity(EntityUid uid)
        {
            if (TryGetNetEntity(uid, out var netEntity))
                return netEntity.Value;

            return NetEntity.Invalid;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NetEntity? GetNetEntity(EntityUid? uid)
        {
            if (uid == null)
                return null;

            return GetNetEntity(uid.Value);
        }

        /// <summary>
        /// A specialized, multi-thread safe version of GetNetEntity used during game state serialization.
        /// It is robust against the entity being deleted by another thread during processing.
        /// </summary>
        public NetEntity GetNetEntity(EntityUid uid, MetaDataComponent? metadata)
        {
            // CRITICAL FIX: This method is now race-condition-proof.
            // If the entity is deleted by another thread while PVS is processing,
            // this will safely return NetEntity.Invalid instead of crashing.
            if (MetaQuery.TryGetComponent(uid, out metadata))
                return metadata.NetEntity;

            return NetEntity.Invalid;
        }

        [return: NotNullIfNotNull("uid")]
        public NetEntity? GetNetEntity(EntityUid? uid, MetaDataComponent? metadata)
        {
            if (uid == null)
                return null;

            return GetNetEntity(uid.Value, metadata);
        }

        #endregion

        #region NetCoordinates <-> EntityCoordinates Translation

        /// <inheritdoc />
        public NetCoordinates GetNetCoordinates(EntityCoordinates coordinates, MetaDataComponent? metadata = null)
        {
            // Pass the metadata hint to the GetNetEntity call for correctness.
            return new NetCoordinates(GetNetEntity(coordinates.EntityId, metadata), coordinates.Position);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NetCoordinates? GetNetCoordinates(EntityCoordinates? coordinates, MetaDataComponent? metadata = null)
        {
            if (coordinates == null)
                return null;

            // Call the correct overload that accepts the metadata hint.
            return GetNetCoordinates(coordinates.Value, metadata);
        }

        /// <inheritdoc />
        public EntityCoordinates GetCoordinates(NetCoordinates coordinates)
        {
            return new EntityCoordinates(GetEntity(coordinates.NetEntity), coordinates.Position);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityCoordinates? GetCoordinates(NetCoordinates? coordinates)
        {
            if (coordinates == null)
                return null;

            return GetCoordinates(coordinates.Value);
        }

        #endregion

        #region Client-side "Ensure" Methods

        /// <inheritdoc />
        public virtual EntityUid EnsureEntity<T>(NetEntity nEntity, EntityUid callerEntity)
        {
            // On the server, we don't need to "ensure" anything; the entity either exists or it doesn't.
            // The client overrides this with logic to handle pending entity states.
            return GetEntity(nEntity);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityUid? EnsureEntity<T>(NetEntity? nEntity, EntityUid callerEntity)
        {
            if (nEntity == null)
                return null;

            return EnsureEntity<T>(nEntity.Value, callerEntity);
        }

        /// <inheritdoc />
        public virtual EntityCoordinates EnsureCoordinates<T>(NetCoordinates netCoordinates, EntityUid callerEntity)
        {
            // See EnsureEntity for explanation.
            return GetCoordinates(netCoordinates);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityCoordinates? EnsureCoordinates<T>(NetCoordinates? netCoordinates, EntityUid callerEntity)
        {
            if (netCoordinates == null)
                return null;

            return EnsureCoordinates<T>(netCoordinates.Value, callerEntity);
        }

        #endregion

        #region Collection Conversion Helpers

        #region GetEntity Collections (NetEntity -> EntityUid)

        /// <inheritdoc />
        public HashSet<EntityUid> GetEntitySet(HashSet<NetEntity> netEntities)
        {
            var entities = new HashSet<EntityUid>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(GetEntity(netEntity));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityUid> GetEntityList(List<NetEntity> netEntities)
        {
            var entities = new List<EntityUid>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(GetEntity(netEntity));
            return entities;
        }

        public Dictionary<EntityUid, T> GetEntityDictionary<T>(Dictionary<NetEntity, T> netEntities)
        {
            var entities = new Dictionary<EntityUid, T>(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.Add(GetEntity(key), value);
            return entities;
        }

        public Dictionary<T, EntityUid> GetEntityDictionary<T>(Dictionary<T, NetEntity> netEntities) where T : notnull
        {
            var entities = new Dictionary<T, EntityUid>(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.Add(key, GetEntity(value));
            return entities;
        }

        public Dictionary<T, EntityUid?> GetEntityDictionary<T>(Dictionary<T, NetEntity?> netEntities) where T : notnull
        {
            var entities = new Dictionary<T, EntityUid?>(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.Add(key, GetEntity(value));
            return entities;
        }

        public Dictionary<EntityUid, EntityUid> GetEntityDictionary(Dictionary<NetEntity, NetEntity> netEntities)
        {
            var entities = new Dictionary<EntityUid, EntityUid>(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.Add(GetEntity(key), GetEntity(value));
            return entities;
        }

        public Dictionary<EntityUid, EntityUid?> GetEntityDictionary(Dictionary<NetEntity, NetEntity?> netEntities)
        {
            var entities = new Dictionary<EntityUid, EntityUid?>(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.Add(GetEntity(key), GetEntity(value));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityUid> GetEntityList(ICollection<NetEntity> netEntities)
        {
            var entities = new List<EntityUid>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(GetEntity(netEntity));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityUid?> GetEntityList(List<NetEntity?> netEntities)
        {
            var entities = new List<EntityUid?>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(GetEntity(netEntity));
            return entities;
        }

        /// <inheritdoc />
        public EntityUid[] GetEntityArray(NetEntity[] netEntities)
        {
            var entities = new EntityUid[netEntities.Length];
            for (var i = 0; i < netEntities.Length; i++)
                entities[i] = GetEntity(netEntities[i]);
            return entities;
        }

        /// <inheritdoc />
        public EntityUid?[] GetEntityArray(NetEntity?[] netEntities)
        {
            var entities = new EntityUid?[netEntities.Length];
            for (var i = 0; i < netEntities.Length; i++)
                entities[i] = GetEntity(netEntities[i]);
            return entities;
        }

        #endregion

        #region EnsureEntity Collections (Client Prediction)

        public HashSet<EntityUid> EnsureEntitySet<T>(HashSet<NetEntity> netEntities, EntityUid callerEntity)
        {
            var entities = new HashSet<EntityUid>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(EnsureEntity<T>(netEntity, callerEntity));
            return entities;
        }

        public void EnsureEntitySet<T>(HashSet<NetEntity> netEntities, EntityUid callerEntity, HashSet<EntityUid> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(EnsureEntity<T>(netEntity, callerEntity));
        }

        /// <inheritdoc />
        public List<EntityUid> EnsureEntityList<T>(List<NetEntity> netEntities, EntityUid callerEntity)
        {
            var entities = new List<EntityUid>(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(EnsureEntity<T>(netEntity, callerEntity));
            return entities;
        }

        public void EnsureEntityList<T>(List<NetEntity> netEntities, EntityUid callerEntity, List<EntityUid> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var netEntity in netEntities)
                entities.Add(EnsureEntity<T>(netEntity, callerEntity));
        }

        public void EnsureEntityDictionary<TComp, TValue>(Dictionary<NetEntity, TValue> netEntities, EntityUid callerEntity, Dictionary<EntityUid, TValue> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(EnsureEntity<TComp>(key, callerEntity), value);
        }

        public void EnsureEntityDictionaryNullableValue<TComp, TValue>(Dictionary<NetEntity, TValue?> netEntities, EntityUid callerEntity, Dictionary<EntityUid, TValue?> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(EnsureEntity<TComp>(key, callerEntity), value);
        }

        public void EnsureEntityDictionary<TComp, TKey>(Dictionary<TKey, NetEntity> netEntities, EntityUid callerEntity, Dictionary<TKey, EntityUid> entities) where TKey : notnull
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(key, EnsureEntity<TComp>(value, callerEntity));
        }

        public void EnsureEntityDictionary<TComp, TKey>(Dictionary<TKey, NetEntity?> netEntities, EntityUid callerEntity, Dictionary<TKey, EntityUid?> entities) where TKey : notnull
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(key, EnsureEntity<TComp>(value, callerEntity));
        }

        public void EnsureEntityDictionary<TComp>(Dictionary<NetEntity, NetEntity> netEntities, EntityUid callerEntity, Dictionary<EntityUid, EntityUid> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(EnsureEntity<TComp>(key, callerEntity), EnsureEntity<TComp>(value, callerEntity));
        }

        public void EnsureEntityDictionary<TComp>(Dictionary<NetEntity, NetEntity?> netEntities, EntityUid callerEntity, Dictionary<EntityUid, EntityUid?> entities)
        {
            entities.Clear();
            entities.EnsureCapacity(netEntities.Count);
            foreach (var (key, value) in netEntities)
                entities.TryAdd(EnsureEntity<TComp>(key, callerEntity), EnsureEntity<TComp>(value, callerEntity));
        }

        #endregion

        #region GetNetEntity Collections (EntityUid -> NetEntity)

        /// <inheritdoc />
        public HashSet<NetEntity> GetNetEntitySet(HashSet<EntityUid> entities)
        {
            var newSet = new HashSet<NetEntity>(entities.Count);
            foreach (var ent in entities)
                newSet.Add(GetNetEntity(ent));
            return newSet;
        }

        /// <inheritdoc />
        public List<NetEntity> GetNetEntityList(List<EntityUid> entities)
        {
            var netEntities = new List<NetEntity>(entities.Count);
            foreach (var entity in entities)
                netEntities.Add(GetNetEntity(entity));
            return netEntities;
        }

        /// <inheritdoc />
        public List<NetEntity> GetNetEntityList(IReadOnlyList<EntityUid> entities)
        {
            var netEntities = new List<NetEntity>(entities.Count);
            foreach (var entity in entities)
                netEntities.Add(GetNetEntity(entity));
            return netEntities;
        }

        /// <inheritdoc />
        public List<NetEntity> GetNetEntityList(ICollection<EntityUid> entities)
        {
            var netEntities = new List<NetEntity>(entities.Count);
            foreach (var entity in entities)
                netEntities.Add(GetNetEntity(entity));
            return netEntities;
        }

        /// <inheritdoc />
        public List<NetEntity?> GetNetEntityList(List<EntityUid?> entities)
        {
            var netEntities = new List<NetEntity?>(entities.Count);
            foreach (var entity in entities)
                netEntities.Add(GetNetEntity(entity));
            return netEntities;
        }

        /// <inheritdoc />
        public NetEntity[] GetNetEntityArray(EntityUid[] entities)
        {
            var netEntities = new NetEntity[entities.Length];
            for (var i = 0; i < entities.Length; i++)
                netEntities[i] = GetNetEntity(entities[i]);
            return netEntities;
        }

        /// <inheritdoc />
        public NetEntity?[] GetNetEntityArray(EntityUid?[] entities)
        {
            var netEntities = new NetEntity?[entities.Length];
            for (var i = 0; i < entities.Length; i++)
                netEntities[i] = GetNetEntity(entities[i]);
            return netEntities;
        }

        /// <inheritdoc />
        public Dictionary<NetEntity, T> GetNetEntityDictionary<T>(Dictionary<EntityUid, T> entities)
        {
            var netEntities = new Dictionary<NetEntity, T>(entities.Count);
            foreach (var (key, value) in entities)
                netEntities.Add(GetNetEntity(key), value);
            return netEntities;
        }

        /// <inheritdoc />
        public Dictionary<T, NetEntity> GetNetEntityDictionary<T>(Dictionary<T, EntityUid> entities) where T : notnull
        {
            var netEntities = new Dictionary<T, NetEntity>(entities.Count);
            foreach (var (key, value) in entities)
                netEntities.Add(key, GetNetEntity(value));
            return netEntities;
        }

        /// <inheritdoc />
        public Dictionary<T, NetEntity?> GetNetEntityDictionary<T>(Dictionary<T, EntityUid?> entities) where T : notnull
        {
            var netEntities = new Dictionary<T, NetEntity?>(entities.Count);
            foreach (var (key, value) in entities)
                netEntities.Add(key, GetNetEntity(value));
            return netEntities;
        }

        /// <inheritdoc />
        public Dictionary<NetEntity, NetEntity> GetNetEntityDictionary(Dictionary<EntityUid, EntityUid> entities)
        {
            var netEntities = new Dictionary<NetEntity, NetEntity>(entities.Count);
            foreach (var (key, value) in entities)
                netEntities.Add(GetNetEntity(key), GetNetEntity(value));
            return netEntities;
        }

        /// <inheritdoc />
        public Dictionary<NetEntity, NetEntity?> GetNetEntityDictionary(Dictionary<EntityUid, EntityUid?> entities)
        {
            var netEntities = new Dictionary<NetEntity, NetEntity?>(entities.Count);
            foreach (var (key, value) in entities)
                netEntities.Add(GetNetEntity(key), GetNetEntity(value));
            return netEntities;
        }

        #endregion

        #region GetCoordinates Collections (NetCoordinates -> EntityCoordinates)

        /// <inheritdoc />
        public HashSet<EntityCoordinates> GetEntitySet(HashSet<NetCoordinates> netEntities)
        {
            var entities = new HashSet<EntityCoordinates>(netEntities.Count);
            foreach (var netCoordinates in netEntities)
                entities.Add(GetCoordinates(netCoordinates));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityCoordinates> GetEntityList(List<NetCoordinates> netEntities)
        {
            var entities = new List<EntityCoordinates>(netEntities.Count);
            foreach (var netCoordinates in netEntities)
                entities.Add(GetCoordinates(netCoordinates));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityCoordinates> GetEntityList(ICollection<NetCoordinates> netEntities)
        {
            var entities = new List<EntityCoordinates>(netEntities.Count);
            foreach (var netCoordinates in netEntities)
                entities.Add(GetCoordinates(netCoordinates));
            return entities;
        }

        /// <inheritdoc />
        public List<EntityCoordinates?> GetEntityList(List<NetCoordinates?> netEntities)
        {
            var entities = new List<EntityCoordinates?>(netEntities.Count);
            foreach (var netCoordinates in netEntities)
                entities.Add(GetCoordinates(netCoordinates));
            return entities;
        }

        /// <inheritdoc />
        public EntityCoordinates[] GetEntityArray(NetCoordinates[] netEntities)
        {
            var entities = new EntityCoordinates[netEntities.Length];
            for (var i = 0; i < netEntities.Length; i++)
                entities[i] = GetCoordinates(netEntities[i]);
            return entities;
        }

        /// <inheritdoc />
        public EntityCoordinates?[] GetEntityArray(NetCoordinates?[] netEntities)
        {
            var entities = new EntityCoordinates?[netEntities.Length];
            for (var i = 0; i < netEntities.Length; i++)
                entities[i] = GetCoordinates(netEntities[i]);
            return entities;
        }

        #endregion

        #region GetNetCoordinates Collections (EntityCoordinates -> NetCoordinates)

        /// <inheritdoc />
        public HashSet<NetCoordinates> GetNetCoordinatesSet(HashSet<EntityCoordinates> entities)
        {
            var newSet = new HashSet<NetCoordinates>(entities.Count);
            foreach (var coordinates in entities)
                newSet.Add(GetNetCoordinates(coordinates));
            return newSet;
        }

        /// <inheritdoc />
        public List<NetCoordinates> GetNetCoordinatesList(List<EntityCoordinates> entities)
        {
            var netEntities = new List<NetCoordinates>(entities.Count);
            foreach (var netCoordinates in entities)
                netEntities.Add(GetNetCoordinates(netCoordinates));
            return netEntities;
        }

        /// <inheritdoc />
        public List<NetCoordinates> GetNetCoordinatesList(ICollection<EntityCoordinates> entities)
        {
            var netEntities = new List<NetCoordinates>(entities.Count);
            foreach (var netCoordinates in entities)
                netEntities.Add(GetNetCoordinates(netCoordinates));
            return netEntities;
        }

        /// <inheritdoc />
        public List<NetCoordinates?> GetNetCoordinatesList(List<EntityCoordinates?> entities)
        {
            var netEntities = new List<NetCoordinates?>(entities.Count);
            foreach (var netCoordinates in entities)
                netEntities.Add(GetNetCoordinates(netCoordinates));
            return netEntities;
        }

        /// <inheritdoc />
        public NetCoordinates[] GetNetCoordinatesArray(EntityCoordinates[] entities)
        {
            var netEntities = new NetCoordinates[entities.Length];
            for (var i = 0; i < entities.Length; i++)
                netEntities[i] = GetNetCoordinates(entities[i]);
            return netEntities;
        }

        /// <inheritdoc />
        public NetCoordinates?[] GetNetCoordinatesArray(EntityCoordinates?[] entities)
        {
            var netEntities = new NetCoordinates?[entities.Length];
            for (var i = 0; i < entities.Length; i++)
                netEntities[i] = GetNetCoordinates(entities[i]);
            return netEntities;
        }

        #endregion

        #endregion
    }
}
