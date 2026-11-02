using TMap.Data;
using TMap.Protocol;
using TMap.Server.Map;
using Xunit;

namespace TMap.Server.Tests;

public sealed class DoppelgangerSkillTests
{
    private static SkillTemplate Template(ushort id, uint weapon = 0, byte max = 1)
        => new(id, 0, 0, 0, 0, 0, 1, max, 0, 0, 0, 0, 0, 0, 1, 0xFFFF, WeaponId: weapon);

    [Theory]
    [InlineData("matching equipment", true)]
    [InlineData("wrong equipment", false)]
    [InlineData("weapon in bag", false)]
    [InlineData("unlearned", false)]
    [InlineData("innate", true)]
    [InlineData("missing template", false)]
    public async Task CreationCopiesOnlyUsableBasicSkillsInNativeOrder(string scenario, bool includeWeaponSkill)
    {
        var t = new TemplateStore();
        t.MonsterTemplates[0] = new MonsterTemplate(0, 10, 0, RecallType: 1, SummonAttr: 1001);
        t.MonAttrs[TemplateStore.MonAttrKey(1001, 10)] = new MonAttrRow(1001, 10, 500, 100, 0);
        var summon = Template(800);
        summon.Data.Add(new SkillDataRow(0, 2, 0, 0, 0, 0, 0, 0)); t.Skills[800] = summon;
        var basic = Template(31, weapon: 2, max: scenario == "innate" ? (byte)0 : (byte)1);
        var free = Template(34); t.Skills[31] = basic; t.Skills[34] = free;
        var ch = new Character { CharId = 1, Hp = 500, MaxHp = 500 };
        ch.Skills.Add(new Map.Skill { SkillId = 800, Level = 1, Template = summon });
        ch.Skills.Add(new Map.Skill { SkillId = 34, Level = 1, Template = free }); // deliberately out of order
        ch.Skills.Add(new Map.Skill { SkillId = 31, Level = scenario is "unlearned" or "innate" ? (byte)0 : (byte)1,
            Template = scenario == "missing template" ? null : basic });
        ch.Skills.Add(new Map.Skill { SkillId = 50, Level = 1, Template = Template(50) });
        var bag = new Inven { InvenId = scenario == "weapon in bag" ? (byte)0 : Proto.InvenEquip };
        bag.Items.Add(new Item { ItemSlot = Proto.EsPrmWeapon, Count = 1, TemplateId = 300,
            Template = new ItemTemplate(300, 0, new float[4], Kind: scenario == "wrong equipment" ? (byte)1 : (byte)2) });
        ch.Invens.Add(bag);
        var h = new MapTestHarness(t);
        var (s, _) = await h.EnterAsync(1, 10, 41, x: 100, z: 100, preSeeded: ch);
        ch.Level = 10; h.World.Clear();
        var finish = new PacketWriter(Msg.CS_FINISHSKILL_ACK);
        finish.WriteUInt32(1); finish.WriteUInt32(1); finish.WriteByte(1);
        finish.WriteFloat(100); finish.WriteFloat(0); finish.WriteFloat(100);
        finish.WriteUInt16(800); finish.WriteUInt32(0); finish.WriteUInt32(0); finish.WriteUInt16(0);
        finish.WriteByte(1); finish.WriteUInt32(1); finish.WriteByte(1);

        await h.CastAndHitAsync(s, finish.ToArray());

        var r = new PacketReader(Assert.Single(h.World.WithId(Msg.MW_CREATERECALLMON_ACK)));
        r.ReadBytes(21); r.ReadString(); r.ReadBytes(48); // native creature header through targetType
        byte count = r.ReadByte(); var skills = new List<ushort>();
        for (int i = 0; i < count; i++) skills.Add(r.ReadUInt16());
        Assert.Equal(includeWeaponSkill ? new ushort[] { 31, 34 } : new ushort[] { 34 }, skills);
        Assert.Equal(0, r.Remaining);
    }
}
