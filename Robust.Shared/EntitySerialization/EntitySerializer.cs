using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using JetBrains.Annotations;
using Robust.Shared.EntitySerialization.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

#nullable enable

namespace Robust.Shared.EntitySerialization;

/// <summary>
/// This class provides methods for deserializing entities from yaml. It provides some more control over
/// serialization than the methods provided by <see cref="MapLoaderSystem"/>.
/// </summary>
public sealed class EntityDeserializer :
    ISerializationContext,
    ITypeSerializer<EntityUid, ValueDataNode>,
    ITypeSerializer<NetEntity, ValueDataNode>
{
    // See the comments around EntitySerializer's version const for information about the different versions.
    public const int OldestSupportedVersion = 3;
    public const int NewestSupportedVersion = EntitySerializer.MapFormatVersion;

    public SerializationManager.SerializerProvider SerializerProvider { get; } = new();

    [Dependency] public readonly EntityManager EntMan = default!;
    [Dependency] public readonly IGameTiming Timing = default!;
    [Dependency] private readonly ISerializationManager _seriMan = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ILogManager _logMan = default!;
    [Dependency] private readonly IDependencyCollection _deps = default!;

    private readonly ISawmill _log;
    private readonly Stopwatch _stopwatch = new();

    public readonly DeserializationOptions Options;
    public readonly MappingDataNode Data;
    public readonly Dictionary<EntityUid, EntData> Entities = new();
    public readonly Dictionary<int, EntData> YamlEntities = new();
    public readonly Dictionary<string, List<EntData>> Prototypes = new();

    public record struct EntData(
        int YamlId,
        MappingDataNode Node,
        Dictionary<string, MappingDataNode>? Components,
        HashSet<string>? MissingComponents,
        bool PostInit,
        bool Paused,
        bool ToDelete);

    public readonly LoadResult Result = new();
    public readonly Dictionary<int, string> TileMap = new();
    public readonly Dictionary<int, EntityUid> UidMap = new();
    public readonly List<int> MapYamlIds = new();
    public readonly List<int> GridYamlIds = new();
    public readonly List<int> OrphanYamlIds = new();
    public readonly List<int> NullspaceYamlIds = new();
    public readonly Dictionary<string, string> RenamedPrototypes;
    public readonly HashSet<string> DeletedPrototypes;

    public readonly HashSet<EntityUid> PostMapInit = new();
    public readonly HashSet<EntityUid> Paused = new();
    public readonly HashSet<EntityUid> ToDelete = new();
    public readonly List<EntityUid> SortedEntities = new();

    private readonly Dictionary<string, MappingDataNode> _components = new();
    public EntData? CurrentReadingEntity;
    public string? CurrentComponent;
    private readonly EntityQuery<MapComponent> _mapQuery;
    private readonly EntityQuery<MapGridComponent> _gridQuery;
    private readonly EntityQuery<TransformComponent> _xformQuery;
    private readonly EntityQuery<MetaDataComponent> _metaQuery;

    public EntityDeserializer(
        IDependencyCollection deps,
        MappingDataNode data,
        DeserializationOptions options,
        Dictionary<string, string>? renamedPrototypes = null,
        HashSet<string>? deletedPrototypes = null)
    {
        deps.InjectDependencies(this);
        _log = _logMan.GetSawmill("entity_deserializer");
        _log.Level = LogLevel.Info;
        SerializerProvider.RegisterSerializer(this);
        Data = data;
        Options = options;
        RenamedPrototypes = renamedPrototypes ?? new();
        DeletedPrototypes = deletedPrototypes ?? new();

        _mapQuery = EntMan.GetEntityQuery<MapComponent>();
        _gridQuery = EntMan.GetEntityQuery<MapGridComponent>();
        _xformQuery = EntMan.GetEntityQuery<TransformComponent>();
        _metaQuery = EntMan.GetEntityQuery<MetaDataComponent>();
    }

    public bool WritingReadingPrototypes { get; private set; }

    public bool TryProcessData()
    {
        ReadMetadata();

        if (Result.Version < OldestSupportedVersion)
        {
            _log.Error($"Cannot handle this map file version, found v{Result.Version} and require at least v{OldestSupportedVersion}");
            return false;
        }

        if (Result.Version > NewestSupportedVersion)
        {
            _log.Error($"Cannot handle this map file version, found v{Result.Version} but require at most v{NewestSupportedVersion}");
            return false;
        }

        if (!ValidatePrototypes())
            return false;

        ReadEntities();
        ReadTileMap();
        ReadMapsAndGrids();
        return true;
    }

    public void CreateEntities()
    {
        AllocateEntities();
        LoadEntities();
        GetRootEntities();
        RemoveEmptyChunks();
        StoreGridTileMap();

        if (Options.AssignMapids)
            AssignMapIds();

        CheckCategory();
    }

    public void StartEntities()
    {
        AdoptGrids();
        ValidateMapIds();
        BuildEntityHierarchy();
        StartEntitiesInternal();
        SetMapInitLifestage();
        SetPaused();
        GetRootNodes();
        PauseMaps();
        InitializeMaps();
        ProcessDeletions();
    }

    private void ReadMetadata()
    {
        var meta = Data.Get<MappingDataNode>("meta");
        Result.Version = meta.Get<ValueDataNode>("format").AsInt();

        if (meta.TryGet<ValueDataNode>("engineVersion", out var engVer))
            Result.EngineVersion = engVer.Value;

        if (meta.TryGet<ValueDataNode>("forkId", out var forkId))
            Result.ForkId = forkId.Value;

        if (meta.TryGet<ValueDataNode>("forkVersion", out var forkVer))
            Result.ForkVersion = forkVer.Value;

        if (meta.TryGet<ValueDataNode>("time", out var timeNode) && DateTime.TryParse(timeNode.Value, out var time))
            Result.Time = time;

        if (meta.TryGet<ValueDataNode>("category", out var catNode) && Enum.TryParse<FileCategory>(catNode.Value, out var res))
            Result.Category = res;
    }

    private bool ValidatePrototypes()
    {
        _stopwatch.Restart();
        var fail = false;
        var key = Result.Version >= 4 ? "proto" : "type";
        var entities = Data.Get<SequenceDataNode>("entities");

        foreach (var metaDef in entities.Cast<MappingDataNode>())
        {
            if (!metaDef.TryGet<ValueDataNode>(key, out var typeNode))
                continue;

            var type = typeNode.Value;
            if (string.IsNullOrWhiteSpace(type))
                continue;

            if (RenamedPrototypes.TryGetValue(type, out var newType))
                type = newType;

            if (DeletedPrototypes.Contains(type))
            {
                _log.Warning("Map contains an obsolete/removed prototype: {0}. This may cause unexpected errors.", type);
                continue;
            }

            if (_proto.HasIndex<EntityPrototype>(type))
                continue;

            _log.Error("Missing prototype for map: {0}", type);
            fail = true;
        }

        _log.Debug($"Verified entities in {_stopwatch.Elapsed}");

        if (!fail) return true;

        _log.Error("Found missing prototypes in map file. Missing prototypes have been dumped to logs.");
        return false;
    }

    private void ReadEntities()
    {
        if (Result.Version == 3)
        {
            ReadEntitiesV3();
            return;
        }

        if (Result.Version < 7)
        {
            ReadEntitiesFallback();
            return;
        }

        var prototypeGroups = Data.Get<SequenceDataNode>("entities");
        foreach (var protoGroup in prototypeGroups.Cast<MappingDataNode>())
        {
            EntProtoId? protoId = null;
            var deletedPrototype = false;
            if (protoGroup.TryGet("proto", out ValueDataNode? protoIdNode)
                && !string.IsNullOrWhiteSpace(protoIdNode.Value))
            {
                if (DeletedPrototypes.Contains(protoIdNode.Value))
                {
                    deletedPrototype = true;
                    if (_proto.HasIndex<EntityPrototype>(protoIdNode.Value))
                        protoId = protoIdNode.Value;
                }
                else if (RenamedPrototypes.TryGetValue(protoIdNode.Value, out var newType))
                    protoId = newType;
                else
                    protoId = protoIdNode.Value;
            }

            var entities = (SequenceDataNode)protoGroup["entities"];
            _proto.TryIndex(protoId, out var proto);

            var protoData = Prototypes.GetOrNew(proto?.ID ?? string.Empty);
            foreach (var entityNode in entities.Cast<MappingDataNode>())
            {
                var yamlId = entityNode.Get<ValueDataNode>("uid").AsInt();
                var postInit = entityNode.TryGet("mapInit", out ValueDataNode? initNode) && initNode.AsBool();
                var paused = entityNode.TryGet("paused", out ValueDataNode? pausedNode) ? pausedNode.AsBool() : !postInit;
                var (comps, missing) = GetComponents(entityNode);
                var entData = new EntData(yamlId, entityNode, comps, missing, postInit, paused, deletedPrototype);
                protoData.Add(entData);
                YamlEntities.Add(yamlId, entData);
            }
        }
    }

    private void ReadEntitiesV3()
    {
        var metadata = Data.Get<MappingDataNode>("meta");
        var preInit = metadata.TryGet("postmapinit", out ValueDataNode? mapInitNode) && !mapInitNode.AsBool();

        var entities = Data.Get<SequenceDataNode>("entities");
        foreach (var entityNode in entities.Cast<MappingDataNode>())
        {
            var yamlId = entityNode.Get<ValueDataNode>("uid").AsInt();
            EntProtoId? protoId = null;
            var toDelete = false;
            if (entityNode.TryGet("type", out ValueDataNode? protoIdNode))
            {
                if (DeletedPrototypes.Contains(protoIdNode.Value))
                {
                    toDelete = true;
                    if (_proto.HasIndex<EntityPrototype>(protoIdNode.Value))
                        protoId = protoIdNode.Value;
                }
                else if (RenamedPrototypes.TryGetValue(protoIdNode.Value, out var newType))
                    protoId = newType;
                else
                    protoId = protoIdNode.Value;
            }

            _proto.TryIndex(protoId, out var proto);
            var protoData = Prototypes.GetOrNew(proto?.ID ?? string.Empty);
            var (comps, missing) = GetComponents(entityNode);
            var entData = new EntData(yamlId, entityNode, comps, missing, PostInit: !preInit, Paused: preInit, toDelete);
            protoData.Add(entData);
            YamlEntities.Add(yamlId, entData);
        }
    }

    private void ReadEntitiesFallback()
    {
        var metadata = Data.Get<MappingDataNode>("meta");
        var preInit = metadata.TryGet("postmapinit", out ValueDataNode? mapInitNode) && !mapInitNode.AsBool();

        var prototypeGroups = Data.Get<SequenceDataNode>("entities");
        foreach (var protoGroup in prototypeGroups.Cast<MappingDataNode>())
        {
            EntProtoId? protoId = null;
            var deletedPrototype = false;
            if (protoGroup.TryGet("proto", out ValueDataNode? protoIdNode)
                && !string.IsNullOrWhiteSpace(protoIdNode.Value))
            {
                if (DeletedPrototypes.Contains(protoIdNode.Value))
                {
                    deletedPrototype = true;
                    if (_proto.HasIndex<EntityPrototype>(protoIdNode.Value))
                        protoId = protoIdNode.Value;
                }
                else if (RenamedPrototypes.TryGetValue(protoIdNode.Value, out var newType))
                    protoId = newType;
                else
                    protoId = protoIdNode.Value;
            }

            var entities = (SequenceDataNode)protoGroup["entities"];
            _proto.TryIndex(protoId, out var proto);

            var protoData = Prototypes.GetOrNew(proto?.ID ?? string.Empty);
            foreach (var entityNode in entities.Cast<MappingDataNode>())
            {
                var yamlId = entityNode.Get<ValueDataNode>("uid").AsInt();
                var (comps, missing) = GetComponents(entityNode);
                var entData = new EntData(yamlId, entityNode, comps, missing, PostInit: !preInit, Paused: preInit, ToDelete: deletedPrototype);
                protoData.Add(entData);
                YamlEntities.Add(yamlId, entData);
            }
        }
    }

    private (Dictionary<string, MappingDataNode>? Comps, HashSet<string>? Missing) GetComponents(MappingDataNode node)
    {
        Dictionary<string, MappingDataNode>? dict = null;
        HashSet<string>? missing = null;

        if (node.TryGet("components", out SequenceDataNode? componentList))
        {
            dict = new(componentList.Count);
            foreach (var compData in componentList.Cast<MappingDataNode>())
            {
                var value = ((ValueDataNode)compData["type"]).Value;
                compData.Remove("type");
                dict.Add(value, compData);
            }
        }

        if (node.TryGet("missingComponents", out SequenceDataNode? missingComponentList))
        {
            missing = new(missingComponentList.Count);
            foreach (var missNode in missingComponentList)
                missing.Add(((ValueDataNode)missNode).Value);
        }

        node.Remove("components");
        node.Remove("missingComponents");
        return (dict, missing);
    }

    private void ReadTileMap()
    {
        _stopwatch.Restart();
        var tileMap = Data.Get<MappingDataNode>("tilemap");
        var migrations = new Dictionary<string, string>();
        foreach (var proto in _proto.EnumeratePrototypes<TileAliasPrototype>())
            migrations.Add(proto.ID, proto.Target);

        foreach (var (key, value) in tileMap.Children)
        {
            var yamlTileId = int.Parse(key, CultureInfo.InvariantCulture);
            var tileName = ((ValueDataNode)value).Value;
            if (migrations.TryGetValue(tileName, out var @new))
                tileName = @new;

            TileMap.Add(yamlTileId, tileName);
        }

        _log.Debug($"Read tilemap in {_stopwatch.Elapsed}");
    }

    private void AllocateEntities()
    {
        _stopwatch.Restart();

        foreach (var (protoId, ents) in Prototypes)
        {
            _proto.TryIndex(protoId, out var proto);

            foreach (var ent in ents)
            {
                // CHANGE: Critical fix for protection level. Use the internal AllocEntity and set prototype manually.
                var entity = EntMan.AllocEntity(out var metadata);
                metadata._entityPrototype = proto;

                Result.Entities.Add(entity);
                UidMap.Add(ent.YamlId, entity);
                Entities.Add(entity, ent);

                if (ent.PostInit) PostMapInit.Add(entity);
                if (ent.Paused) Paused.Add(entity);
                if (ent.ToDelete) ToDelete.Add(entity);

                if (Options.StoreYamlUids)
                    EntMan.AddComponent<YamlUidComponent>(entity).Uid = ent.YamlId;
            }
        }

        _log.Debug($"Allocated {Entities.Count} entities in {_stopwatch.Elapsed}");
    }

    private void ReadMapsAndGrids()
    {
        if (Result.Version < 7) return;
        ReadYamlIdList(Data, "maps", MapYamlIds);
        ReadYamlIdList(Data, "grids", GridYamlIds);
        ReadYamlIdList(Data, "orphans", OrphanYamlIds);
        ReadYamlIdList(Data, "nullspace", NullspaceYamlIds);
    }

    private void ReadYamlIdList(MappingDataNode data, string key, List<int> list)
    {
        var sequence = data.Get<SequenceDataNode>(key);
        list.EnsureCapacity(sequence.Count);
        foreach (var node in sequence)
            list.Add(((ValueDataNode)node).AsInt());
    }

    private void LoadEntities()
    {
        _stopwatch.Restart();
        foreach (var (entity, data) in Entities)
        {
#if EXCEPTION_TOLERANCE
            try
            {
#endif
            CurrentReadingEntity = data;
            LoadEntity(entity, _metaQuery.Comp(entity), data.Components, data.MissingComponents);
#if EXCEPTION_TOLERANCE
            }
            catch (Exception e)
            {
                ToDelete.Add(entity);
                _log.Error($"Encountered error while loading entity. Yaml uid: {data.YamlId}. Loaded entity: {EntMan.ToPrettyString(entity)}. Error:\n{e}.");
            }
#endif
        }

        CurrentReadingEntity = null;
        _log.Debug($"Loaded {Entities.Count} entities in {_stopwatch.Elapsed}");
    }

    private void LoadEntity(
        EntityUid uid,
        MetaDataComponent meta,
        Dictionary<string, MappingDataNode>? comps,
        HashSet<string>? missingComps)
    {
        var proto = meta.EntityPrototype;
        _components.Clear();
        if (comps != null)
        {
            _components.EnsureCapacity(comps.Count);
            foreach (var (name, compData) in comps)
            {
                DebugTools.Assert(missingComps?.Contains(name) != true);

                if (!_factory.TryGetRegistration(name, out _))
                {
                    if (!_factory.IsIgnored(name))
                        _log.Error($"Encountered unregistered component ({name}) while loading entity {EntMan.ToPrettyString(uid)}");
                    continue;
                }

                var datanode = compData;
                if (proto != null && proto.Components.TryGetValue(name, out var protoData))
                    datanode = _seriMan.CombineMappings(compData, protoData.Mapping);

                _components.Add(name, datanode);
            }
        }

        if (proto != null)
        {
            foreach (var (name, entry) in proto.Components)
            {
                if (missingComps?.Contains(name) == true)
                    continue;

                if (_components.ContainsKey(name))
                    continue;

                CurrentComponent = name;
                var compReg = _factory.GetRegistration(name);

                if (!EntMan.TryGetComponent(uid, compReg.Idx, out var component))
                {
                    var newComponent = _factory.GetComponent(compReg);
                    EntMan.AddComponent(uid, newComponent);
                    component = newComponent;
                }

                _seriMan.CopyTo(entry.Component, ref component, this, notNullableOverride: true);

                if (!entry.Component.NetSyncEnabled && compReg.NetID is { } netId)
                    meta.NetComponents.Remove(netId);
            }
        }

        foreach (var (name, data) in _components)
        {
            CurrentComponent = name;
            var compReg = _factory.GetRegistration(name);
            if (!EntMan.TryGetComponent(uid, compReg.Idx, out var existing))
            {
                var newComponent = (IComponent)_seriMan.Read(compReg.Type, data, this)!;

                // TODO: Remove this legacy workaround when components no longer rely on Owner being set pre-injection.
                if (newComponent is ISerializationHooks)
                {
                    existing = _factory.GetComponent(compReg);
                    EntMan.AddComponent(uid, existing);
                    _seriMan.CopyTo(newComponent, ref existing, this, notNullableOverride: true);
                    continue;
                }

                _deps.InjectDependencies(newComponent);
                EntMan.AddComponent(uid, newComponent);
                continue;
            }

            var temp = (IComponent)_seriMan.Read(compReg.Type, data, this)!;
            _seriMan.CopyTo(temp, ref existing, this, notNullableOverride: true);
        }

        _components.Clear();
        CurrentComponent = null;
        if (missingComps is { Count: > 0 })
            meta.LastComponentRemoved = Timing.CurTick;
    }

    private void GetRootEntities()
    {
        if (Result.Version < 7)
        {
            GetRootEntitiesFallback();
            return;
        }

        foreach (var yamlId in MapYamlIds)
        {
            var uid = UidMap[yamlId];
            if (_mapQuery.TryComp(uid, out var map))
            {
                Result.Maps.Add((uid, map));
                EntMan.EnsureComponent<LoadedMapComponent>(uid);
            }
            else
                _log.Error($"Missing map entity: {EntMan.ToPrettyString(uid)}");
        }

        foreach (var yamlId in GridYamlIds)
        {
            var uid = UidMap[yamlId];
            if (_gridQuery.TryComp(uid, out var grid))
                Result.Grids.Add((uid, grid));
            else
                _log.Error($"Missing grid entity: {EntMan.ToPrettyString(uid)}");
        }

        foreach (var yamlId in OrphanYamlIds)
        {
            var uid = UidMap[yamlId];
            if (_mapQuery.HasComponent(uid) || _xformQuery.Comp(uid).ParentUid.IsValid())
                _log.Error($"Entity {EntMan.ToPrettyString(uid)} was incorrectly labelled as an orphan?");
            else
                Result.Orphans.Add(uid);
        }

        foreach (var yamlId in NullspaceYamlIds)
        {
            var uid = UidMap[yamlId];
            if (_mapQuery.HasComponent(uid) || _xformQuery.Comp(uid).ParentUid.IsValid())
                _log.Error($"Entity {EntMan.ToPrettyString(uid)} was incorrectly labelled as a null-space entity?");
            else
                Result.NullspaceEntities.Add(uid);
        }
    }

    public void AssignMapIds()
    {
        foreach (var map in Result.Maps)
            _map.AssignMapId(map);
    }

    private void GetRootEntitiesFallback()
    {
        foreach (var uid in Result.Entities)
        {
            if (_gridQuery.TryComp(uid, out var grid))
            {
                Result.Grids.Add((uid, grid));
                if (_xformQuery.Comp(uid).ParentUid == EntityUid.Invalid && !_mapQuery.HasComp(uid))
                    Result.Orphans.Add(uid);
            }

            if (_mapQuery.TryComp(uid, out var map))
            {
                Result.Maps.Add((uid, map));
                EntMan.EnsureComponent<LoadedMapComponent>(uid);
            }
        }
    }

    private void RemoveEmptyChunks()
    {
        foreach (var uid in Entities.Keys)
        {
            if (!_gridQuery.TryGetComponent(uid, out var gridComp))
                continue;

            foreach (var (index, chunk) in gridComp.Chunks)
            {
                if (chunk.FilledTiles > 0)
                    continue;

                _log.Warning($"Encountered empty chunk while deserializing map. Grid: {EntMan.ToPrettyString(uid)}. Chunk index: {index}");
                gridComp.Chunks.Remove(index);
            }
        }
    }

    private void StoreGridTileMap()
    {
        foreach (var entity in Result.Grids)
            EntMan.EnsureComponent<MapSaveTileMapComponent>(entity).TileMap = TileMap.ShallowClone();
    }

    private void BuildEntityHierarchy()
    {
        _stopwatch.Restart();
        var processed = new HashSet<EntityUid>(Result.Entities.Count);
        foreach (var ent in Result.Entities)
            BuildEntityHierarchy(ent, processed);
        _log.Debug($"Built entity hierarchy for {Result.Entities.Count} entities in {_stopwatch.Elapsed}");
    }

    private void CheckCategory()
    {
        if (Result.Version < 7)
        {
            InferCategory();
            return;
        }

        switch (Result.Category)
        {
            case FileCategory.Map:
                if (Result.Maps.Count == 1 && Result.Orphans.Count == 0) return;
                _log.Error($"Expected file to contain a single map, but instead found {Result.Maps.Count} maps and {Result.Orphans.Count} orphans");
                break;
            case FileCategory.Grid:
                if (Result.Maps.Count == 0 && Result.Grids.Count == 1 && Result.Orphans.Count == 1 && Result.Orphans.First() == Result.Grids.First().Owner) return;
                _log.Error($"Expected file to contain a single grid, but instead found {Result.Grids.Count} grids and {Result.Orphans.Count} orphans");
                break;
            case FileCategory.Entity:
                if (Result.Maps.Count == 0 && Result.Grids.Count == 0 && Result.Orphans.Count == 1) return;
                _log.Error($"Expected file to contain a orphaned entity, but instead found {Result.Orphans.Count} orphans");
                break;
            case FileCategory.Save:
            default:
                return;
        }
    }

    private void InferCategory()
    {
        if (Result.Category != FileCategory.Unknown) return;
        if (Result.Maps.Count == 1) Result.Category = FileCategory.Map;
        else if (Result.Maps.Count == 0 && Result.Grids.Count == 1) Result.Category = FileCategory.Grid;
    }

    private void AdoptGrids()
    {
        foreach (var grid in Result.Grids)
        {
            if (_mapQuery.HasComponent(grid.Owner))
                continue;

            var xform = _xformQuery.Comp(grid.Owner);
            if (xform.ParentUid.IsValid())
                continue;

            DebugTools.Assert(Result.Orphans.Contains(grid.Owner));
            if (Options.LogOrphanedGrids)
                _log.Error($"Encountered an orphaned grid. Automatically creating a map for the grid.");

            var map = _map.CreateUninitializedMap();
            _map.AssignMapId(map);
            Result.Entities.Add(map);
            Result.Maps.Add(map);
            Result.Orphans.Remove(grid.Owner);
            xform._parent = map.Owner;
            DebugTools.Assert(!xform._mapIdInitialized);
        }
    }

    private void ValidateMapIds()
    {
        foreach (var map in Result.Maps)
        {
            if (map.Comp.MapId == MapId.Nullspace || !_map.TryGetMap(map.Comp.MapId, out var e) || e != map.Owner)
                throw new Exception($"Map entity {EntMan.ToPrettyString(map)} has not been assigned a map id");
        }
    }

    private void PauseMaps()
    {
        if (!Options.PauseMaps) return;
        foreach (var ent in Result.Maps)
            _map.SetPaused(ent!, true);
    }

    private void BuildEntityHierarchy(EntityUid uid, HashSet<EntityUid> processed)
    {
        if (!processed.Add(uid)) return;
        if (!_xformQuery.TryComp(uid, out var xform)) return;

        var parent = xform.ParentUid;
        if (parent != EntityUid.Invalid)
            BuildEntityHierarchy(parent, processed);

        if (!Result.Entities.Contains(uid)) return;
        SortedEntities.Add(uid);
    }

    private void StartEntitiesInternal()
    {
        _stopwatch.Restart();
        foreach (var uid in SortedEntities)
            StartupEntity(uid, _metaQuery.GetComponent(uid));
        _log.Debug($"Started up {Result.Entities.Count} entities in {_stopwatch.Elapsed}");
    }

    private void StartupEntity(EntityUid uid, MetaDataComponent metadata)
    {
        ResetNetTicks(uid, metadata);
        EntMan.InitializeEntity(uid, metadata);
        EntMan.StartEntity(uid);
    }

    private void ResetNetTicks(EntityUid uid, MetaDataComponent metadata)
    {
        if (!Entities.TryGetValue(uid, out var entData)) return;
        if (metadata.EntityPrototype is not { } prototype) return;
        if (entData.Components == null) return;

        foreach (var component in metadata.NetComponents.Values)
        {
            var compName = _factory.GetComponentName(component.GetType());
            if (!entData.Components.ContainsKey(compName))
            {
                component.ClearTicks();
                continue;
            }
            if (prototype.Components.ContainsKey(compName))
                component.ClearCreationTick();
        }
    }

    private void SetMapInitLifestage()
    {
        if (PostMapInit.Count == 0) return;
        _stopwatch.Restart();
        foreach (var uid in PostMapInit)
        {
            if (!_metaQuery.TryComp(uid, out var meta)) continue;
            DebugTools.Assert(meta.EntityLifeStage == EntityLifeStage.Initialized);
            EntMan.SetLifeStage(meta, EntityLifeStage.MapInitialized);
        }
        _log.Debug($"Finished flagging mapinit in {_stopwatch.Elapsed}");
    }

    private void SetPaused()
    {
        if (Paused.Count == 0) return;
        _stopwatch.Restart();
        var time = Timing.CurTime;
        var ev = new EntityPausedEvent();
        foreach (var uid in Paused)
        {
            if (!_metaQuery.TryComp(uid, out var meta)) continue;
            meta.PauseTime = time;
            EntMan.EventBus.RaiseLocalEvent(uid, ref ev);
        }
        _log.Debug($"Finished setting PauseTime in {_stopwatch.Elapsed}");
    }

    private void InitializeMaps()
    {
        if (!Options.InitializeMaps)
        {
            if (Options.PauseMaps) return;
            foreach (var ent in Result.Maps)
            {
                if (_metaQuery.Comp(ent.Owner).EntityLifeStage < EntityLifeStage.MapInitialized)
                    _map.SetPaused(ent!, true);
            }
            return;
        }

        foreach (var ent in Result.Maps)
        {
            if (!ent.Comp.MapInitialized)
                _map.InitializeMap(ent!, unpause: !Options.PauseMaps);
        }
    }

    private void ProcessDeletions()
    {
        foreach (var uid in ToDelete)
        {
            EntMan.DeleteEntity(uid);
            Result.Entities.Remove(uid);
        }
    }

    private void GetRootNodes()
    {
        Result.RootNodes.UnionWith(Result.Orphans);
        Result.RootNodes.UnionWith(Result.NullspaceEntities);
        foreach (var map in Result.Maps)
            Result.RootNodes.Add(map.Owner);
    }

    #region ITypeSerializer

    ValidationNode ITypeValidator<EntityUid, ValueDataNode>.Validate(ISerializationManager s, ValueDataNode n, IDependencyCollection d, ISerializationContext? c)
    {
        if (n.Value is "invalid" || int.TryParse(n.Value, out _))
            return new ValidatedValueNode(n);
        return new ErrorNode(n, "Invalid EntityUid");
    }

    DataNode ITypeWriter<EntityUid>.Write(ISerializationManager s, EntityUid v, IDependencyCollection d, bool a, ISerializationContext? c)
    {
        return v.IsValid() ? new ValueDataNode(v.Id.ToString(CultureInfo.InvariantCulture)) : new ValueDataNode("invalid");
    }

    EntityUid ITypeReader<EntityUid, ValueDataNode>.Read(ISerializationManager s, ValueDataNode n, IDependencyCollection d, SerializationHookContext h, ISerializationContext? c, ISerializationManager.InstantiationDelegate<EntityUid>? _)
    {
        if (n.Value == "invalid")
        {
            if (CurrentComponent == "Transform") return EntityUid.Invalid;
            if (!Options.LogInvalidEntities) return EntityUid.Invalid;

            var msg = CurrentReadingEntity is not { } curr
                ? "Encountered invalid EntityUid reference"
                : $"Encountered invalid EntityUid reference while reading entity {curr.YamlId}, component: {CurrentComponent}";
            _log.Error(msg);
            return EntityUid.Invalid;
        }

        if (int.TryParse(n.Value, out var val) && UidMap.TryGetValue(val, out var entity))
            return entity;

        _log.Error($"Invalid yaml entity id: '{val}'");
        return EntityUid.Invalid;
    }

    ValidationNode ITypeValidator<NetEntity, ValueDataNode>.Validate(ISerializationManager s, ValueDataNode n, IDependencyCollection d, ISerializationContext? c)
    {
        if (n.Value is "invalid" || int.TryParse(n.Value, out _))
            return new ValidatedValueNode(n);
        return new ErrorNode(n, "Invalid NetEntity");
    }

    NetEntity ITypeReader<NetEntity, ValueDataNode>.Read(ISerializationManager s, ValueDataNode n, IDependencyCollection d, SerializationHookContext h, ISerializationContext? c, ISerializationManager.InstantiationDelegate<NetEntity>? p)
    {
        var uid = s.Read<EntityUid>(n, c);
        if (EntMan.TryGetNetEntity(uid, out var nent))
            return nent.Value;

        _log.Error($"Failed to get NetEntity for entity {EntMan.ToPrettyString(uid)}");
        return NetEntity.Invalid;
    }

    DataNode ITypeWriter<NetEntity>.Write(ISerializationManager s, NetEntity v, IDependencyCollection d, bool a, ISerializationContext? c)
    {
        return v.IsValid() ? new ValueDataNode(v.Id.ToString(CultureInfo.InvariantCulture)) : new ValueDataNode("invalid");
    }

    #endregion
}
