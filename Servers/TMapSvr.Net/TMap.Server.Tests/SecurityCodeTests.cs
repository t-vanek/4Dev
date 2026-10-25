using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public class SecurityCodeTests
{
    private const string Code = "1234!abc";
    private sealed class Store : ISecurityCodeStore
    {
        public SecurityCodeRow? Row;
        public bool Fail;
        public bool FailLoad;
        public Task<SecurityCodeRow?> LoadSecurityCodeAsync(uint id)
            => FailLoad ? Task.FromException<SecurityCodeRow?>(new IOException("database unavailable")) : Task.FromResult(Row);
        public Task SaveSecurityCodeAsync(uint id, SecurityCodeRow row)
        {
            if (Fail) throw new IOException("database unavailable");
            Row = row;
            return Task.CompletedTask;
        }
    }

    private static Task Send(MapTestHarness h, ClientSession s, ushort id, params string[] values)
    {
        var w = new PacketWriter(id);
        foreach (string v in values) w.WriteString(v);
        return h.Service.DispatchClientAsync(s, w.ToArray());
    }
    private static byte Result(FakeClientChannel c, ushort id) => new PacketReader(c.Last(id)!).ReadByte();

    [Fact]
    public async Task Create_StoresSaltedHash_RejectsDuplicateAndMalformedCodes()
    {
        var h = new MapTestHarness(); var db = new Store(); h.Service.SecurityCodeStore = db;
        var (s, c) = await h.EnterAsync(1, 9, 10);
        await Send(h, s, Msg.CS_SC_CREATECODE_REQ, "short");
        Assert.Equal(3, Result(c, Msg.CS_SC_CREATECODE_ACK)); Assert.Null(db.Row);
        await Send(h, s, Msg.CS_SC_CREATECODE_REQ, Code);
        Assert.Equal(2, Result(c, Msg.CS_SC_CREATECODE_ACK));
        Assert.True(s.SecurityUnlocked); Assert.True(db.Row!.Enabled);
        Assert.DoesNotContain(Code, db.Row.Hash); Assert.True(SecurityCodeHash.Verify(Code, db.Row.Hash));
        Assert.NotEqual(db.Row.Hash, SecurityCodeHash.Create(Code));
        await Send(h, s, Msg.CS_SC_CREATECODE_REQ, "other123!");
        Assert.Equal(1, Result(c, Msg.CS_SC_CREATECODE_ACK));
    }

    [Fact]
    public async Task Relog_LocksInventoryUntilUnlock_CheckDoesNotUnlock()
    {
        var db = new Store { Row = new(SecurityCodeHash.Create(Code), true, 0, 0) };
        var h = new MapTestHarness(); h.Service.SecurityCodeStore = db;
        var (s, c) = await h.EnterAsync(1, 9, 10);
        var info = new PacketReader(c.Last(Msg.CS_CHARINFO_ACK)!); info.ReadUInt32();
        Assert.Equal((true, false, false), (info.ReadBool(), info.ReadBool(), info.ReadBool()));
        c.Clear();
        await Send(h, s, Msg.CS_MOVEITEM_REQ); // no body: the security gate runs before the item handler
        Assert.Equal(0, Result(c, Msg.CS_SC_UNLOCK_ACK));
        Assert.Equal((byte)MoveItemResult.SamePos, Result(c, Msg.CS_MOVEITEM_ACK));
        await Send(h, s, Msg.CS_SC_CHECKCODE_REQ, Code);
        Assert.Equal(9, Result(c, Msg.CS_SC_CHECKCODE_ACK)); Assert.False(s.SecurityUnlocked);
        await Send(h, s, Msg.CS_SC_UNLOCK_REQ, Code);
        Assert.Equal(8, Result(c, Msg.CS_SC_UNLOCK_ACK)); Assert.True(s.SecurityUnlocked);
        h.Service.OnClientDisconnect(s);
        var (again, _) = await h.EnterAsync(1, 9, 11);
        Assert.False(again.SecurityUnlocked);
    }

    [Fact]
    public async Task ChangeDisableEnable_Persists_AndOldCodeStopsWorking()
    {
        var db = new Store { Row = new(SecurityCodeHash.Create(Code), true, 0, 0) };
        var h = new MapTestHarness(); h.Service.SecurityCodeStore = db;
        var (s, c) = await h.EnterAsync(1, 9, 10);
        await Send(h, s, Msg.CS_SC_CHANGECODE_REQ, "wrong123!", "5678!new");
        Assert.Equal(7, Result(c, Msg.CS_SC_CHANGECODE_ACK)); Assert.Equal(1, db.Row!.Tries);
        await Send(h, s, Msg.CS_SC_CHANGECODE_REQ, Code, "5678!new");
        Assert.Equal(5, Result(c, Msg.CS_SC_CHANGECODE_ACK)); Assert.Equal(0, db.Row.Tries);
        Assert.False(SecurityCodeHash.Verify(Code, db.Row.Hash));
        await Send(h, s, Msg.CS_SC_DISABLECODE_REQ, "5678!new");
        Assert.Equal(6, Result(c, Msg.CS_SC_DISABLECODE_ACK)); Assert.False(db.Row.Enabled);
        await Send(h, s, Msg.CS_SC_ENABLECODE_REQ);
        Assert.Equal(4, Result(c, Msg.CS_SC_ENABLECODE_ACK)); Assert.True(db.Row.Enabled);
    }

    [Fact]
    public async Task TenFailures_LockAccountAcrossSessionsAndRestarts_Until24Hours()
    {
        long now = 10_000;
        var db = new Store { Row = new(SecurityCodeHash.Create(Code), true, 0, 0) };
        var h = new MapTestHarness(); h.Service.SecurityCodeStore = db; h.Service.UnixNow = () => now;
        var (s, c) = await h.EnterAsync(1, 9, 10);
        for (int i = 0; i < 10; i++) await Send(h, s, Msg.CS_SC_UNLOCK_REQ, "wrong123!");
        Assert.Equal(10, db.Row!.Tries); Assert.Equal(now + 86400, db.Row.LockedUntil);
        var restarted = new MapTestHarness(); restarted.Service.SecurityCodeStore = db; restarted.Service.UnixNow = () => now;
        var (again, cc) = await restarted.EnterAsync(2, 9, 11);
        await Send(restarted, again, Msg.CS_SC_UNLOCK_REQ, Code);
        Assert.Equal(0, Result(cc, Msg.CS_SC_UNLOCK_ACK)); Assert.False(again.SecurityUnlocked);
        await Send(restarted, again, Msg.CS_SC_GETLOCKTIME_REQ);
        var remaining = new PacketReader(cc.Last(Msg.CS_SC_GETLOCKTIME_ACK)!);
        Assert.Equal((24, 0), (remaining.ReadByte(), remaining.ReadByte()));
        now += 86400;
        await Send(restarted, again, Msg.CS_SC_UNLOCK_REQ, Code);
        Assert.Equal(8, Result(cc, Msg.CS_SC_UNLOCK_ACK)); Assert.Equal(0, db.Row.Tries); Assert.Equal(0, db.Row.LockedUntil);
    }

    [Fact]
    public async Task DatabaseFailure_NeverAcknowledgesAnUnlock()
    {
        var db = new Store { Row = new(SecurityCodeHash.Create(Code), true, 0, 0), Fail = true };
        var h = new MapTestHarness(); h.Service.SecurityCodeStore = db;
        var (s, c) = await h.EnterAsync(1, 9, 10); c.Clear();
        await Send(h, s, Msg.CS_SC_UNLOCK_REQ, Code);
        Assert.False(s.SecurityUnlocked); Assert.True(c.Closed); Assert.False(c.Has(Msg.CS_SC_UNLOCK_ACK));
    }

    [Fact]
    public async Task FailedLoad_CannotBeBypassedByAlreadyQueuedWorldHandshakePackets()
    {
        var h = new MapTestHarness(); h.Service.SecurityCodeStore = new Store { FailLoad = true };
        // The harness deliberately continues the world handshake after the fake socket has been closed.
        var (s, c) = await h.EnterAsync(1, 9, 10);
        Assert.True(c.Closed); Assert.True(s.SecurityStateFailed); Assert.NotEqual(EnterState.InGame, s.State);
    }
}
