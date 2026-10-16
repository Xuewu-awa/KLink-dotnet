using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using fyserver.Models;
using fyserver.Serialization;
using MySqlConnector;
using Npgsql;

namespace fyserver.Services;

/// <summary>
/// 对局历史持久化。关系型模式与玩家数据共用数据库；本地模式写入 FASTER KV。
/// </summary>
public sealed class MatchHistoryService(UserDatabaseConfigurationService databaseConfiguration, AppDataStoreService appData)
{
    private const string RetentionKey = "match:retention";
    private const string LocalPrefix = "match:document:";
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly object _retentionLock = new();
    private bool _schemaReady;
    private string? _schemaKey;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return;

        if (settings.Provider == "local")
        {
            // FASTER is the fast local index, while data/replays is the durable
            // archive. Rehydrate missing records after a process restart.
            await HydrateLocalReplayArchivesAsync(cancellationToken);
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
    }

    public MatchRetentionSettings GetRetentionSettings()
    {
        lock (_retentionLock)
        {
            if (!appData.IsReady) return DefaultRetention;
            var raw = appData.Get(RetentionKey);
            if (raw == null)
            {
                appData.Set(RetentionKey, new JsonObject
                {
                    ["mode"] = DefaultRetention.Mode, ["keepCount"] = DefaultRetention.KeepCount,
                    ["keepDays"] = DefaultRetention.KeepDays, ["cleanupDayUtc"] = DefaultRetention.CleanupDayUtc,
                    ["cleanupHourUtc"] = DefaultRetention.CleanupHourUtc
                }.ToJsonString());
                return DefaultRetention;
            }
            try
            {
                var root = JsonNode.Parse(raw)?.AsObject();
                return NormalizeRetention(new MatchRetentionSettings(
                    root?["mode"]?.GetValue<string>() ?? DefaultRetention.Mode,
                    root?["keepCount"]?.GetValue<int>() ?? DefaultRetention.KeepCount,
                    root?["keepDays"]?.GetValue<int>() ?? DefaultRetention.KeepDays,
                    root?["cleanupDayUtc"]?.GetValue<int>() ?? DefaultRetention.CleanupDayUtc,
                    root?["cleanupHourUtc"]?.GetValue<int>() ?? DefaultRetention.CleanupHourUtc));
            }
            catch { return DefaultRetention; }
        }
    }

    public void SaveRetentionSettings(MatchRetentionSettings settings)
    {
        var normalized = NormalizeRetention(settings);
        lock (_retentionLock)
        {
            if (!appData.IsReady) throw new InvalidOperationException("应用数据数据库尚未初始化");
            var json = new JsonObject
            {
                ["mode"] = normalized.Mode,
                ["keepCount"] = normalized.KeepCount,
                ["keepDays"] = normalized.KeepDays,
                ["cleanupDayUtc"] = normalized.CleanupDayUtc,
                ["cleanupHourUtc"] = normalized.CleanupHourUtc
            };
            appData.Set(RetentionKey, json.ToJsonString(new() { WriteIndented = true }));
        }
    }

    public async Task SaveSnapshotAsync(MatchInfo match, MatchStartingInfo startingInfo, CancellationToken cancellationToken = default)
    {
        match.StartedAtUtc ??= DateTime.UtcNow;
        match.MatchStartingInfo = startingInfo;
        var document = BuildDocument(match, startingInfo);
        ArchiveReplay(document, rebuildIndex: true);   // ★ 文件归档：与数据库配置无关
        await WriteDocumentAsync(document, cancellationToken);
    }

    public async Task AppendActionsAsync(MatchInfo match, IReadOnlyCollection<MatchAction> actions, CancellationToken cancellationToken = default)
    {
        if (actions.Count == 0) return;
        // ★ 文件归档（与数据库配置无关）—— 见 `ArchiveReplay` 的注释。
        //   调用方保证 `match.MatchActions` 里**已经含**这批新动作（`MatchEndpoints.cs:184/352`
        //   先 Add 再传 `recorded`）⇒ `BuildDocument` 拿到的就是完整动作流。
        ArchiveReplay(BuildDocument(match, match.MatchStartingInfo), rebuildIndex: false);
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(match.MatchId, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                var known = document.Actions.Select(x => x.ActionId).ToHashSet();
                document.Actions.AddRange(actions.Where(x => known.Add(x.ActionId)).OrderBy(x => x.ActionId));
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(document, cancellationToken);
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
            AddParameter(exists, "@match", match.MatchId);
            if (await exists.ExecuteScalarAsync(cancellationToken) == null)
                await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
        }
        var inserted = 0;
        foreach (var action in actions.DistinctBy(x => x.ActionId))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = settings.Provider == "postgresql"
                ? "INSERT INTO fy_match_events (match_id, action_id, payload) VALUES (@match, @action, @payload) ON CONFLICT (match_id, action_id) DO NOTHING"
                : "INSERT IGNORE INTO fy_match_events (match_id, action_id, payload) VALUES (@match, @action, @payload)";
            AddParameter(command, "@match", match.MatchId);
            AddParameter(command, "@action", action.ActionId);
            AddParameter(command, "@payload", JsonSerializer.Serialize(action, FyJsonContext.Default.MatchAction));
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE fy_match_history SET action_count = action_count + @inserted, turns = @turns WHERE match_id = @match";
            AddParameter(update, "@match", match.MatchId);
            AddParameter(update, "@inserted", inserted);
            AddParameter(update, "@turns", match.Turns);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(MatchInfo match, CancellationToken cancellationToken = default)
    {
        match.CompletedAtUtc ??= DateTime.UtcNow;
        match.IsCompleted = true;
        match.IsAborted = false;
        ArchiveReplay(BuildDocument(match, match.MatchStartingInfo), rebuildIndex: true);   // ★
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(match.MatchId, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(document, cancellationToken);
            });
            return;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
    }

