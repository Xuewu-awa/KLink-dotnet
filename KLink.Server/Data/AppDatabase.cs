using System.Security.Cryptography;
using System.Text;
using KLink.Server.Model;
using Microsoft.Data.Sqlite;

namespace KLink.Server.Data;

/// <summary>
/// SQLite 持久化（AppDatabase）。
/// 表结构与 Java 版完全一致，可直接复用现有 kards_server.db 数据文件。
/// </summary>
public sealed class AppDatabase
{
    public static readonly string[] EquipmentColumns =
    {
        "avatar",
        "item_Germany", "item_Britain", "item_Soviet", "item_USA", "item_Japan",
        "emote_Start_Germany", "emote_End_Germany", "emote_Good_Germany", "emote_Bad_Germany",
        "emote_Cheer_Germany", "emote_Taunt_Germany", "emote_Poke_Germany", "emote_Proclaim_Germany",
        "emote_Start_Britain", "emote_End_Britain", "emote_Good_Britain", "emote_Bad_Britain",
        "emote_Cheer_Britain", "emote_Taunt_Britain", "emote_Poke_Britain", "emote_Proclaim_Britain",
        "emote_Start_Soviet", "emote_End_Soviet", "emote_Good_Soviet", "emote_Bad_Soviet",
        "emote_Cheer_Soviet", "emote_Taunt_Soviet", "emote_Poke_Soviet", "emote_Proclaim_Soviet",
        "emote_Start_USA", "emote_End_USA", "emote_Good_USA", "emote_Bad_USA",
        "emote_Cheer_USA", "emote_Taunt_USA", "emote_Poke_USA", "emote_Proclaim_USA",
        "emote_Start_Japan", "emote_End_Japan", "emote_Good_Japan", "emote_Bad_Japan",
        "emote_Cheer_Japan", "emote_Taunt_Japan", "emote_Poke_Japan", "emote_Proclaim_Japan"
    };

    private readonly string _connectionString;
    private readonly object _lock = new();
    private readonly Random _random = new();

