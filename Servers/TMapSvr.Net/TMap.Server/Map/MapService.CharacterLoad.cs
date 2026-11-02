using TMap.Data;

namespace TMap.Server.Map;

public sealed partial class MapService
{
    public ICharacterAuxiliaryStore? CharacterAuxiliaryStore { get; set; }

    private async Task LoadRequiredAuxiliaryAsync(ClientSession s, Character ch)
    {
        if ((CharacterAuxiliaryStore ?? _gameDb) is { } db)
        {
            LoadSavedRecalls(ch, await db.LoadRecallsAsync(ch.CharId));
            foreach (var hk in await db.LoadHotkeysAsync(ch.CharId))
            {
                var page = new HotkeyPage { InvenKey = hk.InvenKey };
                if (hk.Slots is not { Length: HotkeyPage.SlotCount })
                    throw new InvalidDataException("Incomplete saved hotkey page.");
                for (int i = 0; i < HotkeyPage.SlotCount; i++)
                    page.Slots[i] = new HotkeySlot(hk.Slots[i].Type, hk.Slots[i].Id);
                ch.HotkeyPages.Add(page);
            }
            (ch.OriCountry, ch.AidCountry, ch.AidDate) = await db.LoadAidAsync(ch.CharId);
            LoadDuringItems(ch, await db.LoadDuringItemsAsync(ch.CharId, s.UserId, LocalDay(UnixNow())));
        }
        await LoadPetsAsync(s, ch);
        await LoadCompanionsAsync(ch);
    }
}