    public async Task MarkAbortedAsync(MatchInfo match, CancellationToken cancellationToken = default)
    {
        match.CompletedAtUtc ??= DateTime.UtcNow;
        match.IsAborted = true;
        ArchiveReplay(BuildDocument(match, match.MatchStartingInfo), rebuildIndex: true);   // ★
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            await WithFileLockAsync(match.MatchId, async () =>
            {
                var document = await ReadLocalAsync(match.MatchId, cancellationToken) ?? BuildDocument(match, match.MatchStartingInfo);
                document.Summary = ToSummary(match, document.Summary.StartedAt);
                await WriteLocalAsync(document, cancellationToken);
            });
            return;
        }
        await UpsertHeaderAsync(settings, BuildDocument(match, match.MatchStartingInfo), cancellationToken);
    }

    public async Task<IReadOnlyList<MatchHistorySummary>> ListRecentAsync(int limit, bool completedOnly = true, int? playerId = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var settings = databaseConfiguration.Load();
        if (settings == null) return [];
        if (settings.Provider == "local")
        {
            var result = new List<MatchHistorySummary>();
            foreach (var document in ReadLocalDocuments())
            {
                if (document != null && (!completedOnly || document.Summary.Status == "completed") &&
                    (playerId == null || document.Summary.LeftPlayerId == playerId || document.Summary.RightPlayerId == playerId)) result.Add(document.Summary);
            }
            return result.OrderByDescending(x => x.CompletedAt ?? x.StartedAt).Take(limit).ToList();
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload, action_count, turns, status, completed_at FROM fy_match_history {(completedOnly ? "WHERE status = 'completed'" : "WHERE 1 = 1")} AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) ORDER BY COALESCE(completed_at, started_at) DESC LIMIT @limit";
        AddParameter(command, "@limit", limit);
        AddParameter(command, "@player", playerId);
        var rows = new List<MatchHistorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            if (document != null)
            {
                DateTime? completedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                rows.Add(document.Summary with
                {
                    ActionCount = reader.GetInt32(1),
                    Turns = reader.GetInt32(2),
                    Status = reader.GetString(3),
                    CompletedAt = completedAt
                });
            }
        }
        return rows;
    }

    public async Task<(IReadOnlyList<MatchHistorySummary> Matches, int Total)> ListAdminAsync(
        int page, int pageSize, string? status = null, int? playerId = null, string? playerName = null, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        status = string.IsNullOrWhiteSpace(status) || status == "all" ? null : status;
        playerName = string.IsNullOrWhiteSpace(playerName) ? null : playerName.Trim();
        var settings = databaseConfiguration.Load();
        if (settings == null) return ([], 0);
        if (settings.Provider == "local")
        {
            var matches = new List<MatchHistorySummary>();
            foreach (var document in ReadLocalDocuments())
            {
                var summary = document?.Summary;
                if (summary == null || status != null && summary.Status != status ||
                    playerId != null && summary.LeftPlayerId != playerId && summary.RightPlayerId != playerId ||
                    playerName != null && !summary.LeftPlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase) &&
                    !summary.RightPlayerName.Contains(playerName, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(summary with { ActionCount = document!.Actions.Count });
            }
            var ordered = matches.OrderByDescending(x => x.CompletedAt ?? x.StartedAt).ThenByDescending(x => x.MatchId).ToList();
            return (ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ordered.Count);
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM fy_match_history WHERE (@status IS NULL OR status = @status) AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) AND (@playerName IS NULL OR LOWER(left_player_name) LIKE @playerName OR LOWER(right_player_name) LIKE @playerName)";
        AddParameter(count, "@status", status);
        AddParameter(count, "@player", playerId);
        AddParameter(count, "@playerName", playerName == null ? null : "%" + playerName.ToLowerInvariant() + "%");
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload, action_count, turns, status, completed_at FROM fy_match_history WHERE (@status IS NULL OR status = @status) AND (@player IS NULL OR left_player_id = @player OR right_player_id = @player) AND (@playerName IS NULL OR LOWER(left_player_name) LIKE @playerName OR LOWER(right_player_name) LIKE @playerName) ORDER BY COALESCE(completed_at, started_at) DESC, match_id DESC LIMIT @limit OFFSET @offset";
        AddParameter(command, "@status", status);
        AddParameter(command, "@player", playerId);
        AddParameter(command, "@playerName", playerName == null ? null : "%" + playerName.ToLowerInvariant() + "%");
        AddParameter(command, "@limit", pageSize);
        AddParameter(command, "@offset", (page - 1) * pageSize);
        var rows = new List<MatchHistorySummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            if (document == null) continue;
            rows.Add(document.Summary with
            {
                ActionCount = reader.GetInt32(1),
                Turns = reader.GetInt32(2),
                Status = reader.GetString(3),
                CompletedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
            });
        }
        return (rows, total);
    }

    public async Task<JsonObject> GetStorageInfoAsync(CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return new JsonObject { ["configured"] = false, ["provider"] = "unconfigured", ["matchCount"] = 0, ["actionCount"] = 0, ["dataBytes"] = 0 };
        if (settings.Provider == "local")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documents = ReadLocalDocuments();
            var fasterBytes = appData.List(LocalPrefix).Sum(item => (long)System.Text.Encoding.UTF8.GetByteCount(item.Value));
            var archiveBytes = Directory.Exists(ReplayDir)
                ? Directory.EnumerateFiles(ReplayDir, "replay-*.json")
                    .Sum(path => new FileInfo(path).Length)
                : 0L;
            return new JsonObject
            {
                ["configured"] = true,
                ["provider"] = "本地模式（FASTER + 磁盘归档）",
                ["location"] = "FASTER: " + LocalPrefix + "; archive: " + ReplayDir,
                ["matchCount"] = documents.Count,
                ["actionCount"] = documents.Sum(document => document.Actions.Count),
                ["dataBytes"] = fasterBytes + archiveBytes
            };
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(action_count), 0), COALESCE((SELECT SUM(LENGTH(payload)) FROM fy_match_history), 0) + COALESCE((SELECT SUM(LENGTH(payload)) FROM fy_match_events), 0) FROM fy_match_history";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new JsonObject
        {
            ["configured"] = true,
            ["provider"] = settings.Provider,
            ["location"] = settings.Database + ".fy_match_history / fy_match_events",
            ["matchCount"] = Convert.ToInt64(reader.GetValue(0)),
            ["actionCount"] = Convert.ToInt64(reader.GetValue(1)),
            ["dataBytes"] = Convert.ToInt64(reader.GetValue(2))
        };
    }

    public async Task<bool> DeleteAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return false;
        if (settings.Provider == "local")
        {
            var deleted = false;
            await WithFileLockAsync(matchId, () =>
            {
                deleted = appData.Delete(LocalKey(matchId));
                return Task.CompletedTask;
            });
            deleted |= DeleteReplayArchive(matchId);
            return deleted;
        }
        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fy_match_history WHERE match_id = @match";
        AddParameter(command, "@match", matchId);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        removed |= DeleteReplayArchive(matchId);
        return removed;
    }

    public async Task<MatchHistoryDocument?> GetAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return null;
        MatchHistoryDocument? document;
        if (settings.Provider == "local")
        {
            document = await ReadLocalAsync(matchId, cancellationToken);
            if (document == null) return null;
            return document;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        var actionCount = 0;
        var turns = 0;
        string? status = null;
        DateTime? completedAt = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload, action_count, turns, status, completed_at FROM fy_match_history WHERE match_id = @match";
            AddParameter(command, "@match", matchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            document = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchHistoryDocument);
            actionCount = reader.GetInt32(1);
            turns = reader.GetInt32(2);
            status = reader.GetString(3);
            completedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
        }
        if (document == null) return null;
        document.Summary = document.Summary with { ActionCount = actionCount, Turns = turns, Status = status!, CompletedAt = completedAt };
        return document;
    }

    public async Task<bool> ExistsAsync(int matchId, CancellationToken cancellationToken = default)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return false;
        if (settings.Provider == "local")
        {
            return appData.Get(LocalKey(matchId)) != null || ReadReplayArchive(matchId) != null;
        }
        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
        AddParameter(command, "@match", matchId);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task<MatchHistoryActionPage?> GetActionsAsync(int matchId, int afterActionId, int limit = 500, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var settings = databaseConfiguration.Load();
        if (settings == null) return null;
        List<MatchAction> actions;
        if (settings.Provider == "local")
        {
            var document = await ReadLocalAsync(matchId, cancellationToken);
            if (document == null) return null;
            actions = document.Actions.Where(x => x.ActionId > afterActionId).OrderBy(x => x.ActionId).Take(limit + 1).ToList();
        }
        else
        {
            await EnsureSchemaAsync(settings, cancellationToken);
            await using var connection = CreateConnection(settings);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM fy_match_events WHERE match_id = @match AND action_id > @after ORDER BY action_id LIMIT @limit";
            AddParameter(command, "@match", matchId);
            AddParameter(command, "@after", afterActionId);
            AddParameter(command, "@limit", limit + 1);
            actions = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var action = JsonSerializer.Deserialize(reader.GetString(0), FyJsonContext.Default.MatchAction);
                if (action != null) actions.Add(action);
            }
            if (actions.Count == 0)
            {
                await using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT 1 FROM fy_match_history WHERE match_id = @match";
                AddParameter(exists, "@match", matchId);
                if (await exists.ExecuteScalarAsync(cancellationToken) == null) return null;
            }
        }
        var hasMore = actions.Count > limit;
        if (hasMore) actions.RemoveAt(actions.Count - 1);
        var next = actions.Count == 0 ? afterActionId : actions[^1].ActionId;
        return new MatchHistoryActionPage(matchId, next, hasMore, actions);
    }

    public async Task<int> CleanupAsync(MatchRetentionSettings? options = null, CancellationToken cancellationToken = default)
    {
        options = NormalizeRetention(options ?? GetRetentionSettings());
        var settings = databaseConfiguration.Load();
        if (settings == null) return 0;
        if (settings.Provider == "local")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var docs = ReadLocalDocuments()
                .Where(document => document.Summary.Status is "completed" or "aborted")
                .Select(document => (Id: document.Summary.MatchId, Summary: document.Summary))
                .ToList();
            var expired = SelectExpired(docs, options);
            foreach (var item in expired)
            {
                appData.Delete(LocalKey(item.Id));
                DeleteReplayArchive(item.Id, rebuildIndex: false);
            }
            if (expired.Count > 0)
            {
                lock (ReplayFileLock) WriteReplayIndex();
            }
            return expired.Count;
        }

        await EnsureSchemaAsync(settings, cancellationToken);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        List<int> ids;
        await using (var command = connection.CreateCommand())
        {
            if (options.Mode == "count")
            {
                command.CommandText = settings.Provider == "postgresql"
                    ? "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') ORDER BY completed_at DESC, match_id DESC OFFSET @keep"
                    : "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') ORDER BY completed_at DESC, match_id DESC LIMIT 18446744073709551615 OFFSET @keep";
                AddParameter(command, "@keep", options.KeepCount);
            }
            else
            {
                command.CommandText = "SELECT match_id FROM fy_match_history WHERE status IN ('completed', 'aborted') AND completed_at < @cutoff ORDER BY completed_at";
                AddParameter(command, "@cutoff", DateTime.UtcNow.AddDays(-options.KeepDays));
            }
            ids = [];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        }
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM fy_match_history WHERE match_id = @match";
            AddParameter(command, "@match", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            DeleteReplayArchive(id, rebuildIndex: false);
        }
        if (ids.Count > 0)
        {
            lock (ReplayFileLock) WriteReplayIndex();
        }
        return ids.Count;
    }

    public static MatchRetentionSettings DefaultRetention { get; } = new("age", 1000, 30, 0, 3);

    public static MatchRetentionSettings NormalizeRetention(MatchRetentionSettings value) => new(
        value.Mode == "count" ? "count" : "age",
        Math.Clamp(value.KeepCount, 1, 1_000_000),
        Math.Clamp(value.KeepDays, 1, 3650),
        Math.Clamp(value.CleanupDayUtc, 0, 6),
        Math.Clamp(value.CleanupHourUtc, 0, 23));

    private async Task WriteDocumentAsync(MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        var settings = databaseConfiguration.Load();
        if (settings == null) return;
        if (settings.Provider == "local")
        {
            await WithFileLockAsync(document.Summary.MatchId, async () =>
            {
                var existing = await ReadLocalAsync(document.Summary.MatchId, cancellationToken);
                if (existing != null) document.Actions = existing.Actions;
                await WriteLocalAsync(document, cancellationToken);
            });
            return;
        }
        await UpsertHeaderAsync(settings, document, cancellationToken);
    }

    private async Task UpsertHeaderAsync(UserDatabaseSettings settings, MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(settings, cancellationToken);
        var summary = document.Summary;
        var payload = JsonSerializer.Serialize(new MatchHistoryDocument
        {
            Summary = document.Summary,
            StartingInfo = document.StartingInfo,
            Actions = []
        }, FyJsonContext.Default.MatchHistoryDocument);
        await using var connection = CreateConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = settings.Provider == "postgresql"
            ? "INSERT INTO fy_match_history (match_id, status, match_type, started_at, completed_at, left_player_id, left_player_name, left_player_tag, right_player_id, right_player_name, right_player_tag, turns, winner_side, action_count, payload) VALUES (@match, @status, @type, @started, @completed, @left_id, @left_name, @left_tag, @right_id, @right_name, @right_tag, @turns, @winner, (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), @payload) ON CONFLICT (match_id) DO UPDATE SET status = EXCLUDED.status, match_type = EXCLUDED.match_type, started_at = EXCLUDED.started_at, completed_at = EXCLUDED.completed_at, left_player_id = EXCLUDED.left_player_id, left_player_name = EXCLUDED.left_player_name, left_player_tag = EXCLUDED.left_player_tag, right_player_id = EXCLUDED.right_player_id, right_player_name = EXCLUDED.right_player_name, right_player_tag = EXCLUDED.right_player_tag, turns = EXCLUDED.turns, winner_side = EXCLUDED.winner_side, action_count = (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), payload = EXCLUDED.payload"
            : "INSERT INTO fy_match_history (match_id, status, match_type, started_at, completed_at, left_player_id, left_player_name, left_player_tag, right_player_id, right_player_name, right_player_tag, turns, winner_side, action_count, payload) VALUES (@match, @status, @type, @started, @completed, @left_id, @left_name, @left_tag, @right_id, @right_name, @right_tag, @turns, @winner, (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), @payload) ON DUPLICATE KEY UPDATE status = VALUES(status), match_type = VALUES(match_type), started_at = VALUES(started_at), completed_at = VALUES(completed_at), left_player_id = VALUES(left_player_id), left_player_name = VALUES(left_player_name), left_player_tag = VALUES(left_player_tag), right_player_id = VALUES(right_player_id), right_player_name = VALUES(right_player_name), right_player_tag = VALUES(right_player_tag), turns = VALUES(turns), winner_side = VALUES(winner_side), action_count = (SELECT COUNT(*) FROM fy_match_events WHERE match_id = @match), payload = VALUES(payload)";
        AddParameter(command, "@match", summary.MatchId);
        AddParameter(command, "@status", summary.Status);
        AddParameter(command, "@type", summary.MatchType);
        AddParameter(command, "@started", summary.StartedAt);
        AddParameter(command, "@completed", summary.CompletedAt is null ? DBNull.Value : summary.CompletedAt.Value);
        AddParameter(command, "@left_id", summary.LeftPlayerId);
        AddParameter(command, "@left_name", summary.LeftPlayerName);
        AddParameter(command, "@left_tag", summary.LeftPlayerTag);
        AddParameter(command, "@right_id", summary.RightPlayerId);
        AddParameter(command, "@right_name", summary.RightPlayerName);
        AddParameter(command, "@right_tag", summary.RightPlayerTag);
        AddParameter(command, "@turns", summary.Turns);
        AddParameter(command, "@winner", summary.WinnerSide is null ? DBNull.Value : summary.WinnerSide);
        AddParameter(command, "@payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(UserDatabaseSettings settings, CancellationToken cancellationToken)
    {
        var schemaKey = $"{settings.Provider}|{settings.Host}|{settings.Port}|{settings.Database}|{settings.Username}";
        if (_schemaReady && _schemaKey == schemaKey) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady && _schemaKey == schemaKey) return;
            await using var connection = CreateConnection(settings);
            await connection.OpenAsync(cancellationToken);
            var statements = settings.Provider == "postgresql"
                ? new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_match_history (match_id INTEGER PRIMARY KEY, status VARCHAR(16) NOT NULL, match_type VARCHAR(32) NOT NULL, started_at TIMESTAMPTZ NOT NULL, completed_at TIMESTAMPTZ NULL, left_player_id INTEGER NOT NULL, left_player_name VARCHAR(255) NOT NULL, left_player_tag VARCHAR(16) NOT NULL, right_player_id INTEGER NOT NULL, right_player_name VARCHAR(255) NOT NULL, right_player_tag VARCHAR(16) NOT NULL, turns INTEGER NOT NULL DEFAULT 0, winner_side VARCHAR(8) NULL, action_count INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_recent ON fy_match_history (status, completed_at DESC)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_left_player ON fy_match_history (left_player_id, completed_at DESC)",
                    "CREATE INDEX IF NOT EXISTS ix_fy_match_history_right_player ON fy_match_history (right_player_id, completed_at DESC)",
                    "CREATE TABLE IF NOT EXISTS fy_match_events (match_id INTEGER NOT NULL REFERENCES fy_match_history(match_id) ON DELETE CASCADE, action_id INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (match_id, action_id))"
                }
                : new[]
                {
                    "CREATE TABLE IF NOT EXISTS fy_match_history (match_id INT PRIMARY KEY, status VARCHAR(16) NOT NULL, match_type VARCHAR(32) NOT NULL, started_at DATETIME(6) NOT NULL, completed_at DATETIME(6) NULL, left_player_id INT NOT NULL, left_player_name VARCHAR(255) NOT NULL, left_player_tag VARCHAR(16) NOT NULL, right_player_id INT NOT NULL, right_player_name VARCHAR(255) NOT NULL, right_player_tag VARCHAR(16) NOT NULL, turns INT NOT NULL DEFAULT 0, winner_side VARCHAR(8) NULL, action_count INT NOT NULL DEFAULT 0, payload LONGTEXT NOT NULL, INDEX ix_fy_match_history_recent (status, completed_at), INDEX ix_fy_match_history_left_player (left_player_id, completed_at), INDEX ix_fy_match_history_right_player (right_player_id, completed_at)) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin",
                    "CREATE TABLE IF NOT EXISTS fy_match_events (match_id INT NOT NULL, action_id INT NOT NULL, payload LONGTEXT NOT NULL, PRIMARY KEY (match_id, action_id), CONSTRAINT fk_fy_match_events_history FOREIGN KEY (match_id) REFERENCES fy_match_history(match_id) ON DELETE CASCADE) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin"
                };
            foreach (var sql in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            _schemaReady = true;
            _schemaKey = schemaKey;
        }
        finally { _schemaLock.Release(); }
    }

    private static MatchHistoryDocument BuildDocument(MatchInfo match, MatchStartingInfo? startingInfo) => new()
    {
        Summary = ToSummary(match, match.StartedAtUtc ?? DateTime.UtcNow),
        StartingInfo = startingInfo,
        Actions = match.SnapshotActions()
    };

    public static MatchHistorySummary CreateSummary(MatchInfo match) => ToSummary(match, match.StartedAtUtc ?? DateTime.UtcNow);

    private static MatchHistorySummary ToSummary(MatchInfo match, DateTime startedAt)
    {
        var data = match.MatchStartingInfo?.MatchAndStartingData.StartingData;
        var leftId = match.Left?.PlayerId ?? data?.PlayerIdLeft ?? 0;
        var rightId = match.Right?.PlayerId ?? data?.PlayerIdRight ?? 0;
        var mode = match.Ex.StartsWith("battle_code", StringComparison.Ordinal) ? "code" : string.IsNullOrWhiteSpace(match.Ex) ? "classic" : match.Ex;
        return new MatchHistorySummary(match.MatchId, mode, match.IsAborted ? "aborted" : match.IsCompleted ? "completed" : "active", startedAt,
            match.CompletedAtUtc, leftId, data?.LeftPlayerName ?? $"#{leftId}", data?.LeftPlayerTag ?? "0000",
            rightId, data?.RightPlayerName ?? $"#{rightId}", data?.RightPlayerTag ?? "0000", match.Turns,
            match.MatchActions.Count, match.WinnerSide);
    }

    private static List<(int Id, MatchHistorySummary Summary)> SelectExpired(List<(int Id, MatchHistorySummary Summary)> items, MatchRetentionSettings options)
    {
        var completed = items.Where(x => x.Summary.Status is "completed" or "aborted").OrderByDescending(x => x.Summary.CompletedAt).ToList();
        return options.Mode == "count"
            ? completed.Skip(options.KeepCount).ToList()
            : completed.Where(x => x.Summary.CompletedAt < DateTime.UtcNow.AddDays(-options.KeepDays)).ToList();
    }

    private static string LocalKey(int matchId) => LocalPrefix + matchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private List<MatchHistoryDocument> ReadLocalDocuments()
    {
        var documents = new Dictionary<int, MatchHistoryDocument>();
        foreach (var item in appData.List(LocalPrefix))
        {
            if (DeserializeLocal(item.Value) is { } document)
            {
                documents[document.Summary.MatchId] = document;
            }
        }

        foreach (var archive in ReadReplayArchives())
        {
            if (!documents.TryGetValue(archive.Summary.MatchId, out var current)
                || IsMoreComplete(archive, current))
            {
                documents[archive.Summary.MatchId] = archive;
            }
        }

        return documents.Values.ToList();
    }

    private Task<MatchHistoryDocument?> ReadLocalAsync(int matchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var local = DeserializeLocal(appData.Get(LocalKey(matchId)));
        var archive = ReadReplayArchive(matchId);
        return Task.FromResult(local is null
            ? archive
            : archive is not null && IsMoreComplete(archive, local) ? archive : local);
    }
    private Task WriteLocalAsync(MatchHistoryDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        appData.Set(LocalKey(document.Summary.MatchId), JsonSerializer.Serialize(document, FyJsonContext.Default.MatchHistoryDocument));
        return Task.CompletedTask;
    }

    private static MatchHistoryDocument? DeserializeLocal(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize(payload, FyJsonContext.Default.MatchHistoryDocument); }
        catch (JsonException) { return null; }
    }

    private static bool IsMoreComplete(MatchHistoryDocument candidate, MatchHistoryDocument current)
    {
        if (candidate.Actions.Count != current.Actions.Count)
        {
            return candidate.Actions.Count > current.Actions.Count;
        }

        static int StatusRank(string status) => status switch
        {
            "completed" => 2,
            "aborted" => 1,
            _ => 0
        };

        return StatusRank(candidate.Summary.Status) > StatusRank(current.Summary.Status)
            || candidate.Summary.CompletedAt > current.Summary.CompletedAt;
    }

    private async Task HydrateLocalReplayArchivesAsync(CancellationToken cancellationToken)
    {
        if (!appData.IsReady) return;

        foreach (var archive in ReadReplayArchives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WithFileLockAsync(archive.Summary.MatchId, async () =>
            {
                var current = DeserializeLocal(appData.Get(LocalKey(archive.Summary.MatchId)));
                if (current is null || IsMoreComplete(archive, current))
                {
                    await WriteLocalAsync(archive, cancellationToken);
                }
            });
        }
    }

    // ==================================================================
    //  回放文件归档（2026-10-04）
    // ==================================================================
    //
    // ## 为什么需要它
    //
    // 上面的持久化走「数据库 / 本地 FASTER KV」，而归档文件是独立的耐久副本。
    // 本地模式的 FASTER 可能只保留当前进程检查点里的部分历史；启动时由
    // `HydrateLocalReplayArchivesAsync` 将磁盘归档补回，读取路径也会合并两者。
    // 因此服务重启后仍能通过 `/replays` 读取已经落盘的完整对局。
    //
    // ## 写什么
    //
    // 直接落**两个与 HTTP API 逐字节同形**的文件，外加一个索引：
    // <code>
    //   &lt;dir&gt;/replay-&lt;id&gt;.json         = `GET /replays/{id}`          （{summary, starting_info}）
    //   &lt;dir&gt;/replay-&lt;id&gt;.actions.json = `GET /replays/{id}/actions`  （{match_id, next_action_id, has_more, actions}）
    //   &lt;dir&gt;/replays-index.json       = `GET /replays`               （{matches:[…]}）
    // </code>
    // ⇒ 与 `tools/ServerBridgeTest --audit-replay` 吃的格式**完全一致**，可以直接审计、零转换。
    //
    // ## 目录
    //
    // `KLINK_REPLAY_DIR` 环境变量优先；否则 `./data/replays`（相对服务进程工作目录，
    // 与 `UserDatabaseConfigurationService` 的 `./data/user-database.json` 同一约定）
    // ⇒ 实测落到 `rel/data/fyserver/data/replays/`，与既有的 `data/live-replays/` 并列。
    //
    // ## 安全
    //
    // **任何异常都吞掉**：归档是尽力而为，绝不能影响对局。
    private static readonly string ReplayDir =
        Environment.GetEnvironmentVariable("KLINK_REPLAY_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(".", "data", "replays");

    private static readonly object ReplayFileLock = new();

    private static IEnumerable<MatchHistoryDocument> ReadReplayArchives()
    {
        var result = new List<MatchHistoryDocument>();
        if (!Directory.Exists(ReplayDir)) return result;

        foreach (string path in Directory.EnumerateFiles(ReplayDir, "replay-*.json"))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".actions.json", StringComparison.Ordinal)
                || !fileName.StartsWith("replay-", StringComparison.Ordinal))
            {
                continue;
            }

            string idText = fileName.Substring(
                "replay-".Length,
                fileName.Length - "replay-".Length - ".json".Length);
            if (!int.TryParse(idText, out int matchId)) continue;

            try
            {
                if (LoadReplayArchive(matchId) is { } document)
                {
                    result.Add(document);
                }
            }
            catch (JsonException)
            {
                // A damaged archive must not hide the remaining match history.
            }
            catch (IOException)
            {
                // The archive is best effort while a match is being written.
            }
        }

        return result;
    }

    private static MatchHistoryDocument? ReadReplayArchive(int matchId)
    {
        try
        {
            return LoadReplayArchive(matchId);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static MatchHistoryDocument? LoadReplayArchive(int matchId)
    {
        string detailPath = Path.Combine(ReplayDir, $"replay-{matchId}.json");
        if (!File.Exists(detailPath)) return null;

        var detail = JsonSerializer.Deserialize(
            File.ReadAllText(detailPath), FyJsonContext.Default.MatchHistoryDetailResponse);
        if (detail is null) return null;

        var actions = new List<MatchAction>();
        string actionsPath = Path.Combine(ReplayDir, $"replay-{matchId}.actions.json");
        if (File.Exists(actionsPath))
        {
            var page = JsonSerializer.Deserialize(
                File.ReadAllText(actionsPath), FyJsonContext.Default.MatchHistoryActionPage);
            if (page is not null) actions = page.Actions;
        }

        var summary = detail.Summary with
        {
            ActionCount = actions.Count > 0 ? actions.Count : detail.Summary.ActionCount
        };
        return new MatchHistoryDocument
        {
            Summary = summary,
            StartingInfo = detail.StartingInfo,
            Actions = actions
        };
    }

    private static bool DeleteReplayArchive(int matchId, bool rebuildIndex = true)
    {
        try
        {
            lock (ReplayFileLock)
            {
                bool deleted = false;
                foreach (string path in new[]
                {
                    Path.Combine(ReplayDir, $"replay-{matchId}.json"),
                    Path.Combine(ReplayDir, $"replay-{matchId}.actions.json")
                })
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    deleted = true;
                }

                if (deleted && rebuildIndex) WriteReplayIndex();
                return deleted;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[replay-archive] 删除失败 match={matchId} dir={ReplayDir}: {ex.Message}");
            return false;
        }
    }

    private static void ArchiveReplay(MatchHistoryDocument document, bool rebuildIndex)
    {
        try
        {
            lock (ReplayFileLock)
            {
                Directory.CreateDirectory(ReplayDir);
                int id = document.Summary.MatchId;
                File.WriteAllText(
                    Path.Combine(ReplayDir, $"replay-{id}.json"),
                    JsonSerializer.Serialize(
                        new MatchHistoryDetailResponse(document.Summary, document.StartingInfo),
                        FyJsonContext.Default.MatchHistoryDetailResponse));
                int nextActionId = document.Actions.Count == 0 ? 0 : document.Actions[^1].ActionId;
                File.WriteAllText(
                    Path.Combine(ReplayDir, $"replay-{id}.actions.json"),
                    JsonSerializer.Serialize(
                        new MatchHistoryActionPage(id, nextActionId, false, document.Actions),
                        FyJsonContext.Default.MatchHistoryActionPage));
                if (rebuildIndex) WriteReplayIndex();
            }
        }
        catch (Exception ex)
        {
            // 尽力而为：磁盘满 / 权限 / 并发 —— 都不该影响对局。
            // 但**不能静默**：否则"回放没落盘"会被误判成"没触发"。写到 stderr，服务端日志里能看见。
            Console.Error.WriteLine($"[replay-archive] 归档失败 match={document.Summary.MatchId} dir={ReplayDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// 重建 `replays-index.json`（形状 = `GET /replays` 的 `{matches:[…]}`）。
    /// 只在「开局快照 / 结束」时重建，不在每次追加动作时重建（那是每回合都发生的高频路径）。
    /// </summary>
    private static void WriteReplayIndex()
    {
        var summaries = new List<MatchHistorySummary>();
        foreach (string path in Directory.EnumerateFiles(ReplayDir, "replay-*.json"))
        {
            if (path.EndsWith(".actions.json", StringComparison.Ordinal)) continue;
            try
            {
                var detail = JsonSerializer.Deserialize(
                    File.ReadAllText(path), FyJsonContext.Default.MatchHistoryDetailResponse);
                if (detail != null) summaries.Add(detail.Summary);
            }
            catch
            {
                // 单个坏文件不该让整个索引写不出来。
            }
        }
        summaries.Sort((a, b) => (b.CompletedAt ?? b.StartedAt).CompareTo(a.CompletedAt ?? a.StartedAt));
        File.WriteAllText(
            Path.Combine(ReplayDir, "replays-index.json"),
            JsonSerializer.Serialize(new MatchHistoryListResponse(summaries), FyJsonContext.Default.MatchHistoryListResponse));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> LocalLocks = new();    private static async Task WithFileLockAsync(int id, Func<Task> action)
    {
        var gate = LocalLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { await action(); } finally { gate.Release(); }
    }

    private static DbConnection CreateConnection(UserDatabaseSettings settings)
    {
        if (settings.Provider == "postgresql")
        {
            var builder = new NpgsqlConnectionStringBuilder { Host = settings.Host, Port = settings.Port, Database = settings.Database, Username = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? SslMode.Require : SslMode.Prefer, Timeout = 8, CommandTimeout = 15, Pooling = true };
            return new NpgsqlConnection(builder.ConnectionString);
        }
        if (settings.Provider == "mysql")
        {
            var builder = new MySqlConnectionStringBuilder { Server = settings.Host, Port = (uint)settings.Port, Database = settings.Database, UserID = settings.Username, Password = settings.Password, SslMode = settings.RequireSsl ? MySqlSslMode.Required : MySqlSslMode.Preferred, ConnectionTimeout = 8, DefaultCommandTimeout = 15, Pooling = true };
            return new MySqlConnection(builder.ConnectionString);
        }
        throw new InvalidOperationException("不支持的对局历史数据库类型");
    }
    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed class MatchHistoryCleanupWorker(MatchHistoryService history, ILogger<MatchHistoryCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastRunForSchedule = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = history.GetRetentionSettings();
            var now = DateTime.UtcNow;
            var daysSince = ((int)now.DayOfWeek - settings.CleanupDayUtc + 7) % 7;
            var scheduled = now.Date.AddDays(-daysSince).AddHours(settings.CleanupHourUtc);
            if (now >= scheduled && (lastRunForSchedule == null || scheduled > lastRunForSchedule))
            {
                try
                {
                    var removed = await history.CleanupAsync(settings, stoppingToken);
                    logger.LogInformation("Weekly match-history cleanup removed {Count} matches", removed);
                    lastRunForSchedule = scheduled;
                }
                catch (Exception ex) { logger.LogError(ex, "Weekly match-history cleanup failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
