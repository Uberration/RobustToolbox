// Filename: RobustToolbox/Robust.Shared/Network/Messages/MsgEntity.cs

using System;
using System.IO;
using Lidgren.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

#nullable enable // CHANGE: Enabled nullable reference types for better code safety.

namespace Robust.Shared.Network.Messages
{
    // This enum was likely moved here from the old EntityManager.cs file.
    public enum EntityMessageType : byte
    {
        Error = 0,
        SystemMessage
    }

    public sealed class MsgEntity : NetMessage
    {
        public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

        public EntityMessageType Type { get; set; }

        // CHANGE: Made explicitly nullable. The payload is only present for SystemMessage type.
        public EntityEventArgs? SystemMessage { get; set; }

        // CHANGE: Removed NetId and EntityUid properties as they were never serialized and were dead code.
        // The target entity is implicitly contained within the SystemMessage (EntityEventArgs).

        public uint Sequence { get; set; }
        public GameTick SourceTick { get; set; }

        public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
        {
            Type = (EntityMessageType)buffer.ReadByte();
            SourceTick = buffer.ReadGameTick();
            Sequence = buffer.ReadUInt32();

            switch (Type)
            {
                case EntityMessageType.SystemMessage:
                {
                    var length = buffer.ReadVariableInt32();
                    using var stream = RobustMemoryManager.GetMemoryStream(length);
                    buffer.ReadAlignedMemory(stream, length);
                    SystemMessage = serializer.Deserialize<EntityEventArgs>(stream);
                    break;
                }
                // CHANGE: Added handling for the Error case to ensure logic is complete.
                case EntityMessageType.Error:
                    // No payload for this message type.
                    break;
            }
        }

        public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
        {
            buffer.Write((byte)Type);
            buffer.Write(SourceTick);
            buffer.Write(Sequence);

            switch (Type)
            {
                case EntityMessageType.SystemMessage:
                {
                    // CHANGE: Critical performance fix. Use a pooled MemoryStream to avoid heap allocations.
                    using var stream = RobustMemoryManager.GetMemoryStream();

                    // Ensure we don't try to serialize a null message.
                    if (SystemMessage == null)
                        throw new InvalidOperationException("Attempted to send a SystemMessage with a null payload.");

                    serializer.Serialize(stream, SystemMessage);

                    buffer.WriteVariableInt32((int)stream.Length);
                    buffer.Write(stream.AsSpan());
                    break;
                }
                // CHANGE: Added handling for the Error case to ensure logic is complete.
                case EntityMessageType.Error:
                    // No payload for this message type.
                    break;
            }
        }

        public override string ToString()
        {
            var timingData = $"T: {SourceTick} S: {Sequence}";
            switch (Type)
            {
                case EntityMessageType.Error:
                    return "MsgEntity Error";
                case EntityMessageType.SystemMessage:
                    return $"MsgEntity Comp, {timingData}, {SystemMessage}";
                default:
                    // CHANGE: Improved exception for better debugging.
                    throw new ArgumentOutOfRangeException(nameof(Type), Type, "Unknown message type.");
            }
        }
    }
}
