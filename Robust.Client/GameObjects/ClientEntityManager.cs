// Filename: Robust.Client/GameObjects/ClientEntityManager.cs

using System;
using System.Collections.Generic;
using Prometheus;
using Robust.Client.GameStates;
using Robust.Client.Player;
using Robust.Client.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
using System.Linq;

#nullable enable

namespace Robust.Client.GameObjects
{
    /// <summary>
    /// The client-side implementation of the <see cref="IEntityManager"/>.
    /// Manages client-specific logic including prediction, game state application, and client-side entities.
    /// </summary>
    public sealed partial class ClientEntityManager : EntityManager, IClientEntityManagerInternal
    {

        #region Dependencies and Internal State

        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IClientNetManager _networkManager = default!;
        [Dependency] private readonly IClientGameTiming _gameTiming = default!;
        [Dependency] private readonly IClientGameStateManager _stateMan = default!;
        [Dependency] private readonly IBaseClient _client = default!;
        [Dependency] private readonly IReplayRecordingManager _replayRecording = default!;

        private readonly PriorityQueue<(uint seq, MsgEntity msg)> _networkMessageQueue = new(new MessageTickComparer());
        private uint _incomingMsgSequence;

        internal event Action? AfterStartup;
        internal event Action? AfterShutdown;

        #endregion

        #region Lifecycle Overrides

        public override void Initialize()
        {
            SetupNetworking();
            ReceivedSystemMessage += (_, systemMsg) => EventBus.RaiseEvent(EventSource.Network, systemMsg);

            base.Initialize();
        }

        public override void Startup()
        {
            base.Startup();
            AfterStartup?.Invoke();
        }

        public override void Shutdown()
        {
            base.Shutdown();
            AfterShutdown?.Invoke();
        }

        public override void FlushEntities()
        {
            // The server doesn't send deletion messages during client shutdown. We must clear
            // all entities manually to prevent stale data issues on reconnect.
            // This is especially important for PVS, which has its own entity lists.
            _stateMan.Reset();

            using var _ = _gameTiming.StartStateApplicationArea();
            base.FlushEntities();
        }

        public override void TickUpdate(float frameTime, bool noPredictions, Histogram? histogram)
        {
            using (histogram?.WithLabels("EntityNet").NewTimer())
            {
                // Process any queued network messages that are due for the last "real" tick
                // (before prediction starts).
                while (_networkMessageQueue.Count > 0 && _networkMessageQueue.Peek().msg.SourceTick <= _gameTiming.LastRealTick)
                {
                    var (_, msg) = _networkMessageQueue.Take();
                    DispatchReceivedNetworkMsg(msg);
                }
            }

            base.TickUpdate(frameTime, noPredictions, histogram);
        }

        #endregion

        #region Entity Management and Prediction

        // This is an explicit interface implementation, not an override. It's fine.
        EntityUid IClientEntityManagerInternal.CreateEntity(string? prototypeName, out MetaDataComponent metadata)
        {
            return base.CreateEntity(prototypeName, out metadata);
        }

        /// <inheritdoc />
        public override void QueueDeleteEntity(EntityUid? uid)
        {
            if (uid == null)
                return;

            if (IsClientSide(uid.Value))
            {
                // This is a purely client-side entity (e.g., UI effect). Delete it normally.
                base.QueueDeleteEntity(uid);
                return;
            }

            // A networked entity cannot be truly deleted by the client.
            // This is likely a predictive action. Instead of deleting, we log an error in case this was not intended.
            if (_client.RunLevel is ClientRunLevel.Connected or ClientRunLevel.InGame)
                LogManager.RootSawmill.Error($"Attempted to queue deletion of a networked entity: {ToPrettyString(uid.Value)}. This is not supported. Trace: {Environment.StackTrace}");
        }

