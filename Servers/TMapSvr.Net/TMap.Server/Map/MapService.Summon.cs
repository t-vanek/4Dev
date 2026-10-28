using TMap.Data;
using TMap.Protocol;

namespace TMap.Server.Map;

/// <summary>
/// Summoning by skill (C++ <c>PerformSkill</c>'s <c>SDT_RECALL</c>/<c>SDT_TRAP</c> case, TObjBase.cpp:3134) and the
/// placed objects it can make (C++ <c>CTSelfObj</c>, <c>OT_SELF</c>). A summon skill's data row names the monster;
/// its template decides what it becomes:
/// <list type="bullet">
/// <item>a <b>self-object</b> (<c>bIsSelf</c>) — created here, right away, with a map-local id: the archer's Rain of
/// Arrows, the mage's Ice Rain, the sorcerer's crystals and eyes, fireworks. It does not move; its owner's client
/// makes it cast.</item>
/// <item>a <b>summon</b> — the sorcerer's rituals, the fireball mine, the doppelganger: asked of the world, which
/// allocates the id (<c>MW_CREATERECALLMON</c>, MapService.Recall.cs).</item>
/// </list>
/// <para>Its life is the skill's buff duration; a new main / mine / auto summon replaces the old one, and at most one
/// "maintain" object is kept. Self-objects die with their owner, when their time is up, when the owner leaves the map
/// or logs out, and when dismissed.</para>
/// <para>The owner's remain skills may lengthen a summon's life (<c>SCT_INCLIFTTIME</c>) and allow more maintain
/// objects (<c>MTYPE_RMC</c>); no live skill does, so these are 0 and 1. Taming (<c>SER_MONSTER</c>) lives in
/// MapService.Tame.cs. <b>Not ported:</b> the doppelganger's equipped-skill check. The summon belongs to the
/// skill's target in the C++; here the target must be the caster, the only case the client sends.</para>
/// </summary>
public sealed partial class MapService
{
    private const byte SdtRecall = 2, SdtTrap = 4;                // SKILL_DATA_TYPE
    private const byte SerMonster = 7;                             // SKILL_EXEC_RECALL SER_MONSTER (taming)
    private const byte TskillrangePoint = 1;                       // TSKILLRANGE_POINT
    private const byte TrecallSkill = 4, TrecallMaintain = 5;      // TRECALL_TYPE
    private const byte MaxSummonAttrLevel = 140;

    private ushort _selfObjCounter;
    private readonly HashSet<uint> _selfObjIds = new();

    /// <summary>C++ <c>LockSelfMonID</c> (TMapSvr.cpp:8332) — <c>MAKELONG(counter, serverId)</c>, skipping ids in use.</summary>
    private uint LockSelfObjId()
    {
        uint id;
        do id = (uint)(_selfObjCounter++ | (_opt.ServerId << 16)); while (id == 0 || _selfObjIds.Contains(id));
        _selfObjIds.Add(id);
        return id;
    }

    /// <summary>A summon's skills: each chart skill at <c>min(bSkillLevel, maxLevel)</c> (CreateRecallMon).</summary>
    private void AddSummonSkills(RecallMon mon, IEnumerable<ushort> ids, byte skillLevel)
    {
        foreach (var id in ids)
            if (_templates.Skills.TryGetValue(id, out var t))
                mon.Skills.Add(new Skill { SkillId = id, Level = Math.Min(skillLevel, t.MaxLevel == 0 ? skillLevel : t.MaxLevel), Template = t });
    }

