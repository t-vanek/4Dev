using Microsoft.Extensions.Logging;
using TWorld.Data;
using TWorld.Protocol;

namespace TWorld.Server.World;

public sealed partial class WorldService
{
    public void NotifyMailDelivered(uint postId, uint charId, string sender, string receiver, string title, byte type)
    {
        if (!_state.Characters.TryGetValue(charId, out var ch)) return;
        _state.FindMapSvr(ch.MainId)?.Send(new PacketWriter(Msg.MW_POSTRECV_REQ).WriteUInt32(postId)
            .WriteString(sender).WriteString(ch.Name).WriteString(title).WriteByte(type));
    }
    public IReservedPostStore? ReservedPostStore { get; set; }
    private long _reservedPostMinute = -1;

    /// <summary>Native minute worker: at most 30 reservations, notify the recipient's current main.
    /// Delivery itself is durable and also works while the recipient is offline.</summary>
    public async Task RunReservedPostsAsync(DateTime utcNow)
    {
        long minute = utcNow.Ticks / TimeSpan.TicksPerMinute;
        if (ReservedPostStore is not { } db || minute == _reservedPostMinute) return;
        _reservedPostMinute = minute;
        try
        {
            for (int i = 0; i < 30; i++)
            {
                var result = await db.DeliverReservedPostAsync();
                if (result.Result != 0) break;
                if (_state.CharactersByName.TryGetValue(result.Receiver, out var ch))
                    _state.FindMapSvr(ch.MainId)?.Send(new PacketWriter(Msg.MW_POSTRECV_REQ).WriteUInt32(result.PostId)
                        .WriteString(result.Sender).WriteString(result.Receiver).WriteString(result.Title).WriteByte(1));
            }
        }
        catch (Exception ex) { _log.LogError(ex, "Reserved post delivery failed; retrying next minute."); }
    }
}
