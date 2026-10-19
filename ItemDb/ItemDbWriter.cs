using Microsoft.Data.Sqlite;

/// <summary>
/// Writes the item database: the schema the query service reads, and one
/// prepared insert per table. Everything goes in one transaction; indexes are
/// created at the end, which is much faster than keeping them up to date row
/// by row.
/// </summary>
public sealed class ItemDbWriter : IDisposable
{
    /// <summary>
    /// Stored as the file's user_version. Raise it whenever the schema or what
    /// goes into it changes, and existing databases are rebuilt.
    /// </summary>
    public const int SchemaVersion = 1;

    private const string Schema = @"
        CREATE TABLE item (
            id             INT PRIMARY KEY,
            name           TEXT NOT NULL,
            description    TEXT,
            froob_friendly INTEGER NOT NULL DEFAULT 0,
            in_game        INTEGER NOT NULL DEFAULT 0,
            rollable       INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE item_stat (
            item_id INT    NOT NULL REFERENCES item(id),
            stat_id INT    NOT NULL,
            value   BIGINT NOT NULL,
            PRIMARY KEY (item_id, stat_id)
        );

        CREATE TABLE item_skill_check (
            item_id     INT    NOT NULL REFERENCES item(id),
            skill_check INT    NOT NULL,
            stat_id     INT    NOT NULL,
            value       BIGINT NOT NULL,
            PRIMARY KEY (item_id, skill_check, stat_id)
        );

        CREATE TABLE item_modifier (
            item_id       INT  NOT NULL REFERENCES item(id),
            event_type    INT  NOT NULL,
            function_type INT  NOT NULL,
            list_index    INT  NOT NULL,
            op_index      INT  NOT NULL,
            operator      INT  NOT NULL,
            value_type    TEXT NOT NULL,
            value         REAL,
            PRIMARY KEY (item_id, event_type, function_type, list_index, op_index)
        );

        CREATE TABLE item_modifier_criteria (
            item_id       INT    NOT NULL REFERENCES item(id),
            event_type    INT    NOT NULL,
            function_type INT    NOT NULL,
            list_index    INT    NOT NULL,
            op_index      INT    NOT NULL,
            crit_index    INT    NOT NULL,
            stat          INT    NOT NULL,
            value         BIGINT NOT NULL,
            operator      INT    NOT NULL,
            PRIMARY KEY (item_id, event_type, function_type, list_index, op_index, crit_index)
        );

        CREATE TABLE item_modifier_text (
            item_id       INT  NOT NULL REFERENCES item(id),
            event_type    INT  NOT NULL,
            function_type INT  NOT NULL,
            list_index    INT  NOT NULL,
            op_index      INT  NOT NULL,
            sort_index    INT  NOT NULL,
            value         TEXT NOT NULL,
            PRIMARY KEY (item_id, event_type, function_type, list_index, op_index, sort_index)
        );

        CREATE TABLE item_requirement (
            item_id           INT    NOT NULL REFERENCES item(id),
            outer_action_type INT    NOT NULL,
            inner_action_type INT    NOT NULL,
            list_index        INT    NOT NULL,
            stat              INT    NOT NULL,
            value             BIGINT NOT NULL,
            operator          INT    NOT NULL,
            PRIMARY KEY (item_id, outer_action_type, inner_action_type, list_index, stat)
        );

        CREATE TABLE item_relation (
            id           INTEGER PRIMARY KEY,
            group_id     INTEGER NOT NULL,
            item_id      INTEGER NOT NULL REFERENCES item(id),
            is_group_rep INTEGER NOT NULL DEFAULT 0
        );";

    private const string Indexes = @"
        CREATE INDEX idx_item_name                  ON item (name);
        CREATE INDEX idx_item_stat_stat_item        ON item_stat (stat_id, item_id);
        CREATE INDEX idx_item_relation_item_rep     ON item_relation (item_id, is_group_rep);
        CREATE INDEX idx_item_relation_group_id     ON item_relation (group_id);
        CREATE INDEX idx_item_modifier_item         ON item_modifier (item_id);
        CREATE INDEX idx_item_modifier_event        ON item_modifier (event_type, function_type);
        CREATE INDEX idx_item_modifier_criteria_item ON item_modifier_criteria (item_id);
        CREATE INDEX idx_item_modifier_text_item    ON item_modifier_text (item_id);
        CREATE INDEX idx_item_requirement_item      ON item_requirement (item_id);
        CREATE INDEX idx_item_requirement_action    ON item_requirement (outer_action_type, inner_action_type);";

    private readonly SqliteConnection _con;
    private readonly SqliteTransaction _tx;
    private readonly Dictionary<string, SqliteCommand> _inserts = new();

    public ItemDbWriter(string path)
    {
        _con = new SqliteConnection($"Data Source={path};Pooling=False");
        _con.Open();

        // A half-written file is thrown away, so nothing needs to survive a crash.
        Execute("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Execute(Schema);

        _tx = _con.BeginTransaction();
    }

    public void Item(int id, string name, string? description) =>
        Insert("INSERT INTO item (id, name, description) VALUES ($0, $1, $2)", id, name, description);

    public void Stat(int itemId, int statId, long value) =>
        Insert("INSERT INTO item_stat (item_id, stat_id, value) VALUES ($0, $1, $2)", itemId, statId, value);

    public void SkillCheck(int itemId, int skillCheck, int statId, long value) =>
        Insert("INSERT INTO item_skill_check (item_id, skill_check, stat_id, value) VALUES ($0, $1, $2, $3)",
            itemId, skillCheck, statId, value);

    public void Modifier(int itemId, int eventType, int functionType, int listIndex, int opIndex, int op, string valueType, double? value) =>
        Insert(@"INSERT INTO item_modifier (item_id, event_type, function_type, list_index, op_index, operator, value_type, value)
                 VALUES ($0, $1, $2, $3, $4, $5, $6, $7)",
            itemId, eventType, functionType, listIndex, opIndex, op, valueType, value);

    public void ModifierCriterion(int itemId, int eventType, int functionType, int listIndex, int opIndex, int critIndex, int stat, long value, int op) =>
        Insert(@"INSERT INTO item_modifier_criteria (item_id, event_type, function_type, list_index, op_index, crit_index, stat, value, operator)
                 VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8)",
            itemId, eventType, functionType, listIndex, opIndex, critIndex, stat, value, op);

    public void ModifierText(int itemId, int eventType, int functionType, int listIndex, int opIndex, int sortIndex, string value) =>
        Insert(@"INSERT INTO item_modifier_text (item_id, event_type, function_type, list_index, op_index, sort_index, value)
                 VALUES ($0, $1, $2, $3, $4, $5, $6)",
            itemId, eventType, functionType, listIndex, opIndex, sortIndex, value);

    public void Requirement(int itemId, int outerActionType, int innerActionType, int listIndex, int stat, long value, int op) =>
        Insert(@"INSERT INTO item_requirement (item_id, outer_action_type, inner_action_type, list_index, stat, value, operator)
                 VALUES ($0, $1, $2, $3, $4, $5, $6)",
            itemId, outerActionType, innerActionType, listIndex, stat, value, op);

    public void Relation(int groupId, int itemId, bool isGroupRep) =>
        Insert("INSERT OR IGNORE INTO item_relation (group_id, item_id, is_group_rep) VALUES ($0, $1, $2)",
            groupId, itemId, isGroupRep ? 1 : 0);

    public void Flags(int itemId, int froobFriendly, int inGame, int rollable) =>
        Insert("UPDATE item SET froob_friendly = $1, in_game = $2, rollable = $3 WHERE id = $0",
            itemId, froobFriendly, inGame, rollable);

    /// <summary>Commits everything and builds the indexes.</summary>
    public void Complete()
    {
        _tx.Commit();
        Execute(Indexes);
        Execute($"PRAGMA journal_mode=DELETE; PRAGMA user_version={SchemaVersion}; ANALYZE;");
    }

    public void Dispose()
    {
        foreach (var cmd in _inserts.Values) cmd.Dispose();
        _tx.Dispose();
        _con.Dispose();
    }

    /// <summary>The schema version a database file was written with, or 0.</summary>
    public static int ReadSchemaVersion(string path)
    {
        using var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Insert(string sql, params object?[] values)
    {
        if (!_inserts.TryGetValue(sql, out var cmd))
        {
            cmd = _con.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _tx;
            for (int i = 0; i < values.Length; i++)
                cmd.Parameters.Add(new SqliteParameter($"${i}", null));
            cmd.Prepare();
            _inserts[sql] = cmd;
        }

        for (int i = 0; i < values.Length; i++)
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;

        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