    /// <summary>C++ <c>PerformSkill</c> <c>SDT_RECALL</c>/<c>SDT_TRAP</c> for a player casting on itself: every summon
    /// row of the skill makes its object. <paramref name="pos"/> is the ground point the client picked.</summary>
    private void PerformSummon(ClientSession s, Character ch, SkillTemplate tpl, byte level, (float X, float Y, float Z) pos)
    {
        foreach (var d in tpl.Data)
        {
            if (d.Type is not (SdtRecall or SdtTrap)) continue;
            // Evocate Monster cast on oneself calls the tamed monster (old sources' TObjBase.cpp PerformSkill:
            // SER_MONSTER takes m_wTemptedMon, fails without one); cast on an enemy, see MapService.Tame.cs.
            ushort monId;
            if (d.Exec == SerMonster)
            {
                if (ch.Persist.TemptedMon == 0) return;               // PERFORM_FAIL
                monId = ch.Persist.TemptedMon;
            }
            else monId = (ushort)tpl.GetValue(d, level);
            if (!_templates.MonsterTemplates.TryGetValue(monId, out var mt)) return;   // PERFORM_MISS
            uint attr = (uint)(mt.SummonAttr | (Math.Min(ch.Level, MaxSummonAttrLevel) << 16));
            CheckRecallMon(s, ch, mt);

            float x, y = ch.PosY, z;
            if (tpl.TargetRange == TskillrangePoint) { x = pos.X; y = pos.Y; z = pos.Z; }
            else
            {
                float rad = ch.Dir * MathF.PI / 900f;
                x = ch.PosX - 2f * MathF.Sin(rad); z = ch.PosZ - 2f * MathF.Cos(rad);
            }
            if (ch.Recalls.Count >= MaxRecallMon) return;             // PERFORM_FAIL — only moving summons count

            uint life = tpl.MaintainTick(level) + GetRecallLifeTime(ch);   // the passive bonus
            if (mt.IsSelf != 0)
            {
                var power = CasterPower(ch, tpl);
                CreateSelfObj(s, ch, mt, attr, level, power, x, y, z, life);
                continue;
            }

            // A summon: the world allocates its id. Template 0 is the doppelganger — it copies the caster.
            bool copy = mt.Id == 0;
            var skills = copy ? ch.Skills.Where(k => k.SkillId is >= 31 and <= 34).Select(k => k.SkillId).ToList() : mt.Skills.ToList();
            var cp = CasterPower(ch, tpl);
            SendMW_CREATERECALLMON_ACK(new RecallRecord(ch.CharId, s.Key, 0, monId, attr, 0, 0, "", ch.Level,
                copy ? ch.Class : mt.Class, copy ? ch.Race : mt.Race,
                copy ? ch.Action : TaStand, copy ? (byte)(ch.Hp == 0 ? 3 : 1) : (byte)1, copy ? ch.Mode : MtNormal,
                copy ? MaxHpFor(ch) : 0, copy ? MaxMpFor(ch) : 0, copy ? MaxHpFor(ch) : 0, copy ? MaxMpFor(ch) : 0,
                cp.Crit, level, x, y, z, ch.Dir, life, 0, 0, 0, skills));
        }
    }

    /// <summary>The caster's attack figures a self-object keeps (C++ <c>Defend</c>'s powers, stored by
    /// <c>CreateRecallMon</c>) — shown in <c>CS_ADDSELFOBJ_ACK</c>; <see cref="AttackerPower.Crit"/> is its <c>bHit</c>.</summary>
    private AttackerPower CasterPower(Character ch, SkillTemplate tpl) => AttackerPower.Of(ch, _templates, tpl);

    /// <summary>C++ <c>CreateRecallMon</c> for a self-object (TMapSvr.cpp:7520): made right here, on the owner's map.</summary>
    private RecallMon? CreateSelfObj(ClientSession s, Character ch, MonsterTemplate mt, uint attr, byte skillLevel,
        AttackerPower power, float x, float y, float z, uint lifeMs)
    {
        if (_templates.MonAttr((ushort)(attr & 0xFFFF), (byte)(attr >> 16)) is not { } a) return null;
        var mon = new RecallMon
        {
            Id = LockSelfObjId(), ObjType = RecallMon.OtSelf, OwnerId = ch.CharId, ChartId = mt.Id, Template = mt, Attr = a,
            Level = ch.Level, AtkLevel = ch.Level, AtkSkillLevel = skillLevel, Hit = power.Crit, RecallType = mt.RecallType,
            Action = TaStand, Status = 1, Mode = MtNormal, MaxHp = a.MaxHp, MaxMp = a.MaxMp,
            Channel = s.Channel, MapId = ch.MapId, Country = ch.Country, AidCountry = ch.AidCountry, Region = ch.RegionId,
            PosX = x, PosY = y, PosZ = z, Dir = ch.Dir,
            SnapPysMin = power.PysMin, SnapPysMax = power.PysMax, SnapMgMin = power.MgMin, SnapMgMax = power.MgMax,
            SnapAttackLevel = power.AttackLevel,
        };
        mon.Hp = mon.MaxHp; mon.Mp = mon.MaxMp;
        AddSummonSkills(mon, mt.Skills, skillLevel);
        if (lifeMs != 0) { mon.RecallTickMs = NowMs; mon.DurationMs = lifeMs; }
        ch.SelfObjs[mon.Id] = mon;
        if (s.State == EnterState.InGame) EnterRecall(mon);
        return mon;
    }

