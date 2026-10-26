using AOSharp.Common.GameData;
using AOSharp.Core.UI;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net;
using System.Text;

public class AOItemQueryService
{
    private readonly string _dbPath;
    private HttpListener? _listener;
    private Thread? _serverThread;
    private volatile bool _stopped;
    private Server _server;
    private volatile bool _dbReady;
    public AOItemQueryService(Server server)
    {
        SQLitePCL.Batteries_V2.Init();
        _server = server;
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AOSharp", "AOItemQueryService", server == Server.PRK ? "items_prk.db" : "items_retail.db"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

    }

    private GameThread? _game;

    public void Start()
    {
        _game = new GameThread();
        EnsureDatabase();

        int port = _server == Server.Retail ? 7070 : 7071;
        string url = $"http://localhost:{port}/";

        // Every client builds the same database from the same game files, so
        // whichever client holds the port answers for all of them. The others
        // keep trying, so the service outlives the client that had it.
        _serverThread = new Thread(() =>
        {
            while (!_stopped)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    Thread.Sleep(3000);
                    continue;
                }
                catch (Exception ex)
                {
                    _game?.Say($"[AoDbServer] Failed to start: {ex.Message}");
                    return;
                }

                _listener = listener;
                if (_stopped)
                {
                    listener.Close();
                    return;
                }

                while (listener.IsListening)
                {
                    try
                    {
                        var context = listener.GetContext();
                        ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                    }
                    catch (HttpListenerException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (Exception ex) { _game?.Say($"[AoDbServer] Accept error: {ex.Message}"); }
                }
                return;
            }
        });