    public AppDatabase(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        lock (_lock)
        {
            using var conn = Open();
            var userSql = new StringBuilder();
            userSql.Append("CREATE TABLE IF NOT EXISTS users (")
                .Append("id INTEGER PRIMARY KEY AUTOINCREMENT,")
                .Append("username TEXT NOT NULL UNIQUE,")
                .Append("password TEXT,")
                .Append("player_name TEXT NOT NULL,")
                .Append("player_tag INTEGER NOT NULL,")
                .Append("player_jwt TEXT,")
                .Append("is_online INTEGER NOT NULL DEFAULT 0,")
                .Append("created_at TEXT NOT NULL");
            foreach (var column in EquipmentColumns)
                userSql.Append(',').Append(column).Append(" TEXT");
            userSql.Append(')');
            Exec(conn, userSql.ToString());
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_users_player_name_tag ON users(player_name, player_tag)");
            Exec(conn, "CREATE TABLE IF NOT EXISTS decks ("
                + "id INTEGER PRIMARY KEY AUTOINCREMENT,"
                + "user_id INTEGER NOT NULL,"
                + "name TEXT,"
                + "card_back TEXT,"
                + "main_faction TEXT,"
                + "ally_faction TEXT,"
                + "deck_code TEXT,"
                + "favorite INTEGER NOT NULL DEFAULT 0,"
                + "last_played TEXT,"
                + "create_date TEXT,"
                + "modify_date TEXT,"
                + "FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE)");
            Exec(conn, "CREATE TABLE IF NOT EXISTS server_settings (key TEXT PRIMARY KEY, value TEXT)");
            Exec(conn, "CREATE TABLE IF NOT EXISTS ban_entries (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER, ip TEXT, device TEXT, expires_at INTEGER)");
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ==================== 用户 ====================

    public UserRecord? GetOrCreateUser(string username, string password)
    {
        lock (_lock)
        {
            using var conn = Open();
            var existing = FindUserByUsername(conn, username);
            if (existing != null)
            {
                // 验证密码（即使是本地服务器也应校验，防止冒名登录）
                if (!VerifyPassword(existing.Password, password))
                    return null;
                return existing;
            }
            // 新用户：player_name 固定 <anon>，tag 分配随机唯一值（避免唯一索引冲突）
            int tag = FindFreeTag(conn, "<anon>");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO users (username, password, player_name, player_tag, player_jwt, created_at) "
                + "VALUES ($u, $p, '<anon>', $t, '', $time)";
            cmd.Parameters.AddWithValue("$u", username);
            cmd.Parameters.AddWithValue("$p", HashPassword(password ?? ""));
            cmd.Parameters.AddWithValue("$t", tag);
            cmd.Parameters.AddWithValue("$time", Util.TimeUtil.NowIso());
            cmd.ExecuteNonQuery();
            return FindUserById(conn, LastInsertRowId(conn));
        }
    }

    public UserRecord? FindUserById(int id) { lock (_lock) { using var c = Open(); return FindUserById(c, id); } }
    public UserRecord? FindUserByUsername(string username) { lock (_lock) { using var c = Open(); return FindUserByUsername(c, username); } }
    public UserRecord? FindUserByJwt(string jwt) { lock (_lock) { using var c = Open(); return FindUserByJwt(c, jwt); } }

    public List<UserRecord> ListUsers()
    {
        lock (_lock)
        {
            using var conn = Open();
            var users = new List<UserRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM users ORDER BY id ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                users.Add(ReadUser(reader));
            return users;
        }
    }

    public void UpdateUserJwt(int id, string jwt)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET player_jwt=$j WHERE id=$id";
            cmd.Parameters.AddWithValue("$j", jwt);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public UserRecord? SetPlayerName(int userId, string name)
    {
        lock (_lock)
        {
            using var conn = Open();
            int tag = FindFreeTag(conn, name);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET player_name=$n, player_tag=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", tag);
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
            return FindUserById(conn, userId);
        }
    }

    public void SetOnline(int userId, bool online)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET is_online=$o WHERE id=$id";
            cmd.Parameters.AddWithValue("$o", online ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateEquipment(int userId, string slot, string faction, string itemId)
    {
        string? column = EquipmentColumn(slot, faction);
        if (column is null) return;
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE users SET {column}=$v WHERE id=$id";
            cmd.Parameters.AddWithValue("$v", itemId);
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.ExecuteNonQuery();
        }
    }

    // ==================== 卡组 ====================

    public DeckRecord? CreateDeck(int userId, string name, string mainFaction, string allyFaction, string deckCode)
    {
        lock (_lock)
        {
            using var conn = Open();
            string now = Util.TimeUtil.NowIso();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO decks (user_id, name, main_faction, ally_faction, card_back, deck_code, favorite, last_played, create_date, modify_date) "
                + "VALUES ($u, $n, $m, $a, '', $c, 0, $t, $t, $t)";
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$m", mainFaction);
            cmd.Parameters.AddWithValue("$a", allyFaction);
            cmd.Parameters.AddWithValue("$c", deckCode);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
            return FindDeckById(conn, LastInsertRowId(conn));
        }
    }

    public List<DeckRecord> ListDecks(int userId)
    {
        lock (_lock)
        {
            using var conn = Open();
            var decks = new List<DeckRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM decks WHERE user_id=$id ORDER BY id ASC";
            cmd.Parameters.AddWithValue("$id", userId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                decks.Add(ReadDeck(reader));
            return decks;
        }
    }

    public DeckRecord? FindDeckById(int deckId) { lock (_lock) { using var c = Open(); return FindDeckById(c, deckId); } }
    public bool HasDeck(int deckId) => FindDeckById(deckId) != null;
    public DeckRecord? FindDeckForUser(int userId, int deckId) { lock (_lock) { using var c = Open(); return FindDeckForUser(c, userId, deckId); } }

    public void RenameDeck(int deckId, string name) => UpdateDeckField(deckId, "name", name);
    public void UpdateDeckCode(int deckId, string deckCode) => UpdateDeckField(deckId, "deck_code", deckCode);
    public void UpdateCardBack(int deckId, string cardBack) => UpdateDeckField(deckId, "card_back", cardBack);

    public void ToggleFavorite(int deckId)
    {
        lock (_lock)
        {
            using var conn = Open();
            var deck = FindDeckById(conn, deckId);
            if (deck is null) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE decks SET favorite=$f, modify_date=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$f", deck.Favorite ? 0 : 1);
            cmd.Parameters.AddWithValue("$t", Util.TimeUtil.NowIso());
            cmd.Parameters.AddWithValue("$id", deckId);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteDeck(int deckId)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM decks WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", deckId);
            cmd.ExecuteNonQuery();
        }
    }

    private void UpdateDeckField(int deckId, string field, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE decks SET {field}=$v, modify_date=$t WHERE id=$id";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$t", Util.TimeUtil.NowIso());
            cmd.Parameters.AddWithValue("$id", deckId);
            cmd.ExecuteNonQuery();
        }
    }

    // ==================== 密码 ====================

    /// <summary>SHA-256 + 随机盐，格式 "base64salt:base64hash"（与 Java 版一致）。</summary>
    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Sha256WithSalt(salt, password);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    /// <summary>验证密码是否匹配存储的哈希。兼容旧版明文密码（无 ':' 分隔符时直接比较）。</summary>
    private static bool VerifyPassword(string? stored, string? password)
    {
        stored ??= "";
        password ??= "";
        int sep = stored.IndexOf(':');
        if (sep < 0)
            return stored == password; // 旧版明文密码
        try
        {
            byte[] salt = Convert.FromBase64String(stored[..sep]);
            string expected = Convert.ToBase64String(Sha256WithSalt(salt, password));
            return expected == stored[(sep + 1)..];
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Sha256WithSalt(byte[] salt, string password)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        using var sha = SHA256.Create();
        sha.TransformBlock(salt, 0, salt.Length, null, 0);
        sha.TransformFinalBlock(passwordBytes, 0, passwordBytes.Length);
        return sha.Hash!;
    }

    // ==================== 内部查询 ====================

    private static UserRecord? FindUserById(SqliteConnection conn, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    private static UserRecord? FindUserByUsername(SqliteConnection conn, string username)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username=$u";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    private static UserRecord? FindUserByJwt(SqliteConnection conn, string jwt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE player_jwt=$j";
        cmd.Parameters.AddWithValue("$j", jwt);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    private static int LastInsertRowId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static DeckRecord? FindDeckById(SqliteConnection conn, int deckId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM decks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", deckId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDeck(reader) : null;
    }

    private static DeckRecord? FindDeckForUser(SqliteConnection conn, int userId, int deckId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM decks WHERE id=$id AND user_id=$u";
        cmd.Parameters.AddWithValue("$id", deckId);
        cmd.Parameters.AddWithValue("$u", userId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDeck(reader) : null;
    }

    private int FindFreeTag(SqliteConnection conn, string playerName)
    {
        for (int i = 0; i < 40; i++)
        {
            int tag = _random.Next(10000);
            if (!PlayerTagExists(conn, playerName, tag))
                return tag;
        }
        for (int tag = 0; tag < 10000; tag++)
        {
            if (!PlayerTagExists(conn, playerName, tag))
                return tag;
        }
        return _random.Next(10000);
    }

    private static bool PlayerTagExists(SqliteConnection conn, string playerName, int tag)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM users WHERE player_name=$n AND player_tag=$t";
        cmd.Parameters.AddWithValue("$n", playerName);
        cmd.Parameters.AddWithValue("$t", tag);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static UserRecord ReadUser(SqliteDataReader reader)
    {
        var user = new UserRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Username = reader.GetString(reader.GetOrdinal("username")),
            Password = reader.IsDBNull(reader.GetOrdinal("password")) ? null : reader.GetString(reader.GetOrdinal("password")),
            PlayerName = reader.GetString(reader.GetOrdinal("player_name")),
            PlayerTag = reader.GetInt32(reader.GetOrdinal("player_tag")),
            PlayerJwt = reader.IsDBNull(reader.GetOrdinal("player_jwt")) ? "" : reader.GetString(reader.GetOrdinal("player_jwt")),
            IsOnline = reader.GetInt32(reader.GetOrdinal("is_online")) != 0,
        };
        foreach (var column in EquipmentColumns)
        {
            int index = reader.GetOrdinal(column);
            user.Equipment[column] = reader.IsDBNull(index) ? null : reader.GetString(index);
        }
        return user;
    }

    private static DeckRecord ReadDeck(SqliteDataReader reader)
    {
        return new DeckRecord
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            UserId = reader.GetInt32(reader.GetOrdinal("user_id")),
            Name = reader.IsDBNull(reader.GetOrdinal("name")) ? "" : reader.GetString(reader.GetOrdinal("name")),
            CardBack = reader.IsDBNull(reader.GetOrdinal("card_back")) ? "" : reader.GetString(reader.GetOrdinal("card_back")),
            MainFaction = reader.IsDBNull(reader.GetOrdinal("main_faction")) ? "" : reader.GetString(reader.GetOrdinal("main_faction")),
            AllyFaction = reader.IsDBNull(reader.GetOrdinal("ally_faction")) ? "" : reader.GetString(reader.GetOrdinal("ally_faction")),
            DeckCode = reader.IsDBNull(reader.GetOrdinal("deck_code")) ? "" : reader.GetString(reader.GetOrdinal("deck_code")),
            Favorite = reader.GetInt32(reader.GetOrdinal("favorite")) != 0,
            LastPlayed = reader.IsDBNull(reader.GetOrdinal("last_played")) ? "" : reader.GetString(reader.GetOrdinal("last_played")),
            CreateDate = reader.IsDBNull(reader.GetOrdinal("create_date")) ? "" : reader.GetString(reader.GetOrdinal("create_date")),
            ModifyDate = reader.IsDBNull(reader.GetOrdinal("modify_date")) ? "" : reader.GetString(reader.GetOrdinal("modify_date")),
        };
    }

    // ==================== 装备列映射 ====================

    public static string? EquipmentColumn(string slot, string? faction)
    {
        if (slot == "avatar") return "avatar";
        if (faction is null) return null;
        string cleanFaction = faction.Trim();
        if (slot == "item_1") return "item_" + cleanFaction;
        string? prefix = slot switch
        {
            "emote_1" => "emote_Start_",
            "emote_2" => "emote_End_",
            "emote_3" => "emote_Good_",
            "emote_4" => "emote_Bad_",
            "emote_5" => "emote_Cheer_",
            "emote_6" => "emote_Taunt_",
            "emote_7" => "emote_Poke_",
            "emote_8" => "emote_Proclaim_",
            _ => null,
        };
        return prefix is null ? null : prefix + cleanFaction;
    }

    public static string SlotFromColumn(string column)
    {
        if (column == "avatar") return "avatar";
        if (column.StartsWith("item_")) return "item_1";
        if (column.StartsWith("emote_Start_")) return "emote_1";
        if (column.StartsWith("emote_End_")) return "emote_2";
        if (column.StartsWith("emote_Good_")) return "emote_3";
        if (column.StartsWith("emote_Bad_")) return "emote_4";
        if (column.StartsWith("emote_Cheer_")) return "emote_5";
        if (column.StartsWith("emote_Taunt_")) return "emote_6";
        if (column.StartsWith("emote_Poke_")) return "emote_7";
        if (column.StartsWith("emote_Proclaim_")) return "emote_8";
        return "";
    }

    public static string FactionFromColumn(string column)
    {
        if (column == "avatar") return "";
        int index = column.LastIndexOf('_');
        return index >= 0 ? column[(index + 1)..] : "";
    }
}