        /// <inheritdoc />
        public override void PredictedDeleteEntity(Entity<MetaDataComponent?, TransformComponent?> ent)
        {
            if (!MetaQuery.Resolve(ent.Owner, ref ent.Comp1) || ent.Comp1.EntityLifeStage >= EntityLifeStage.Terminating)
                return;

            // For client-side entities, we can delete them immediately.
            if (ent.Comp1.NetEntity.IsClientSide())
            {
                DeleteEntity(ent.Owner, ent.Comp1, ent.Comp2!); // Comp2 is resolved implicitly by DeleteEntity logic.
                return;
            }

            // For networked entities, "predictive deletion" means moving it to null-space.
            // The entity will be properly deleted when the server confirms it via a game state update.
            if (TransformQuery.Resolve(ent.Owner, ref ent.Comp2))
                _xforms.DetachEntity(ent.Owner, ent.Comp2);
        }

        /// <inheritdoc />
        public override void PredictedQueueDeleteEntity(Entity<MetaDataComponent?> ent)
        {
             if (IsQueuedForDeletion(ent.Owner) || !MetaQuery.Resolve(ent.Owner, ref ent.Comp) || ent.Comp.EntityLifeStage >= EntityLifeStage.Terminating)
                 return;

            if (ent.Comp.NetEntity.IsClientSide())
            {
                base.QueueDeleteEntity(ent.Owner);
            }
            else if (TransformQuery.TryComp(ent.Owner, out var xform))
            {
                _xforms.DetachEntity(ent.Owner, xform);
            }
        }

        [Obsolete("use variant without TransformComponent")]
        public override void PredictedQueueDeleteEntity(Entity<MetaDataComponent?, TransformComponent?> ent)
            => PredictedQueueDeleteEntity(new Entity<MetaDataComponent?>(ent.Owner, ent.Comp1));

        #endregion

        #region Dirtying Overrides (Prediction)

        /// <inheritdoc />
        public override void DirtyEntity(EntityUid uid, MetaDataComponent? meta = null)
        {
            // Client only dirties components during prediction.
            if (_gameTiming.InPrediction)
                base.DirtyEntity(uid, meta);
        }

        /// <inheritdoc />
        public override void Dirty(EntityUid uid, IComponent component, MetaDataComponent? meta = null)
        {
            if (_gameTiming.InPrediction)
                base.Dirty(uid, component, meta);
        }

        /// <inheritdoc />
        public override void Dirty<T>(Entity<T> ent, MetaDataComponent? meta = null)
        {
            if (_gameTiming.InPrediction)
                base.Dirty(ent, meta);
        }

        /// <inheritdoc />
        public override void Dirty<T1, T2>(Entity<T1, T2> ent, MetaDataComponent? meta = null)
        {
            if (_gameTiming.InPrediction)
                base.Dirty(ent, meta);
        }

        /// <inheritdoc />
        public override void Dirty<T1, T2, T3>(Entity<T1, T2, T3> ent, MetaDataComponent? meta = null)
        {
            if (_gameTiming.InPrediction)
                base.Dirty(ent, meta);
        }

        /// <inheritdoc />
        public override void Dirty<T1, T2, T3, T4>(Entity<T1, T2, T3, T4> ent, MetaDataComponent? meta = null)
        {
            if (_gameTiming.InPrediction)
                base.Dirty(ent, meta);
        }

        #endregion

        #region Event Broadcasting

        public override void RaisePredictiveEvent<T>(T msg)
        {
            var session = _playerManager.LocalSession;

            // A predictive event can only be raised if there is a local player session.
            // If we're not in a game (e.g., in the main menu), this will be null.
            if (session == null)
            {
                LogManager.RootSawmill.Warning($"Attempted to raise a predictive event ({typeof(T).Name}) with no active player session.");
                return;
            }

            // Inform the game state manager that we are sending a message, and get its sequence number for prediction.
            var sequence = _stateMan.SystemMessageDispatched(msg);

            // Call the method directly on this instance to resolve the ambiguity between
            // IEntityNetworkManager.SendSystemNetworkMessage(msg, bool) and
            // ClientEntityManager.SendSystemNetworkMessage(msg, uint).
            SendSystemNetworkMessage(msg, sequence);

            // If prediction is disabled, we don't apply the event locally. We wait for the server's authoritative state.
            if (!_stateMan.IsPredictionEnabled && _client.RunLevel != ClientRunLevel.SinglePlayerGame)
                return;

            // Ensure we are in a valid prediction context before applying the event.
            DebugTools.Assert(
                (_gameTiming.InPrediction && _gameTiming.IsFirstTimePredicted) || _client.RunLevel == ClientRunLevel.SinglePlayerGame,
                "Predictive event raised outside of a valid prediction context.");

            // Raise the event locally for immediate feedback (this is the "prediction" part).
            var eventArgs = new EntitySessionEventArgs(session);
            EventBus.RaiseEvent(EventSource.Local, msg);
            EventBus.RaiseEvent(EventSource.Local, new EntitySessionMessage<T>(eventArgs, msg));
        }
        /// <inheritdoc />
        public override void RaiseSharedEvent<T>(T message, EntityUid? user = null)
        {
            // Only raise shared events locally if we are the user and it's the first prediction tick.
            if (user == null || user != _playerManager.LocalEntity || !_gameTiming.IsFirstTimePredicted)
                return;

            EventBus.RaiseEvent(EventSource.Local, ref message);
        }