        _serverThread.IsBackground = true;
        _serverThread.Start();
    }

    /// <summary>
    /// Builds the item database from this client's game files when it is
    /// missing or out of date. Queries answer 503 until it is ready.
    /// </summary>
    private void EnsureDatabase()
    {
        var gameDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;

        if (ItemDbBuilder.IsCurrent(_dbPath, gameDirectory))
        {
            _dbReady = true;
            return;
        }

        Chat.WriteLine("Item database: building from your game files. Item search works once it is ready, in about a minute.", ChatColor.Orange);

        // The build runs in its own process, so the game never carries it, and its result goes to
        // chat through the game thread.
        Task.Run(() =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                RunBuilder(gameDirectory);
                _dbReady = true;
                _game?.Say("Item database: ready.", ChatColor.Green);
            }
            catch (Exception ex)
            {
                _game?.Say($"Item database: build failed - {ex.Message}", ChatColor.Red);
            }
        });
    }

    /// <summary>
    /// Runs ItemDbBuild, which ships in builder\ next to this service, and waits for it. The service
    /// is loaded from memory and has no path of its own; AOSharp's Bootstrap is loaded from
    /// Plugins\AOSharp.Bootstrap, so the service's folder is its sibling Plugins\AOItemQueryService.
    /// </summary>
    private void RunBuilder(string gameDirectory)
    {
        var builder = FindBuilder()
            ?? throw new FileNotFoundException("ItemDbBuild.exe is missing next to the item service. Compile it again in AOSharp.");

        var start = new ProcessStartInfo(builder)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(gameDirectory);
        start.ArgumentList.Add(_dbPath);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output.Wait();

        if (process.ExitCode != 0)
            throw new Exception(string.IsNullOrWhiteSpace(error) ? $"the builder exited with code {process.ExitCode}" : error.Trim());
    }

    private static string? FindBuilder()
    {
        var candidates = new List<string>();

        var own = typeof(AOItemQueryService).Assembly.Location;
        if (!string.IsNullOrEmpty(own))
            candidates.Add(Path.Combine(Path.GetDirectoryName(own)!, "builder", "ItemDbBuild.exe"));

        var bootstrap = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "AOSharp.Bootstrap")?.Location;
        if (!string.IsNullOrEmpty(bootstrap))
        {
            var plugins = Path.GetDirectoryName(Path.GetDirectoryName(bootstrap)!)!;
            candidates.Add(Path.Combine(plugins, "AOItemQueryService", "builder", "ItemDbBuild.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Stop()
    {
        _stopped = true;
        _listener?.Stop();
        _listener?.Close();
        _game?.Dispose();
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                return;
            }

            var path = req.Url?.AbsolutePath ?? "";
            var query = req.QueryString;

            object? result = null;
            int statusCode = 200;
            string? error = null;

            // Icons come from the game, not the database.
            if (!_dbReady && path != "/icons")
            {
                statusCode = 503;
                error = "item database is building";
            }
            else if (path == "/search")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = reader.ReadToEnd();
                var searchReq = JsonConvert.DeserializeObject<SearchRequest>(body) ?? new();
                result = Search(searchReq);
            }
            else if (path == "/item")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItem(id);
            }
            else if (path == "/item/modifiers")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItemModifiers(id);
            }
            else if (path == "/item/modifiers/criteria")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItemModifierCriteria(id);
            }
            else if (path == "/item/modifiers/text")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItemModifierText(id);
            }
            else if (path == "/item/requirements")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItemRequirements(id);
            }
            else if (path == "/items/icons")
            {
                var raw = query["ids"];
                if (string.IsNullOrEmpty(raw)) { statusCode = 400; error = "ids required"; }
                else result = GetItemIcons(raw.Split(',').Select(int.Parse).ToList());
            }
            else if (path == "/icons")
            {
                var raw = query["id"];

                if (string.IsNullOrEmpty(raw))
                {
                    statusCode = 400;
                    error = "id required";
                }
                else if (!int.TryParse(raw, out var iconId))
                {
                    statusCode = 400;
                    error = "invalid id";
                }
                else
                {
                    result = GetIcon(iconId);

                    if (result == null)
                    {
                        statusCode = 404;
                        error = "icon not found";
                    }
                }
            }
            else if (path == "/item/resolve")
            {
                if (!int.TryParse(query["lowId"], out var lowId)) { statusCode = 400; error = "lowId required"; }
                else result = ResolveItem(lowId);
            }
            else if (path == "/items/names")
            {
                var raw = query["ids"];
                if (string.IsNullOrEmpty(raw)) { statusCode = 400; error = "ids required"; }
                else result = GetItemNames(raw.Split(',').Select(int.Parse).ToList());
            }
            else if (path == "/item/skillchecks")
            {
                if (!int.TryParse(query["id"], out var id)) { statusCode = 400; error = "id required"; }
                else result = GetItemSkillChecks(id);
            }
            else if (path == "/item/members")
            {
                if (!int.TryParse(query["lowId"], out var lowId)) { statusCode = 400; error = "lowId required"; }
                else result = GetItemMembers(lowId);
            }
            else
            {
                statusCode = 404;
                error = "Not found";
            }

            var json = error != null
                ? JsonConvert.SerializeObject(new { error })
                : JsonConvert.SerializeObject(result);

            var buffer = Encoding.UTF8.GetBytes(json);
            res.StatusCode = statusCode;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = buffer.Length;
            res.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner != null)
            {
                _game?.Say($"[AoDbServer] {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }
    public List<Dictionary<string, object?>> Search(SearchRequest r)
    {
        var sql = new StringBuilder(@"
            SELECT
                i.id AS low_id,
                i.name,
                i.description,
                icon.value AS icon,
                COALESCE(
                    NULLIF(
                        (
                            SELECT json_group_array(json_object(
                                'id',   ir2.item_id,
                                'name', mi.name,
                                'ql',   COALESCE(qs.value, 1),
                                'icon', mi_icon.value
                            ))
                            FROM item_relation ir
                            JOIN item_relation ir2 ON ir2.group_id = ir.group_id
                            LEFT JOIN item mi           ON mi.id = ir2.item_id
                            LEFT JOIN item_stat qs      ON qs.item_id = ir2.item_id AND qs.stat_id = 54
                            LEFT JOIN item_stat mi_icon ON mi_icon.item_id = ir2.item_id AND mi_icon.stat_id = 79
                            WHERE ir.item_id = i.id AND ir.is_group_rep = 1
                            ORDER BY COALESCE(qs.value, 1) ASC
                        ),
                        '[]'
                    ),
                    json_array(json_object(
                        'id',   i.id,
                        'name', i.name,
                        'ql',   COALESCE(self_ql.value, 1),
                        'icon', icon.value
                    ))
                ) AS members
            FROM item i
            LEFT JOIN item_stat icon    ON icon.item_id = i.id AND icon.stat_id = 79
            LEFT JOIN item_stat self_ql ON self_ql.item_id = i.id AND self_ql.stat_id = 54
            WHERE NOT EXISTS (
                SELECT 1 FROM item_relation ir
                WHERE ir.item_id = i.id AND ir.is_group_rep = 0
        )");

        if (r.IdPairs != null && r.IdPairs.Count > 0)
            return SearchByPairs(r.IdPairs, r.Name);

        var parameters = new Dictionary<string, object>();

        const string groupMembers = @"(
            SELECT ir2.item_id
            FROM item_relation ir
            JOIN item_relation ir2 ON ir2.group_id = ir.group_id
            WHERE ir.item_id = i.id AND ir.is_group_rep = 1
            UNION ALL SELECT i.id
        )";

        if (r.Id.HasValue)
        {
            sql.Append(" AND i.id = @id");
            parameters["@id"] = r.Id.Value;
        }
        else if (!string.IsNullOrEmpty(r.Name))
        {
            sql.Append(@" AND (
                name_matches(@name, i.name)
                OR EXISTS (
                    SELECT 1 FROM item_relation ir
                    JOIN item_relation ir2 ON ir2.group_id = ir.group_id
                    JOIN item mi ON mi.id = ir2.item_id
                    WHERE ir.item_id = i.id AND ir.is_group_rep = 1
                      AND name_matches(@name, mi.name)
                )
            )");
            parameters["@name"] = r.Name;
        }

        if (r.Ids != null && r.Ids.Count > 0)
        {
            var placeholders = string.Join(",", r.Ids.Select((_, i) => $"@bulkId{i}"));
            sql.Append($" AND i.id IN ({placeholders})");
            for (int i = 0; i < r.Ids.Count; i++)
                parameters[$"@bulkId{i}"] = r.Ids[i];
        }

        // Chains that hold any of these ids, wherever in the chain: the id may
        // be the representative itself or any of its members. Sent as one JSON
        // parameter, as the list runs to thousands.
        if (r.MemberIds != null && r.MemberIds.Count > 0)
        {
            sql.Append(@" AND i.id IN (
                SELECT j.value FROM json_each(@memberIds) j
                UNION
                SELECT rep.item_id FROM json_each(@memberIds) j
                JOIN item_relation m   ON m.item_id = j.value
                JOIN item_relation rep ON rep.group_id = m.group_id AND rep.is_group_rep = 1
            )");
            parameters["@memberIds"] = JsonConvert.SerializeObject(r.MemberIds);
        }


        if (r.StatFilter != null)
        {
            foreach (var (f, i) in r.StatFilter.Where(f => f.Enabled).Select((f, i) => (f, i)))
            {
                sql.Append($@" AND EXISTS (
                    SELECT 1 FROM item_stat sf{i}
                    WHERE sf{i}.item_id IN {groupMembers}
                    AND sf{i}.stat_id = @sfStat{i}");
                    parameters[$"@sfStat{i}"] = f.StatId;
                    if (f.Min.HasValue) { sql.Append($" AND sf{i}.value >= @sfMin{i}"); parameters[$"@sfMin{i}"] = f.Min.Value; }
                    if (f.Max.HasValue) { sql.Append($" AND sf{i}.value <= @sfMax{i}"); parameters[$"@sfMax{i}"] = f.Max.Value; }
                    sql.Append(")");
            }
        }

        if (r.ReqFilter != null)
        {
            foreach (var (f, i) in r.ReqFilter.Where(f => f.Enabled).Select((f, i) => (f, i)))
            {
                sql.Append($@" AND EXISTS (
                    SELECT 1 FROM item_requirement rf{i}
                    WHERE rf{i}.item_id IN {groupMembers}
                    AND rf{i}.stat = @rfStat{i}");
                    parameters[$"@rfStat{i}"] = f.StatId;
                    if (f.Min.HasValue) { sql.Append($" AND rf{i}.value >= @rfMin{i}"); parameters[$"@rfMin{i}"] = f.Min.Value; }
                    if (f.Max.HasValue) { sql.Append($" AND rf{i}.value <= @rfMax{i}"); parameters[$"@rfMax{i}"] = f.Max.Value; }
                    sql.Append(")");
            }
        }

        if (r.ModFilter != null)
        {
            foreach (var (f, i) in r.ModFilter.Where(f => f.Enabled).Select((f, i) => (f, i)))
            {
                var ftTypes = GetFunctionTypes(f.ModType);
                var ftPlaceholders = string.Join(",", ftTypes.Select((_, j) => $"@mfFt{i}_{j}"));
                for (int j = 0; j < ftTypes.Count; j++)
                    parameters[$"@mfFt{i}_{j}"] = ftTypes[j];

                if (f.StatId.HasValue)
                {
                    sql.Append($@" AND EXISTS (
                        SELECT 1 FROM item_modifier mf{i}_s
                        JOIN item_modifier mf{i}_v
                        ON  mf{i}_v.item_id       = mf{i}_s.item_id
                        AND mf{i}_v.event_type    = mf{i}_s.event_type
                        AND mf{i}_v.function_type = mf{i}_s.function_type
                        AND mf{i}_v.list_index    = mf{i}_s.list_index
                        AND mf{i}_v.operator      = 39
                        WHERE mf{i}_s.item_id IN {groupMembers}
                        AND   mf{i}_s.function_type IN ({ftPlaceholders})
                        AND   mf{i}_s.operator = 0
                        AND   mf{i}_s.value    = @mfStat{i}");
                        parameters[$"@mfStat{i}"] = f.StatId.Value;
                        if (f.Min.HasValue) { sql.Append($" AND mf{i}_v.value >= @mfMin{i}"); parameters[$"@mfMin{i}"] = f.Min.Value; }
                        if (f.Max.HasValue) { sql.Append($" AND mf{i}_v.value <= @mfMax{i}"); parameters[$"@mfMax{i}"] = f.Max.Value; }
                        sql.Append(")");
                }
                else
                {
                    sql.Append($@" AND EXISTS (
                        SELECT 1 FROM item_modifier mf{i}_v
                        WHERE mf{i}_v.item_id IN {groupMembers}
                        AND   mf{i}_v.function_type IN ({ftPlaceholders})
                        AND   mf{i}_v.operator = 39");
                        if (f.Min.HasValue) { sql.Append($" AND mf{i}_v.value >= @mfMin{i}"); parameters[$"@mfMin{i}"] = f.Min.Value; }
                        if (f.Max.HasValue) { sql.Append($" AND mf{i}_v.value <= @mfMax{i}"); parameters[$"@mfMax{i}"] = f.Max.Value; }
                        sql.Append(")");
                }
            }
        }

        sql.Append(" LIMIT @limit OFFSET @offset");
        parameters["@limit"] = r.Limit;
        parameters["@offset"] = r.Offset;

        return Query(sql.ToString(), parameters);
    }

    private List<Dictionary<string, object?>> SearchByPairs(List<IdPair> pairs, string? name)
    {
        var cteRows = string.Join(",\n", pairs.Select((_, i) => $"(@low{i}, @high{i})"));
        var parameters = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Count; i++)
        {
            parameters[$"@low{i}"] = pairs[i].LowId;
            parameters[$"@high{i}"] = pairs[i].HighId;
        }

        var sql = $@"
        WITH pairs(low_id, high_id) AS (VALUES {cteRows})
        SELECT
            i.id       AS low_id,
            i.name,
            i.description,
            icon.value AS icon,
            CASE WHEN p.low_id = p.high_id
                THEN json_array(
                    json_object('id', p.low_id, 'name', lm.name, 'ql', COALESCE(lql.value, 1), 'icon', li.value)
                )
                ELSE json_array(
                    json_object('id', p.low_id,  'name', lm.name,  'ql', COALESCE(lql.value, 1), 'icon', li.value),
                    json_object('id', p.high_id, 'name', hm.name,  'ql', COALESCE(hql.value, 1), 'icon', hi.value)
                )
            END AS members
        FROM pairs p
        JOIN item i          ON i.id = p.low_id
        LEFT JOIN item_stat icon ON icon.item_id = i.id       AND icon.stat_id = 79
        LEFT JOIN item      lm   ON lm.id        = p.low_id
        LEFT JOIN item_stat lql  ON lql.item_id  = p.low_id  AND lql.stat_id  = 54
        LEFT JOIN item_stat li   ON li.item_id   = p.low_id  AND li.stat_id   = 79
        LEFT JOIN item      hm   ON hm.id        = p.high_id
        LEFT JOIN item_stat hql  ON hql.item_id  = p.high_id AND hql.stat_id  = 54
        LEFT JOIN item_stat hi   ON hi.item_id   = p.high_id AND hi.stat_id   = 79";

        if (!string.IsNullOrEmpty(name))
        {
            sql += " WHERE name_matches(@name, i.name)";
            parameters["@name"] = name;
        }

        return Query(sql, parameters);
    }

    public static class FuzzyMatcher
    {
        public static bool Match(string query, string name)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(name))
                return false;

            var words = SplitWords(name);       // ["might","of","the","revenant"]
            var normName = Normalize(name);     // "mightoftherevenant"
            var queryParts = query.Trim()
                                  .ToLowerInvariant()
                                  .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // "miys" → Normalize("Miy's") == "miys"  ✓
            var normQuery = Normalize(query);
            if (normName.Contains(normQuery))
                return true;

            // Single token only — "motr" vs initials "motr" of "Might of the Revenant"
            if (queryParts.Length == 1)
            {
                var initials = new string(words.Select(w => w[0]).ToArray()); // "motr"
                if (initials.Contains(normQuery))
                    return true;
            }

            // "barr str"  → ["barr","str"]  vs ["barrow","strength"]  ✓
            // "m o t r"   → ["m","o","t","r"] vs ["might","of","the","revenant"] ✓
            if (AllTokensPrefixWords(queryParts, words))
                return true;

            return false;
        }

        private static string Normalize(string s)
        {
            return new string([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        }

        private static string[] SplitWords(string s)
        {
            return s.ToLowerInvariant().Split([' ', '\t', '-', '_', '\'', '/'], StringSplitOptions.RemoveEmptyEntries);
        }

        // "barr str" needs "barr" to match before "str" in the word list.
        private static bool AllTokensPrefixWords(string[] tokens, string[] words)
        {
            if (tokens.Length == 0) return false;

            int wi = 0;
            foreach (var token in tokens)
            {
                bool found = false;
                while (wi < words.Length)
                {
                    if (words[wi++].StartsWith(token, StringComparison.Ordinal))
                    { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }
    }

    private static List<int> GetFunctionTypes(string modType) => modType switch
    {
        "stat"      => new() { (int)FunctionType.ModifyStat, (int)FunctionType.ModifyNanoStat },
        "cast"      => new() { (int)FunctionType.CastNano, (int)FunctionType.TeamCastNano, (int)FunctionType.AreaCastNano },
        "upload"    => new() { (int)FunctionType.UploadNano },
        "hit"       => new() { (int)FunctionType.Hit },
        "lock"      => new() { (int)FunctionType.LockSkill },
        _           => new()
    };

    public List<Dictionary<string, object?>> GetItemMembers(int lowId)
    {
        var result = Query(@"
            SELECT ir2.item_id AS id, COALESCE(ql.value, 1) AS ql
            FROM item_relation ir
            JOIN item_relation ir2 ON ir2.group_id = ir.group_id
            LEFT JOIN item_stat ql ON ql.item_id = ir2.item_id AND ql.stat_id = 54
            WHERE ir.item_id = @lowId
            ORDER BY ql.value ASC",
            new Dictionary<string, object> { ["@lowId"] = lowId });

        if (result.Count > 0)
            return result;

        // No relation found, return the item itself twice with its actual QL.
        return Query(@"
            SELECT item_id AS id, COALESCE(value, 1) AS ql
            FROM item_stat
            WHERE item_id = @lowId AND stat_id = 54",
            new Dictionary<string, object> { ["@lowId"] = lowId })
            is { Count: > 0 } qlResult
            ? new List<Dictionary<string, object?>>
            {
            new(qlResult[0]),
            new(qlResult[0])
            }
            : new List<Dictionary<string, object?>>
            {
            new()
            {
                ["id"] = lowId,
                ["ql"] = 1
            },
            new()
            {
                ["id"] = lowId,
                ["ql"] = 1
            }
            };
    }

    public Dictionary<string, object?>? ResolveItem(int lowId)
    {
        var rows = Query(@"
            SELECT
            @lowId AS low_id,
            (
                SELECT ir2.item_id
                FROM item_relation ir
                JOIN item_relation ir2 ON ir2.group_id = ir.group_id
                LEFT JOIN item_stat ql ON ql.item_id = ir2.item_id AND ql.stat_id = 54
                WHERE ir.item_id = @lowId AND ir.is_group_rep = 1
                ORDER BY COALESCE(ql.value, 1) DESC
                LIMIT 1
            ) AS high_id,
            COALESCE((
                SELECT MIN(ql.value)
                FROM item_relation ir
                JOIN item_relation ir2 ON ir2.group_id = ir.group_id
                JOIN item_stat ql ON ql.item_id = ir2.item_id AND ql.stat_id = 54
                WHERE ir.item_id = @lowId AND ir.is_group_rep = 1
            ), 1) AS low_ql,
            COALESCE((
                SELECT MAX(ql.value)
                FROM item_relation ir
                JOIN item_relation ir2 ON ir2.group_id = ir.group_id
                JOIN item_stat ql ON ql.item_id = ir2.item_id AND ql.stat_id = 54
                WHERE ir.item_id = @lowId AND ir.is_group_rep = 1
            ), 1) AS high_ql,
            i.name, i.description, icon.value AS icon
            FROM item i
            LEFT JOIN item_stat icon ON icon.item_id = i.id AND icon.stat_id = 79
            WHERE i.id = @lowId
            LIMIT 1",
            new Dictionary<string, object> { ["@lowId"] = lowId });
        return rows.Count > 0 ? rows[0] : null;
    }

    public List<Dictionary<string, object?>> GetItem(int id)
    {
        return Query("SELECT stat_id, value FROM item_stat WHERE item_id = @id", new Dictionary<string, object> { ["@id"] = id });
    }

    public List<Dictionary<string, object?>> GetItemModifiers(int id)
    {
        return Query(@"
            SELECT event_type, function_type, list_index, op_index, operator, value_type, value
            FROM item_modifier
            WHERE item_id = @id
            ORDER BY event_type, function_type, list_index, op_index",
            new Dictionary<string, object> { ["@id"] = id });
    }

    public List<Dictionary<string, object?>> GetItemModifierCriteria(int id)
    {
        return Query(@"
            SELECT event_type, function_type, list_index, op_index, crit_index, stat, value, operator
            FROM item_modifier_criteria
            WHERE item_id = @id
            ORDER BY event_type, function_type, list_index, op_index, crit_index",
               new Dictionary<string, object> { ["@id"] = id });
    }

    public List<Dictionary<string, object?>> GetItemModifierText(int id) =>
        Query(@"
            SELECT event_type, function_type, list_index, op_index, sort_index, value
            FROM item_modifier_text
            WHERE item_id = @id
            ORDER BY event_type, function_type, list_index, op_index, sort_index",
            new Dictionary<string, object> { ["@id"] = id });

    public List<Dictionary<string, object?>> GetItemRequirements(int id)
    {
        return Query("SELECT outer_action_type, inner_action_type, list_index, stat, value, operator FROM item_requirement WHERE item_id = @id",
           new Dictionary<string, object> { ["@id"] = id });
    }

    public List<Dictionary<string, object?>> GetItemSkillChecks(int id)
    {
        return Query("SELECT skill_check, stat_id, value FROM item_skill_check WHERE item_id = @id",
            new Dictionary<string, object> { ["@id"] = id });
    }

    public Dictionary<string, object?>? GetIcon(int iconId)
    {
        var bytes = _game!.Run(() => RenderIcon(iconId));
        if (bytes == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["icon"] = iconId,
            ["iconData"] = Convert.ToBase64String(bytes)
        };
    }

    public List<Dictionary<string, object?>> GetItemIcons(List<int> idList)
    {
        var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
        var parameters = idList.Select((v, i) => new { Key = $"@id{i}", Value = (object)v })
                                 .ToDictionary(x => x.Key, x => x.Value);

        var rows = Query($"SELECT item_id, value AS icon FROM item_stat WHERE stat_id = 79 AND item_id IN ({placeholders})", parameters);

        // One trip to the game thread for the whole batch, not one per icon.
        var iconIds = rows.Select(r => r["icon"]).OfType<long>().Select(id => (int)id).Distinct().ToList();
        var rendered = _game!.Run(() => iconIds.ToDictionary(id => id, RenderIcon));

        foreach (var row in rows)
        {
            row["iconData"] = row["icon"] is long iconId && rendered.TryGetValue((int)iconId, out var bytes) && bytes != null
                ? Convert.ToBase64String(bytes)
                : null;
        }

        return rows;
    }

    /// <summary>Reads an icon from the game's textures as PNG. Game thread only.</summary>
    private static unsafe byte[]? RenderIcon(int iconId)
    {
        var sprite = GuiResourceManager.GetTexture(iconId);
        return sprite == null ? null : sprite->ToPngBytes();
    }

    public List<Dictionary<string, object?>> GetItemNames(List<int> idList)
    {
        var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
        var parameters = idList.Select((v, i) => new { Key = $"@id{i}", Value = (object)v }).ToDictionary(x => x.Key, x => x.Value);
        return Query($"SELECT id, name FROM item WHERE id IN ({placeholders})", parameters);
    }

    private List<Dictionary<string, object?>> Query(string sql, Dictionary<string, object>? parameters = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var con = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;");

        try
        {
            con.Open();
        }
        catch (Exception ex)
        {
            _game?.Say($"[DB] {ex.Message}");
            throw;
        }

        con.CreateFunction("name_matches", (string? query, string? name) =>
            query != null && name != null && FuzzyMatcher.Match(query, name) ? 1 : 0);

        using var cmd = con.CreateCommand();

        cmd.CommandText = sql;

        if (parameters != null)
            foreach (var kvp in parameters)
                cmd.Parameters.AddWithValue(kvp.Key, kvp.Value);

        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
public class IdPair
{
    [JsonProperty("lowId")] public int LowId { get; set; }
    [JsonProperty("highId")] public int HighId { get; set; }
}

public class SearchRequest
{
    public string? Name { get; set; }
    public int? Id { get; set; }
    public List<int>? Ids { get; set; }
    public List<int>? MemberIds { get; set; }
    public List<IdPair>? IdPairs { get; set; }

    public int Limit { get; set; } = 50;
    public int Offset { get; set; } = 0;
    public List<ModFilterEntry>? ModFilter { get; set; }
    public List<StatFilterEntry>? StatFilter { get; set; }
    public List<StatFilterEntry>? ReqFilter { get; set; }
}

public enum FunctionType
{
    ModifyNanoStat = 53012,
    ModifyStat = 53045,
    CastNano = 53051,
    TeamCastNano = 53066,
    AreaCastNano = 53087,
    UploadNano = 53019,
    Hit = 53002,
    LockSkill = 53033
}


public class StatFilterEntry
{
    [JsonProperty("statId")] public int StatId { get; set; }
    [JsonProperty("min")] public int? Min { get; set; }
    [JsonProperty("max")] public int? Max { get; set; }
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
}

public class ModFilterEntry
{
    [JsonProperty("uid")] public string Uid { get; set; }
    [JsonProperty("modType")] public string ModType { get; set; }
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

    // Only present for "stat" and "lock" types
    [JsonProperty("statId")] public int? StatId { get; set; }

    [JsonProperty("min")] public int? Min { get; set; }
    [JsonProperty("max")] public int? Max { get; set; }
}

public enum Server
{
    PRK,
    Retail
}