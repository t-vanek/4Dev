using Microsoft.Data.SqlClient;
using TMap.Data;

internal static class CharacterStateCheck
{
    public static async Task RunAsync(GameDatabase map, string connection, List<string> passed)
    {
        if (!new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("4DevQuestTest_", StringComparison.Ordinal))
            throw new InvalidOperationException("Isolated database required");
        async Task Exec(string sql)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync();
        }
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Broken(string name, Func<Task> read)
        {
            // Identifiers below are compile-time fixture names, never user input.
            await Exec($"EXEC sp_rename '{name}', 'TestHiddenCharacterPart'");
            try
            {
                try { await read(); throw new InvalidOperationException($"{name} failure returned partial character data"); }
                catch (SqlException ex) when (ex.Number is 208 or 2812) { }
            }
            finally { await Exec($"EXEC sp_rename 'TestHiddenCharacterPart', '{name}'"); }
        }

        var companion = new CompanionRow(1, 200, 3, "Saved companion", 123, 15000, 2, 4,
            [1, 2, 3, 4, 5, 6], 11, [100, 101], [0, 0], 0);
        await map.SaveCompanionAsync(1, companion);
        await map.SaveLastCompanionAsync(1, 1); await map.SaveMedalsAsync(1, 987);
        var loaded = await new GameDatabase(connection).LoadCompanionsAsync(1);
        Require(loaded.SummonedSlot == 1 && loaded.Medals == 987, "Companion selection/medals did not reload");
        var held = loaded.Companions.Single();
        Require(held.Name == companion.Name && held.Stats.SequenceEqual(companion.Stats) &&
            held.ItemIds.SequenceEqual(companion.ItemIds) && held.Level == 3 && held.Life == 15000 && held.Exp == 123,
            "Companion fields or equipped items did not roundtrip");
        await map.SaveCompanionAsync(1, companion with { Exp = 456, ItemIds = [102, 103] });
        loaded = await new GameDatabase(connection).LoadCompanionsAsync(1);
        Require(loaded.Companions.Count == 1 && loaded.Companions[0].Exp == 456 &&
            loaded.Companions[0].ItemIds.SequenceEqual(new ushort[] { 102, 103 }), "Companion update duplicated or lost fields");
        passed.Add("native companion insert/update and fresh-connection reload preserve stats, equipment, selection and medals");

        var pet = new PetRow(5, "Saved mount", 0, 7);
        await map.SavePetAsync(1, pet); await map.SavePetAsync(1, pet with { Name = "Updated mount" });
        await Exec("INSERT TMOUNTITEMTABLE(dwUserID,wItemID,bType,dEndTime) VALUES(9,300,1,'19000101')");
        var pets = await new GameDatabase(connection).LoadPetsAsync(9);
        var saddle = await map.GetSaddleAsync(9);
        Require(pets.Count == 1 && pets[0] == pet with { Name = "Updated mount" }, "Native pet upsert did not retain account ownership");
        Require(saddle == new SaddleRow(300, 0, 1), "Permanent saddle did not reload");
        passed.Add("native pet upsert resolves the synthetic account and preserves permanent mount/saddle ownership");

        await Broken("TCOMPANIONITEMTABLE", async () => { await map.LoadCompanionsAsync(1); });
        loaded = await map.LoadCompanionsAsync(1);
        Require(loaded.Medals == 987 && loaded.Companions[0].ItemIds[0] == 102, "Companion item read failure changed persisted data");
        passed.Add("companion-item query failure returns no partial owned list and preserves equipment after retry");
        await Broken("TGetMedals", async () => { await map.LoadCompanionsAsync(1); });
        loaded = await map.LoadCompanionsAsync(1);
        Require(loaded.Medals == 987 && loaded.SummonedSlot == 1, "Failed medals read reset companion state");
        passed.Add("late medals-procedure failure returns no partial companion state and retry restores the balance");
        await Broken("TPETTABLE", async () => { await map.LoadPetsAsync(9); });
        Require((await map.LoadPetsAsync(9)).Single().Name == "Updated mount", "Pet read failure changed ownership");
        passed.Add("pet query failure propagates and restored storage retains the owned mount");
        await Broken("TMOUNTITEMTABLE", async () => { await map.GetSaddleAsync(9); });
        Require((await map.GetSaddleAsync(9)).ItemId == 300, "Saddle failure changed the saved item");
        passed.Add("saddle query failure propagates and retry restores the permanent saddle");

