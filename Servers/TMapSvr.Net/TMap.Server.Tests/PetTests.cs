using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>An in-memory pet store recording what the map asked of it.</summary>
internal sealed class FakePetStore : IPetStore
{
    public List<(uint UserId, ushort PetId)> Deleted { get; } = new();
    public List<(uint CharId, PetRow Pet)> Saved { get; } = new();
    public SaddleRow Saddle { get; set; }
    public bool SaddleDeleted { get; private set; }
    public List<SaddleRow> SaddlesSet { get; } = new();

    public Task<List<PetRow>> LoadPetsAsync(uint userId) => Task.FromResult(new List<PetRow>());
    public Task SavePetAsync(uint charId, PetRow pet) { Saved.Add((charId, pet)); return Task.CompletedTask; }
    public Task DeletePetAsync(uint userId, ushort petId) { Deleted.Add((userId, petId)); return Task.CompletedTask; }
    public Task<SaddleRow> GetSaddleAsync(uint userId) => Task.FromResult(Saddle);
    public Task SetSaddleAsync(uint userId, SaddleRow saddle) { SaddlesSet.Add(saddle); return Task.CompletedTask; }
    public Task DeleteSaddleAsync(uint userId) { SaddleDeleted = true; return Task.CompletedTask; }
}

/// <summary>
/// Pets and mounts on top of the summon core (C++ CSHandler.cpp:11547-12015, SSHandler.cpp:1632-1827): making and
/// extending a pet, calling it through the world, riding it, sending it away, and the riding rules.
/// </summary>
public class PetTests
{
    private const ushort Mount = 5, PlainMon = 31029, SaddledMon = 31104, SummonAttr = 1001;
    private const ushort MountItem = 7549, MountItem30d = 7600, PermanentItem = 7601, UnknownMountItem = 7602, Potion = 4001;
    private const ushort SaddleItem = 7700;
    private const long Now = 2_000_000_000;
    private const byte Level = 20;
    private const uint UserId = 77, Key = 5;

    private static TemplateStore Store()
    {
        var t = new TemplateStore();
        t.Mounts[Mount] = new MountTemplate(Mount, PlainMon, SaddledMon);
        foreach (var id in new[] { PlainMon, SaddledMon })
            t.MonsterTemplates[id] = new MonsterTemplate(id, 1, 0, Skill1: 0, RecallType: RecallMon.TypePet, SummonAttr: SummonAttr, Race: 51);
        t.MonAttrs[TemplateStore.MonAttrKey(SummonAttr, Level)] = new MonAttrRow(SummonAttr, Level, 900, 300, 10, AttackLevel: 7, AtkMin: 11, AtkMax: 22);
        t.Items[MountItem] = new ItemTemplate(MountItem, 0, new float[4], Type: 12, Kind: 23, UseValue: Mount, UseTime: 2, UseType: 0x01);   // 2 hours
        t.Items[MountItem30d] = new ItemTemplate(MountItem30d, 0, new float[4], Type: 12, Kind: 23, UseValue: Mount, UseTime: 30, UseType: 0x02);
        t.Items[PermanentItem] = new ItemTemplate(PermanentItem, 0, new float[4], Type: 12, Kind: 23, UseValue: Mount, UseType: 64);
        t.Items[UnknownMountItem] = new ItemTemplate(UnknownMountItem, 0, new float[4], Type: 12, Kind: 23, UseValue: 99, UseType: 64);
        t.Items[Potion] = new ItemTemplate(Potion, 0, new float[4], Type: 7, Kind: 12);
        t.Items[SaddleItem] = new ItemTemplate(SaddleItem, 0, new float[4], Type: 21, Kind: 98, UseValue: 1, UseTime: 10);
        return t;
    }

