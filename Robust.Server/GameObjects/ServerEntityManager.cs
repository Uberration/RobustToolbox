// Filename: Robust.Server/GameObjects/ServerEntityManager.cs

using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Prometheus;
using Robust.Server.GameStates;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Replays;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared;
using System.Linq;

#if EXCEPTION_TOLERANCE
using Robust.Shared.Exceptions;
#endif

namespace Robust.Server.GameObjects
{
    /// <summary>
    /// The server-side implementation of the <see cref="IEntityManager"/>.
    /// Manages server-specific entity logic, including networking and game state.
    /// </summary>
    [UsedImplicitly]
    public sealed class ServerEntityManager : EntityManager, IServerEntityManager
    {
        #region Dependencies and Internal State

        private static readonly Gauge EntitiesCount = Metrics.CreateGauge(
            "robust_entities_count",
            "Amount of alive entities.");

        [Dependency] private readonly IReplayRecordingManager _replay = default!;
        [Dependency] private readonly IServerNetManager _networkManager = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;
#if EXCEPTION_TOLERANCE
        [Dependency] private readonly IRuntimeLog _runtimeLog = default!;
#endif

        private ISawmill _netEntSawmill = default!;
        private PvsSystem _pvs = default!;

        private readonly PriorityQueue<MsgEntity> _networkMessageQueue = new(new MessageSequenceComparer());
        private readonly Dictionary<ICommonSession, uint> _lastProcessedSequences = new();
        private bool _logLateMessages;

        #endregion

        #region Lifecycle Overrides

        public override void Initialize()
        {
            _netEntSawmill = LogManager.GetSawmill("net.ent");
            SetupNetworking();
            ReceivedSystemMessage += (_, systemMsg) => EventBus.RaiseEvent(EventSource.Network, systemMsg);

            base.Initialize();
        }

        public override void Startup()
        {
            base.Startup();
            _pvs = System<PvsSystem>();
        }

        public override void TickUpdate(float frameTime, bool noPredictions, Histogram? histogram)
        {
            // Process any queued network messages that are due for this tick.
            using (histogram?.WithLabels("EntityNet").NewTimer())
            {
                while (_networkMessageQueue.Count > 0 && _networkMessageQueue.Peek().SourceTick <= CurrentTick)
                {
                    DispatchEntityNetworkMessage(_networkMessageQueue.Take());
                }
            }

            // Run the base tick update (entity systems, event queue, etc.).
            base.TickUpdate(frameTime, noPredictions, histogram);

            // Update the entity count metric.
            // FIX: Changed Entities.Count to the public property EntityCount.
            EntitiesCount.Set(EntityCount);
        }

        #endregion

        #region Entity Management Overrides

        /// <inheritdoc />
        internal override EntityUid CreateEntity(string? prototypeName, out MetaDataComponent metadata, IEntityLoadContext? context = null)
        {
            // First, call the base implementation to allocate the entity and load its components.
            var entity = base.CreateEntity(prototypeName, out metadata, context);

            // Now, perform server-specific logic if the entity was created from a prototype.
            if (prototypeName != null && PrototypeManager.TryIndex<EntityPrototype>(prototypeName, out var prototype))
            {
                // This is a network optimization: if a component's state matches the prototype,
                // we don't need to send its data to the client, as the client can load it locally.
                ClearTicks(entity, prototype);
            }

            return entity;
        }

        /// <inheritdoc />
        internal override void SetLifeStage(MetaDataComponent meta, EntityLifeStage stage)
        {
            base.SetLifeStage(meta, stage);
            // Ensure the PVS system is aware of lifecycle changes (e.g., entity deletion).
            _pvs.SyncMetadata(meta);
        }

        /// <summary>
        /// Clears the creation and last-modified ticks on components that are identical to the entity's prototype.
        /// This prevents redundant data from being sent to clients when an entity is first created.
        /// </summary>
        private void ClearTicks(EntityUid entity, EntityPrototype prototype)
        {
            foreach (var (netId, component) in GetNetComponents(entity))
            {
                // Only clear ticks for components defined in the prototype.
                // Other components (e.g., ContainerManager) might be added programmatically
                // and their state will need to be sent.
                var compName = ComponentFactory.GetComponentName(netId);
                if (prototype.Components.ContainsKey(compName))
                    component.ClearTicks();
            }
        }

        #endregion

        #region Networking and Event Broadcasting

        public override IEntityNetworkManager EntityNetManager => this;

        public event EventHandler<object>? ReceivedSystemMessage;

        public void SetupNetworking()
        {
            _networkManager.RegisterNetMessage<MsgEntity>(HandleEntityNetworkMessage);
            _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
            _configurationManager.OnValueChanged(CVars.NetLogLateMsg, b => _logLateMessages = b, true);
        }

        public uint GetLastMessageSequence(ICommonSession? session)
        {
            return session == null ? default : _lastProcessedSequences.GetValueOrDefault(session);
        }

