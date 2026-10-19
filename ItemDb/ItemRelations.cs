using System.Reflection;

/// <summary>
/// One row of ItemRelations.csv: an item, the group of QL variants it belongs
/// to, and flags the game data doesn't carry.
/// </summary>
public record ItemRelation(int Group, int ItemId, int FroobFriendly, int InGame, int Rollable);

/// <summary>
/// Reads ItemRelations.csv, which is built into this DLL. The file is
/// maintained by hand; the game has no record of which items are QL variants
/// of each other.
/// </summary>
public static class ItemRelations
{
    private const string ResourceName = "ItemRelations.csv";

    public static List<ItemRelation> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not built into this DLL.");
        using var reader = new StreamReader(stream);

        var rows = new List<ItemRelation>();
        reader.ReadLine(); // header

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var f = line.Split(',');
            rows.Add(new ItemRelation(int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4])));
        }

        return rows;
    }
}
