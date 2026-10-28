using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>Social, batch S1c: soulmates on the map — the fees and NPC-call item, the world's answers, and the bond's effects.</summary>
public class SoulmateTests
{
    private const uint Ann = 1, Bob = 2, Cid = 3;
    private const ushort Caller = 7643;                         // Fortune-teller Evocation (IK_NPCCALL)
    private const uint SearchFee = 300, RegFee = 500;
    private static readonly ItemTemplate CallerTpl = new(Caller, 0, new[] { 1f, 0f, 0f, 0f }, Kind: 55);

    private static TemplateStore Store()
    {
        var t = new TemplateStore { Rate1st = 1.0f };
        t.Classes[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Races[0] = new StatSeed(1, 1, 1, 1, 1, 1);
        t.Formulas[5] = new FormulaRow(10, 0f, 0f);                       // FTYPE_AL: a hit on a PC connects
        t.SoulmateCost[30] = (SearchFee, RegFee);
        t.Items[Caller] = CallerTpl;
        return t;
    }

    private static Character Pc(uint id, string name, uint hp = 100)
    {
        var ch = new Character { CharId = id, Name = name, Level = 30, MaxHp = 100, Hp = hp, MaxMp = 50, Mp = 50, Cooper = 1000 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        return ch;
    }

    private static async Task<(MapTestHarness h, ClientSession a, FakeClientChannel ca, Character ann, Character bob)> Setup()
    {
        var h = new MapTestHarness(Store());
        var ann = Pc(Ann, "Ann"); var bob = Pc(Bob, "Bob");
        var (a, ca) = await h.EnterAsync(Ann, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ann);
        await h.EnterAsync(Bob, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: bob);
        ann.Level = bob.Level = 30; ann.Name = "Ann"; bob.Name = "Bob";
        h.Service.CombatRng = new Random(1);
        ca.Clear(); h.World.Clear();
        return (h, a, ca, ann, bob);
    }

    private static byte[] Cs(ushort id, Action<PacketWriter>? body = null) { var w = new PacketWriter(id); body?.Invoke(w); return w.ToArray(); }

    private static byte[] World(ushort id, ClientSession s, Action<PacketWriter> body)
    {
        var w = new PacketWriter(id);
        w.WriteUInt32(s.CharId); w.WriteUInt32(s.Key);
        body(w);
        return w.ToArray();
    }

    private static byte[] Search(byte inv = 0xFF, byte slot = 0xFF) => Cs(Msg.CS_SOULMATESEARCH_REQ, w => { w.WriteByte(inv); w.WriteByte(slot); });

    private static void GiveCaller(MapTestHarness _, Character ch)
        => ch.FindInven(0xFF)!.Items.Add(new Item { ItemSlot = 4, TemplateId = Caller, Count = 2, Template = CallerTpl });

    [Fact]
    public async Task ASearch_GoesToTheWorld_WithTheLowestLevelAround()
    {
        var (h, a, _, _, bob) = await Setup();
        bob.Level = 29;

        await h.Service.DispatchClientAsync(a, Search());

        var r = new PacketReader(h.World.Last(Msg.MW_SOULMATESEARCH_ACK)!);
        Assert.Equal((Ann, a.Key, (byte)29, (byte)0xFF, (byte)0xFF), (r.ReadUInt32(), r.ReadUInt32(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
    }

    [Fact]
    public async Task ASearch_IsRefused_WithoutTheFee_OrWithinADayOfTheLastBond()
    {
        var (h, a, ca, ann, _) = await Setup();

        ann.Cooper = 10;
        await h.Service.DispatchClientAsync(a, Search());
        Assert.Equal(4, new PacketReader(ca.Last(Msg.CS_SOULMATESEARCH_ACK)!).ReadByte());       // SOULMATE_NEEDMONEY
        ann.Cooper = 1000; ann.SoulSilence = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        await h.Service.DispatchClientAsync(a, Search());
        Assert.Equal(2, new PacketReader(ca.Last(Msg.CS_SOULMATESEARCH_ACK)!).ReadByte());       // SOULMATE_SILENCE
        Assert.False(h.World.Has(Msg.MW_SOULMATESEARCH_ACK));
    }

    [Fact]
    public async Task TheNpcCallItem_OnlyWorksOnMaps0And8()
    {
        var (h, a, ca, ann, _) = await Setup();
        ann.MapId = 3;
        GiveCaller(h, ann);

        await h.Service.DispatchClientAsync(a, Search(0xFF, 4));

        Assert.Equal(7, new PacketReader(ca.Last(Msg.CS_SOULMATESEARCH_ACK)!).ReadByte());       // SOULMATE_INVALIDPOS
    }

    [Fact]
    public async Task TheWorldsFind_IsPaidFor_AndBecomesTheSoulmate()
    {
        var (h, a, ca, ann, _) = await Setup();
        GiveCaller(h, ann);

        await h.Service.DispatchWorldAsync(World(Msg.MW_SOULMATESEARCH_REQ, a, w =>
        {
            w.WriteByte(0); w.WriteUInt32(Bob); w.WriteString("Bob"); w.WriteUInt32(7); w.WriteByte(0xFF); w.WriteByte(4);
        }));

        Assert.Equal((Bob, "Bob"), (ann.SoulmateId, ann.SoulmateName));
        Assert.Equal(700u, ann.Cooper);
        Assert.Equal(1, ann.FindInven(0xFF)!.FindItem(4)!.Count);
        var r = new PacketReader(ca.Last(Msg.CS_SOULMATESEARCH_ACK)!);
        Assert.Equal(((byte)0, Bob, "Bob", 7u), (r.ReadByte(), r.ReadUInt32(), r.ReadString(), r.ReadUInt32()));
        Assert.Equal((0u, 0u, 700u), (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
    }

    [Fact]
    public async Task ChoosingByName_ALookFirst_ThenTheBond()
    {
        var (h, a, ca, ann, _) = await Setup();

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_SOULMATEREGREADY_REQ, w => { w.WriteString("Bob"); w.WriteByte(0xFF); w.WriteByte(0xFF); }));
        var look = new PacketReader(h.World.Last(Msg.MW_SOULMATEREG_ACK)!);
        look.ReadUInt32(); look.ReadUInt32();
        Assert.Equal(("Bob", (byte)0), (look.ReadString(), look.ReadByte()));
        await h.Service.DispatchWorldAsync(World(Msg.MW_SOULMATEREG_REQ, a, w =>
        {
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteUInt32(Bob); w.WriteString("Bob"); w.WriteUInt32(7);
        }));
        var ready = new PacketReader(ca.Last(Msg.CS_SOULMATEREGREADY_ACK)!);
        Assert.Equal(((byte)0, "Bob"), (ready.ReadByte(), ready.ReadString()));
        Assert.Equal(0u, ann.SoulmateId);

        await h.Service.DispatchWorldAsync(World(Msg.MW_SOULMATEREG_REQ, a, w =>
        {
            w.WriteByte(0); w.WriteByte(1); w.WriteByte(0xFF); w.WriteByte(0xFF); w.WriteUInt32(Bob); w.WriteString("Bob"); w.WriteUInt32(7);
        }));
        Assert.Equal((Bob, 500u), (ann.SoulmateId, ann.Cooper));
        Assert.Equal(0, new PacketReader(ca.Last(Msg.CS_SOULMATEREG_ACK)!).ReadByte());

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_SOULMATEREG_REQ, w => { w.WriteString("Bob"); w.WriteByte(0xFF); w.WriteByte(0xFF); }));
        Assert.Equal(5, new PacketReader(ca.Last(Msg.CS_SOULMATEREG_ACK)!).ReadByte());          // SOULMATE_ALREADY
    }

    [Fact]
    public async Task Ending_GoesToTheWorld_AndItsAnswerStartsTheDay()
    {
        var (h, a, ca, ann, _) = await Setup();

        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_SOULMATEEND_REQ));
        Assert.False(h.World.Has(Msg.MW_SOULMATEEND_ACK));                                       // no soulmate
        ann.SoulmateId = Bob; ann.SoulmateName = "Bob";
        await h.Service.DispatchClientAsync(a, Cs(Msg.CS_SOULMATEEND_REQ));
        Assert.True(h.World.Has(Msg.MW_SOULMATEEND_ACK));

        await h.Service.DispatchWorldAsync(World(Msg.MW_SOULMATEEND_REQ, a, w => { w.WriteByte(0); w.WriteUInt32(12345); }));
        Assert.Equal((0u, "", 12345u), (ann.SoulmateId, ann.SoulmateName, ann.SoulSilence));
        Assert.Equal(0, new PacketReader(ca.Last(Msg.CS_SOULMATEEND_ACK)!).ReadByte());
    }

    [Fact]
    public async Task OnEntering_TheWorldTellsTheSoulmate()
    {
        var (h, a, ca, ann, _) = await Setup();

        await h.Service.DispatchWorldAsync(World(Msg.MW_SOULMATE_REQ, a, w => { w.WriteUInt32(Bob); w.WriteString("Bob"); w.WriteUInt32(0); }));

        Assert.Equal(Bob, ann.SoulmateId);
        var r = new PacketReader(ca.Last(Msg.CS_SOULMATE_ACK)!);
        Assert.Equal((Bob, "Bob"), (r.ReadUInt32(), r.ReadString()));
    }

    [Fact]
    public async Task APartyKill_GivesTheSoulmateATenthMore_AndALevelIsToldToTheWorld()
    {
        var t = new TemplateStore();
        t.LevelExp[5] = 100; t.LevelExp[6] = 100_000;
        var h = new MapTestHarness(t);
        var (s1, _) = await h.EnterAsync(1, 1, 1, name: "A", x: 100, z: 100);
        var (s2, _) = await h.EnterAsync(2, 2, 2, name: "B", x: 100, z: 100);
        foreach (var s in new[] { s1, s2 }) { s.Char!.Level = 5; s.Char.PartyId = 7; s.Char.PartyType = 0; s.Char.Hp = 100; s.Char.MaxHp = 100; }
        s1.Char!.SoulmateId = 2;
        var mob = new Monster { Id = 0x70001, ChartId = 500, Level = 5, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50, Exp = 1000,
            PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.World.Clear();

        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));
        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));

        Assert.Equal(322u, s1.Char.Exp);                                                         // see PartyTests
        Assert.Equal(322u + 32u, s2.Char!.Exp);
        var up = new PacketReader(h.World.Last(Msg.MW_LEVELUP_ACK)!);
        up.ReadUInt32(); up.ReadUInt32();
        Assert.Equal(6, up.ReadByte());
    }

    [Fact]
    public async Task AHurtPlayer_TakesHp_FromItsSoulmateInTheParty()
    {
        var h = new MapTestHarness(Store());
        var ann = Pc(Ann, "Ann", hp: 12); var bob = Pc(Bob, "Bob"); var cid = Pc(Cid, "Cid");
        await h.EnterAsync(Ann, 1, 1, x: 100, z: 100, name: "Ann", preSeeded: ann);
        await h.EnterAsync(Bob, 2, 2, x: 101, z: 100, name: "Bob", preSeeded: bob);
        var (c, _) = await h.EnterAsync(Cid, 3, 3, x: 102, z: 100, name: "Cid", preSeeded: cid);
        ann.Hp = 12; ann.PartyId = bob.PartyId = 5;
        bob.SoulmateId = Ann;
        h.Service.CombatRng = new Random(1);

        await h.CastAndHitAsync(c, MapTestHarness.DefendReq(Cid, Ann, attackType: 1, targetType: 1));

        Assert.Equal(65u, bob.Hp);                                                               // keeps 65%
        Assert.InRange(ann.Hp, 41u, 42u);                                                        // 12 − (5..6) + 35
    }
}
