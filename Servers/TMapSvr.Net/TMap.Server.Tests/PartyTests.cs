using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

/// <summary>
/// Phase 38 — the map-side PARTY gameplay: a monster killed by a party member is owned by the party
/// (<c>OWNER_PARTY</c> keeper), its exp is split among the near party members (party-size bonus + level-weighted
/// share + level-gap scale), and its corpse is lootable by any party member (PT_FREE), broadcasting the loot to
/// near members. Membership management is world-authoritative (relays deferred). All DB-free.
/// </summary>
public class PartyTests
{
    private const ushort DropId = 300;
    private const ushort Party = 7;

    private static TemplateStore LootStore()
    {
        var t = new TemplateStore();
        t.Items[DropId] = new ItemTemplate(DropId, 0, new[] { 1f, 0f, 0f, 0f });
        return t;
    }

    // A level-5 mob worth 1000 base exp; MaxHp 10 so a couple of naked hits kill it. No loot table by default.
    private static Monster ExpMob() => new()
    { Id = 0x70001, ChartId = 500, Level = 5, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50, DefendPower = 0,
      Exp = 1000, PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0 };

    // Same mob but with a 100%-drop table of one fixed item.
    private static Monster LootMob() => new()
    { Id = 0x70002, ChartId = 500, Level = 5, MaxHp = 10, Hp = 10, MaxMp = 50, Mp = 50, DefendPower = 100,
      PosX = 100, PosZ = 100, Region = 7, Channel = 1, MapId = 0, ItemProb = 100, DropCount = 1, MaxWeight = 1,
      DropRows = new[] { new MonItemRow(DropId, Weight: 1, ChartType: 1, 100, 100, 100, 100) } };

    private static Character BagChar(uint id)
    {
        var ch = new Character { CharId = id, Name = "P" + id, Level = 5, MaxHp = 100, Hp = 100 };
        ch.Invens.Add(new Inven { InvenId = 0xFF });
        return ch;
    }

    // ==================== shared exp ====================