    /// <summary>C++ <c>CTPlayer::DeleteSelfMon</c> (TPlayer.cpp:3207): <c>OnDie</c> (its monsters' hate goes back to the
    /// owner), off the map, out of the list, id released.</summary>
    private void DeleteSelfObj(Character ch, uint id, bool exitMap = true)
    {
        if (!ch.SelfObjs.Remove(id, out var mon)) return;
        SummonOnDie(mon);
        if (mon.InMap) LeaveRecall(mon, exitMap, forever: true);
        _selfObjIds.Remove(id);
    }

    /// <summary>C++ <c>CTRecallMon::OnDie</c> / <c>CTSelfObj::OnDie</c> (TRecallMon.cpp:63, TSelfObj.cpp:23): every
    /// monster around that hated the summon now hates its owner instead, by as much. A self-object of the skill kind
    /// whose AI attacks first (<c>MONAI_FIRSTATK</c>) hands nothing over, as in the C++.</summary>
    private void SummonOnDie(RecallMon m)
    {
        if (m.IsSelf && m.Template is { RecallType: TrecallSkill, AiType: 1 }) return;
        if (!m.InMap || _state.FindByChar(m.OwnerId) is not { Char: { } owner }) return;
        foreach (var mon in _state.MonstersAround(m).ToList())
        {
            if (!mon.AggroTable.ContainsKey(Monster.AggroKey(m.Id, m.ObjType))) continue;
            mon.AddAggro(m.OwnerId, m.OwnerId, OtPc, WarCountryOf(owner), mon.FindAggro(m.Id, m.ObjType));
            LeaveAggro(mon, m.OwnerId, m.Id, m.ObjType, NowMs);
        }
    }

    private void ClearSelfObjs(Character ch)
    {
        foreach (var id in ch.SelfObjs.Keys.ToList()) DeleteSelfObj(ch, id);
    }

    /// <summary>The self-object half of <c>CheckTimeRecallMon</c> (TPlayer.cpp:4214): one whose time is up, or whose
    /// owner is gone, dies here and now.</summary>
    private void RunSelfObjTimers()
    {
        long now = NowMs;
        foreach (var s in _state.AllInGame())
        {
            if (s.Char is not { SelfObjs.Count: > 0 } ch) continue;
            foreach (var m in ch.SelfObjs.Values.ToList())
                if (m.Expired(now)) DeleteSelfObj(ch, m.Id);
        }
    }

    // ================================ senders ================================

    /// <summary>C++ <c>SendCS_ADDSELFOBJ_ACK</c> (CSSender.cpp:703) — field-exact.</summary>
    private void SendCS_ADDSELFOBJ_ACK(ClientSession s, RecallMon m, bool newMember)
    {
        var w = new PacketWriter(Msg.CS_ADDSELFOBJ_ACK, capacity: 112);
        w.WriteUInt32(m.OwnerId); w.WriteUInt32(m.Id); w.WriteUInt16(m.ChartId); w.WriteByte(m.Country); w.WriteByte(m.AidCountry);
        w.WriteByte(0);                                                   // GetColor — deferred
        w.WriteByte(m.Level);
        w.WriteUInt32(m.MaxHp); w.WriteUInt32(m.Hp); w.WriteUInt32(m.MaxMp); w.WriteUInt32(m.Mp);
        w.WriteFloat(m.PosX); w.WriteFloat(m.PosY); w.WriteFloat(m.PosZ); w.WriteUInt16(m.Pitch); w.WriteUInt16(m.Dir);
        w.WriteByte(m.Action); w.WriteByte(m.Mode); w.WriteByte((byte)(newMember ? 1 : 0));
        w.WriteUInt32(m.Region); w.WriteByte(m.RecallType); w.WriteByte(m.Hit); w.WriteByte(m.AtkSkillLevel);
        w.WriteUInt16(m.SnapAttackLevel); w.WriteByte(m.AtkLevel);
        w.WriteUInt32(m.SnapPysMin); w.WriteUInt32(m.SnapPysMax); w.WriteUInt32(m.SnapMgMin); w.WriteUInt32(m.SnapMgMax);
        w.WriteUInt32(m.LifeLeft(NowMs));
        w.WriteByte((byte)m.MaintainSkills.Count);
        foreach (var buff in m.MaintainSkills) WriteMaintainSkill(w, buff, NowMs);
        s.Send(w);
    }

