using Microsoft.Data.Sqlite;

namespace FreshDays;

public sealed class SqliteStore(string path)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private async Task<SqliteConnection> Open()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task Initialize()
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS lists (name TEXT PRIMARY KEY, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS subscribers (user_id TEXT PRIMARY KEY); CREATE TABLE IF NOT EXISTS notifications (day TEXT NOT NULL, user_id TEXT NOT NULL, retry_key TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (day, user_id));";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task<string> ReadInventory() => ReadList("inventory");
    public Task ReplaceInventory(string json) => ReplaceList("inventory", json);
    public async Task<string> ReadList(string name)
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM lists WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return (string?)await cmd.ExecuteScalarAsync() ?? "[]";
    }
    public async Task ReplaceList(string name, string json)
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO lists(name,data) VALUES($name,$data) ON CONFLICT(name) DO UPDATE SET data=excluded.data";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$data", json);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task Subscribe(string userId) => await ChangeSubscriber(userId, true);
    public async Task Unsubscribe(string userId) => await ChangeSubscriber(userId, false);
    private async Task ChangeSubscriber(string userId, bool add)
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = add ? "INSERT OR IGNORE INTO subscribers(user_id) VALUES($id)" : "DELETE FROM subscribers WHERE user_id=$id";
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<IReadOnlyList<string>> Subscribers()
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM subscribers ORDER BY user_id";
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }
    public async Task<(string Key, bool Sent)> Notification(string day, string userId)
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO notifications(day,user_id,retry_key) VALUES($day,$id,$key); SELECT retry_key,sent FROM notifications WHERE day=$day AND user_id=$id;";
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$key", Guid.NewGuid().ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetInt64(1) != 0);
    }
    public async Task MarkSent(string day, string userId)
    {
        await using var connection = await Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE notifications SET sent=1 WHERE day=$day AND user_id=$id";
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync();
    }
}
