using System;
using System.Collections.Generic;
using Nightshare.Core.Protocol;
using Xunit;

namespace Nightshare.Tests
{
    public class WorldSnapshotMessageTests
    {
        [Fact]
        public void SnapshotHeaderRoundTrips()
        {
            var sent = new WorldSnapshotV1
            {
                PacketCount = 17,
                TotalBytes = 1_197_295,
                TotalGameSeconds = 69826,
                GameplayGameDay = 4,
            };

            using var r = new NetReader(sent.Serialise());
            Assert.Equal(MessageType.WorldSnapshotV1, r.Type);

            var got = WorldSnapshotV1.Parse(r);
            Assert.Equal(17, got.PacketCount);
            Assert.Equal(1_197_295, got.TotalBytes);
            Assert.Equal(69826, got.TotalGameSeconds);
            Assert.Equal(4, got.GameplayGameDay);
        }

        /// <summary>
        /// The largest real packet measured in game was InventoriesManager at about
        /// 612 KB. A snapshot has to carry that intact or a join delivers a broken world.
        /// </summary>
        [Fact]
        public void AManagerPacketCarriesTheLargestRealPayload()
        {
            var payload = new byte[612_003];
            new Random(11).NextBytes(payload);

            var sent = new ManagerPacketV1
            {
                PacketGuid = "2605882F-31F5-4D75-9F72-802B33601A6B",
                ManagerTypeName = "InventoriesManager",
                Payload = payload,
            };

            using var r = new NetReader(sent.Serialise());
            var got = ManagerPacketV1.Parse(r);

            Assert.Equal(payload.Length, got.Payload.Length);
            Assert.Equal(payload, got.Payload);
        }

        /// <summary>
        /// A whole snapshot must fit the transport's frame limit even as one message per
        /// manager. Pinned so a future change to MaxFrameSize cannot silently break joins.
        /// </summary>
        [Fact]
        public void TheLargestPacketFitsInAFrame()
        {
            var payload = new byte[612_003];
            var message = new ManagerPacketV1
            {
                PacketGuid = "2605882F-31F5-4D75-9F72-802B33601A6B",
                ManagerTypeName = "InventoriesManager",
                Payload = payload,
            }.Serialise();

            Assert.True(message.Length < Nightshare.Core.Transport.FrameCodec.MaxFrameSize,
                $"a {message.Length} byte packet exceeds the {Nightshare.Core.Transport.FrameCodec.MaxFrameSize} byte frame limit");
        }

        [Fact]
        public void AnEmptyPacketRoundTrips()
        {
            var sent = new ManagerPacketV1
            {
                PacketGuid = "A909911D-90D3-4C1F-89FB-3083D62F349C",
                ManagerTypeName = "GhostManager",
                Payload = Array.Empty<byte>(),
            };

            using var r = new NetReader(sent.Serialise());
            var got = ManagerPacketV1.Parse(r);

            Assert.Equal("GhostManager", got.ManagerTypeName);
            Assert.Empty(got.Payload);
        }

        /// <summary>
        /// A realistic snapshot, header plus every world-owned manager at its measured
        /// size, reassembles in order with the byte total intact.
        /// </summary>
        [Fact]
        public void AFullSnapshotReassemblesInOrder()
        {
            var measured = new (string name, int size)[]
            {
                ("PersonDataManager", 562_917),
                ("InventoriesManager", 611_409),
                ("ScriptableObjectVariableDatabase", 6_824),
                ("RentManager", 5_436),
                ("AiOwnerManager", 3_488),
                ("NewsManager", 2_590),
                ("ApartmentManager", 2_025),
                ("EconomyManager", 1_242),
                ("WeatherForecastController", 263),
                ("CurfewManager", 12),
                ("CollectibleManager", 8),
                ("FishingManager", 8),
                ("GreenhouseManager", 4),
                ("TimeOfDayManager", 4),
                ("GhostManager", 0),
                ("GhostSystemInitializer", 0),
                ("TravelManager", 0),
            };

            var expectedTotal = 0;
            foreach (var m in measured) expectedTotal += m.size;

            var wire = new List<byte[]>
            {
                new WorldSnapshotV1
                {
                    PacketCount = measured.Length,
                    TotalBytes = expectedTotal,
                    TotalGameSeconds = 69826,
                    GameplayGameDay = 4,
                }.Serialise(),
            };

            foreach (var m in measured)
            {
                wire.Add(new ManagerPacketV1
                {
                    PacketGuid = $"guid-{m.name}",
                    ManagerTypeName = m.name,
                    Payload = new byte[m.size],
                }.Serialise());
            }

            // Reassemble exactly as the client does.
            using var headerReader = new NetReader(wire[0]);
            var header = WorldSnapshotV1.Parse(headerReader);

            var received = 0;
            var names = new List<string>();
            for (var i = 1; i < wire.Count; i++)
            {
                using var r = new NetReader(wire[i]);
                var p = ManagerPacketV1.Parse(r);
                received += p.Payload.Length;
                names.Add(p.ManagerTypeName);
            }

            Assert.Equal(header.PacketCount, names.Count);
            Assert.Equal(header.TotalBytes, received);
            Assert.Equal("PersonDataManager", names[0]);
            Assert.Equal("TravelManager", names[names.Count - 1]);
        }
    }
}