        /// <inheritdoc />
        public override void RaiseSharedEvent<T>(T message, EntityUid? user = null)
        {
            var filter = user != null
                ? Filter.Broadcast().RemoveWhereAttachedEntity(e => e == user.Value)
                : Filter.Broadcast();

            foreach (var session in filter.Recipients)
            {
                EntityNetManager.SendSystemNetworkMessage(message, session.Channel);
            }
        }

        public override void RaiseSharedEvent<T>(T message, ICommonSession? user = null)
        {
            var filter = user != null
                ? Filter.Broadcast().RemovePlayer(user)
                : Filter.Broadcast();

            foreach (var session in filter.Recipients)
            {
                EntityNetManager.SendSystemNetworkMessage(message, session.Channel);
            }
        }

        public void SendSystemNetworkMessage(EntityEventArgs message, bool recordReplay = true)
        {
            var msg = new MsgEntity
            {
                Type = EntityMessageType.SystemMessage,
                SystemMessage = message,
                SourceTick = CurrentTick
            };

            if (recordReplay)
                _replay.RecordServerMessage(message);

            _networkManager.ServerSendToAll(msg);
        }

        public void SendSystemNetworkMessage(EntityEventArgs message, INetChannel targetConnection)
        {
            var msg = new MsgEntity
            {
                Type = EntityMessageType.SystemMessage,
                SystemMessage = message,
                SourceTick = CurrentTick
            };

            _networkManager.ServerSendMessage(msg, targetConnection);
        }

        private void HandleEntityNetworkMessage(MsgEntity message)
        {
            if (_logLateMessages && message.SourceTick < CurrentTick)
            {
                _netEntSawmill.Warning(
                    "Got late MsgEntity! Diff: {Diff}, Player: {Player}, Msg: {Msg}",
                    (int)CurrentTick.Value - (int)message.SourceTick.Value,
                    message.MsgChannel.UserName,
                    message.SystemMessage);
            }

            _networkMessageQueue.Add(message);
        }

        private void DispatchEntityNetworkMessage(MsgEntity message)
        {
            if (!message.MsgChannel.IsConnected)
                return;

            var player = _playerManager.GetSessionByChannel(message.MsgChannel);

            // FIX: Added null check. The player might have disconnected between message
            // reception and processing.
            if (player == null)
                return;

            if (message.Sequence != 0 && _lastProcessedSequences[player] < message.Sequence)
            {
                _lastProcessedSequences[player] = message.Sequence;
            }

#if EXCEPTION_TOLERANCE
            try
#endif
            {
                if (message.Type == EntityMessageType.SystemMessage && message.SystemMessage != null)
                {
                    if (message.SystemMessage is not { } msg)
                        return;

                    // Raise the base, non-session-specific event for general listeners.
                    ReceivedSystemMessage?.Invoke(this, msg);

                    // Now, create and raise the strongly-typed session message through the EventBus.
                    var msgType = msg.GetType();
                    var sessionType = typeof(EntitySessionMessage<>).MakeGenericType(msgType);
                    var sessionMsg = Activator.CreateInstance(sessionType, new EntitySessionEventArgs(player), msg)!;

                    // We must be extremely specific to resolve the ambiguity. We want the generic RaiseEvent method
                    // that takes two arguments, where the second argument is NOT a by-ref parameter.
                    var method = typeof(IBroadcastEventBus).GetMethods()
                        .Single(m =>
                            m.Name == nameof(IBroadcastEventBus.RaiseEvent) &&
                            m.IsGenericMethodDefinition &&
                            m.GetParameters().Length == 2 &&
                            !m.GetParameters()[1].ParameterType.IsByRef);

                    var generic = method.MakeGenericMethod(sessionType);
                    generic.Invoke(EventBus, new object[] { EventSource.Network, sessionMsg });
                }
            }
#if EXCEPTION_TOLERANCE
            catch (Exception e)
            {
                _runtimeLog.LogException(e, $"{nameof(DispatchEntityNetworkMessage)}({message.Type})");
            }
#endif
        }

        #endregion

        #region Event Handlers and Helpers

        private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
        {
            switch (args.NewStatus)
            {
                case SessionStatus.Connected:
                    _lastProcessedSequences.Add(args.Session, 0);
                    break;

                case SessionStatus.Disconnected:
                    _lastProcessedSequences.Remove(args.Session);
                    break;
            }
        }


        internal sealed class MessageSequenceComparer : IComparer<MsgEntity>

        {
            public int Compare(MsgEntity? x, MsgEntity? y)
            {
                // Handle nulls gracefully.
                if (x == null && y == null) return 0;
                if (x == null) return 1;
                if (y == null) return -1;

                // This is the CORRECT inverted logic. By comparing Y to X, we make the Max-Heap
                // prioritize the item with the SMALLEST tick value, effectively turning it into a Min-Heap.
                var tickCmp = y.SourceTick.CompareTo(x.SourceTick);
                if (tickCmp != 0)
                    return tickCmp;

                // If ticks are equal, sort by sequence number (also inverted).
                return y.Sequence.CompareTo(x.Sequence);
            }
        }

        #endregion
    }
}