    /// <summary>C++ <c>SendCS_DELSELFOBJ_ACK</c> (CSSender.cpp:1022).</summary>
    private static void SendCS_DELSELFOBJ_ACK(ClientSession s, uint id, bool exitMap)
    {
        var w = new PacketWriter(Msg.CS_DELSELFOBJ_ACK, capacity: 8);
        w.WriteUInt32(id); w.WriteByte((byte)(exitMap ? 1 : 0));
        s.Send(w);
    }
}

/// <summary>
/// The attack figures one hit is computed from — the C++ <c>Defend</c> parameters (<c>dwPysMin/MaxPower</c>,
/// <c>dwMgMin/MaxPower</c>, <c>wAttackLevel</c>, <c>bCP</c>) plus who is attacking. A player's come from its stat sheet;
/// a summon's from its stats row and its owner's gear (C++ <c>CTRecallMon::GetMinAP</c>…).
/// </summary>
public readonly record struct AttackerPower(uint ShortMin, uint ShortMax, uint LongMin, uint LongMax, uint MgMin, uint MgMax,
    ushort AttackLevel, byte Crit, bool IsMagic, bool IsLong, byte Level, byte Country, byte AidCountry, byte Class)
{
    /// <summary>The physical band of the skill's own range (what <c>CS_SKILLUSE_ACK</c> / <c>CS_DEFEND_ACK</c> show).</summary>
    public uint PysMin => IsLong ? LongMin : ShortMin;
    public uint PysMax => IsLong ? LongMax : ShortMax;
    public uint ApMin(bool magic, bool arrow) => magic ? MgMin : arrow ? LongMin : ShortMin;
    public uint ApMax(bool magic, bool arrow) => magic ? MgMax : arrow ? LongMax : ShortMax;

    public static AttackerPower Of(Monster mon, SkillTemplate? tpl)
    {
        bool magic = tpl?.GetAttackType() == SkillTemplate.SatMagic;
        return new AttackerPower(mon.GetMinAp(), mon.GetMaxAp(), mon.GetMinLongAp(), mon.GetMaxLongAp(),
            mon.GetMinMagicAp(), mon.GetMaxMagicAp(), magic ? mon.GetMagicAttackLevel() : mon.GetAttackLevel(),
            magic ? mon.GetMagicCritProb() : mon.GetCritProb(), magic, tpl?.IsLongAttack() ?? false,
            mon.Level, mon.Country, mon.AidCountry, 0);
    }

    /// <summary>A player's figures for a skill (by the skill's attack type and range), as <c>CS_SKILLUSE</c> reports them.</summary>
    public static AttackerPower Of(Character ch, TemplateStore t, SkillTemplate? tpl)
    {
        bool magic = tpl?.GetAttackType() == SkillTemplate.SatMagic;
        bool isLong = tpl?.IsLongAttack() ?? false;
        return new AttackerPower(
            StatEngine.MinAp(ch, false, t), StatEngine.MaxAp(ch, false, t), StatEngine.MinAp(ch, true, t), StatEngine.MaxAp(ch, true, t),
            StatEngine.MinMagicAp(ch, t), StatEngine.MaxMagicAp(ch, t),
            magic ? StatEngine.MagicAtkLevel(ch, t) : StatEngine.AttackLevel(ch, t),
            magic ? StatEngine.CriticalMagicProb(ch, t) : StatEngine.CriticalPysProb(ch, t),
            magic, isLong, ch.Level, ch.Country, ch.AidCountry, ch.Class);
    }
}
