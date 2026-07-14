using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace SailwindCoop.Networking.Packets
{
    /// <summary>
    /// Binary packet serialization for network communication.
    /// Packet structure: [PacketType:byte][PayloadLength:int][Payload:bytes]
    /// </summary>
    public static class PacketSerializer
    {
        private const int HeaderSize = 1 + 4; // PacketType (1 byte) + PayloadLength (4 bytes)

        /// <summary>
        /// Creates a packet with the specified type and payload.
        /// </summary>
        /// <param name="type">The packet type.</param>
        /// <param name="writePayload">Action to write payload data. Can be null for empty packets.</param>
        /// <returns>Complete packet as byte array.</returns>
        public static byte[] WritePacket(PacketType type, Action<BinaryWriter> writePayload = null)
        {
            using (var payloadStream = new MemoryStream())
            {
                if (writePayload != null)
                {
                    using (var writer = new BinaryWriter(payloadStream))
                    {
                        writePayload(writer);
                    }
                }

                var payload = payloadStream.ToArray();
                var packet = new byte[HeaderSize + payload.Length];

                packet[0] = (byte)type;

                // Write payload length as little-endian int
                packet[1] = (byte)payload.Length;
                packet[2] = (byte)(payload.Length >> 8);
                packet[3] = (byte)(payload.Length >> 16);
                packet[4] = (byte)(payload.Length >> 24);

                // Copy payload
                if (payload.Length > 0)
                {
                    Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);
                }

                return packet;
            }
        }

        /// <summary>
        /// Reads a packet and returns its type and a reader for the payload.
        /// </summary>
        /// <param name="data">Raw packet data.</param>
        /// <returns>Tuple of PacketType and BinaryReader positioned at payload start.</returns>
        /// <exception cref="ArgumentException">Thrown if packet data is invalid.</exception>
        public static (PacketType type, BinaryReader reader) ReadPacket(byte[] data)
        {
            if (data == null || data.Length < HeaderSize)
            {
                throw new ArgumentException("Invalid packet: data is null or too short.", nameof(data));
            }

            var type = (PacketType)data[0];

            // Read payload length as little-endian int
            int payloadLength = data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24);

            if (payloadLength < 0 || data.Length < HeaderSize + payloadLength)
            {
                throw new ArgumentException($"Invalid packet: declared payload length {payloadLength} exceeds available data.", nameof(data));
            }

            // Create a memory stream over the payload portion only
            var payloadStream = new MemoryStream(data, HeaderSize, payloadLength, writable: false);
            var reader = new BinaryReader(payloadStream);

            return (type, reader);
        }

        /// <summary>
        /// Attempts to read a packet without throwing exceptions.
        /// </summary>
        /// <param name="data">Raw packet data.</param>
        /// <param name="type">The parsed packet type.</param>
        /// <param name="reader">Reader for the payload, or null on failure.</param>
        /// <returns>True if packet was successfully parsed.</returns>
        public static bool TryReadPacket(byte[] data, out PacketType type, out BinaryReader reader)
        {
            type = default;
            reader = null;

            if (data == null || data.Length < HeaderSize)
            {
                return false;
            }

            type = (PacketType)data[0];

            int payloadLength = data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24);

            if (payloadLength < 0 || data.Length < HeaderSize + payloadLength)
            {
                return false;
            }

            var payloadStream = new MemoryStream(data, HeaderSize, payloadLength, writable: false);
            reader = new BinaryReader(payloadStream);

            return true;
        }

        /// <summary>
        /// Gets the packet type from raw data without parsing the full packet.
        /// </summary>
        /// <param name="data">Raw packet data.</param>
        /// <returns>The packet type, or null if data is invalid.</returns>
        public static PacketType? PeekPacketType(byte[] data)
        {
            if (data == null || data.Length < 1)
            {
                return null;
            }

            return (PacketType)data[0];
        }

        #region Boat Data Serialization

        /// <summary>
        /// Writes a complete boat world state packet for initial sync.
        /// </summary>
        public static void WriteBoatWorldState(BinaryWriter writer, BoatWorldStatePacket packet)
        {
            // Boats array
            writer.Write(packet.Boats?.Length ?? 0);
            if (packet.Boats != null)
            {
                foreach (var boat in packet.Boats)
                {
                    WriteNetworkBoatData(writer, boat);
                }
            }

            // Current boat name
            writer.Write(packet.CurrentBoatName ?? "");

            // Wind state
            WriteVector3(writer, packet.WindState);

            // Weather state
            WriteWeatherState(writer, packet.WeatherState);

            // Host player position/rotation
            WriteVector3(writer, packet.HostPlayerPosition);
            WriteQuaternion(writer, packet.HostPlayerRotation);
            writer.Write(packet.IsHostOnBoat);  // BUG-027 fix

            // BUG-018 fix: Host's offset and region for cross-region join
            WriteVector3(writer, packet.HostOffset);
            writer.Write(packet.HostRegionName ?? "");
            writer.Write(packet.NearestPortName ?? "");

            // World items (not parented to any boat)
            writer.Write(packet.WorldItems?.Length ?? 0);
            if (packet.WorldItems != null)
            {
                foreach (var item in packet.WorldItems)
                {
                    WriteNetworkSaveData(writer, item);
                }
            }

            writer.Write(packet.IsRecovery);  // BS2
        }

        /// <summary>
        /// Reads a complete boat world state packet.
        /// </summary>
        public static BoatWorldStatePacket ReadBoatWorldState(BinaryReader reader)
        {
            var packet = new BoatWorldStatePacket();

            // Boats array
            int boatCount = reader.ReadInt32();
            packet.Boats = new NetworkBoatData[boatCount];
            for (int i = 0; i < boatCount; i++)
            {
                packet.Boats[i] = ReadNetworkBoatData(reader);
            }

            // Current boat name
            packet.CurrentBoatName = reader.ReadString();

            // Wind state
            packet.WindState = ReadVector3(reader);

            // Weather state
            packet.WeatherState = ReadWeatherState(reader);

            // Host player position/rotation
            packet.HostPlayerPosition = ReadVector3(reader);
            packet.HostPlayerRotation = ReadQuaternion(reader);
            packet.IsHostOnBoat = reader.ReadBoolean();  // BUG-027 fix

            // BUG-018 fix: Host's offset and region for cross-region join
            packet.HostOffset = ReadVector3(reader);
            packet.HostRegionName = reader.ReadString();
            packet.NearestPortName = reader.ReadString();

            // World items (not parented to any boat)
            int worldItemCount = reader.ReadInt32();
            packet.WorldItems = new NetworkSaveData[worldItemCount];
            for (int i = 0; i < worldItemCount; i++)
            {
                packet.WorldItems[i] = ReadNetworkSaveData(reader);
            }

            packet.IsRecovery = reader.ReadBoolean();  // BS2

            return packet;
        }

        /// <summary>
        /// Writes a single boat's network data.
        /// </summary>
        public static void WriteNetworkBoatData(BinaryWriter writer, NetworkBoatData boat)
        {
            // Identity
            writer.Write(boat.Name ?? "");

            // Transform
            WriteVector3(writer, boat.Position);
            WriteQuaternion(writer, boat.Rotation);

            // Masts enabled
            writer.Write(boat.MastsEnabled?.Length ?? 0);
            if (boat.MastsEnabled != null)
            {
                foreach (var enabled in boat.MastsEnabled)
                {
                    writer.Write(enabled);
                }
            }

            // Sails
            writer.Write(boat.Sails?.Length ?? 0);
            if (boat.Sails != null)
            {
                foreach (var sail in boat.Sails)
                {
                    WriteNetworkSailData(writer, sail);
                }
            }

            // Part options
            writer.Write(boat.PartActiveOptions?.Length ?? 0);
            if (boat.PartActiveOptions != null)
            {
                foreach (var opt in boat.PartActiveOptions)
                {
                    writer.Write(opt);
                }
            }

            // Items (using complete save data)
            writer.Write(boat.Items?.Length ?? 0);
            if (boat.Items != null)
            {
                foreach (var item in boat.Items)
                {
                    WriteNetworkSaveData(writer, item);
                }
            }

            // Rope lengths
            writer.Write(boat.RopeLengths?.Length ?? 0);
            if (boat.RopeLengths != null)
            {
                foreach (var len in boat.RopeLengths)
                {
                    writer.Write(len);
                }
            }

            // Anchor
            writer.Write(boat.IsAnchored);
            writer.Write(boat.AnchorRopeLength);

            // Mooring ropes
            writer.Write(boat.MooringRopes?.Length ?? 0);
            if (boat.MooringRopes != null)
            {
                foreach (var mooring in boat.MooringRopes)
                {
                    writer.Write(mooring.IsMoored);
                    writer.Write(mooring.DockPosition.x);
                    writer.Write(mooring.DockPosition.y);
                    writer.Write(mooring.DockPosition.z);
                    writer.Write(mooring.LengthSquared);
                    writer.Write((byte)mooring.TargetKind);
                    writer.Write(mooring.TowBoatName ?? "");
                    writer.Write(mooring.CleatPath ?? "");
                }
            }

            // Damage
            writer.Write(boat.WaterLevel);
            writer.Write(boat.HullDamage);
            writer.Write(boat.Oakum);

            // Ownership
            writer.Write(boat.IsOwned);

            // Dirt texture
            if (boat.DirtTexture != null && boat.DirtTexture.Length > 0)
            {
                writer.Write(true);  // has texture
                writer.Write(boat.DirtTexture.Length);
                writer.Write(boat.DirtTexture);
            }
            else
            {
                writer.Write(false);  // no texture
            }
        }

        /// <summary>
        /// Reads a single boat's network data.
        /// </summary>
        public static NetworkBoatData ReadNetworkBoatData(BinaryReader reader)
        {
            var boat = new NetworkBoatData();

            // Identity
            boat.Name = reader.ReadString();

            // Transform
            boat.Position = ReadVector3(reader);
            boat.Rotation = ReadQuaternion(reader);

            // Masts enabled
            int mastCount = reader.ReadInt32();
            boat.MastsEnabled = new bool[mastCount];
            for (int i = 0; i < mastCount; i++)
            {
                boat.MastsEnabled[i] = reader.ReadBoolean();
            }

            // Sails
            int sailCount = reader.ReadInt32();
            boat.Sails = new NetworkSailData[sailCount];
            for (int i = 0; i < sailCount; i++)
            {
                boat.Sails[i] = ReadNetworkSailData(reader);
            }

            // Part options
            int partCount = reader.ReadInt32();
            boat.PartActiveOptions = new int[partCount];
            for (int i = 0; i < partCount; i++)
            {
                boat.PartActiveOptions[i] = reader.ReadInt32();
            }

            // Items (using complete save data)
            int itemCount = reader.ReadInt32();
            boat.Items = new NetworkSaveData[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                boat.Items[i] = ReadNetworkSaveData(reader);
            }

            // Rope lengths
            int ropeCount = reader.ReadInt32();
            boat.RopeLengths = new float[ropeCount];
            for (int i = 0; i < ropeCount; i++)
            {
                boat.RopeLengths[i] = reader.ReadSingle();
            }

            // Anchor
            boat.IsAnchored = reader.ReadBoolean();
            boat.AnchorRopeLength = reader.ReadSingle();

            // Mooring ropes
            int mooringCount = reader.ReadInt32();
            boat.MooringRopes = new NetworkMooringData[mooringCount];
            for (int i = 0; i < mooringCount; i++)
            {
                boat.MooringRopes[i] = new NetworkMooringData
                {
                    IsMoored = reader.ReadBoolean(),
                    DockPosition = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    ),
                    LengthSquared = reader.ReadSingle(),
                    TargetKind = (MooringTargetKind)reader.ReadByte(),
                    TowBoatName = reader.ReadString(),
                    CleatPath = reader.ReadString()
                };
            }

            // Damage
            boat.WaterLevel = reader.ReadSingle();
            boat.HullDamage = reader.ReadSingle();
            boat.Oakum = reader.ReadSingle();

            // Ownership
            boat.IsOwned = reader.ReadBoolean();

            // Dirt texture
            if (reader.ReadBoolean())  // has texture
            {
                int length = reader.ReadInt32();
                boat.DirtTexture = reader.ReadBytes(length);
            }

            return boat;
        }

        /// <summary>
        /// Writes sail data for network sync.
        /// </summary>
        public static void WriteNetworkSailData(BinaryWriter writer, NetworkSailData sail)
        {
            writer.Write(sail.PrefabIndex);
            writer.Write(sail.MastIndex);
            writer.Write(sail.InstallHeight);
            writer.Write(sail.MinAngle);
            writer.Write(sail.MaxAngle);
            writer.Write(sail.Health);
            writer.Write(sail.Color);
            writer.Write(sail.ScaleY);  // BS1
            writer.Write(sail.ScaleZ);  // BS1
        }

        /// <summary>
        /// Reads sail data from network.
        /// </summary>
        public static NetworkSailData ReadNetworkSailData(BinaryReader reader)
        {
            return new NetworkSailData
            {
                PrefabIndex = reader.ReadInt32(),
                MastIndex = reader.ReadInt32(),
                InstallHeight = reader.ReadSingle(),
                MinAngle = reader.ReadSingle(),
                MaxAngle = reader.ReadSingle(),
                Health = reader.ReadSingle(),
                Color = reader.ReadInt32(),
                ScaleY = reader.ReadSingle(),  // BS1
                ScaleZ = reader.ReadSingle()   // BS1
            };
        }

        /// <summary>
        /// Writes complete item save data for network sync.
        /// Mirrors SavePrefabData structure for full item state capture.
        /// </summary>
        public static void WriteNetworkSaveData(BinaryWriter writer, NetworkSaveData item)
        {
            // Identity
            writer.Write(item.InstanceId);
            writer.Write(item.PrefabIndex);

            // Transform
            WriteVector3(writer, item.Position);
            WriteQuaternion(writer, item.Rotation);
            writer.Write(item.IsWorldPosition);
            writer.Write(item.ParentBoatName ?? "");

            // Item state
            writer.Write(item.IsSold);
            writer.Write(item.Health);
            writer.Write(item.Amount);
            writer.Write(item.InventorySlot);
            writer.Write(item.CrateId);
            writer.Write(item.MissionIndex);
            writer.Write(item.ParentObject);
            writer.Write(item.DaysInStorage);

            // Extra values
            writer.Write(item.ExtraValue0);
            writer.Write(item.ExtraValue1);
            writer.Write(item.ExtraValue2);
            writer.Write(item.ExtraValue3);
            writer.Write(item.ExtraValue4);

            // Chart data
            writer.Write(item.HasChartData);
            if (item.HasChartData)
            {
                WriteNetworkChartData(writer, item.ChartData);
            }
        }

        /// <summary>
        /// Reads complete item save data from network.
        /// </summary>
        public static NetworkSaveData ReadNetworkSaveData(BinaryReader reader)
        {
            var item = new NetworkSaveData();

            // Identity
            item.InstanceId = reader.ReadInt32();
            item.PrefabIndex = reader.ReadInt32();

            // Transform
            item.Position = ReadVector3(reader);
            item.Rotation = ReadQuaternion(reader);
            item.IsWorldPosition = reader.ReadBoolean();
            item.ParentBoatName = reader.ReadString();

            // Item state
            item.IsSold = reader.ReadBoolean();
            item.Health = reader.ReadSingle();
            item.Amount = reader.ReadSingle();
            item.InventorySlot = reader.ReadInt32();
            item.CrateId = reader.ReadInt32();
            item.MissionIndex = reader.ReadInt32();
            item.ParentObject = reader.ReadInt32();
            item.DaysInStorage = reader.ReadInt32();

            // Extra values
            item.ExtraValue0 = reader.ReadSingle();
            item.ExtraValue1 = reader.ReadSingle();
            item.ExtraValue2 = reader.ReadSingle();
            item.ExtraValue3 = reader.ReadSingle();
            item.ExtraValue4 = reader.ReadSingle();

            // Chart data
            item.HasChartData = reader.ReadBoolean();
            if (item.HasChartData)
            {
                item.ChartData = ReadNetworkChartData(reader);
            }

            return item;
        }

        /// <summary>
        /// Writes chart/map data for network sync.
        /// </summary>
        public static void WriteNetworkChartData(BinaryWriter writer, NetworkChartData chartData)
        {
            // Lines
            writer.Write(chartData.Lines?.Length ?? 0);
            if (chartData.Lines != null)
            {
                foreach (var line in chartData.Lines)
                {
                    writer.Write(line.StartX);
                    writer.Write(line.StartY);
                    writer.Write(line.EndX);
                    writer.Write(line.EndY);
                    writer.Write(line.Color);
                }
            }

            // Points
            writer.Write(chartData.Points?.Length ?? 0);
            if (chartData.Points != null)
            {
                foreach (var point in chartData.Points)
                {
                    writer.Write(point.PosX);
                    writer.Write(point.PosY);
                }
            }
        }

        /// <summary>
        /// Reads chart/map data from network.
        /// </summary>
        public static NetworkChartData ReadNetworkChartData(BinaryReader reader)
        {
            var chartData = new NetworkChartData();

            // Lines
            int lineCount = reader.ReadInt32();
            chartData.Lines = new NetworkChartLine[lineCount];
            for (int i = 0; i < lineCount; i++)
            {
                chartData.Lines[i] = new NetworkChartLine
                {
                    StartX = reader.ReadSingle(),
                    StartY = reader.ReadSingle(),
                    EndX = reader.ReadSingle(),
                    EndY = reader.ReadSingle(),
                    Color = reader.ReadInt32()
                };
            }

            // Points
            int pointCount = reader.ReadInt32();
            chartData.Points = new NetworkChartPoint[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                chartData.Points[i] = new NetworkChartPoint
                {
                    PosX = reader.ReadSingle(),
                    PosY = reader.ReadSingle()
                };
            }

            return chartData;
        }

        /// <summary>
        /// Writes a Vector3 to the binary stream.
        /// </summary>
        public static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        /// <summary>
        /// Reads a Vector3 from the binary stream.
        /// </summary>
        public static Vector3 ReadVector3(BinaryReader reader)
        {
            return new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        /// <summary>
        /// Writes a Quaternion to the binary stream.
        /// </summary>
        public static void WriteQuaternion(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.x);
            writer.Write(q.y);
            writer.Write(q.z);
            writer.Write(q.w);
        }

        /// <summary>
        /// Reads a Quaternion from the binary stream.
        /// </summary>
        public static Quaternion ReadQuaternion(BinaryReader reader)
        {
            return new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        /// <summary>
        /// Writes a boat transform packet for continuous sync.
        /// </summary>
        public static void WriteBoatTransform(BinaryWriter writer, BoatTransformPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            WriteVector3(writer, packet.Position);
            WriteQuaternion(writer, packet.Rotation);
            WriteVector3(writer, packet.Velocity);
            WriteVector3(writer, packet.AngularVelocity);
            writer.Write(packet.IsAnchored);
            writer.Write(packet.IsPrimary);
        }

        /// <summary>
        /// Reads a boat transform packet.
        /// </summary>
        public static BoatTransformPacket ReadBoatTransform(BinaryReader reader)
        {
            return new BoatTransformPacket
            {
                BoatName = reader.ReadString(),
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                Velocity = ReadVector3(reader),
                AngularVelocity = ReadVector3(reader),
                IsAnchored = reader.ReadBoolean(),
                IsPrimary = reader.ReadBoolean()
            };
        }

        // Boat ownership packets. Write order MUST equal Read order.

        public static void WriteBoatOwnershipChanged(BinaryWriter writer, BoatOwnershipChangedPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.IsOwned);
        }

        public static BoatOwnershipChangedPacket ReadBoatOwnershipChanged(BinaryReader reader)
        {
            return new BoatOwnershipChangedPacket
            {
                BoatName = reader.ReadString(),
                IsOwned = reader.ReadBoolean()
            };
        }

        public static void WriteBoatPurchaseRequest(BinaryWriter writer, BoatPurchaseRequestPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
        }

        public static BoatPurchaseRequestPacket ReadBoatPurchaseRequest(BinaryReader reader)
        {
            return new BoatPurchaseRequestPacket
            {
                BoatName = reader.ReadString()
            };
        }

        #endregion

        #region Control Packets Serialization

        // === Control Packets ===

        public static void WriteRopeState(BinaryWriter writer, RopeStatePacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.RopeIndex);
            writer.Write(packet.RopeName ?? "");  // BUG-009: Add rope name for reliable sync
            writer.Write(packet.Length);
            writer.Write(packet.IsFinal);
        }

        public static RopeStatePacket ReadRopeState(BinaryReader reader)
        {
            return new RopeStatePacket
            {
                BoatName = reader.ReadString(),
                RopeIndex = reader.ReadInt32(),
                RopeName = reader.ReadString(),  // BUG-009: Read rope name
                Length = reader.ReadSingle(),
                IsFinal = reader.ReadBoolean()
            };
        }

        public static void WriteHelmState(BinaryWriter writer, HelmStatePacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.Input);
            writer.Write(packet.IsFinal);
        }

        public static HelmStatePacket ReadHelmState(BinaryReader reader)
        {
            return new HelmStatePacket
            {
                BoatName = reader.ReadString(),
                Input = reader.ReadSingle(),
                IsFinal = reader.ReadBoolean()
            };
        }

        public static void WriteHelmDenied(BinaryWriter writer, HelmDeniedPacket packet)
        {
            writer.Write(packet.BoatName);
        }

        public static HelmDeniedPacket ReadHelmDenied(BinaryReader reader)
        {
            return new HelmDeniedPacket
            {
                BoatName = reader.ReadString()
            };
        }

        public static void WriteAnchorEvent(BinaryWriter writer, AnchorEventPacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.IsSet);
            writer.Write(packet.RopeLength);
        }

        public static AnchorEventPacket ReadAnchorEvent(BinaryReader reader)
        {
            return new AnchorEventPacket
            {
                BoatName = reader.ReadString(),
                IsSet = reader.ReadBoolean(),
                RopeLength = reader.ReadSingle()
            };
        }

        public static void WriteMooringState(BinaryWriter writer, MooringStatePacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.RopeIndex);
            writer.Write(packet.IsMoored);
            writer.Write((byte)packet.TargetKind);
            writer.Write(packet.DockPosition.x);
            writer.Write(packet.DockPosition.y);
            writer.Write(packet.DockPosition.z);
            writer.Write(packet.LengthSquared);
            writer.Write(packet.TowBoatName ?? "");
            writer.Write(packet.CleatPath ?? "");
        }

        public static MooringStatePacket ReadMooringState(BinaryReader reader)
        {
            return new MooringStatePacket
            {
                BoatName = reader.ReadString(),
                RopeIndex = reader.ReadInt32(),
                IsMoored = reader.ReadBoolean(),
                TargetKind = (MooringTargetKind)reader.ReadByte(),
                DockPosition = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                ),
                LengthSquared = reader.ReadSingle(),
                TowBoatName = reader.ReadString(),
                CleatPath = reader.ReadString()
            };
        }

        public static void WriteApplyForce(BinaryWriter writer, ApplyForcePacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.Force.x);
            writer.Write(packet.Force.y);
            writer.Write(packet.Force.z);
            writer.Write(packet.WorldPoint.x);
            writer.Write(packet.WorldPoint.y);
            writer.Write(packet.WorldPoint.z);
            writer.Write(packet.IsSailPush);
        }

        public static ApplyForcePacket ReadApplyForce(BinaryReader reader)
        {
            return new ApplyForcePacket
            {
                BoatName = reader.ReadString(),
                Force = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                ),
                WorldPoint = new Vector3(
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle()
                ),
                IsSailPush = reader.ReadBoolean()
            };
        }

        // Event-based push sync serialization

        public static void WritePushStart(BinaryWriter writer, PushStartPacket packet)
        {
            writer.Write(packet.PushType);
            writer.Write(packet.BoatName);
            WriteVector3(writer, packet.Direction);
            WriteVector3(writer, packet.Position);
            writer.Write(packet.ForceMult);
            writer.Write(packet.SailIndex);
        }

        public static PushStartPacket ReadPushStart(BinaryReader reader)
        {
            return new PushStartPacket
            {
                PushType = reader.ReadByte(),
                BoatName = reader.ReadString(),
                Direction = ReadVector3(reader),
                Position = ReadVector3(reader),
                ForceMult = reader.ReadSingle(),
                SailIndex = reader.ReadInt32()
            };
        }

        public static void WritePushUpdate(BinaryWriter writer, PushUpdatePacket packet)
        {
            WriteVector3(writer, packet.Direction);
            WriteVector3(writer, packet.Position);
        }

        public static PushUpdatePacket ReadPushUpdate(BinaryReader reader)
        {
            return new PushUpdatePacket
            {
                Direction = ReadVector3(reader),
                Position = ReadVector3(reader)
            };
        }

        // PushStop has no data - nothing to serialize

        public static void WriteMooringRopeLength(BinaryWriter writer, MooringRopeLengthPacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.RopeIndex);
            writer.Write(packet.LengthSquared);
            writer.Write(packet.IsFinal);
        }

        public static MooringRopeLengthPacket ReadMooringRopeLength(BinaryReader reader)
        {
            return new MooringRopeLengthPacket
            {
                BoatName = reader.ReadString(),
                RopeIndex = reader.ReadInt32(),
                LengthSquared = reader.ReadSingle(),
                IsFinal = reader.ReadBoolean()
            };
        }

        public static void WriteHelmInput(BinaryWriter writer, HelmInputPacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.InputDelta);
        }

        public static HelmInputPacket ReadHelmInput(BinaryReader reader)
        {
            return new HelmInputPacket
            {
                BoatName = reader.ReadString(),
                InputDelta = reader.ReadSingle()
            };
        }

        public static void WriteHelmLock(BinaryWriter writer, HelmLockPacket packet)
        {
            writer.Write(packet.BoatName);
            writer.Write(packet.IsLocked);
        }

        public static HelmLockPacket ReadHelmLock(BinaryReader reader)
        {
            return new HelmLockPacket
            {
                BoatName = reader.ReadString(),
                IsLocked = reader.ReadBoolean()
            };
        }

        #endregion

        #region Weather Packets

        // ========== Weather Packets ==========

        public static void WriteWeatherState(BinaryWriter writer, WeatherStatePacket packet)
        {
            WriteVector3(writer, packet.Wind);
            writer.Write(packet.TargetWeatherIndex);
            writer.Write(packet.WeatherLerp);
            writer.Write(packet.RainIntensity);
            writer.Write(packet.RegionIndex);

            // Storm positions array
            writer.Write(packet.StormPositions?.Length ?? 0);
            if (packet.StormPositions != null)
            {
                foreach (var pos in packet.StormPositions)
                {
                    WriteVector3(writer, pos);
                }
            }

            // Active storm index (-1 if none)
            writer.Write(packet.ActiveStormIndex);

            // WavesInertia sync
            WriteQuaternion(writer, packet.WaveDirection);
            writer.Write(packet.WaveInertia);
            writer.Write(packet.WaveMagnitude);

            // Crest ocean time sync (wave phase)
            writer.Write(packet.OceanTime);

            // OceanUpdaterCrest crossfade drive-state inputs
            writer.Write(packet.HostCurrentMult);
            writer.Write(packet.HostWavesUp);
            writer.Write(packet.HostTargetInertiaAngle);
            writer.Write(packet.HostWindWavesWeight);
        }

        public static WeatherStatePacket ReadWeatherState(BinaryReader reader)
        {
            var packet = new WeatherStatePacket
            {
                Wind = ReadVector3(reader),
                TargetWeatherIndex = reader.ReadInt32(),
                WeatherLerp = reader.ReadSingle(),
                RainIntensity = reader.ReadSingle(),
                RegionIndex = reader.ReadInt32()
            };

            int stormCount = reader.ReadInt32();
            packet.StormPositions = new Vector3[stormCount];
            for (int i = 0; i < stormCount; i++)
            {
                packet.StormPositions[i] = ReadVector3(reader);
            }

            // Active storm index (-1 if none)
            packet.ActiveStormIndex = reader.ReadInt32();

            // WavesInertia sync
            packet.WaveDirection = ReadQuaternion(reader);
            packet.WaveInertia = reader.ReadSingle();
            packet.WaveMagnitude = reader.ReadSingle();

            // Crest ocean time sync (wave phase)
            packet.OceanTime = reader.ReadSingle();

            // OceanUpdaterCrest crossfade drive-state inputs
            packet.HostCurrentMult = reader.ReadSingle();
            packet.HostWavesUp = reader.ReadByte();
            packet.HostTargetInertiaAngle = reader.ReadSingle();
            packet.HostWindWavesWeight = reader.ReadSingle();

            return packet;
        }

        #endregion

        #region Shipyard Packets Serialization

        public static void WriteShipyardCustomization(BinaryWriter writer, ShipyardCustomizationPacket packet)
        {
            writer.Write(packet.BoatName ?? "");

            // Masts
            writer.Write(packet.MastsEnabled?.Length ?? 0);
            if (packet.MastsEnabled != null)
            {
                foreach (var enabled in packet.MastsEnabled)
                    writer.Write(enabled);
            }

            // Sails (reuse existing sail serialization pattern)
            writer.Write(packet.Sails?.Length ?? 0);
            if (packet.Sails != null)
            {
                foreach (var sail in packet.Sails)
                {
                    writer.Write(sail.PrefabIndex);
                    writer.Write(sail.MastIndex);
                    writer.Write(sail.InstallHeight);
                    writer.Write(sail.MinAngle);
                    writer.Write(sail.MaxAngle);
                    writer.Write(sail.Health);
                    writer.Write(sail.Color);
                    writer.Write(sail.ScaleY);  // BS1-live
                    writer.Write(sail.ScaleZ);  // BS1-live
                }
            }

            // Part options
            writer.Write(packet.PartActiveOptions?.Length ?? 0);
            if (packet.PartActiveOptions != null)
            {
                foreach (var option in packet.PartActiveOptions)
                    writer.Write(option);
            }
        }

        public static ShipyardCustomizationPacket ReadShipyardCustomization(BinaryReader reader)
        {
            var packet = new ShipyardCustomizationPacket();

            packet.BoatName = reader.ReadString();

            // Masts
            int mastCount = reader.ReadInt32();
            packet.MastsEnabled = new bool[mastCount];
            for (int i = 0; i < mastCount; i++)
                packet.MastsEnabled[i] = reader.ReadBoolean();

            // Sails
            int sailCount = reader.ReadInt32();
            packet.Sails = new NetworkSailData[sailCount];
            for (int i = 0; i < sailCount; i++)
            {
                packet.Sails[i] = new NetworkSailData
                {
                    PrefabIndex = reader.ReadInt32(),
                    MastIndex = reader.ReadInt32(),
                    InstallHeight = reader.ReadSingle(),
                    MinAngle = reader.ReadSingle(),
                    MaxAngle = reader.ReadSingle(),
                    Health = reader.ReadSingle(),
                    Color = reader.ReadInt32(),
                    ScaleY = reader.ReadSingle(),  // BS1-live
                    ScaleZ = reader.ReadSingle()   // BS1-live
                };
            }

            // Part options
            int partCount = reader.ReadInt32();
            packet.PartActiveOptions = new int[partCount];
            for (int i = 0; i < partCount; i++)
                packet.PartActiveOptions[i] = reader.ReadInt32();

            return packet;
        }

        // Shipyard cradle state. Write order MUST equal Read order.

        public static void WriteShipyardState(BinaryWriter writer, ShipyardStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.Active);
        }

        public static ShipyardStatePacket ReadShipyardState(BinaryReader reader)
        {
            return new ShipyardStatePacket
            {
                BoatName = reader.ReadString(),
                Active = reader.ReadBoolean()
            };
        }

        // Shipyard order request. Write order MUST equal Read order.

        public static void WriteShipyardOrderRequest(BinaryWriter writer, ShipyardOrderRequestPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.Region);
            writer.Write(packet.Total);
        }

        public static ShipyardOrderRequestPacket ReadShipyardOrderRequest(BinaryReader reader)
        {
            return new ShipyardOrderRequestPacket
            {
                BoatName = reader.ReadString(),
                Region = reader.ReadInt32(),
                Total = reader.ReadInt32()
            };
        }

        // SE rig state (sail-extras blob). Write order MUST equal Read order.

        public static void WriteSERigState(BinaryWriter writer, SERigStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.RigBlob ?? "");
        }

        public static SERigStatePacket ReadSERigState(BinaryReader reader)
        {
            return new SERigStatePacket
            {
                BoatName = reader.ReadString(),
                RigBlob = reader.ReadString()
            };
        }

        #endregion

        #region Trapdoor Packets Serialization

        // (v0.2.32) Trapdoor state. Write order MUST equal Read order.
        public static void WriteTrapdoorState(BinaryWriter writer, TrapdoorStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.Key ?? "");
            writer.Write(packet.IsOpen);
            writer.Write(packet.IsGunportGroup);
        }

        public static TrapdoorStatePacket ReadTrapdoorState(BinaryReader reader)
        {
            return new TrapdoorStatePacket
            {
                BoatName = reader.ReadString(),
                Key = reader.ReadString(),
                IsOpen = reader.ReadBoolean(),
                IsGunportGroup = reader.ReadBoolean()
            };
        }

        #endregion

        #region Cutter Packets Serialization

        // (v0.2.32) Leopard cutter state. Write order MUST equal Read order.
        public static void WriteCutterState(BinaryWriter writer, CutterStatePacket packet)
        {
            writer.Write(packet.Active);
            WriteVector3(writer, packet.RealPosition);
            WriteQuaternion(writer, packet.Rotation);
            writer.Write(packet.IsRequest);
        }

        public static CutterStatePacket ReadCutterState(BinaryReader reader)
        {
            return new CutterStatePacket
            {
                Active = reader.ReadBoolean(),
                RealPosition = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                IsRequest = reader.ReadBoolean()
            };
        }

        #endregion

        #region Oar Packets Serialization

        // (v0.2.32) Leopard oar input. Write order MUST equal Read order.
        public static void WriteOarInput(BinaryWriter writer, OarInputPacket packet)
        {
            writer.Write(packet.KeyBits);
            writer.Write(packet.AuthorId);
        }

        public static OarInputPacket ReadOarInput(BinaryReader reader)
        {
            return new OarInputPacket
            {
                KeyBits = reader.ReadByte(),
                AuthorId = reader.ReadUInt64()
            };
        }

        #endregion

        #region Time Packets Serialization

        public static void WriteTimeState(BinaryWriter writer, TimeStatePacket packet)
        {
            writer.Write(packet.GlobalTime);
            writer.Write(packet.Timescale);
            writer.Write(packet.Day);
            writer.Write(packet.MoonPhase);
        }

        public static TimeStatePacket ReadTimeState(BinaryReader reader)
        {
            return new TimeStatePacket
            {
                GlobalTime = reader.ReadSingle(),
                Timescale = reader.ReadSingle(),
                Day = reader.ReadInt32(),
                MoonPhase = reader.ReadSingle()
            };
        }

        #endregion

        #region Survival Packets Serialization

        // === Survival Packets ===

        public static void WriteSurvivalStats(BinaryWriter writer, SurvivalStatsPacket packet)
        {
            writer.Write(packet.Food);
            writer.Write(packet.Water);
            writer.Write(packet.Sleep);
            writer.Write(packet.FoodDebt);
            writer.Write(packet.SleepDebt);
            writer.Write(packet.Alcohol);
            writer.Write(packet.Vitamins);
            writer.Write(packet.Protein);
        }

        public static SurvivalStatsPacket ReadSurvivalStats(BinaryReader reader)
        {
            return new SurvivalStatsPacket
            {
                Food = reader.ReadSingle(),
                Water = reader.ReadSingle(),
                Sleep = reader.ReadSingle(),
                FoodDebt = reader.ReadSingle(),
                SleepDebt = reader.ReadSingle(),
                Alcohol = reader.ReadSingle(),
                Vitamins = reader.ReadSingle(),
                Protein = reader.ReadSingle()
            };
        }

        public static void WriteActivityState(BinaryWriter writer, ActivityStatePacket packet)
        {
            writer.Write((byte)packet.Flags);
            writer.Write((byte)packet.TobaccoType);
            writer.Write(packet.MovementSqrMagnitude);
            writer.Write(packet.PumpIntensity);
        }

        public static ActivityStatePacket ReadActivityState(BinaryReader reader)
        {
            return new ActivityStatePacket
            {
                Flags = (ActivityFlags)reader.ReadByte(),
                TobaccoType = (TobaccoType)reader.ReadByte(),
                MovementSqrMagnitude = reader.ReadSingle(),
                PumpIntensity = reader.ReadSingle()
            };
        }

        public static void WriteConsumptionDelta(BinaryWriter writer, ConsumptionDeltaPacket packet)
        {
            writer.Write(packet.DeltaFood);
            writer.Write(packet.DeltaWater);
            writer.Write(packet.DeltaSleep);
            writer.Write(packet.DeltaFoodDebt);
            writer.Write(packet.DeltaSleepDebt);
            writer.Write(packet.DeltaAlcohol);
            writer.Write(packet.DeltaVitamins);
            writer.Write(packet.DeltaProtein);
        }

        public static ConsumptionDeltaPacket ReadConsumptionDelta(BinaryReader reader)
        {
            return new ConsumptionDeltaPacket
            {
                DeltaFood = reader.ReadSingle(),
                DeltaWater = reader.ReadSingle(),
                DeltaSleep = reader.ReadSingle(),
                DeltaFoodDebt = reader.ReadSingle(),
                DeltaSleepDebt = reader.ReadSingle(),
                DeltaAlcohol = reader.ReadSingle(),
                DeltaVitamins = reader.ReadSingle(),
                DeltaProtein = reader.ReadSingle()
            };
        }

        #endregion

        #region Sleep Packets Serialization

        public static void WriteSleepRequest(BinaryWriter writer, SleepRequestPacket packet)
        {
            writer.Write(packet.IsTavern);
            writer.Write(packet.IsMoored);
            writer.Write(packet.AuthorId);
        }

        public static SleepRequestPacket ReadSleepRequest(BinaryReader reader)
        {
            return new SleepRequestPacket
            {
                IsTavern = reader.ReadBoolean(),
                IsMoored = reader.ReadBoolean(),
                AuthorId = reader.ReadUInt64()
            };
        }

        public static void WriteSleepWaiting(BinaryWriter writer, SleepWaitingPacket packet)
        {
            writer.Write(packet.IsTavern);
            writer.Write(packet.AuthorId);
        }

        public static SleepWaitingPacket ReadSleepWaiting(BinaryReader reader)
        {
            return new SleepWaitingPacket
            {
                IsTavern = reader.ReadBoolean(),
                AuthorId = reader.ReadUInt64()
            };
        }

        public static void WriteSleepApproved(BinaryWriter writer, SleepApprovedPacket packet)
        {
            writer.Write(packet.IsTavern);
            writer.Write(packet.IsTimeskip);
        }

        public static SleepApprovedPacket ReadSleepApproved(BinaryReader reader)
        {
            return new SleepApprovedPacket
            {
                IsTavern = reader.ReadBoolean(),
                IsTimeskip = reader.ReadBoolean()
            };
        }

        public static void WriteSleepCancelled(BinaryWriter writer, SleepCancelledPacket packet)
        {
            writer.Write(packet.AuthorId);
        }

        public static SleepCancelledPacket ReadSleepCancelled(BinaryReader reader)
        {
            return new SleepCancelledPacket
            {
                AuthorId = reader.ReadUInt64()
            };
        }

        public static void WriteSleepCycleState(BinaryWriter writer, SleepCycleStatePacket packet)
        {
            writer.Write(packet.EyesClosed);
            writer.Write(packet.TimeScale);
            writer.Write(packet.FixedDeltaTime);
            writer.Write(packet.FadeTarget);
            writer.Write(packet.FadeDuration);
        }

        public static SleepCycleStatePacket ReadSleepCycleState(BinaryReader reader)
        {
            return new SleepCycleStatePacket
            {
                EyesClosed = reader.ReadBoolean(),
                TimeScale = reader.ReadSingle(),
                FixedDeltaTime = reader.ReadSingle(),
                FadeTarget = reader.ReadSingle(),
                FadeDuration = reader.ReadSingle()
            };
        }

        public static void WriteWakeUp(BinaryWriter writer, WakeUpPacket packet)
        {
            writer.Write(packet.WasManual);
        }

        public static WakeUpPacket ReadWakeUp(BinaryReader reader)
        {
            return new WakeUpPacket
            {
                WasManual = reader.ReadBoolean()
            };
        }

        #endregion

        #region Damage Packets Serialization

        public static void WriteDamageState(BinaryWriter writer, DamageStatePacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.WaterLevel);
            writer.Write(packet.HullDamage);
            writer.Write(packet.Oakum);
            writer.Write(packet.Sunk);
        }

        public static DamageStatePacket ReadDamageState(BinaryReader reader)
        {
            return new DamageStatePacket
            {
                BoatName = reader.ReadString(),
                WaterLevel = reader.ReadSingle(),
                HullDamage = reader.ReadSingle(),
                Oakum = reader.ReadSingle(),
                Sunk = reader.ReadBoolean()
            };
        }

        public static void WriteDamageImpact(BinaryWriter writer, DamageImpactPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.HullDamage);
        }

        public static DamageImpactPacket ReadDamageImpact(BinaryReader reader)
        {
            return new DamageImpactPacket
            {
                BoatName = reader.ReadString(),
                HullDamage = reader.ReadSingle()
            };
        }

        public static void WriteGuestPumpInput(BinaryWriter writer, GuestPumpInputPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.PumpInput);
        }

        public static GuestPumpInputPacket ReadGuestPumpInput(BinaryReader reader)
        {
            return new GuestPumpInputPacket
            {
                BoatName = reader.ReadString(),
                PumpInput = reader.ReadSingle()
            };
        }

        public static void WriteGuestOakumRepair(BinaryWriter writer, GuestOakumRepairPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.ItemInstanceId);
        }

        public static GuestOakumRepairPacket ReadGuestOakumRepair(BinaryReader reader)
        {
            return new GuestOakumRepairPacket
            {
                BoatName = reader.ReadString(),
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteGuestBailRequest(BinaryWriter writer, GuestBailRequestPacket packet)
        {
            writer.Write(packet.BoatName ?? "");
            writer.Write(packet.BottleInstanceId);
            writer.Write(packet.AmountBailed);
        }

        public static GuestBailRequestPacket ReadGuestBailRequest(BinaryReader reader)
        {
            return new GuestBailRequestPacket
            {
                BoatName = reader.ReadString(),
                BottleInstanceId = reader.ReadInt32(),
                AmountBailed = reader.ReadSingle()
            };
        }

        #endregion

        #region Item Packets Serialization

        public static void WriteItemPickedUp(BinaryWriter writer, ItemPickedUpPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.PlayerSteamId);
            writer.Write(packet.InventorySlot);
            writer.Write(packet.PrefabIndex);
            WriteVector3(writer, packet.Position);
            writer.Write(packet.ParentBoatName ?? "");
            writer.Write(packet.IsLocalPosition);
        }

        public static ItemPickedUpPacket ReadItemPickedUp(BinaryReader reader)
        {
            return new ItemPickedUpPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                PlayerSteamId = reader.ReadUInt64(),
                InventorySlot = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                Position = ReadVector3(reader),
                ParentBoatName = reader.ReadString(),
                IsLocalPosition = reader.ReadBoolean()
            };
        }

        public static void WriteItemDropped(BinaryWriter writer, ItemDroppedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            WriteVector3(writer, packet.Position);
            WriteQuaternion(writer, packet.Rotation);
            writer.Write(packet.ParentBoatName ?? "");
            writer.Write(packet.IsLocalPosition);
        }

        public static ItemDroppedPacket ReadItemDropped(BinaryReader reader)
        {
            return new ItemDroppedPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                ParentBoatName = reader.ReadString(),
                IsLocalPosition = reader.ReadBoolean()
            };
        }

        public static void WriteItemPickupRequest(BinaryWriter writer, ItemPickupRequestPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.PrefabIndex);
            writer.Write(packet.InventorySlot);
            WriteVector3(writer, packet.Position);
            writer.Write(packet.ParentBoatName ?? "");
            writer.Write(packet.IsLocalPosition);
        }

        public static ItemPickupRequestPacket ReadItemPickupRequest(BinaryReader reader)
        {
            return new ItemPickupRequestPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                InventorySlot = reader.ReadInt32(),
                Position = ReadVector3(reader),
                ParentBoatName = reader.ReadString(),
                IsLocalPosition = reader.ReadBoolean()
            };
        }

        public static void WriteItemPickupDenied(BinaryWriter writer, ItemPickupDeniedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.Reason);
        }

        public static ItemPickupDeniedPacket ReadItemPickupDenied(BinaryReader reader)
        {
            return new ItemPickupDeniedPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                Reason = reader.ReadByte()
            };
        }

        public static void WriteItemSpawned(BinaryWriter writer, ItemSpawnedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.PrefabIndex);
            WriteVector3(writer, packet.Position);
            WriteQuaternion(writer, packet.Rotation);
            writer.Write(packet.ParentBoatName ?? "");
            writer.Write(packet.IsLocalPosition);
            writer.Write(packet.Health);
            writer.Write(packet.Amount);
            writer.Write(packet.MissionIndex);
        }

        public static ItemSpawnedPacket ReadItemSpawned(BinaryReader reader)
        {
            return new ItemSpawnedPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                ParentBoatName = reader.ReadString(),
                IsLocalPosition = reader.ReadBoolean(),
                Health = reader.ReadSingle(),
                Amount = reader.ReadSingle(),
                MissionIndex = reader.ReadInt32()
            };
        }

        public static void WriteItemDestroyed(BinaryWriter writer, ItemDestroyedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
        }

        public static ItemDestroyedPacket ReadItemDestroyed(BinaryReader reader)
        {
            return new ItemDestroyedPacket
            {
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteItemAmountChanged(BinaryWriter writer, ItemAmountChangedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.NewAmount);
        }

        public static ItemAmountChangedPacket ReadItemAmountChanged(BinaryReader reader)
        {
            return new ItemAmountChangedPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                NewAmount = reader.ReadSingle()
            };
        }

        public static void WriteItemCrate(BinaryWriter writer, ItemCratePacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.CrateInstanceId);
        }

        public static ItemCratePacket ReadItemCrate(BinaryReader reader)
        {
            return new ItemCratePacket
            {
                ItemInstanceId = reader.ReadInt32(),
                CrateInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteItemHung(BinaryWriter writer, ItemHungPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.HookInstanceId);
        }

        public static ItemHungPacket ReadItemHung(BinaryReader reader)
        {
            return new ItemHungPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                HookInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteItemUnhung(BinaryWriter writer, ItemUnhungPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
        }

        public static ItemUnhungPacket ReadItemUnhung(BinaryReader reader)
        {
            return new ItemUnhungPacket
            {
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteCrateUnsealed(BinaryWriter writer, CrateUnsealedPacket packet)
        {
            writer.Write(packet.CrateInstanceId);
            writer.Write(packet.SpawnedItems?.Length ?? 0);
            if (packet.SpawnedItems != null)
            {
                foreach (var item in packet.SpawnedItems)
                {
                    writer.Write(item.InstanceId);
                    writer.Write(item.PrefabIndex);
                    writer.Write(item.Health);
                    writer.Write(item.Amount);
                    writer.Write(item.IsSmoked);
                    writer.Write(item.IsDried);
                }
            }
        }

        public static CrateUnsealedPacket ReadCrateUnsealed(BinaryReader reader)
        {
            var crateId = reader.ReadInt32();
            var count = reader.ReadInt32();
            var items = new CrateSpawnedItemData[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = new CrateSpawnedItemData
                {
                    InstanceId = reader.ReadInt32(),
                    PrefabIndex = reader.ReadInt32(),
                    Health = reader.ReadSingle(),
                    Amount = reader.ReadSingle(),
                    IsSmoked = reader.ReadBoolean(),
                    IsDried = reader.ReadBoolean()
                };
            }
            return new CrateUnsealedPacket
            {
                CrateInstanceId = crateId,
                SpawnedItems = items
            };
        }

        public static void WriteItemHealthChanged(BinaryWriter writer, ItemHealthChangedPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.NewHealth);
        }

        public static ItemHealthChangedPacket ReadItemHealthChanged(BinaryReader reader)
        {
            return new ItemHealthChangedPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                NewHealth = reader.ReadSingle()
            };
        }

        public static void WriteCrateUnsealRequest(BinaryWriter writer, CrateUnsealRequestPacket packet)
        {
            writer.Write(packet.CrateInstanceId);
            writer.Write(packet.CratePrefabIndex);
        }

        public static CrateUnsealRequestPacket ReadCrateUnsealRequest(BinaryReader reader)
        {
            return new CrateUnsealRequestPacket
            {
                CrateInstanceId = reader.ReadInt32(),
                CratePrefabIndex = reader.ReadInt32()
            };
        }

        public static void WriteCargoInsertRequest(BinaryWriter writer, CargoInsertRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.ItemInstanceId);
        }

        public static CargoInsertRequestPacket ReadCargoInsertRequest(BinaryReader reader)
        {
            return new CargoInsertRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteCargoInserted(BinaryWriter writer, CargoInsertedPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.Price);
            writer.Write(packet.RequesterSteamId);
        }

        public static CargoInsertedPacket ReadCargoInserted(BinaryReader reader)
        {
            return new CargoInsertedPacket
            {
                PortIndex = reader.ReadInt32(),
                ItemInstanceId = reader.ReadInt32(),
                Price = reader.ReadInt32(),
                RequesterSteamId = reader.ReadUInt64()
            };
        }

        public static void WriteCargoWithdrawRequest(BinaryWriter writer, CargoWithdrawRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.ItemInstanceId);
        }

        public static CargoWithdrawRequestPacket ReadCargoWithdrawRequest(BinaryReader reader)
        {
            return new CargoWithdrawRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteCargoWithdrawn(BinaryWriter writer, CargoWithdrawnPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.Price);
            writer.Write(packet.RequesterSteamId);
        }

        public static CargoWithdrawnPacket ReadCargoWithdrawn(BinaryReader reader)
        {
            return new CargoWithdrawnPacket
            {
                PortIndex = reader.ReadInt32(),
                ItemInstanceId = reader.ReadInt32(),
                Price = reader.ReadInt32(),
                RequesterSteamId = reader.ReadUInt64()
            };
        }

        public static void WriteItemResync(BinaryWriter writer, ItemResyncPacket packet)
        {
            writer.Write(packet.InstanceId);
            writer.Write(packet.PrefabIndex);
            writer.Write(packet.Health);
            writer.Write(packet.Amount);
            writer.Write(packet.Sold);
            writer.Write(packet.IsOnBoat);
            writer.Write(packet.BoatName ?? "");
            WriteVector3(writer, packet.LocalPosition);
            WriteQuaternion(writer, packet.Rotation);
            writer.Write(packet.MissionIndex);
        }

        public static ItemResyncPacket ReadItemResync(BinaryReader reader)
        {
            return new ItemResyncPacket
            {
                InstanceId = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                Health = reader.ReadSingle(),
                Amount = reader.ReadSingle(),
                Sold = reader.ReadBoolean(),
                IsOnBoat = reader.ReadBoolean(),
                BoatName = reader.ReadString(),
                LocalPosition = ReadVector3(reader),
                Rotation = ReadQuaternion(reader),
                MissionIndex = reader.ReadInt32()
            };
        }

        /// <summary>
        /// Serialize light state (lantern on/off).
        /// </summary>
        public static void WriteLightState(BinaryWriter writer, LightStatePacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.IsOn);
        }

        /// <summary>
        /// Deserialize light state (lantern on/off).
        /// </summary>
        public static LightStatePacket ReadLightState(BinaryReader reader)
        {
            return new LightStatePacket
            {
                ItemInstanceId = reader.ReadInt32(),
                IsOn = reader.ReadBoolean()
            };
        }

        /// <summary>
        /// Serialize nail state change.
        /// </summary>
        public static void WriteNailState(BinaryWriter writer, NailStatePacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.Nailed);
        }

        /// <summary>
        /// Deserialize nail state change.
        /// </summary>
        public static NailStatePacket ReadNailState(BinaryReader reader)
        {
            return new NailStatePacket
            {
                ItemInstanceId = reader.ReadInt32(),
                Nailed = reader.ReadBoolean()
            };
        }

        /// <summary>
        /// Serialize pipe filled with tobacco.
        /// </summary>
        public static void WritePipeFilled(BinaryWriter writer, PipeFilledPacket packet)
        {
            writer.Write(packet.PipeInstanceId);
            writer.Write(packet.TobaccoType);
        }

        /// <summary>
        /// Deserialize pipe filled with tobacco.
        /// </summary>
        public static PipeFilledPacket ReadPipeFilled(BinaryReader reader)
        {
            return new PipeFilledPacket
            {
                PipeInstanceId = reader.ReadInt32(),
                TobaccoType = reader.ReadInt32()
            };
        }

        #endregion

        #region Mission Packets

        public static void WriteNetworkMissionData(BinaryWriter writer, NetworkMissionData data)
        {
            writer.Write(data.SlotIndex);
            writer.Write(data.IsValid);
            if (data.IsValid)
            {
                writer.Write(data.OriginPortIndex);
                writer.Write(data.DestinationPortIndex);
                writer.Write(data.GoodPrefabIndex);
                writer.Write(data.GoodCount);
                writer.Write(data.DeliveredCount);
                writer.Write(data.TotalPrice);
                writer.Write(data.InsuranceLevel);
                writer.Write(data.Distance);
                writer.Write(data.DueDay);
            }
        }

        public static NetworkMissionData ReadNetworkMissionData(BinaryReader reader)
        {
            var data = new NetworkMissionData
            {
                SlotIndex = reader.ReadInt32(),
                IsValid = reader.ReadBoolean()
            };
            if (data.IsValid)
            {
                data.OriginPortIndex = reader.ReadInt32();
                data.DestinationPortIndex = reader.ReadInt32();
                data.GoodPrefabIndex = reader.ReadInt32();
                data.GoodCount = reader.ReadInt32();
                data.DeliveredCount = reader.ReadInt32();
                data.TotalPrice = reader.ReadInt32();
                data.InsuranceLevel = reader.ReadSingle();
                data.Distance = reader.ReadSingle();
                data.DueDay = reader.ReadInt32();
            }
            return data;
        }

        public static void WriteMissionStateSync(BinaryWriter writer, MissionStateSyncPacket packet)
        {
            for (int i = 0; i < 5; i++)
            {
                WriteNetworkMissionData(writer, packet.Missions[i]);
            }
        }

        public static MissionStateSyncPacket ReadMissionStateSync(BinaryReader reader)
        {
            var packet = new MissionStateSyncPacket
            {
                Missions = new NetworkMissionData[5]
            };
            for (int i = 0; i < 5; i++)
            {
                packet.Missions[i] = ReadNetworkMissionData(reader);
            }
            return packet;
        }

        public static void WriteMissionAccepted(BinaryWriter writer, MissionAcceptedPacket packet)
        {
            WriteNetworkMissionData(writer, packet.Mission);
        }

        public static MissionAcceptedPacket ReadMissionAccepted(BinaryReader reader)
        {
            return new MissionAcceptedPacket
            {
                Mission = ReadNetworkMissionData(reader)
            };
        }

        public static void WriteMissionProgress(BinaryWriter writer, MissionProgressPacket packet)
        {
            writer.Write(packet.SlotIndex);
            writer.Write(packet.DeliveredCount);
            writer.Write(packet.GoodName ?? ""); // Must match ReadMissionProgress order
            writer.Write(packet.GoodCount);
        }

        public static MissionProgressPacket ReadMissionProgress(BinaryReader reader)
        {
            return new MissionProgressPacket
            {
                SlotIndex = reader.ReadInt32(),
                DeliveredCount = reader.ReadInt32(),
                GoodName = reader.ReadString(),
                GoodCount = reader.ReadInt32()
            };
        }

        public static void WriteMissionEnded(BinaryWriter writer, MissionEndedPacket packet)
        {
            writer.Write(packet.SlotIndex);
            writer.Write(packet.MissionName ?? ""); // Must match ReadMissionEnded order
        }

        public static MissionEndedPacket ReadMissionEnded(BinaryReader reader)
        {
            return new MissionEndedPacket
            {
                SlotIndex = reader.ReadInt32(),
                MissionName = reader.ReadString()
            };
        }

        public static void WriteMissionAcceptRequest(BinaryWriter writer, MissionAcceptRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.BoardSlot);
            writer.Write(packet.Page);
            writer.Write(packet.IsWorldMission);
        }

        public static MissionAcceptRequestPacket ReadMissionAcceptRequest(BinaryReader reader)
        {
            return new MissionAcceptRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                BoardSlot = reader.ReadInt32(),
                Page = reader.ReadInt32(),
                IsWorldMission = reader.ReadBoolean()
            };
        }

        public static void WriteMissionAbandonRequest(BinaryWriter writer, MissionAbandonRequestPacket packet)
        {
            writer.Write(packet.SlotIndex);
        }

        public static MissionAbandonRequestPacket ReadMissionAbandonRequest(BinaryReader reader)
        {
            return new MissionAbandonRequestPacket
            {
                SlotIndex = reader.ReadInt32()
            };
        }

        public static void WriteMissionBoardRequest(BinaryWriter writer, MissionBoardRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.Page);
            writer.Write(packet.IsWorldMission);
        }

        public static MissionBoardRequestPacket ReadMissionBoardRequest(BinaryReader reader)
        {
            return new MissionBoardRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                Page = reader.ReadInt32(),
                IsWorldMission = reader.ReadBoolean()
            };
        }

        public static void WriteMissionBoardResponse(BinaryWriter writer, MissionBoardResponsePacket packet)
        {
            writer.Write(packet.TotalCount);
            int count = packet.Missions?.Length ?? 0;
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                WriteNetworkMissionData(writer, packet.Missions[i]);
            }
        }

        public static MissionBoardResponsePacket ReadMissionBoardResponse(BinaryReader reader)
        {
            var packet = new MissionBoardResponsePacket
            {
                TotalCount = reader.ReadInt32()
            };
            int count = reader.ReadInt32();
            packet.Missions = new NetworkMissionData[count];
            for (int i = 0; i < count; i++)
            {
                packet.Missions[i] = ReadNetworkMissionData(reader);
            }
            return packet;
        }

        #endregion

        #region Economy Packets

        public static void WriteCurrencySync(BinaryWriter writer, CurrencySyncPacket packet)
        {
            for (int i = 0; i < 4; i++)
            {
                writer.Write(packet.Currency[i]);
            }
        }

        public static CurrencySyncPacket ReadCurrencySync(BinaryReader reader)
        {
            var packet = new CurrencySyncPacket
            {
                Currency = new int[4]
            };
            for (int i = 0; i < 4; i++)
            {
                packet.Currency[i] = reader.ReadInt32();
            }
            return packet;
        }

        public static void WriteReputationSync(BinaryWriter writer, ReputationSyncPacket packet)
        {
            for (int i = 0; i < 4; i++)
            {
                writer.Write(packet.Reputation[i]);
            }
        }

        public static ReputationSyncPacket ReadReputationSync(BinaryReader reader)
        {
            var packet = new ReputationSyncPacket
            {
                Reputation = new int[4]
            };
            for (int i = 0; i < 4; i++)
            {
                packet.Reputation[i] = reader.ReadInt32();
            }
            return packet;
        }

        public static void WriteDeliverGoodRequest(BinaryWriter writer, DeliverGoodRequestPacket packet)
        {
            writer.Write(packet.ItemInstanceId);
            writer.Write(packet.PrefabIndex);
            writer.Write(packet.PortIndex);
        }

        public static DeliverGoodRequestPacket ReadDeliverGoodRequest(BinaryReader reader)
        {
            return new DeliverGoodRequestPacket
            {
                ItemInstanceId = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                PortIndex = reader.ReadInt32()
            };
        }

        public static void WriteExchangeRequest(BinaryWriter writer, ExchangeRequestPacket packet)
        {
            writer.Write(packet.SellCurrency);
            writer.Write(packet.BuyCurrency);
            writer.Write(packet.Amount);
        }

        public static ExchangeRequestPacket ReadExchangeRequest(BinaryReader reader)
        {
            return new ExchangeRequestPacket
            {
                SellCurrency = reader.ReadInt32(),
                BuyCurrency = reader.ReadInt32(),
                Amount = reader.ReadInt32()
            };
        }

        #endregion

        #region Recovery Packets

        public static void WriteRecoveryStarted(BinaryWriter writer, RecoveryStartedPacket packet)
        {
            writer.Write(packet.Reason);
        }

        public static RecoveryStartedPacket ReadRecoveryStarted(BinaryReader reader)
        {
            return new RecoveryStartedPacket
            {
                Reason = reader.ReadByte()
            };
        }

        public static void WriteRecoveryEnded(BinaryWriter writer, RecoveryEndedPacket packet)
        {
            // Empty packet - no data to write
        }

        public static RecoveryEndedPacket ReadRecoveryEnded(BinaryReader reader)
        {
            return new RecoveryEndedPacket();
        }

        #endregion

        #region Trading Packets

        public static void WriteNetworkPriceReport(BinaryWriter writer, NetworkPriceReport report)
        {
            writer.Write(report.PortIndex);
            writer.Write(report.Day);
            writer.Write(report.Approved);

            // Write buy prices (65 ints)
            writer.Write(report.BuyPrices?.Length ?? 0);
            if (report.BuyPrices != null)
            {
                foreach (var price in report.BuyPrices)
                    writer.Write(price);
            }

            // Write sell prices (65 ints)
            writer.Write(report.SellPrices?.Length ?? 0);
            if (report.SellPrices != null)
            {
                foreach (var price in report.SellPrices)
                    writer.Write(price);
            }
        }

        public static NetworkPriceReport ReadNetworkPriceReport(BinaryReader reader)
        {
            var report = new NetworkPriceReport
            {
                PortIndex = reader.ReadInt32(),
                Day = reader.ReadInt32(),
                Approved = reader.ReadBoolean()
            };

            int buyCount = reader.ReadInt32();
            report.BuyPrices = new int[buyCount];
            for (int i = 0; i < buyCount; i++)
                report.BuyPrices[i] = reader.ReadInt32();

            int sellCount = reader.ReadInt32();
            report.SellPrices = new int[sellCount];
            for (int i = 0; i < sellCount; i++)
                report.SellPrices[i] = reader.ReadInt32();

            return report;
        }

        public static void WritePriceKnowledgeSync(BinaryWriter writer, PriceKnowledgeSyncPacket packet)
        {
            writer.Write(packet.Reports?.Length ?? 0);
            if (packet.Reports != null)
            {
                foreach (var report in packet.Reports)
                    WriteNetworkPriceReport(writer, report);
            }
        }

        public static PriceKnowledgeSyncPacket ReadPriceKnowledgeSync(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var reports = new NetworkPriceReport[count];
            for (int i = 0; i < count; i++)
                reports[i] = ReadNetworkPriceReport(reader);

            return new PriceKnowledgeSyncPacket { Reports = reports };
        }

        public static void WritePriceDiscovery(BinaryWriter writer, PriceDiscoveryPacket packet)
        {
            WriteNetworkPriceReport(writer, packet.Report);
        }

        public static PriceDiscoveryPacket ReadPriceDiscovery(BinaryReader reader)
        {
            return new PriceDiscoveryPacket { Report = ReadNetworkPriceReport(reader) };
        }

        public static void WriteIslandSupplySync(BinaryWriter writer, IslandSupplySyncPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.Supply?.Length ?? 0);
            if (packet.Supply != null)
            {
                foreach (var supply in packet.Supply)
                    writer.Write(supply);
            }
        }

        public static IslandSupplySyncPacket ReadIslandSupplySync(BinaryReader reader)
        {
            var packet = new IslandSupplySyncPacket
            {
                PortIndex = reader.ReadInt32()
            };

            int count = reader.ReadInt32();
            packet.Supply = new float[count];
            for (int i = 0; i < count; i++)
                packet.Supply[i] = reader.ReadSingle();

            return packet;
        }

        #endregion

        #region Trade Request Packets

        public static void WriteMarketTradeRequest(BinaryWriter writer, MarketTradeRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.GoodIndex);
            writer.Write(packet.IsBuying);
            writer.Write(packet.CurrencyIndex);  // T3
        }

        public static MarketTradeRequestPacket ReadMarketTradeRequest(BinaryReader reader)
        {
            return new MarketTradeRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                GoodIndex = reader.ReadInt32(),
                IsBuying = reader.ReadBoolean(),
                CurrencyIndex = reader.ReadInt32()  // T3
            };
        }

        public static void WriteMarketTradeResult(BinaryWriter writer, MarketTradeResultPacket packet)
        {
            writer.Write(packet.Success);
            writer.Write(packet.Reason);
            writer.Write(packet.CurrencyIndex);
            writer.Write(packet.Amount);
            writer.Write(packet.IsBuying);
        }

        public static MarketTradeResultPacket ReadMarketTradeResult(BinaryReader reader)
        {
            return new MarketTradeResultPacket
            {
                Success = reader.ReadBoolean(),
                Reason = reader.ReadByte(),
                CurrencyIndex = reader.ReadInt32(),
                Amount = reader.ReadInt32(),
                IsBuying = reader.ReadBoolean()
            };
        }

        public static void WriteShopTradeRequest(BinaryWriter writer, ShopTradeRequestPacket packet)
        {
            writer.Write(packet.PortIndex);
            writer.Write(packet.ShopkeeperPosX);
            writer.Write(packet.ShopkeeperPosY);
            writer.Write(packet.ShopkeeperPosZ);
            writer.Write(packet.GoodIndex);
            writer.Write(packet.Price);
            writer.Write(packet.IsBuying);
            writer.Write(packet.CurrencyIndex); // Must match ReadShopTradeRequest order
            writer.Write(packet.PrefabIndex);   // Must match ReadShopTradeRequest order
            // v0.2.20 appended item-state fields - order MUST mirror ReadShopTradeRequest exactly.
            writer.Write(packet.ItemAmount);
            writer.Write(packet.ItemHealth);
            writer.Write(packet.FoodDried);
            writer.Write(packet.FoodSmoked);
            writer.Write(packet.FoodSalted);
            writer.Write(packet.FoodSpoiled);
        }

        public static ShopTradeRequestPacket ReadShopTradeRequest(BinaryReader reader)
        {
            return new ShopTradeRequestPacket
            {
                PortIndex = reader.ReadInt32(),
                ShopkeeperPosX = reader.ReadSingle(),
                ShopkeeperPosY = reader.ReadSingle(),
                ShopkeeperPosZ = reader.ReadSingle(),
                GoodIndex = reader.ReadInt32(),
                Price = reader.ReadInt32(),
                IsBuying = reader.ReadBoolean(),
                CurrencyIndex = reader.ReadInt32(),
                PrefabIndex = reader.ReadInt32(),
                // v0.2.20 appended item-state fields - order MUST mirror WriteShopTradeRequest exactly.
                ItemAmount = reader.ReadSingle(),
                ItemHealth = reader.ReadSingle(),
                FoodDried = reader.ReadSingle(),
                FoodSmoked = reader.ReadSingle(),
                FoodSalted = reader.ReadSingle(),
                FoodSpoiled = reader.ReadSingle()
            };
        }

        public static void WriteShopTradeResult(BinaryWriter writer, ShopTradeResultPacket packet)
        {
            writer.Write(packet.Success);
            writer.Write(packet.Reason);
            writer.Write(packet.PriceAmount);
            writer.Write(packet.CurrencyIndex);
            writer.Write(packet.SpawnedItemId); // v0.2.20 appended - must mirror ReadShopTradeResult
        }

        public static ShopTradeResultPacket ReadShopTradeResult(BinaryReader reader)
        {
            return new ShopTradeResultPacket
            {
                Success = reader.ReadBoolean(),
                Reason = reader.ReadByte(),
                PriceAmount = reader.ReadInt32(),
                CurrencyIndex = reader.ReadInt32(),
                SpawnedItemId = reader.ReadInt32() // v0.2.20 appended - must mirror WriteShopTradeResult
            };
        }

        public static void WriteTradeFeedEvent(BinaryWriter writer, TradeFeedEventPacket packet)
        {
            writer.Write(packet.ActorSteamId);
            writer.Write(packet.Flags);
            writer.Write(packet.CurrencyIndex);
            writer.Write(packet.Price);
            writer.Write(packet.ItemName ?? ""); // Must mirror ReadTradeFeedEvent order
        }

        public static TradeFeedEventPacket ReadTradeFeedEvent(BinaryReader reader)
        {
            return new TradeFeedEventPacket
            {
                ActorSteamId = reader.ReadUInt64(),
                Flags = reader.ReadByte(),
                CurrencyIndex = reader.ReadByte(),
                Price = reader.ReadInt32(),
                ItemName = reader.ReadString()
            };
        }

        public static void WriteGhostItemPurge(BinaryWriter writer, GhostItemPurgePacket packet)
        {
            writer.Write(packet.ItemInstanceId); // Must mirror ReadGhostItemPurge order
        }

        public static GhostItemPurgePacket ReadGhostItemPurge(BinaryReader reader)
        {
            return new GhostItemPurgePacket
            {
                ItemInstanceId = reader.ReadInt32()
            };
        }

        public static void WriteGuestJoinComplete(BinaryWriter writer, GuestJoinCompletePacket packet)
        {
            // No payload: the packet type plus the transport-level sender SteamId carry everything.
        }

        public static GuestJoinCompletePacket ReadGuestJoinComplete(BinaryReader reader)
        {
            return new GuestJoinCompletePacket();
        }

        public static void WritePingRequest(BinaryWriter writer, PingRequestPacket packet)
        {
            writer.Write(packet.SendTime);
        }

        public static PingRequestPacket ReadPingRequest(BinaryReader reader)
        {
            return new PingRequestPacket
            {
                SendTime = reader.ReadSingle()
            };
        }

        public static void WritePingReply(BinaryWriter writer, PingReplyPacket packet)
        {
            writer.Write(packet.SendTime);
        }

        public static PingReplyPacket ReadPingReply(BinaryReader reader)
        {
            return new PingReplyPacket
            {
                SendTime = reader.ReadSingle()
            };
        }

        public static void WriteShopItemBought(BinaryWriter writer, ShopItemBoughtPacket packet)
        {
            writer.Write(packet.PrefabIndex);
            writer.Write(packet.PositionX);
            writer.Write(packet.PositionY);
            writer.Write(packet.PositionZ);
        }

        public static ShopItemBoughtPacket ReadShopItemBought(BinaryReader reader)
        {
            return new ShopItemBoughtPacket
            {
                PrefabIndex = reader.ReadInt32(),
                PositionX = reader.ReadSingle(),
                PositionY = reader.ReadSingle(),
                PositionZ = reader.ReadSingle()
            };
        }

        public static void WriteNetworkDaySheet(BinaryWriter writer, NetworkDaySheet sheet)
        {
            writer.Write(sheet.Day);

            // Write profits (15 categories)
            for (int i = 0; i < 15; i++)
            {
                writer.Write(sheet.Profits != null && i < sheet.Profits.Length ? sheet.Profits[i] : 0);
            }

            // Write expenses (15 categories)
            for (int i = 0; i < 15; i++)
            {
                writer.Write(sheet.Expenses != null && i < sheet.Expenses.Length ? sheet.Expenses[i] : 0);
            }
        }

        public static NetworkDaySheet ReadNetworkDaySheet(BinaryReader reader)
        {
            var sheet = new NetworkDaySheet
            {
                Day = reader.ReadInt32(),
                Profits = new int[15],
                Expenses = new int[15]
            };

            for (int i = 0; i < 15; i++)
            {
                sheet.Profits[i] = reader.ReadInt32();
            }

            for (int i = 0; i < 15; i++)
            {
                sheet.Expenses[i] = reader.ReadInt32();
            }

            return sheet;
        }

        public static void WriteTransactionDelta(BinaryWriter writer, TransactionDeltaPacket packet)
        {
            writer.Write(packet.CurrencyIndex);
            writer.Write(packet.Amount);
            writer.Write(packet.Category);
            writer.Write(packet.IsProfit);
        }

        public static TransactionDeltaPacket ReadTransactionDelta(BinaryReader reader)
        {
            return new TransactionDeltaPacket
            {
                CurrencyIndex = reader.ReadInt32(),
                Amount = reader.ReadInt32(),
                Category = reader.ReadInt32(),
                IsProfit = reader.ReadBoolean()
            };
        }

        public static void WriteDayLogsFullSync(BinaryWriter writer, DayLogsFullSyncPacket packet)
        {
            // 4 currencies
            for (int c = 0; c < 4; c++)
            {
                // 21 sheets per currency (20 days + allTime)
                for (int s = 0; s < 21; s++)
                {
                    if (packet.Logs != null && c < packet.Logs.Length &&
                        packet.Logs[c] != null && s < packet.Logs[c].Length)
                    {
                        WriteNetworkDaySheet(writer, packet.Logs[c][s]);
                    }
                    else
                    {
                        WriteNetworkDaySheet(writer, new NetworkDaySheet { Profits = new int[15], Expenses = new int[15] });
                    }
                }
            }
        }

        public static DayLogsFullSyncPacket ReadDayLogsFullSync(BinaryReader reader)
        {
            var logs = new NetworkDaySheet[4][];

            for (int c = 0; c < 4; c++)
            {
                logs[c] = new NetworkDaySheet[21];
                for (int s = 0; s < 21; s++)
                {
                    logs[c][s] = ReadNetworkDaySheet(reader);
                }
            }

            return new DayLogsFullSyncPacket { Logs = logs };
        }

        #endregion

        #region Fishing Packets

        // ========== Fishing Packets ==========

        public static void WriteFishingState(BinaryWriter w, FishingStatePacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.LineLength);
            w.Write(p.Tension);
            w.Write(p.FishEnergy);
        }

        public static FishingStatePacket ReadFishingState(BinaryReader r)
        {
            return new FishingStatePacket
            {
                RodInstanceId = r.ReadInt32(),
                LineLength = r.ReadSingle(),
                Tension = r.ReadSingle(),
                FishEnergy = r.ReadSingle()
            };
        }

        public static void WriteFishingLineLength(BinaryWriter w, FishingLineLengthPacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.LineLength);
        }

        public static FishingLineLengthPacket ReadFishingLineLength(BinaryReader r)
        {
            return new FishingLineLengthPacket
            {
                RodInstanceId = r.ReadInt32(),
                LineLength = r.ReadSingle()
            };
        }

        public static void WriteFishBite(BinaryWriter w, FishBitePacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.FishPrefabIndex);
        }

        public static FishBitePacket ReadFishBite(BinaryReader r)
        {
            return new FishBitePacket
            {
                RodInstanceId = r.ReadInt32(),
                FishPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteFishEscape(BinaryWriter w, FishEscapePacket p)
        {
            w.Write(p.RodInstanceId);
        }

        public static FishEscapePacket ReadFishEscape(BinaryReader r)
        {
            return new FishEscapePacket
            {
                RodInstanceId = r.ReadInt32()
            };
        }

        public static void WriteFishCollectRequest(BinaryWriter w, FishCollectRequestPacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.RodPrefabIndex);
            w.Write(p.FishPrefabIndex);
        }

        public static FishCollectRequestPacket ReadFishCollectRequest(BinaryReader r)
        {
            return new FishCollectRequestPacket
            {
                RodInstanceId = r.ReadInt32(),
                RodPrefabIndex = r.ReadInt32(),
                FishPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteFishCollectResponse(BinaryWriter w, FishCollectResponsePacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.FishItemId);
            w.Write(p.HookConsumed);
        }

        public static FishCollectResponsePacket ReadFishCollectResponse(BinaryReader r)
        {
            return new FishCollectResponsePacket
            {
                RodInstanceId = r.ReadInt32(),
                FishItemId = r.ReadInt32(),
                HookConsumed = r.ReadBoolean()
            };
        }

        public static void WriteRodOwnerChanged(BinaryWriter w, RodOwnerChangedPacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.NewOwnerId);
        }

        public static RodOwnerChangedPacket ReadRodOwnerChanged(BinaryReader r)
        {
            return new RodOwnerChangedPacket
            {
                RodInstanceId = r.ReadInt32(),
                NewOwnerId = r.ReadUInt64()
            };
        }

        public static void WriteFishingCast(BinaryWriter w, FishingCastPacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.ThrowCharge);
        }

        public static FishingCastPacket ReadFishingCast(BinaryReader r)
        {
            return new FishingCastPacket
            {
                RodInstanceId = r.ReadInt32(),
                ThrowCharge = r.ReadSingle()
            };
        }

        public static void WriteFishingBobberSync(BinaryWriter w, FishingBobberSyncPacket p)
        {
            w.Write(p.RodInstanceId);
            w.Write(p.BoatName ?? "");
            WriteVector3(w, p.Position);
            w.Write(p.InWater);
        }

        public static FishingBobberSyncPacket ReadFishingBobberSync(BinaryReader r)
        {
            return new FishingBobberSyncPacket
            {
                RodInstanceId = r.ReadInt32(),
                BoatName = r.ReadString(),
                Position = ReadVector3(r),
                InWater = r.ReadByte()
            };
        }

        public static void WriteChipLogThrow(BinaryWriter w, ChipLogThrowPacket p)
        {
            w.Write(p.ItemInstanceId);
        }

        public static ChipLogThrowPacket ReadChipLogThrow(BinaryReader r)
        {
            return new ChipLogThrowPacket
            {
                ItemInstanceId = r.ReadInt32()
            };
        }

        public static void WriteChipLogLineSync(BinaryWriter w, ChipLogLineSyncPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.LineLength);
            w.Write(p.Thrown);
        }

        public static ChipLogLineSyncPacket ReadChipLogLineSync(BinaryReader r)
        {
            return new ChipLogLineSyncPacket
            {
                ItemInstanceId = r.ReadInt32(),
                LineLength = r.ReadSingle(),
                Thrown = r.ReadBoolean()
            };
        }

        #endregion

        #region Navigation Packets

        public static void WriteNavItemState(BinaryWriter w, NavItemStatePacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write((byte)p.StateType);
            w.Write(p.Value);
        }

        public static NavItemStatePacket ReadNavItemState(BinaryReader r)
        {
            return new NavItemStatePacket
            {
                ItemInstanceId = r.ReadInt32(),
                StateType = (NavItemStateType)r.ReadByte(),
                Value = r.ReadSingle()
            };
        }

        public static void WriteMapFoldState(BinaryWriter w, MapFoldStatePacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.IsFolded);
        }

        public static MapFoldStatePacket ReadMapFoldState(BinaryReader r)
        {
            return new MapFoldStatePacket
            {
                ItemInstanceId = r.ReadInt32(),
                IsFolded = r.ReadBoolean()
            };
        }

        public static void WriteMapDrawRequest(BinaryWriter w, MapDrawRequestPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.PrefabIndex);
        }

        public static MapDrawRequestPacket ReadMapDrawRequest(BinaryReader r)
        {
            return new MapDrawRequestPacket
            {
                ItemInstanceId = r.ReadInt32(),
                PrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteMapDrawResponse(BinaryWriter w, MapDrawResponsePacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.Granted);
            w.Write(p.LockedBySteamId);
            w.Write(p.RequesterSteamId);
        }

        public static MapDrawResponsePacket ReadMapDrawResponse(BinaryReader r)
        {
            return new MapDrawResponsePacket
            {
                ItemInstanceId = r.ReadInt32(),
                Granted = r.ReadBoolean(),
                LockedBySteamId = r.ReadUInt64(),
                RequesterSteamId = r.ReadUInt64()
            };
        }

        public static void WriteMapDrawLocked(BinaryWriter w, MapDrawLockedPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.LockedBySteamId);
        }

        public static MapDrawLockedPacket ReadMapDrawLocked(BinaryReader r)
        {
            return new MapDrawLockedPacket
            {
                ItemInstanceId = r.ReadInt32(),
                LockedBySteamId = r.ReadUInt64()
            };
        }

        public static void WriteMapDrawRelease(BinaryWriter w, MapDrawReleasePacket p)
        {
            w.Write(p.ItemInstanceId);
        }

        public static MapDrawReleasePacket ReadMapDrawRelease(BinaryReader r)
        {
            return new MapDrawReleasePacket
            {
                ItemInstanceId = r.ReadInt32()
            };
        }

        public static void WriteMapLine(BinaryWriter w, MapLinePacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.StartX);
            w.Write(p.StartY);
            w.Write(p.EndX);
            w.Write(p.EndY);
            w.Write(p.Color);
        }

        public static MapLinePacket ReadMapLine(BinaryReader r)
        {
            return new MapLinePacket
            {
                ItemInstanceId = r.ReadInt32(),
                StartX = r.ReadSingle(),
                StartY = r.ReadSingle(),
                EndX = r.ReadSingle(),
                EndY = r.ReadSingle(),
                Color = r.ReadInt32()
            };
        }

        public static void WriteMapTempLine(BinaryWriter w, MapTempLinePacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.HasLine);
            if (p.HasLine)
            {
                w.Write(p.StartX);
                w.Write(p.StartY);
                w.Write(p.EndX);
                w.Write(p.EndY);
                w.Write(p.Color);
            }
        }

        public static MapTempLinePacket ReadMapTempLine(BinaryReader r)
        {
            var p = new MapTempLinePacket
            {
                ItemInstanceId = r.ReadInt32(),
                HasLine = r.ReadBoolean()
            };
            if (p.HasLine)
            {
                p.StartX = r.ReadSingle();
                p.StartY = r.ReadSingle();
                p.EndX = r.ReadSingle();
                p.EndY = r.ReadSingle();
                p.Color = r.ReadInt32();
            }
            return p;
        }

        public static void WriteMapFullSync(BinaryWriter w, MapFullSyncPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.Lines?.Length ?? 0);
            if (p.Lines != null)
            {
                foreach (var line in p.Lines)
                {
                    w.Write(line.StartX);
                    w.Write(line.StartY);
                    w.Write(line.EndX);
                    w.Write(line.EndY);
                    w.Write(line.Color);
                }
            }
        }

        public static MapFullSyncPacket ReadMapFullSync(BinaryReader r)
        {
            var p = new MapFullSyncPacket
            {
                ItemInstanceId = r.ReadInt32()
            };
            int count = r.ReadInt32();
            p.Lines = new MapLinePacket[count];
            for (int i = 0; i < count; i++)
            {
                p.Lines[i] = new MapLinePacket
                {
                    ItemInstanceId = p.ItemInstanceId,
                    StartX = r.ReadSingle(),
                    StartY = r.ReadSingle(),
                    EndX = r.ReadSingle(),
                    EndY = r.ReadSingle(),
                    Color = r.ReadInt32()
                };
            }
            return p;
        }

        public static void WriteChartSession(BinaryWriter w, ChartSessionPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.Active);
            w.Write(p.KitPos);
            w.Write(p.UserSteamId);
        }

        public static ChartSessionPacket ReadChartSession(BinaryReader r)
        {
            return new ChartSessionPacket
            {
                ItemInstanceId = r.ReadInt32(),
                Active = r.ReadBoolean(),
                KitPos = r.ReadSByte(),
                UserSteamId = r.ReadUInt64()
            };
        }

        public static void WriteChartCursor(BinaryWriter w, ChartCursorPacket p)
        {
            w.Write(p.ItemInstanceId);
            w.Write(p.Tool);
            w.Write(p.CursorX);
            w.Write(p.CursorY);
        }

        public static ChartCursorPacket ReadChartCursor(BinaryReader r)
        {
            return new ChartCursorPacket
            {
                ItemInstanceId = r.ReadInt32(),
                Tool = r.ReadByte(),
                CursorX = r.ReadSingle(),
                CursorY = r.ReadSingle()
            };
        }

        #endregion

        #region Cooking Packets

        public static void WriteCookingState(BinaryWriter w, CookingStatePacket packet)
        {
            // Foods
            w.Write(packet.Foods?.Count ?? 0);
            if (packet.Foods != null)
            {
                foreach (var food in packet.Foods)
                {
                    w.Write(food.InstanceId);
                    w.Write(food.Amount);
                    w.Write(food.CurrentHeat);
                    w.Write(food.Spoiled);
                    w.Write(food.Salted);
                    w.Write(food.Smoked);
                    w.Write(food.Dried);
                    w.Write(food.StoveSlotIndex);
                    w.Write(food.StoveInstanceId);
                }
            }

            // Soups
            w.Write(packet.Soups?.Count ?? 0);
            if (packet.Soups != null)
            {
                foreach (var soup in packet.Soups)
                {
                    w.Write(soup.InstanceId);
                    w.Write(soup.CurrentWater);
                    w.Write(soup.CurrentEnergy);
                    w.Write(soup.CurrentUncookedEnergy);
                    w.Write(soup.CurrentSpoiled);
                    w.Write(soup.CurrentVitamins);
                    w.Write(soup.CurrentProtein);
                    w.Write(soup.CurrentSalted);
                    w.Write(soup.CurrentHeat);
                }
            }

            // Kettles
            w.Write(packet.Kettles?.Count ?? 0);
            if (packet.Kettles != null)
            {
                foreach (var kettle in packet.Kettles)
                {
                    w.Write(kettle.InstanceId);
                    w.Write(kettle.CurrentWater);
                    w.Write(kettle.CurrentTeaAmount);
                    w.Write(kettle.CurrentCookedTeaAmount);
                    w.Write(kettle.CurrentTeaType);
                    w.Write(kettle.CurrentHeat);
                }
            }
        }

        public static CookingStatePacket ReadCookingState(BinaryReader r)
        {
            var packet = new CookingStatePacket();

            // Foods
            int foodCount = r.ReadInt32();
            packet.Foods = new List<FoodCookingState>(foodCount);
            for (int i = 0; i < foodCount; i++)
            {
                packet.Foods.Add(new FoodCookingState
                {
                    InstanceId = r.ReadInt32(),
                    Amount = r.ReadSingle(),
                    CurrentHeat = r.ReadSingle(),
                    Spoiled = r.ReadSingle(),
                    Salted = r.ReadSingle(),
                    Smoked = r.ReadSingle(),
                    Dried = r.ReadSingle(),
                    StoveSlotIndex = r.ReadInt32(),
                    StoveInstanceId = r.ReadInt32()
                });
            }

            // Soups
            int soupCount = r.ReadInt32();
            packet.Soups = new List<SoupState>(soupCount);
            for (int i = 0; i < soupCount; i++)
            {
                packet.Soups.Add(new SoupState
                {
                    InstanceId = r.ReadInt32(),
                    CurrentWater = r.ReadSingle(),
                    CurrentEnergy = r.ReadSingle(),
                    CurrentUncookedEnergy = r.ReadSingle(),
                    CurrentSpoiled = r.ReadSingle(),
                    CurrentVitamins = r.ReadSingle(),
                    CurrentProtein = r.ReadSingle(),
                    CurrentSalted = r.ReadSingle(),
                    CurrentHeat = r.ReadSingle()
                });
            }

            // Kettles
            int kettleCount = r.ReadInt32();
            packet.Kettles = new List<KettleState>(kettleCount);
            for (int i = 0; i < kettleCount; i++)
            {
                packet.Kettles.Add(new KettleState
                {
                    InstanceId = r.ReadInt32(),
                    CurrentWater = r.ReadSingle(),
                    CurrentTeaAmount = r.ReadSingle(),
                    CurrentCookedTeaAmount = r.ReadSingle(),
                    CurrentTeaType = r.ReadInt32(),
                    CurrentHeat = r.ReadSingle()
                });
            }

            return packet;
        }

        public static void WriteFoodPlaceOnStoveRequest(BinaryWriter w, FoodPlaceOnStoveRequestPacket packet)
        {
            w.Write(packet.FoodInstanceId);
            w.Write(packet.FoodPrefabIndex);
            w.Write(packet.StoveInstanceId);
            w.Write(packet.StovePrefabIndex);
        }

        public static FoodPlaceOnStoveRequestPacket ReadFoodPlaceOnStoveRequest(BinaryReader r)
        {
            return new FoodPlaceOnStoveRequestPacket
            {
                FoodInstanceId = r.ReadInt32(),
                FoodPrefabIndex = r.ReadInt32(),
                StoveInstanceId = r.ReadInt32(),
                StovePrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteFoodRemoveFromStoveRequest(BinaryWriter w, FoodRemoveFromStoveRequestPacket packet)
        {
            w.Write(packet.FoodInstanceId);
            w.Write(packet.FoodPrefabIndex);
        }

        public static FoodRemoveFromStoveRequestPacket ReadFoodRemoveFromStoveRequest(BinaryReader r)
        {
            return new FoodRemoveFromStoveRequestPacket
            {
                FoodInstanceId = r.ReadInt32(),
                FoodPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteFoodCutRequest(BinaryWriter w, FoodCutRequestPacket packet)
        {
            w.Write(packet.KnifeInstanceId);
            w.Write(packet.KnifePrefabIndex);
            w.Write(packet.FoodInstanceId);
            w.Write(packet.FoodPrefabIndex);
        }

        public static FoodCutRequestPacket ReadFoodCutRequest(BinaryReader r)
        {
            return new FoodCutRequestPacket
            {
                KnifeInstanceId = r.ReadInt32(),
                KnifePrefabIndex = r.ReadInt32(),
                FoodInstanceId = r.ReadInt32(),
                FoodPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteFoodCutResult(BinaryWriter w, FoodCutResultPacket packet)
        {
            w.Write(packet.OriginalFoodId);
            w.Write(packet.SliceInstanceIds?.Count ?? 0);
            if (packet.SliceInstanceIds != null)
            {
                foreach (var id in packet.SliceInstanceIds)
                    w.Write(id);
            }
        }

        public static FoodCutResultPacket ReadFoodCutResult(BinaryReader r)
        {
            var packet = new FoodCutResultPacket
            {
                OriginalFoodId = r.ReadInt32()
            };
            int count = r.ReadInt32();
            packet.SliceInstanceIds = new List<int>(count);
            for (int i = 0; i < count; i++)
                packet.SliceInstanceIds.Add(r.ReadInt32());
            return packet;
        }

        public static void WriteFoodSaltRequest(BinaryWriter w, FoodSaltRequestPacket packet)
        {
            w.Write(packet.SaltInstanceId);
            w.Write(packet.SaltPrefabIndex);
            w.Write(packet.FoodInstanceId);
            w.Write(packet.FoodPrefabIndex);
        }

        public static FoodSaltRequestPacket ReadFoodSaltRequest(BinaryReader r)
        {
            return new FoodSaltRequestPacket
            {
                SaltInstanceId = r.ReadInt32(),
                SaltPrefabIndex = r.ReadInt32(),
                FoodInstanceId = r.ReadInt32(),
                FoodPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteSoupAddFoodRequest(BinaryWriter w, SoupAddFoodRequestPacket packet)
        {
            w.Write(packet.FoodInstanceId);
            w.Write(packet.FoodPrefabIndex);
            w.Write(packet.SoupInstanceId);
            w.Write(packet.SoupPrefabIndex);
        }

        public static SoupAddFoodRequestPacket ReadSoupAddFoodRequest(BinaryReader r)
        {
            return new SoupAddFoodRequestPacket
            {
                FoodInstanceId = r.ReadInt32(),
                FoodPrefabIndex = r.ReadInt32(),
                SoupInstanceId = r.ReadInt32(),
                SoupPrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteAddWaterRequest(BinaryWriter w, AddWaterRequestPacket packet)
        {
            w.Write(packet.BottleInstanceId);
            w.Write(packet.ContainerInstanceId);
        }

        public static AddWaterRequestPacket ReadAddWaterRequest(BinaryReader r)
        {
            return new AddWaterRequestPacket
            {
                BottleInstanceId = r.ReadInt32(),
                ContainerInstanceId = r.ReadInt32()
            };
        }

        public static void WriteKettleAddTeaRequest(BinaryWriter w, KettleAddTeaRequestPacket packet)
        {
            w.Write(packet.TeaInstanceId);
            w.Write(packet.TeaPrefabIndex);
            w.Write(packet.KettleInstanceId);
            w.Write(packet.KettlePrefabIndex);
        }

        public static KettleAddTeaRequestPacket ReadKettleAddTeaRequest(BinaryReader r)
        {
            return new KettleAddTeaRequestPacket
            {
                TeaInstanceId = r.ReadInt32(),
                TeaPrefabIndex = r.ReadInt32(),
                KettleInstanceId = r.ReadInt32(),
                KettlePrefabIndex = r.ReadInt32()
            };
        }

        public static void WriteKettlePourRequest(BinaryWriter w, KettlePourRequestPacket packet)
        {
            w.Write(packet.KettleInstanceId);
            w.Write(packet.MugInstanceId);
        }

        public static KettlePourRequestPacket ReadKettlePourRequest(BinaryReader r)
        {
            return new KettlePourRequestPacket
            {
                KettleInstanceId = r.ReadInt32(),
                MugInstanceId = r.ReadInt32()
            };
        }

        public static void WriteFuelInserted(BinaryWriter w, FuelInsertedPacket packet)
        {
            w.Write(packet.FuelInstanceId);
            w.Write(packet.StoveInstanceId);
        }

        public static FuelInsertedPacket ReadFuelInserted(BinaryReader r)
        {
            return new FuelInsertedPacket
            {
                FuelInstanceId = r.ReadInt32(),
                StoveInstanceId = r.ReadInt32()
            };
        }

        #endregion

        #region NPC Boat Packets

        // ============ NPC Boat Packets ============

        public static void WriteNPCBoatState(BinaryWriter writer, NPCBoatStatePacket packet)
        {
            writer.Write(packet.HierarchyPath);
            WriteVector3(writer, packet.Position);
            WriteQuaternion(writer, packet.Rotation);

            writer.Write(packet.SailLengths?.Length ?? 0);
            if (packet.SailLengths != null)
            {
                foreach (var len in packet.SailLengths)
                {
                    writer.Write(len);
                }
            }
        }

        public static NPCBoatStatePacket ReadNPCBoatState(BinaryReader reader)
        {
            var packet = new NPCBoatStatePacket
            {
                HierarchyPath = reader.ReadString(),
                Position = ReadVector3(reader),
                Rotation = ReadQuaternion(reader)
            };

            int sailCount = reader.ReadInt32();
            packet.SailLengths = new float[sailCount];
            for (int i = 0; i < sailCount; i++)
            {
                packet.SailLengths[i] = reader.ReadSingle();
            }

            return packet;
        }

        public static void WriteNPCBoatSnapshot(BinaryWriter writer, NPCBoatSnapshotPacket packet)
        {
            writer.Write(packet.Boats?.Length ?? 0);
            if (packet.Boats != null)
            {
                foreach (var boat in packet.Boats)
                {
                    WriteNPCBoatState(writer, boat);
                }
            }
        }

        public static NPCBoatSnapshotPacket ReadNPCBoatSnapshot(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var packet = new NPCBoatSnapshotPacket
            {
                Boats = new NPCBoatStatePacket[count]
            };

            for (int i = 0; i < count; i++)
            {
                packet.Boats[i] = ReadNPCBoatState(reader);
            }

            return packet;
        }

        // NPC boat damage/sink packets. Write order MUST equal Read order.

        public static void WriteNPCBoatDamage(BinaryWriter writer, NPCBoatDamagePacket packet)
        {
            writer.Write(packet.HierarchyPath ?? "");
            writer.Write(packet.WaterLevel);
            writer.Write(packet.HullDamage);
            writer.Write(packet.Sunk);
        }

        public static NPCBoatDamagePacket ReadNPCBoatDamage(BinaryReader reader)
        {
            return new NPCBoatDamagePacket
            {
                HierarchyPath = reader.ReadString(),
                WaterLevel = reader.ReadSingle(),
                HullDamage = reader.ReadSingle(),
                Sunk = reader.ReadBoolean()
            };
        }

        public static void WriteNPCBoatHitRequest(BinaryWriter writer, NPCBoatHitRequestPacket packet)
        {
            writer.Write(packet.HierarchyPath ?? "");
            writer.Write(packet.ImpactForce);
        }

        public static NPCBoatHitRequestPacket ReadNPCBoatHitRequest(BinaryReader reader)
        {
            return new NPCBoatHitRequestPacket
            {
                HierarchyPath = reader.ReadString(),
                ImpactForce = reader.ReadSingle()
            };
        }

        #endregion

        #region Cleaning Packets

        // ============ CLEANING ============

        public static void WriteCleaningStroke(BinaryWriter w, CleaningStrokePacket packet)
        {
            w.Write(packet.BoatName ?? "");
            w.Write(packet.UVX);
            w.Write(packet.UVY);
        }

        public static CleaningStrokePacket ReadCleaningStroke(BinaryReader r)
        {
            return new CleaningStrokePacket
            {
                BoatName = r.ReadString(),
                UVX = r.ReadSingle(),
                UVY = r.ReadSingle()
            };
        }

        public static void WriteCleanFully(BinaryWriter w, CleanFullyPacket packet)
        {
            w.Write(packet.BoatName ?? "");
        }

        public static CleanFullyPacket ReadCleanFully(BinaryReader r)
        {
            return new CleanFullyPacket
            {
                BoatName = r.ReadString()
            };
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for BinaryWriter to handle Unity and Steamworks types.
    /// </summary>
    public static class BinaryWriterExtensions
    {
        public static void Write(this BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        public static void Write(this BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        public static void Write(this BinaryWriter writer, SteamId value)
        {
            writer.Write(value.Value);
        }

        /// <summary>
        /// Writes a compressed Quaternion using smallest-three encoding (10 bytes -> 7 bytes).
        /// Useful for high-frequency rotation sync.
        /// </summary>
        public static void WriteCompressed(this BinaryWriter writer, Quaternion value)
        {
            // Normalize to ensure unit quaternion
            value = Quaternion.Normalize(value);

            // Find the largest component
            float absX = Mathf.Abs(value.x);
            float absY = Mathf.Abs(value.y);
            float absZ = Mathf.Abs(value.z);
            float absW = Mathf.Abs(value.w);

            int largestIndex = 0;
            float largest = absX;

            if (absY > largest) { largestIndex = 1; largest = absY; }
            if (absZ > largest) { largestIndex = 2; largest = absZ; }
            if (absW > largest) { largestIndex = 3; }

            // Get the sign of the largest component (we'll reconstruct with positive sign)
            float sign = 1f;
            switch (largestIndex)
            {
                case 0: sign = value.x >= 0 ? 1f : -1f; break;
                case 1: sign = value.y >= 0 ? 1f : -1f; break;
                case 2: sign = value.z >= 0 ? 1f : -1f; break;
                case 3: sign = value.w >= 0 ? 1f : -1f; break;
            }

            // Write which component is largest (2 bits packed in first byte with first component)
            // Write the three smallest components as shorts (-1 to 1 mapped to short range)
            float a, b, c;
            switch (largestIndex)
            {
                case 0: a = value.y * sign; b = value.z * sign; c = value.w * sign; break;
                case 1: a = value.x * sign; b = value.z * sign; c = value.w * sign; break;
                case 2: a = value.x * sign; b = value.y * sign; c = value.w * sign; break;
                default: a = value.x * sign; b = value.y * sign; c = value.z * sign; break;
            }

            writer.Write((byte)largestIndex);
            writer.Write((short)(a * 32767f));
            writer.Write((short)(b * 32767f));
            writer.Write((short)(c * 32767f));
        }

        /// <summary>
        /// Writes a Vector3 with half-precision floats (12 bytes -> 6 bytes).
        /// Good for positions where full precision isn't needed.
        /// </summary>
        public static void WriteHalf(this BinaryWriter writer, Vector3 value)
        {
            writer.Write(FloatToHalf(value.x));
            writer.Write(FloatToHalf(value.y));
            writer.Write(FloatToHalf(value.z));
        }

        private static ushort FloatToHalf(float value)
        {
            int i = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            int sign = (i >> 16) & 0x8000;
            int exponent = ((i >> 23) & 0xFF) - 127 + 15;
            int mantissa = i & 0x007FFFFF;

            if (exponent <= 0)
            {
                return (ushort)sign;
            }
            if (exponent > 30)
            {
                return (ushort)(sign | 0x7C00);
            }

            return (ushort)(sign | (exponent << 10) | (mantissa >> 13));
        }
    }

    /// <summary>
    /// Extension methods for BinaryReader to handle Unity and Steamworks types.
    /// </summary>
    public static class BinaryReaderExtensions
    {
        public static Vector3 ReadVector3(this BinaryReader reader)
        {
            return new Vector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        public static Quaternion ReadQuaternion(this BinaryReader reader)
        {
            return new Quaternion(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle()
            );
        }

        public static SteamId ReadSteamId(this BinaryReader reader)
        {
            return new SteamId { Value = reader.ReadUInt64() };
        }

        /// <summary>
        /// Reads a compressed Quaternion written with WriteCompressed.
        /// </summary>
        public static Quaternion ReadCompressedQuaternion(this BinaryReader reader)
        {
            int largestIndex = reader.ReadByte();
            float a = reader.ReadInt16() / 32767f;
            float b = reader.ReadInt16() / 32767f;
            float c = reader.ReadInt16() / 32767f;

            // Reconstruct the largest component
            float largest = Mathf.Sqrt(1f - a * a - b * b - c * c);

            switch (largestIndex)
            {
                case 0: return new Quaternion(largest, a, b, c);
                case 1: return new Quaternion(a, largest, b, c);
                case 2: return new Quaternion(a, b, largest, c);
                default: return new Quaternion(a, b, c, largest);
            }
        }

        /// <summary>
        /// Reads a half-precision Vector3 written with WriteHalf.
        /// </summary>
        public static Vector3 ReadHalfVector3(this BinaryReader reader)
        {
            return new Vector3(
                HalfToFloat(reader.ReadUInt16()),
                HalfToFloat(reader.ReadUInt16()),
                HalfToFloat(reader.ReadUInt16())
            );
        }

        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exponent = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;

            if (exponent == 0)
            {
                return 0f;
            }
            if (exponent == 31)
            {
                return sign == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            }

            exponent = exponent - 15 + 127;
            int i = (sign << 31) | (exponent << 23) | (mantissa << 13);

            return BitConverter.ToSingle(BitConverter.GetBytes(i), 0);
        }
    }
}
