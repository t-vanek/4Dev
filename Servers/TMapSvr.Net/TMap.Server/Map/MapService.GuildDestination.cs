using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    // C++ CheckMapGuild: LastDestination is MAKELONG(source portal, destination portal).
    // The destination's territory owns a PCT_GUILD room; mercenary allegiance does not grant ownership.
    private bool CheckMapGuild(Character ch)
    {
        uint last = ch.Persist.LastDestination;
        if (ch.MapId == 0 || last == 0 || !_templates.Portals.TryGetValue((ushort)last, out var source)
            || !source.Destinations.TryGetValue((ushort)(last >> 16), out var destination)
            || !destination.Conditions.Any(c => c.Type == PctGuild)
            || !_templates.Portals.TryGetValue(destination.DestId, out var target)
            || !_territories.TryGetValue(target.LocalId, out var local)) return false;
        return local.Guild != ch.GuildId;
    }

    private void ReturnFromGuildDestination(ClientSession s, Character ch)
    {
        ushort spawn = ch.Persist.LastSpawnId != 0 ? ch.Persist.LastSpawnId : ch.Persist.SpawnId;
        if (spawn != 0 && Teleport(s, ch, spawn)) return;
        // No fabricated fallback coordinates: refuse a restricted room when its return spawn is missing.
        s.EntryValidated = false; s.State = EnterState.Entering; s.Conn.Close();
    }
}
