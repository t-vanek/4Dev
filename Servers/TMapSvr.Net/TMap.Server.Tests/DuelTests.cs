using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Duels (C++ OnCS_DUELINVITE_REQ / OnCS_DUELINVITEREPLY_REQ / OnCS_DUELEND_REQ, the SM_DUEL* timer, CanDuel, DuelLose):
/// invite, accept, a 10 s standby then the start, and the ends — a knockout (no death, both healed), giving up, the
/// 5 minute limit, logging out.
/// </summary>
public class DuelTests
{
    private const uint A = 1, B = 2, C = 3;
    private const byte Yes = 0, No = 1;

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL: a hit on a PC connects
        t.LevelPvPoint[19] = 14;
        t.PvPointKill[(1, 2)] = (80, 20);
        return t;
    }

    private static Character Pc(uint id, string name)
        => new() { CharId = id, Name = name, Level = 19, MaxHp = 100, Hp = 100, MaxMp = 50, Mp = 50 };

    private sealed record Arena(MapTestHarness H, ClientSession Sa, FakeClientChannel Ca, Character A,
        ClientSession Sb, FakeClientChannel Cb, Character B);

    private static async Task<Arena> Two(float bx = 110)
    {
        var h = new MapTestHarness(Store());
        var a = Pc(A, "Alice"); var b = Pc(B, "Bob");
        var (sa, ca) = await h.EnterAsync(A, 1, 1, x: 100, z: 100, name: "Alice", preSeeded: a);
        var (sb, cb) = await h.EnterAsync(B, 2, 2, x: bx, z: 100, name: "Bob", preSeeded: b);
        a.Country = b.Country = 1; a.AidCountry = b.AidCountry = 3;
        h.Service.CombatRng = new Random(1);
        ca.Clear(); cb.Clear();
        return new Arena(h, sa, ca, a, sb, cb, b);
    }

    private static byte[] Invite(uint target) => new PacketWriter(Msg.CS_DUELINVITE_REQ).WriteUInt32(target).ToArray();
    private static byte[] Reply(byte result, uint inviter)
        => new PacketWriter(Msg.CS_DUELINVITEREPLY_REQ).WriteByte(result).WriteUInt32(inviter).ToArray();
    private static byte[] Hit(uint attacker, uint target, uint host)
        => MapTestHarness.DefendReq(attacker, target, attackType: 1, targetType: 1, hostId: host);

    private static async Task Seconds(MapTestHarness h, int n)
    {
        for (int i = 0; i < n; i++) await h.Service.OnTimerAsync();
    }

    private static async Task<Arena> Started()
    {
        var x = await Two();
        await x.H.Service.DispatchClientAsync(x.Sa, Invite(B));
        await x.H.Service.DispatchClientAsync(x.Sb, Reply(Yes, A));
        await Seconds(x.H, 10);
        x.Ca.Clear(); x.Cb.Clear();
        return x;
    }

    private static (byte Result, uint Inviter, uint Target) Start(FakeClientChannel c)
    {
        var r = new PacketReader(c.Last(Msg.CS_DUELSTART_ACK)!);
        return (r.ReadByte(), r.ReadUInt32(), r.ReadUInt32());
    }

    private static uint Loser(FakeClientChannel c) => new PacketReader(c.Last(Msg.CS_DUELEND_ACK)!).ReadUInt32();

    // ================================ invite and accept ================================

    [Fact]
    public async Task AnInvite_AsksTheOtherPlayer()
    {
        var x = await Two();

        await x.H.Service.DispatchClientAsync(x.Sa, Invite(B));

        Assert.Equal(A, new PacketReader(x.Cb.Last(Msg.CS_DUELINVITE_ACK)!).ReadUInt32());
    }

    [Fact]
    public async Task OffMap0_AnInviteFails()
    {
        var x = await Two();
        x.A.MapId = 3;

        await x.H.Service.DispatchClientAsync(x.Sa, Invite(B));

        Assert.Equal(((byte)3, A, B), Start(x.Ca));                          // DUEL_FAIL
        Assert.False(x.Cb.Has(Msg.CS_DUELINVITE_ACK));
    }

    [Fact]
    public async Task AYes_SetsUpTheArena_AndTenSecondsLaterTheDuelStarts()
    {
        var x = await Two();
        await x.H.Service.DispatchClientAsync(x.Sa, Invite(B));

        await x.H.Service.DispatchClientAsync(x.Sb, Reply(Yes, A));

        foreach (var c in new[] { x.Ca, x.Cb })
        {
            var r = new PacketReader(c.Last(Msg.CS_DUELSTANDBY_ACK)!);
            Assert.Equal((A, B, 105f, 100f), (r.ReadUInt32(), r.ReadUInt32(), r.ReadFloat(), r.ReadFloat()));
        }
        Assert.Equal((B, A), (x.A.DuelTarget, x.B.DuelTarget));
        await Seconds(x.H, 9);
        Assert.False(x.Ca.Has(Msg.CS_DUELSTART_ACK));
        await Seconds(x.H, 1);
        Assert.Equal(((byte)0, A, B), Start(x.Ca));
        Assert.Equal(((byte)0, A, B), Start(x.Cb));
    }

    [Fact]
    public async Task ANo_TellsBoth()
    {
        var x = await Two();

        await x.H.Service.DispatchClientAsync(x.Sb, Reply(No, A));

        Assert.Equal((No, A, B), Start(x.Ca));
        Assert.Equal((No, A, B), Start(x.Cb));
        Assert.Equal(0u, x.A.DuelId);
    }

    [Fact]
    public async Task TooFarApart_NoDuel()
    {
        var x = await Two(bx: 221);                                          // 60.5 from the midpoint

        await x.H.Service.DispatchClientAsync(x.Sb, Reply(Yes, A));

        Assert.Equal(((byte)3, A, B), Start(x.Cb));
        Assert.Equal(0u, x.B.DuelId);
    }

    // ================================ the fight and its ends ================================

    [Fact]
    public async Task AKnockout_EndsTheDuel_WithoutDeath_AndBothHealed()
    {
        var x = await Started();
        x.B.Hp = 5; x.A.Hp = 30;

        await x.H.CastAndHitAsync(x.Sa, Hit(A, B, host: A));

        Assert.Equal((100u, 100u), (x.A.Hp, x.B.Hp));
        Assert.False(x.Cb.Has(Msg.CS_DIE_ACK));
        Assert.Equal(B, Loser(x.Ca));
        Assert.Equal(B, Loser(x.Cb));
        Assert.Equal(0u, x.A.PvpTotalPoint);                                // no PvP points for a duel
        var win = new PacketReader(x.Ca.Last(Msg.CS_SYSTEMMSG_ACK)!);
        Assert.Equal(((byte)8, "Alice", "Bob"), (win.ReadByte(), win.ReadString(), win.ReadString()));   // SM_DUAL_WIN

        await Seconds(x.H, 1);
        Assert.Equal((0u, 0u), (x.A.DuelId, x.B.DuelId));                  // cleared the next second
    }

    [Fact]
    public async Task SomeoneElseOfTheSameSide_CannotTouchADueller()
    {
        var x = await Started();
        var c = Pc(C, "Carl");
        var (sc, _) = await x.H.EnterAsync(C, 3, 3, x: 102, z: 100, name: "Carl", preSeeded: c);
        c.Country = 1; c.AidCountry = 3;

        await x.H.CastAndHitAsync(sc, Hit(C, B, host: C));

        Assert.Equal(100u, x.B.Hp);
        Assert.NotEqual(0u, x.B.DuelId);
    }

    [Fact]
    public async Task AnEnemy_BreaksTheDuelUp()
    {
        var x = await Started();
        var c = Pc(C, "Carl");
        var (sc, _) = await x.H.EnterAsync(C, 3, 3, x: 102, z: 100, name: "Carl", preSeeded: c);
        c.Country = 0; c.AidCountry = 3;

        await x.H.CastAndHitAsync(sc, Hit(C, B, host: C));

        Assert.True(x.B.Hp < 100);
        Assert.Equal(0u, Loser(x.Cb));                                       // no loser
    }

    [Fact]
    public async Task GivingUp_Loses()
    {
        var x = await Started();

        await x.H.Service.DispatchClientAsync(x.Sb, new PacketWriter(Msg.CS_DUELEND_REQ).ToArray());

        Assert.Equal(B, Loser(x.Ca));
        Assert.True(x.Ca.Has(Msg.CS_SYSTEMMSG_ACK));
    }

    [Fact]
    public async Task FiveMinutesUp_NoLoser()
    {
        var x = await Started();

        await Seconds(x.H, 299);
        Assert.False(x.Ca.Has(Msg.CS_DUELEND_ACK));
        await Seconds(x.H, 1);

        Assert.Equal(0u, Loser(x.Ca));
        Assert.False(x.Ca.Has(Msg.CS_SYSTEMMSG_ACK));
    }

    [Fact]
    public async Task LoggingOut_EndsItWithNoLoser()
    {
        var x = await Started();

        x.H.Service.OnClientDisconnect(x.Sb);

        Assert.Equal(0u, Loser(x.Ca));
    }

    [Fact]
    public async Task WhileInADuel_AnotherInviteFails()
    {
        var x = await Started();

        await x.H.Service.DispatchClientAsync(x.Sa, Invite(B));

        Assert.Equal(((byte)3, A, B), Start(x.Ca));
    }
}
