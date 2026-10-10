using Microsoft.Data.Sqlite;
using KLink.Server.Model;

namespace KLink.Server.Data;

/// <summary>
/// Small SQLite persistence layer used by the local server.
/// The upstream tree references this type from the HTTP/WS handlers but did not
/// include the implementation, so keep the schema deliberately boring and
/// compatible with the handler DTOs.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public AppDatabase(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(AppContext.BaseDirectory, "data", "klink.db");
        if (!string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = string.Equals(path, ":memory:", StringComparison.OrdinalIgnoreCase)
                ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE,
                password TEXT NOT NULL DEFAULT '',
                player_name TEXT NOT NULL DEFAULT '',
                player_tag INTEGER NOT NULL DEFAULT 0,
                player_jwt TEXT NOT NULL DEFAULT '',
                is_online INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS decks(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                card_back TEXT NOT NULL DEFAULT '',
                main_faction TEXT NOT NULL DEFAULT '',
                ally_faction TEXT NOT NULL DEFAULT '',
                deck_code TEXT NOT NULL DEFAULT '',
                favorite INTEGER NOT NULL DEFAULT 0,
                last_played TEXT NOT NULL DEFAULT '',
                create_date TEXT NOT NULL DEFAULT '',
                modify_date TEXT NOT NULL DEFAULT '',
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS decks_user_idx ON decks(user_id);
            CREATE TABLE IF NOT EXISTS equipment(
                user_id INTEGER NOT NULL,
                item_key TEXT NOT NULL,
                item_id TEXT,
                PRIMARY KEY(user_id, item_key),
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public UserRecord? GetOrCreateUser(string username, string password)
    {
        username = (username ?? "guest").Trim();
        if (username.Length == 0) username = "guest";
        lock (_lock)
        {
            using var db = Open();
            using var find = db.CreateCommand();
            find.CommandText = "SELECT id, password FROM users WHERE username = $username";
            find.Parameters.AddWithValue("$username", username);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                int id = reader.GetInt32(0);
                string stored = reader.GetString(1);
                if (stored.Length > 0 && stored != (password ?? "")) return null;
                reader.Close();
                if (stored.Length == 0 && !string.IsNullOrEmpty(password))
                {
                    using var setPassword = db.CreateCommand();
                    setPassword.CommandText = "UPDATE users SET password = $password WHERE id = $id";
                    setPassword.Parameters.AddWithValue("$password", password);
                    setPassword.Parameters.AddWithValue("$id", id);
                    setPassword.ExecuteNonQuery();
                }
                return FindUserById(db, id);
            }
            reader.Close();
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO users(username,password,player_name,player_tag) VALUES($u,$p,$n,$tag); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$u", username);
            insert.Parameters.AddWithValue("$p", password ?? "");
            insert.Parameters.AddWithValue("$n", username);
            insert.Parameters.AddWithValue("$tag", StableTag(username));
            int idNew = Convert.ToInt32(insert.ExecuteScalar());
            return FindUserById(db, idNew);
        }
    }

    public UserRecord? FindUserById(int id)
    {
        lock (_lock)
        {
            using var db = Open();
            return FindUserById(db, id);
        }
    }

    private static UserRecord? FindUserById(SqliteConnection db, int id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,username,password,player_name,player_tag,player_jwt,is_online FROM users WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var user = new UserRecord
        {
            Id = r.GetInt32(0), Username = r.GetString(1), Password = r.GetString(2),
            PlayerName = r.GetString(3), PlayerTag = r.GetInt32(4), PlayerJwt = r.GetString(5),
            IsOnline = r.GetInt32(6) != 0,
        };
        r.Close();
        using var eq = db.CreateCommand();
        eq.CommandText = "SELECT item_key,item_id FROM equipment WHERE user_id=$id";
        eq.Parameters.AddWithValue("$id", id);
        using var er = eq.ExecuteReader();
        while (er.Read()) user.Equipment[er.GetString(0)] = er.IsDBNull(1) ? null : er.GetString(1);
        return user;
    }

    public UserRecord? FindUserByJwt(string jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM users WHERE player_jwt=$jwt";
            cmd.Parameters.AddWithValue("$jwt", jwt);
            object? value = cmd.ExecuteScalar();
            return value is null || value is DBNull ? null : FindUserById(db, Convert.ToInt32(value));
        }
    }

    public IReadOnlyList<UserRecord> ListUsers()
    {
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM users ORDER BY id";
            using var r = cmd.ExecuteReader();
            var ids = new List<int>();
            while (r.Read()) ids.Add(r.GetInt32(0));
            r.Close();
            return ids.Select(id => FindUserById(db, id)!).Where(x => x is not null).ToArray();
        }
    }

    public UserRecord? SetPlayerName(int id, string name) { Exec("UPDATE users SET player_name=$v WHERE id=$id", ("$v", name), ("$id", id)); return FindUserById(id); }
    public void UpdateUserJwt(int id, string jwt) => Exec("UPDATE users SET player_jwt=$v WHERE id=$id", ("$v", jwt), ("$id", id));
    public void SetOnline(int id, bool online) => Exec("UPDATE users SET is_online=$v WHERE id=$id", ("$v", online ? 1 : 0), ("$id", id));

    public List<DeckRecord> ListDecks(int userId)
    {
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM decks WHERE user_id=$u ORDER BY favorite DESC, id";
            cmd.Parameters.AddWithValue("$u", userId);
            using var r = cmd.ExecuteReader();
            var ids = new List<int>(); while (r.Read()) ids.Add(r.GetInt32(0)); r.Close();
            return ids.Select(id => FindDeckById(db, id)).Where(x => x is not null).Cast<DeckRecord>().ToList();
        }
    }

    public DeckRecord? FindDeckById(int id)
    {
        lock (_lock)
        {
            using var db = Open();
            return FindDeckById(db, id);
        }
    }

    public DeckRecord? FindDeckForUser(int userId, int id)
    {
        lock (_lock)
        {
            using var db = Open();
            var d = FindDeckById(db, id);
            return d?.UserId == userId ? d : null;
        }
    }

    private static DeckRecord? FindDeckById(SqliteConnection db, int id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,user_id,name,card_back,main_faction,ally_faction,deck_code,favorite,last_played,create_date,modify_date FROM decks WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new DeckRecord { Id=r.GetInt32(0), UserId=r.GetInt32(1), Name=r.GetString(2), CardBack=r.GetString(3), MainFaction=r.GetString(4), AllyFaction=r.GetString(5), DeckCode=r.GetString(6), Favorite=r.GetInt32(7)!=0, LastPlayed=r.GetString(8), CreateDate=r.GetString(9), ModifyDate=r.GetString(10) };
    }

    public DeckRecord? CreateDeck(int userId, string name, string mainFaction, string allyFaction, string code)
    {
        string now = DateTime.UtcNow.ToString("O");
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO decks(user_id,name,main_faction,ally_faction,deck_code,create_date,modify_date) VALUES($u,$n,$m,$a,$c,$t,$t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(name) ? "New Deck" : name); cmd.Parameters.AddWithValue("$m", mainFaction ?? ""); cmd.Parameters.AddWithValue("$a", allyFaction ?? ""); cmd.Parameters.AddWithValue("$c", code ?? ""); cmd.Parameters.AddWithValue("$t", now);
            return FindDeckById(db, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    public void DeleteDeck(int id) => Exec("DELETE FROM decks WHERE id=$id", ("$id", id));
    public void UpdateDeckCode(int id, string code) => Exec("UPDATE decks SET deck_code=$v,modify_date=$t WHERE id=$id", ("$v", code), ("$t", DateTime.UtcNow.ToString("O")), ("$id", id));
    public void UpdateCardBack(int id, string value) => Exec("UPDATE decks SET card_back=$v,modify_date=$t WHERE id=$id", ("$v", value), ("$t", DateTime.UtcNow.ToString("O")), ("$id", id));
    public void RenameDeck(int id, string value) => Exec("UPDATE decks SET name=$v,modify_date=$t WHERE id=$id", ("$v", value), ("$t", DateTime.UtcNow.ToString("O")), ("$id", id));
    public void ToggleFavorite(int id) => Exec("UPDATE decks SET favorite=CASE favorite WHEN 0 THEN 1 ELSE 0 END,modify_date=$t WHERE id=$id", ("$t", DateTime.UtcNow.ToString("O")), ("$id", id));

    public void UpdateEquipment(int userId, string slot, string faction, string itemId)
    {
        string? key = EquipmentColumn(slot, faction); if (key is null) return;
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO equipment(user_id,item_key,item_id) VALUES($u,$k,$v) ON CONFLICT(user_id,item_key) DO UPDATE SET item_id=excluded.item_id";
            cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", string.IsNullOrEmpty(itemId) ? DBNull.Value : itemId); cmd.ExecuteNonQuery();
        }
    }

    public static string? EquipmentColumn(string? slot, string? faction)
    {
        if (string.IsNullOrWhiteSpace(slot)) return null;
        if (slot.Equals("avatar", StringComparison.OrdinalIgnoreCase)) return "avatar";
        if (string.IsNullOrWhiteSpace(faction)) return slot;
        return slot + ":" + faction;
    }

    private void Exec(string sql, params (string Name, object Value)[] values)
    {
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static int StableTag(string username)
    {
        uint hash = 2166136261;
        foreach (char c in username) hash = (hash ^ c) * 16777619;
        return (int)(hash % 900000) + 100000;
    }
}