        await Exec("INSERT TVIEW_DURINGITEMTABLE VALUES(9,400,1,5000,'20300101'); INSERT TEXPITEMTABLE VALUES(1,401,1,1000,'20300101')");
        // The optional PC-bang proc is deliberately absent. Owned premium/EXP rows must still survive.
        var during = await map.LoadDuringItemsAsync(1, 9, 20261030);
        Require(during.Premium.Single().Remain == 5000 && during.Exp.Single().Remain == 1000 && during.InPcBang == 0,
            "Missing optional PC-bang procedure lost owned timed items");
        passed.Add("premium and EXP items load fully when the optional PC-bang procedure is absent");
        await Broken("TEXPITEMTABLE", async () => { await map.LoadDuringItemsAsync(1, 9, 20261030); });
        during = await map.LoadDuringItemsAsync(1, 9, 20261030);
        Require(during.Premium.Count == 1 && during.Exp.Count == 1, "Failed EXP query lost the earlier premium rows");
        passed.Add("late EXP-item query failure rejects a partial premium load and retries without loss");
        await Broken("TVIEW_DURINGITEMTABLE", async () => { await map.LoadDuringItemsAsync(1, 9, 20261030); });
        Require((await map.LoadDuringItemsAsync(1, 9, 20261030)).Premium.Single().ItemId == 400, "Premium read did not recover");
        passed.Add("owned premium query failure propagates independently of optional PC-bang fallback");

        await Exec(@"INSERT TRECALLMONTABLE(dwOwnerID,dwID,wMonID,wPetID,dwATTR,bLevel,dwHP,dwMP,bSkillLevel,wPosX,wPosY,wPosZ,dwTime,bEffect)
VALUES(1,8,200,0,1001,3,90,80,1,-1,2,3,1000,4);
INSERT TRECALLMAINTAINTABLE(dwCharID,dwRecallID,wSkillID,bLevel,dwRemainTick,bAttackType,dwAttackID,bHostType,dwHostID,bAttackCountry)
VALUES(1,8,500,2,900,1,1,CHAR(1),1,2);");
        var recalls = await map.LoadRecallsAsync(1);
        Require(recalls.Recalls.Single().X == -1 && recalls.Buffs.Single().HostType == 1, "Recall signed position or host byte did not roundtrip");
        await Broken("TRECALLMAINTAINTABLE", async () => { await map.LoadRecallsAsync(1); });
        recalls = await new GameDatabase(connection).LoadRecallsAsync(1);
        Require(recalls.Recalls.Count == 1 && recalls.Buffs.Single().Remain == 900, "Recall retry lost its buff");
        passed.Add("recall/buff query failure returns no partial summons; retry preserves signed positions and buff fields");

        await Broken("TAIDTABLE", async () => { await map.LoadAidAsync(1); });
        Require((await map.LoadAidAsync(1)).OriCountry == 1, "Allegiance read did not recover");
        passed.Add("allegiance query failure propagates and restored tables reload the original country");

        await map.SaveHotkeysAsync(1, [new(2, 9, [(1, 600), (2, 601)])], requireProcedures: true);
        await Exec("EXEC sp_rename 'THOTKEYTABLE', 'TestSavedHotkeyTable'");
        await Exec("CREATE VIEW THOTKEYTABLE AS SELECT dwCharID,bInvenID FROM TestSavedHotkeyTable");
        try
        {
            try { await map.LoadHotkeysAsync(1); throw new InvalidOperationException("Malformed hotkey schema returned an empty log"); }
            catch (InvalidDataException) { }
        }
        finally { await Exec("DROP VIEW THOTKEYTABLE; EXEC sp_rename 'TestSavedHotkeyTable', 'THOTKEYTABLE'"); }
        var hotkey = (await map.LoadHotkeysAsync(1)).Single(p => p.InvenKey == 9);
        Require(hotkey.Slots.Length == 12 && hotkey.Slots[0].Id == 600 && hotkey.Slots[1].Id == 601, "Schema retry lost saved hotkeys");
        passed.Add("malformed hotkey schema rejects admission data instead of silently skipping persisted pages");
    }
}