        /// <inheritdoc />
        public override void RaiseSharedEvent<T>(T message, ICommonSession? user = null)
        {
            // Only raise shared events locally if we are the user and it's the first prediction tick.
            if (user == null || user != _playerManager.LocalSession || !_gameTiming.IsFirstTimePredicted)
                return;

            EventBus.RaiseEvent(EventSource.Local, ref message);
        }

        #endregion

        #region Networking Implementation

        public override IEntityNetworkManager EntityNetManager => this;

        public event EventHandler<object>? ReceivedSystemMessage;

        public void SetupNetworking()
        {
            _networkManager.RegisterNetMessage<MsgEntity>(HandleEntityNetworkMessage);
        }

        public void SendSystemNetworkMessage(EntityEventArgs message, bool recordReplay = true)
        {
            // The '0u' suffix explicitly tells the compiler this is a uint, resolving the ambiguity.
            SendSystemNetworkMessage(message, 0u);
        }

        public void SendSystemNetworkMessage(EntityEventArgs message, uint sequence)
        {
            var msg = new MsgEntity
            {
                Type = EntityMessageType.SystemMessage,
                SystemMessage = message,
                SourceTick = _gameTiming.CurTick,
                Sequence = sequence
            };

            _networkManager.ClientSendMessage(msg);
        }

        public void SendSystemNetworkMessage(EntityEventArgs message, INetChannel? channel)
        {
            // The client can only send messages to the server, not to arbitrary channels.
            throw new NotSupportedException();
        }

        private void HandleEntityNetworkMessage(MsgEntity message)
        {
            // If the message is old, dispatch it immediately. Otherwise, queue it for its target tick.
            if (message.SourceTick <= _gameTiming.LastRealTick)
            {
                DispatchReceivedNetworkMsg(message);
                return;
            }

            _networkMessageQueue.Add((++_incomingMsgSequence, message));
        }

        private void DispatchReceivedNetworkMsg(MsgEntity message)
        {
            if (message.Type == EntityMessageType.SystemMessage && message.SystemMessage != null)
            {
                _replayRecording.RecordReplayMessage(message.SystemMessage);
                DispatchReceivedNetworkMsg(message.SystemMessage);
            }
        }

        public void DispatchReceivedNetworkMsg(EntityEventArgs msg)
        {
            if (_playerManager.LocalSession == null) return;

               var session = _playerManager.LocalSession!;

            // Raise the base, non-session-specific event for general listeners.
            ReceivedSystemMessage?.Invoke(this, msg);

            // Now, create and raise the strongly-typed session message through the EventBus using reflection.
            var msgType = msg.GetType();
            var sessionType = typeof(EntitySessionMessage<>).MakeGenericType(msgType);
            var sessionMsg = Activator.CreateInstance(sessionType, new EntitySessionEventArgs(session), msg)!;

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

        private sealed class MessageTickComparer : IComparer<(uint seq, MsgEntity msg)>
        {
            public int Compare((uint seq, MsgEntity msg) x, (uint seq, MsgEntity msg) y)
            {
                // Invert tick comparison to make the PriorityQueue a Min-Heap.
                var cmp = x.msg.SourceTick.CompareTo(y.msg.SourceTick);
                if (cmp != 0)
                    return cmp;

                // Use sequence number as a tie-breaker.
                return x.seq.CompareTo(y.seq);
            }
        }

        #endregion
    }
}