    private static async Task<(MapTestHarness h, ClientSession s1, ClientSession s2)> KillWithTwo(
        byte lvl1, byte lvl2, ushort party1, ushort party2)
    {
        var h = new MapTestHarness();
        var (s1, _) = await h.EnterAsync(1, 1, 1, name: "A", x: 100, z: 100);
        var (s2, _) = await h.EnterAsync(2, 2, 2, name: "B", x: 100, z: 100);
        s1.Char!.Level = lvl1; s1.Char.PartyId = party1; s1.Char.PartyType = 0;   // PT_FREE
        s2.Char!.Level = lvl2; s2.Char.PartyId = party2; s2.Char.PartyType = 0;
        var mob = ExpMob();
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));   // P1 (the party member) kills
        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));
        return (h, s1, s2);
    }

    [Fact]
    public async Task PartyKill_SplitsExp_AmongNearMembers()
    {
        var (_, s1, s2) = await KillWithTwo(lvl1: 5, lvl2: 5, party1: Party, party2: Party);

        // getExp = 1000·63/100 = 630; ×(1+0.01·(2²/2+2−1.5))=×1.025 → 645; each = 645·5/10 = 322 (×LevelRate 1.0).
        Assert.Equal(322u, s1.Char!.Exp);
        Assert.Equal(322u, s2.Char!.Exp);
    }

    [Fact]
    public async Task PartyKill_LevelWeighted_HigherLevelGetsMore()
    {
        var (_, s1, s2) = await KillWithTwo(lvl1: 5, lvl2: 6, party1: Party, party2: Party);

        // totalLevel 11; L5 = 645·5/11=293 (×1.0); L6 = 645·6/11=351 ×LevelRate(6,5)=0.9 → ceil(315.9)=316.
        Assert.Equal(293u, s1.Char!.Exp);
        Assert.Equal(316u, s2.Char!.Exp);
        Assert.True(s2.Char.Exp > s1.Char.Exp);
    }

    [Fact]
    public async Task PartyKill_ExcludesNonMembers()
    {
        var (_, s1, s2) = await KillWithTwo(lvl1: 5, lvl2: 5, party1: Party, party2: 99);   // P2 in a different party

        // P1 is a lone party-7 member: n=1 ⇒ bonus 1.0 ⇒ full 630. P2 (party 99) shares nothing.
        Assert.Equal(630u, s1.Char!.Exp);
        Assert.Equal(0u, s2.Char!.Exp);
    }

    [Fact]
    public async Task SoloKill_UnchangedByPartyPath()
    {
        var (_, s1, s2) = await KillWithTwo(lvl1: 5, lvl2: 5, party1: 0, party2: 0);   // neither partied

        Assert.Equal(630u, s1.Char!.Exp);   // solo: getExp·LevelRate = 630
        Assert.Equal(0u, s2.Char!.Exp);     // a bystander gets nothing
    }

    [Fact]
    public async Task PartyKill_AssignsPartyKeeper()
    {
        var h = new MapTestHarness();
        var (s1, _) = await h.EnterAsync(1, 1, 1, name: "A", x: 100, z: 100);
        s1.Char!.PartyId = Party; s1.Char.PartyType = 0;
        var mob = ExpMob();
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);

        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));

        Assert.Equal((byte)OwnerType.Party, mob.KeeperType);
        Assert.Equal((uint)Party, mob.KeeperId);
    }

    // ==================== party loot ====================

    private static async Task<(MapTestHarness h, ClientSession s1, FakeClientChannel c1, ClientSession s2,
        FakeClientChannel c2, Monster mob)> PartyLootKill(ushort party2)
    {
        var h = new MapTestHarness(LootStore());
        var (s1, c1) = await h.EnterAsync(1, 1, 1, name: "A", x: 100, z: 100, preSeeded: BagChar(1));
        var (s2, c2) = await h.EnterAsync(2, 2, 2, name: "B", x: 100, z: 100, preSeeded: BagChar(2));
        s1.Char!.PartyId = Party; s1.Char.PartyType = 0;
        s2.Char!.PartyId = party2; s2.Char.PartyType = 0;
        var mob = LootMob();
        h.Service.SpawnMonster(mob);
        h.Service.CombatRng = new Random(1);
        h.Service.LootRng = new Random(1);
        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));   // P1 (party 7) kills
        await h.CastAndHitAsync(s1, MapTestHarness.DefendReq(1, mob.Id));
        c1.Clear(); c2.Clear();
        return (h, s1, c1, s2, c2, mob);
    }

    [Fact]
    public async Task PartyCorpse_OtherMemberCanLoot()
    {
        var (h, _, _, s2, c2, mob) = await PartyLootKill(party2: Party);
        Assert.Equal((byte)OwnerType.Party, mob.KeeperType);
        var slot = mob.CorpseInven.Items[0].ItemSlot;

        await h.Service.DispatchClientAsync(s2, MapTestHarness.MonItemTakeReq(mob.Id, slot));   // a DIFFERENT member takes

        Assert.Equal((byte)MonItemTakeResult.Success, new PacketReader(c2.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
        Assert.Empty(mob.CorpseInven.Items);
        Assert.Contains(s2.Char!.Invens.SelectMany(i => i.Items), it => it.TemplateId == DropId);
    }

    [Fact]
    public async Task PartyCorpse_NonMemberDenied()
    {
        var (h, _, _, s2, c2, mob) = await PartyLootKill(party2: 99);   // P2 not in the keeper party
        var slot = mob.CorpseInven.Items[0].ItemSlot;

        await h.Service.DispatchClientAsync(s2, MapTestHarness.MonItemTakeReq(mob.Id, slot));

        Assert.Equal((byte)MonItemTakeResult.NotFound, new PacketReader(c2.Last(Msg.CS_MONITEMTAKE_ACK)!).ReadByte());
        Assert.Single(mob.CorpseInven.Items);   // still on the corpse
    }

    [Fact]
    public async Task PartyLoot_BroadcastsToNearMembers()
    {
        var (h, s1, _, _, c2, mob) = await PartyLootKill(party2: Party);
        var slot = mob.CorpseInven.Items[0].ItemSlot;

        await h.Service.DispatchClientAsync(s1, MapTestHarness.MonItemTakeReq(mob.Id, slot));   // P1 loots

        Assert.True(c2.Has(Msg.CS_PARTYITEMTAKE_ACK));   // the near party member is notified
        var r = new PacketReader(c2.Last(Msg.CS_PARTYITEMTAKE_ACK)!);
        Assert.Equal(1u, r.ReadUInt32());                // dwCharID = the looter (P1)
        Assert.Equal(DropId, r.ReadUInt16());            // item block (addItemId=false → wItemID first)
    }

    // ==================== masked accessors ====================

    [Fact]
    public void SoloType_MasksPartyState()
    {
        var ch = new Character { PartyId = 7, PartyChiefId = 3, CommanderId = 2, PartyType = 1 /*PT_SOLO*/ };
        Assert.Equal(0, ch.GetPartyId());
        Assert.Equal(0u, ch.GetPartyChiefId());
        Assert.Equal(0, ch.GetCommanderId());

        ch.PartyType = 0; // PT_FREE
        Assert.Equal((ushort)7, ch.GetPartyId());
        Assert.Equal(3u, ch.GetPartyChiefId());
        Assert.Equal((ushort)2, ch.GetCommanderId());
    }
}
