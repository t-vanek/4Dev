using Microsoft.Extensions.Logging.Abstractions;
using TWorld.Data;
using TWorld.Protocol;
using TWorld.Server.World;
using Xunit;

namespace TWorld.Server.Tests;

public class ReservedPostTests
{
    [Fact]
    public async Task WolverineNotificationUsesRecipientIdCurrentNameAndCurrentMain()
    {
        await using var h = new WorldTestHost(); using var a = await h.ConnectAsync(); using var b = await h.ConnectAsync();
        a.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x401).WriteByte(1).WriteByte(1));
        b.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x402).WriteByte(1).WriteByte(1));
        for (int i = 0; i < 200 && h.State.Servers.Count != 2; i++) await Task.Delay(5);
        Assert.Equal(2, h.State.Servers.Count);
        h.State.Characters[1] = new Character { CharId = 1, Name = "Renamed", MainId = 2 };
        h.Service.NotifyMailDelivered(77, 1, "Operator", "OldName", "Reward", 1);
        var r = b.Receive(TimeSpan.FromSeconds(3));
        Assert.Equal(Msg.MW_POSTRECV_REQ, r.Id); Assert.Equal(77u, r.ReadUInt32()); Assert.Equal("Operator", r.ReadString());
        Assert.Equal("Renamed", r.ReadString()); Assert.Equal("Reward", r.ReadString()); Assert.Equal(1, r.ReadByte()); Assert.Equal(0, r.Remaining);
    }
    private sealed class Store : IReservedPostStore
    {
        public int Calls;
        public int Available;
        public bool Fail;
        public Task<ReservedPostResult> DeliverReservedPostAsync()
        {
            Calls++;
            if (Fail) return Task.FromException<ReservedPostResult>(new IOException("DB down"));
            return Task.FromResult(Available-- > 0 ? new ReservedPostResult(0, (uint)Calls, "NPC", "Hero", "00000005Title")
                : new ReservedPostResult(1, 0, "", "", ""));
        }
    }

    [Fact]
    public async Task DeliversAtMostThirtyPerMinuteAndContinuesNextMinuteWhileOffline()
    {
        var svc = new WorldService(new WorldState(), null, NullLogger<WorldService>.Instance);
        var db = new Store { Available = 35 }; svc.ReservedPostStore = db;
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        await svc.RunReservedPostsAsync(now); Assert.Equal(30, db.Calls);
        await svc.RunReservedPostsAsync(now.AddSeconds(59)); Assert.Equal(30, db.Calls);
        await svc.RunReservedPostsAsync(now.AddMinutes(1)); Assert.Equal(36, db.Calls);
    }

    [Fact]
    public async Task DatabaseFailureDoesNotCrashTimerAndIsRetriedNextMinute()
    {
        var svc = new WorldService(new WorldState(), null, NullLogger<WorldService>.Instance);
        var db = new Store { Fail = true }; svc.ReservedPostStore = db;
        var now = DateTime.UtcNow;
        await svc.RunReservedPostsAsync(now); Assert.Equal(1, db.Calls);
        db.Fail = false; db.Available = 1;
        await svc.RunReservedPostsAsync(now.AddMinutes(1)); Assert.Equal(3, db.Calls);
    }

    [Fact]
    public async Task DeliveredPostNotificationUsesTheCurrentMainAndNativeWireBody()
    {
        await using var h = new WorldTestHost();
        using var a = await h.ConnectAsync(); using var b = await h.ConnectAsync();
        a.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x401).WriteByte(1).WriteByte(1));
        b.Send(new PacketWriter(Msg.MW_CONNECT_ACK).WriteUInt16(0x402).WriteByte(1).WriteByte(1));
        for (int i = 0; i < 200 && h.State.Servers.Count != 2; i++) await Task.Delay(5);
        Assert.Equal(2, h.State.Servers.Count);
        var ch = new Character { CharId = 1, Name = "Hero", MainId = 2 };
        h.State.Characters[1] = ch; h.State.CharactersByName[ch.Name] = ch;
        h.Service.ReservedPostStore = new Store { Available = 1 };
        await h.Service.RunReservedPostsAsync(DateTime.UtcNow);
        var r = b.Receive(TimeSpan.FromSeconds(3));
        Assert.Equal(Msg.MW_POSTRECV_REQ, r.Id); Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal("NPC", r.ReadString()); Assert.Equal("Hero", r.ReadString());
        Assert.Equal("00000005Title", r.ReadString()); Assert.Equal(1, r.ReadByte()); Assert.Equal(0, r.Remaining);
    }
}
