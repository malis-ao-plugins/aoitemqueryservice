using AODB;
using AODB.Common.RDBObjects;
using AODB.Common.Spells;

/// <summary>
/// Builds the item database from the game's own data files: every item and
/// nano in the client's resource database, plus the hand-kept QL relations.
///
/// It writes to a temporary file and moves it into place only when it is
/// complete, so the query service never sees a half-built database.
/// </summary>
public static class ItemDbBuilder
{
    private const int ItemRecordType = 1000020;
    private const int NanoRecordType = 1040005;

    // The spell argument numbers the web page reads criteria and texts by.
    private const int CriteriaOperator = 26;
    private const int TextOperator = 65;

    /// <summary>
    /// True when the database exists, was written with the current schema,
    /// and is newer than the game's resource index, which a patch rewrites.
    /// </summary>
    public static bool IsCurrent(string dbPath, string gameDirectory)
    {
        if (!File.Exists(dbPath)) return false;

        try
        {
            if (ItemDbWriter.ReadSchemaVersion(dbPath) != ItemDbWriter.SchemaVersion) return false;
        }
        catch
        {
            return false;
        }

        var index = Path.Combine(gameDirectory, "cd_image", "data", "db", "ResourceDatabase.idx");
        return !File.Exists(index) || File.GetLastWriteTimeUtc(dbPath) > File.GetLastWriteTimeUtc(index);
    }

    public static void Build(string gameDirectory, string dbPath, Action<string> log)
    {
        var tempPath = dbPath + ".building";
        File.Delete(tempPath);

        using (var rdb = new RdbController(gameDirectory))
        using (var db = new ItemDbWriter(tempPath))
        {
            var records = rdb.RecordTypeToId;
            var nanoIds = records[NanoRecordType].Keys.ToList();
            var nanoSet = nanoIds.ToHashSet();

            // An id that is both an item and a nano is written once, as the nano.
            var itemIds = records[ItemRecordType].Keys.Where(id => !nanoSet.Contains(id)).ToList();

            var written = new HashSet<int>();
            WriteAll(db, itemIds, id => rdb.Get<ItemObject>(id), written, "items", log);
            WriteAll(db, nanoIds, id => rdb.Get<NanoObject>(id), written, "nanos", log);
            WriteRelations(db, written);

            log("Item database: building indexes.");
            db.Complete();
        }

        File.Move(tempPath, dbPath, overwrite: true);
    }

    private static void WriteAll(ItemDbWriter db, List<int> ids, Func<int, ItemBase> read, HashSet<int> written, string label, Action<string> log)
    {
        int incomplete = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            var item = read(ids[i]);
            if (!item.ReadComplete) incomplete++;

            WriteItem(db, ids[i], item);
            written.Add(ids[i]);

            if ((i + 1) % 20000 == 0)
                log($"Item database: {label} {i + 1}/{ids.Count}");
        }

        log($"Item database: {ids.Count} {label}, {incomplete} only partly readable.");
    }

    private static void WriteItem(ItemDbWriter db, int id, ItemBase item)
    {
        db.Item(id, item.Name, item.Description);

        foreach (var (stat, value) in item.Stats)
            db.Stat(id, stat, value);

        foreach (var (skillCheck, stats) in item.SkillPstLists)
            foreach (var (stat, value) in stats)
                db.SkillCheck(id, skillCheck, stat, value);

        foreach (var (eventType, spells) in item.Events)
        {
            // Spells of one type within an event are numbered in order.
            var listIndex = new Dictionary<int, int>();
            foreach (var spell in spells)
            {
                listIndex.TryGetValue(spell.TypeId, out var index);
                listIndex[spell.TypeId] = index + 1;
                WriteSpell(db, id, eventType, spell, index);
            }
        }

        foreach (var (action, criteria) in item.ActionCriteria)
            for (int i = 0; i < criteria.Count; i++)
                db.Requirement(id, action, action, i, criteria[i].Value1, criteria[i].Value2, criteria[i].Operator);
    }

    /// <summary>
    /// A spell's arguments are written in the order its format lists them,
    /// then any values it read that the format doesn't list, by stat.
    /// Numbers go under their own stat; the spell's criteria, when it has
    /// any, come first under <see cref="CriteriaOperator"/>; its texts, which
    /// the format numbers rather than names, go together under
    /// <see cref="TextOperator"/>, one row per text in format order.
    /// </summary>
    private static void WriteSpell(ItemDbWriter db, int id, int eventType, SpellData spell, int listIndex)
    {
        int fn = spell.TypeId;
        int opIndex = 0;

        if (spell.Criteria.Count > 0)
        {
            for (int ci = 0; ci < spell.Criteria.Count; ci++)
            {
                var c = spell.Criteria[ci];
                db.ModifierCriterion(id, eventType, fn, listIndex, opIndex, ci, c.Value1, c.Value2, c.Operator);
            }
            db.Modifier(id, eventType, fn, listIndex, opIndex++, CriteriaOperator, "criteria", null);
        }

        var format = SpellFormats.Get(fn);
        var listed = new HashSet<int>();

        int textOpIndex = -1;
        int textIndex = 0;
        foreach (var arg in format?.Arguments ?? [])
        {
            if (arg.IsString)
            {
                if (textOpIndex < 0)
                {
                    textOpIndex = opIndex++;
                    db.Modifier(id, eventType, fn, listIndex, textOpIndex, TextOperator, "text", null);
                }
                db.ModifierText(id, eventType, fn, listIndex, textOpIndex, textIndex++, spell.Strings[arg.Index].TrimEnd('\0'));
            }
            else if (listed.Add(arg.Stat) && spell.TryGetStat(arg.Stat, out var value))
            {
                db.Modifier(id, eventType, fn, listIndex, opIndex++, arg.Stat, "num", value);
            }
        }

        // Some formats read values they don't list (textures do).
        foreach (var (stat, value) in spell.Stats)
            if (!listed.Contains(stat))
                db.Modifier(id, eventType, fn, listIndex, opIndex++, stat, "num", value);
    }

    /// <summary>
    /// Groups are numbered from 1 in the order of the file's group column; the
    /// first item listed in a group is the one searches show. Items the game
    /// doesn't have are skipped.
    /// </summary>
    private static void WriteRelations(ItemDbWriter db, HashSet<int> written)
    {
        var groups = ItemRelations.Load().GroupBy(r => r.Group).OrderBy(g => g.Key);

        int groupId = 1;
        foreach (var group in groups)
        {
            int index = 0;
            foreach (var r in group)
            {
                if (written.Contains(r.ItemId))
                {
                    db.Relation(groupId, r.ItemId, index == 0);
                    db.Flags(r.ItemId, r.FroobFriendly, r.InGame, r.Rollable);
                }
                index++;
            }
            groupId++;
        }
    }
}