    private static async Task<(MapTestHarness h, ClientSession s, FakeClientChannel c, Character ch, FakePetStore db)> Setup(
        Action<Character>? seed = null)
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var db = new FakePetStore();
        h.Service.PetStore = db;
        h.Service.UnixNow = () => Now;
        var ch = new Character { CharId = 1, Name = "Rider", MaxHp = 100, Hp = 100 };
        var bag = new Inven { InvenId = 0 };
        foreach (var (slot, item) in new[] { (1, MountItem), (2, MountItem30d), (3, PermanentItem), (4, UnknownMountItem), (5, Potion), (6, SaddleItem) })
            bag.Items.Add(new Item { ItemSlot = (byte)slot, TemplateId = item, Template = t.Items[item], Count = 2 });
        ch.Invens.Add(bag);
        seed?.Invoke(ch);
        var (s, c) = await h.EnterAsync(1, UserId, Key, name: "Rider", preSeeded: ch);
        ch.Level = Level;
        c.Clear(); h.World.Clear();
        return (h, s, c, ch, db);
    }

    private static void GivePet(Character ch, long end = 0) =>
        ch.Pets[Mount] = new Pet { PetId = Mount, Name = "Blaze", EndTime = end, Template = new MountTemplate(Mount, PlainMon, SaddledMon) };

    private static byte[] Make(byte slot, string name = "Blaze")
    {
        var w = new PacketWriter(Msg.CS_PETMAKE_REQ);
        w.WriteByte(0); w.WriteByte(slot); w.WriteString(name);
        return w.ToArray();
    }

    private static byte[] Req(ushort id, Action<PacketWriter> body) { var w = new PacketWriter(id); body(w); return w.ToArray(); }

    /// <summary>The world's answer to a summon request: the same record, with an id.</summary>
    private static byte[] WorldCreates(byte[] mapAck, uint monId)
    {
        var raw = (byte[])mapAck.Clone();
        PacketHeader.WriteId(raw, Msg.MW_CREATERECALLMON_REQ);
        BitConverter.GetBytes(monId).CopyTo(raw, PacketHeader.Size + 8);
        return raw;
    }

    private static byte[] WorldDeletes(uint charId, uint monId, byte forever = 1) => Req(Msg.MW_RECALLMONDEL_REQ, w =>
    {
        w.WriteUInt32(charId); w.WriteUInt32(Key); w.WriteUInt32(monId); w.WriteByte(forever);
    });

    private static async Task<RecallMon> CallAndSpawn(MapTestHarness h, ClientSession s, Character ch, uint monId = 900)
    {
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRECALL_REQ, w => w.WriteUInt16(Mount)));
        await h.Service.DispatchWorldAsync(WorldCreates(h.World.Last(Msg.MW_CREATERECALLMON_ACK)!, monId));
        return ch.Recalls[monId];
    }

    // ================================ make / delete ================================

    [Fact]
    public async Task UsingAMountItem_MakesAPet_ForItsDuration_AndUsesTheItem()
    {
        var (h, s, c, ch, _) = await Setup();

        await h.Service.DispatchClientAsync(s, Make(1));

        var r = new PacketReader(c.Last(Msg.CS_PETMAKE_ACK)!);
        Assert.Equal(0, r.ReadByte());                       // PET_SUCCESS
        Assert.Equal(Mount, r.ReadUInt16());
        Assert.Equal("Blaze", r.ReadString());
        Assert.Equal(Now + 2 * 3600, r.ReadInt64());         // DURINGTYPE_TIME: hours
        Assert.Equal(0, r.Remaining);
        Assert.Equal(1, ch.FindInven(0)!.Items.First(i => i.ItemSlot == 1).Count);
    }

    [Fact]
    public async Task ASecondItem_ExtendsAnUnexpiredPet_AndAPermanentOne_MakesItPermanent()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch, Now + 100));

        await h.Service.DispatchClientAsync(s, Make(2, "Renamed"));
        Assert.Equal(Now + 100 + 30 * 86400, ch.Pets[Mount].EndTime);   // += for an unexpired pet
        Assert.Equal("Renamed", ch.Pets[Mount].Name);

        await h.Service.DispatchClientAsync(s, Make(3));
        Assert.Equal(0, ch.Pets[Mount].EndTime);                         // permanent
    }

    [Fact]
    public async Task AnExpiredPet_IsRenewedFromNow()
    {
        var (h, s, _, ch, _) = await Setup(ch => GivePet(ch, Now - 5000));
        await h.Service.DispatchClientAsync(s, Make(2));
        Assert.Equal(Now + 30 * 86400, ch.Pets[Mount].EndTime);
    }

    [Fact]
    public async Task NotAMountItem_IsRefused_AndAnUnknownMount_StillUsesTheItem()
    {
        var (h, s, c, ch, _) = await Setup();

        await h.Service.DispatchClientAsync(s, Make(5));
        Assert.Equal(2, new PacketReader(c.Last(Msg.CS_PETMAKE_ACK)!).ReadByte());     // PET_NOTITEM
        Assert.Equal(2, ch.FindInven(0)!.Items.First(i => i.ItemSlot == 5).Count);

        await h.Service.DispatchClientAsync(s, Make(4));
        Assert.Equal(3, new PacketReader(c.Last(Msg.CS_PETMAKE_ACK)!).ReadByte());     // PET_NOTFOUND
        Assert.Equal(1, ch.FindInven(0)!.Items.First(i => i.ItemSlot == 4).Count);    // faithful: already used
        Assert.Empty(ch.Pets);
    }

    [Fact]
    public async Task OnlyAnExpiredPet_CanBeDeleted_AndTheAccountRowGoes()
    {
        var (h, s, c, ch, db) = await Setup(ch => GivePet(ch, Now + 100));
        byte[] del = Req(Msg.CS_PETDEL_REQ, w => w.WriteUInt16(Mount));

        await h.Service.DispatchClientAsync(s, del);
        Assert.Equal(5, new PacketReader(c.Last(Msg.CS_PETDEL_ACK)!).ReadByte());      // PET_USETIME

        ch.Pets[Mount].EndTime = Now - 1;
        await h.Service.DispatchClientAsync(s, del);
        Assert.Equal(0, new PacketReader(c.Last(Msg.CS_PETDEL_ACK)!).ReadByte());
        Assert.Empty(ch.Pets);
        Assert.Equal((UserId, Mount), Assert.Single(db.Deleted));
    }

    [Fact]
    public async Task ThePetList_IsSentAtLogin()
    {
        var t = Store();
        var h = new MapTestHarness(t);
        var ch = new Character { CharId = 1, Name = "Rider", MaxHp = 100, Hp = 100 };
        GivePet(ch, 1234);
        var (_, c) = await h.EnterAsync(1, UserId, Key, preSeeded: ch);

        var r = new PacketReader(c.Last(Msg.CS_PETLIST_ACK)!);
        Assert.Equal(1, r.ReadByte());
        Assert.Equal(Mount, r.ReadUInt16());
        Assert.Equal("Blaze", r.ReadString());
        Assert.Equal(1234, r.ReadInt64());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0, r.Remaining);
        Assert.True(c.Has(Msg.CS_SENDSADDLE_REQ));
    }

    // ================================ calling ================================

    [Fact]
    public async Task CallingAPet_AsksTheWorldForTheMountSummon()
    {
        var (h, s, _, ch, _) = await Setup(ch => GivePet(ch));

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRECALL_REQ, w => w.WriteUInt16(Mount)));

        var r = new PacketReader(h.World.Last(Msg.MW_CREATERECALLMON_ACK)!);
        Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(Key, r.ReadUInt32());
        Assert.Equal(0u, r.ReadUInt32());                                   // the world picks the id
        Assert.Equal(PlainMon, r.ReadUInt16());
        Assert.Equal(SummonAttr | ((uint)Level << 16), r.ReadUInt32());     // MAKELONG(wSummonAttr, level)
        Assert.Equal(Mount, r.ReadUInt16());
        r.ReadByte();
        Assert.Equal("Blaze", r.ReadString());
        Assert.Equal(Level, r.ReadByte());
        r.ReadByte(); Assert.Equal(51, r.ReadByte());                       // class, race from the template
        r.ReadByte(); Assert.Equal(1, r.ReadByte()); r.ReadByte();          // action, OS_WAKEUP, mode
        for (int i = 0; i < 4; i++) Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(100, r.ReadByte()); Assert.Equal(1, r.ReadByte());     // hit, skill level
        r.ReadFloat(); r.ReadFloat(); r.ReadFloat(); r.ReadUInt16();
        Assert.Equal(0u, r.ReadUInt32());                                   // permanent: no life limit
        r.ReadByte(); r.ReadUInt32(); r.ReadByte();
        Assert.Equal(0, r.ReadByte());                                      // no skills
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public async Task ATimedPet_LivesAtMostSevenDays_AndASaddle_SwitchesTheModel()
    {
        var (h, s, _, ch, _) = await Setup(ch => { GivePet(ch, Now + 30 * 86400); ch.Saddle = new SaddleRow(SaddleItem, 0, 1); });

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRECALL_REQ, w => w.WriteUInt16(Mount)));

        var raw = h.World.Last(Msg.MW_CREATERECALLMON_ACK)!;
        Assert.Equal(SaddledMon, BitConverter.ToUInt16(raw, PacketHeader.Size + 12));
        var r = new PacketReader(raw);
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt16(); r.ReadUInt32(); r.ReadUInt16(); r.ReadByte(); r.ReadString();
        for (int i = 0; i < 6; i++) r.ReadByte();
        for (int i = 0; i < 4; i++) r.ReadUInt32();
        r.ReadByte(); r.ReadByte(); r.ReadFloat(); r.ReadFloat(); r.ReadFloat(); r.ReadUInt16();
        Assert.Equal(604_800_000u, r.ReadUInt32());                         // min(PET_LIVE_DURATION, remaining) ms
    }

    [Theory]
    [InlineData("dead", 1)]
    [InlineData("expired", 5)]
    [InlineData("unknown", 3)]
    [InlineData("blocked", 1)]
    public async Task CallingAPet_Fails(string why, byte result)
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        switch (why)
        {
            case "dead": ch.Hp = 0; break;
            case "expired": ch.Pets[Mount].EndTime = Now - 1; break;
            case "unknown": ch.Pets.Clear(); break;
            case "blocked": ch.MaintainSkills.Add(new MaintainSkill { SkillId = 805 }); break;   // TBLOCKRIDE_SKILL
        }

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRECALL_REQ, w => w.WriteUInt16(Mount)));

        Assert.Equal(result, new PacketReader(c.Last(Msg.CS_PETRECALL_ACK)!).ReadByte());
        Assert.False(h.World.Has(Msg.MW_CREATERECALLMON_ACK));
    }

    [Fact]
    public async Task TheWorldsAnswer_ShowsTheMountToEveryoneAround()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        var (_, other) = await h.EnterAsync(2, 2, 2, name: "Watcher");
        other.Clear();

        var mon = await CallAndSpawn(h, s, ch);

        Assert.Equal(RecallMon.TypePet, mon.RecallType);
        Assert.Equal(900u, mon.MaxHp);                                    // from TMONATTRCHART (1001, level 20)
        foreach (var viewer in new[] { c, other })
        {
            var r = new PacketReader(viewer.Last(Msg.CS_ADDRECALLMON_ACK)!);
            Assert.Equal(1u, r.ReadUInt32());                             // host
            Assert.Equal(900u, r.ReadUInt32());
            Assert.Equal(PlainMon, r.ReadUInt16());
            Assert.Equal(Mount, r.ReadUInt16());
            r.ReadByte();
            Assert.Equal("Blaze", r.ReadString());
            r.ReadByte(); r.ReadByte(); r.ReadByte(); Assert.Equal(Level, r.ReadByte());
            Assert.Equal(900u, r.ReadUInt32()); Assert.Equal(900u, r.ReadUInt32());
            Assert.Equal(300u, r.ReadUInt32()); Assert.Equal(300u, r.ReadUInt32());
            r.ReadFloat(); r.ReadFloat(); r.ReadFloat(); r.ReadUInt16(); r.ReadUInt16(); r.ReadByte(); r.ReadByte();
            r.ReadByte(); r.ReadByte();
            Assert.Equal(1, r.ReadByte());                                // bNewMember
            r.ReadUInt32();
            Assert.Equal(RecallMon.TypePet, r.ReadByte());
            Assert.Equal(100, r.ReadByte()); Assert.Equal(1, r.ReadByte());
            Assert.Equal(7, r.ReadUInt16()); Assert.Equal(Level, r.ReadByte());
            Assert.Equal(11u, r.ReadUInt32()); Assert.Equal(22u, r.ReadUInt32());
            r.ReadUInt32(); r.ReadUInt32();
            Assert.Equal(0u, r.ReadUInt32());                             // immortal: no life left to show
            r.ReadUInt32(); r.ReadByte();
            Assert.Equal(0, r.ReadByte());
            Assert.Equal(0, r.Remaining);
        }
    }

    [Fact]
    public async Task CallingAgain_SendsTheOldMountAway_First()
    {
        var (h, s, _, ch, _) = await Setup(ch => GivePet(ch));
        await CallAndSpawn(h, s, ch, 900);
        h.World.Clear();

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRECALL_REQ, w => w.WriteUInt16(Mount)));

        var del = new PacketReader(h.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32();
        Assert.Equal(900u, del.ReadUInt32());
        Assert.True(h.World.Has(Msg.MW_CREATERECALLMON_ACK));
    }

    // ================================ riding ================================

    [Fact]
    public async Task Riding_IsToldToTheWorldAndEveryoneAround()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        var (_, other) = await h.EnterAsync(2, 2, 2, name: "Watcher");
        await CallAndSpawn(h, s, ch);
        other.Clear(); c.Clear();

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRIDING_REQ, w => { w.WriteUInt32(900); w.WriteByte(1); }));

        Assert.Equal(900u, ch.Riding);
        foreach (var viewer in new[] { c, other })
        {
            var r = new PacketReader(viewer.Last(Msg.CS_PETRIDING_ACK)!);
            Assert.Equal(0, r.ReadByte()); Assert.Equal(1u, r.ReadUInt32()); Assert.Equal(900u, r.ReadUInt32());
            Assert.Equal(1, r.ReadByte());                                // PETACTION_RIDING
        }
        var mw = new PacketReader(h.World.Last(Msg.MW_PETRIDING_ACK)!);
        Assert.Equal(1u, mw.ReadUInt32()); Assert.Equal(Key, mw.ReadUInt32()); Assert.Equal(900u, mw.ReadUInt32());

        // A player walking into view sees the rider already mounted (CS_ENTER_ACK dwRiding).
        var (_, late) = await h.EnterAsync(3, 3, 3, name: "Late");
        Assert.True(late.Has(Msg.CS_ADDRECALLMON_ACK));
    }

    [Fact]
    public async Task SendingTheMountAway_Dismounts()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        await CallAndSpawn(h, s, ch);
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRIDING_REQ, w => { w.WriteUInt32(900); w.WriteByte(1); }));
        c.Clear(); h.World.Clear();

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETCANCEL_REQ, _ => { }));
        Assert.True(h.World.Has(Msg.MW_RECALLMONDEL_ACK));
        await h.Service.DispatchWorldAsync(WorldDeletes(1, 900));

        Assert.Equal(0u, ch.Riding);
        Assert.Empty(ch.Recalls);
        var ride = new PacketReader(c.Last(Msg.CS_PETRIDING_ACK)!);
        ride.ReadByte(); ride.ReadUInt32(); Assert.Equal(900u, ride.ReadUInt32()); Assert.Equal(2, ride.ReadByte());
        var del = new PacketReader(c.Last(Msg.CS_DELRECALLMON_ACK)!);
        Assert.Equal(1u, del.ReadUInt32()); Assert.Equal(900u, del.ReadUInt32()); Assert.Equal(1, del.ReadByte()); Assert.Equal(1, del.ReadByte());
    }

    [Fact]
    public async Task WhileRiding_ItemsCannotBeUsed()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        ch.Riding = 900;

        await h.Service.DispatchClientAsync(s, Req(Msg.CS_ITEMUSE_REQ, w =>
        {
            w.WriteUInt16(Potion); w.WriteByte(0); w.WriteByte(5); w.WriteUInt16(0); w.WriteByte(0);
        }));

        Assert.Equal((byte)ItemUseResult.Riding, new PacketReader(c.Last(Msg.CS_ITEMUSE_ACK)!).ReadByte());
    }

    [Fact]
    public async Task ATeleport_Dismounts_AndTheUnriddenMountIsSentAwayOnArrival()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        await CallAndSpawn(h, s, ch);
        await h.Service.DispatchClientAsync(s, Req(Msg.CS_PETRIDING_REQ, w => { w.WriteUInt32(900); w.WriteByte(1); }));
        h.World.Clear();

        // The world's teleport step: leave the map (dismount, summons off the map), then re-enter.
        var start = Req(Msg.MW_STARTTELEPORT_REQ, w =>
        {
            w.WriteUInt32(1); w.WriteUInt32(Key); w.WriteByte(1); w.WriteUInt16(0); w.WriteFloat(5000); w.WriteFloat(0); w.WriteFloat(5000);
        });
        await h.Service.DispatchWorldAsync(start);
        Assert.Equal(0u, ch.Riding);
        Assert.False(ch.Recalls[900].InMap);

        await h.FinishTeleportAsync(s);
        var del = new PacketReader(h.World.Last(Msg.MW_RECALLMONDEL_ACK)!);
        del.ReadUInt32(); del.ReadUInt32(); Assert.Equal(900u, del.ReadUInt32());
    }

    [Fact]
    public async Task ATeleport_TakesTheSummonOffQuietly_SoTheClientDoesNotKillIt()
    {
        var (h, s, c, ch, _) = await Setup(ch => GivePet(ch));
        await CallAndSpawn(h, s, ch);
        c.Clear();

        await h.Service.DispatchWorldAsync(Req(Msg.MW_STARTTELEPORT_REQ, w =>
        {
            w.WriteUInt32(1); w.WriteUInt32(Key); w.WriteByte(1); w.WriteUInt16(0); w.WriteFloat(5000); w.WriteFloat(0); w.WriteFloat(5000);
        }));

        // C++ LeaveAllRecallMon → LeaveMAP(mon, FALSE): bExitMAP 0 — the client deletes it; with 1 it plays its death.
        var del = new PacketReader(c.Last(Msg.CS_DELRECALLMON_ACK)!);
        del.ReadUInt32(); Assert.Equal(900u, del.ReadUInt32());
        Assert.Equal(0, del.ReadByte());
        Assert.Contains(900u, ch.Recalls.Keys);                             // kept, to come back at the destination
    }

    // ================================ summon core ================================

    [Fact]
    public async Task OnlyTheOwner_MovesItsSummon()
    {
        var (h, s, _, ch, _) = await Setup(ch => GivePet(ch));
        var (s2, _) = await h.EnterAsync(2, 2, 2, name: "Thief");
        var mon = await CallAndSpawn(h, s, ch);
        float x0 = mon.PosX;

        byte[] Move(float x) => Req(Msg.CS_MONMOVE_REQ, w =>
        {
            w.WriteUInt16(1); w.WriteUInt32(900); w.WriteByte(RecallMon.OtRecall); w.WriteByte(1); w.WriteUInt16(0);
            w.WriteFloat(x); w.WriteFloat(0); w.WriteFloat(557); w.WriteUInt16(0); w.WriteUInt16(0);
            w.WriteByte(4); w.WriteByte(4); w.WriteByte(0);
        });

        await h.Service.DispatchClientAsync(s2, Move(x0 + 10));
        Assert.Equal(x0, mon.PosX);
        await h.Service.DispatchClientAsync(s, Move(x0 + 10));
        Assert.Equal(x0 + 10, mon.PosX);
    }

    [Fact]
    public async Task ASummon_WhoseLifeRunsOut_IsSentAwayOnce()
    {
        var (h, s, _, ch, _) = await Setup(ch => GivePet(ch, Now + 3600));
        await CallAndSpawn(h, s, ch);                                    // 3600 s of life
        h.World.Clear();

        for (int i = 0; i < 3602; i++) await h.Service.OnTimerAsync();

        Assert.Single(h.World.WithId(Msg.MW_RECALLMONDEL_ACK));
    }
}
