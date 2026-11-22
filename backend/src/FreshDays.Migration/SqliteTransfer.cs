using Microsoft.Data.Sqlite;
using Npgsql;
using System.Text;

namespace FreshDays.Migration;

internal static partial class SqliteTransfer
{
    public static async Task<(bool ContentMatches, string Report)> Run(string path, string connectionString, bool dryRun)
    {
        var sqliteString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var sqlite = new SqliteConnection(sqliteString.ToString());
        await sqlite.OpenAsync();
        // A single read transaction keeps all source groups on the same SQLite snapshot.
        using var snapshot = sqlite.BeginTransaction(deferred: true);
        await using var getEncoding = new SqliteCommand("PRAGMA encoding;", sqlite, snapshot);
        var encodingName = (string)(await getEncoding.ExecuteScalarAsync())!;
        var encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        sqlite.CreateFunction<byte[], string?>("migration_text", bytes => DecodeText(bytes, encoding), isDeterministic: true);

        var lists = await ReadJson(sqlite, snapshot, ReadLists);
        var subscribers = await ReadJson(sqlite, snapshot, ReadSubscribers);
        var notifications = await ReadJson(sqlite, snapshot, ReadNotifications);

        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var destination = await source.OpenConnectionAsync();
        await using var transfer = await destination.BeginTransactionAsync();
        await using (var stage = new NpgsqlCommand(StageSource, destination, transfer))
        {
            stage.Parameters.AddWithValue("lists", lists);
            stage.Parameters.AddWithValue("subscribers", subscribers);
            stage.Parameters.AddWithValue("notifications", notifications);
            await stage.ExecuteNonQueryAsync();
        }

        await using (var validate = new NpgsqlCommand(ValidateSource, destination, transfer))
        {
            var invalid = (string)(await validate.ExecuteScalarAsync())!;
            if (invalid.Length > 0)
                throw new InvalidDataException($"Invalid or ambiguous SQLite rows:{Environment.NewLine}{invalid}");
        }

        await PostgresMigrations.Apply(transfer);
        await using (var insert = new NpgsqlCommand(TransferRows, destination, transfer))
        {
            insert.Parameters.AddWithValue("household", PostgresStore.LegacyHouseholdId);
            await insert.ExecuteNonQueryAsync();
        }

        bool matches;
        string report;
        await using (var verify = new NpgsqlCommand(VerifyRows, destination, transfer))
        {
            verify.Parameters.AddWithValue("household", PostgresStore.LegacyHouseholdId);
            verify.Parameters.AddWithValue("dry_run", dryRun);
            await using var result = await verify.ExecuteReaderAsync();
            await result.ReadAsync();
            matches = result.GetBoolean(0);
            report = result.GetString(1);
        }

        if (matches && !dryRun)
            await transfer.CommitAsync();

        return (matches, report);
    }

    private static async Task<string> ReadJson(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = new SqliteCommand(sql, connection, transaction);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string? DecodeText(byte[] bytes, Encoding encoding)
    {
        try
        {
            var value = encoding.GetString(bytes);
            // PostgreSQL cannot store embedded NULs.
            return value.Contains('\0') ? null : value;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // Decode raw bytes so SQLite's text conversion cannot replace invalid characters.
    // Missing, malformed and non-text values become null while their rowids remain available.
    private const string ReadLists = """
        SELECT json_group_array(json_object(
            'id', rowid,
            'name', CASE WHEN typeof(name) = 'text' THEN migration_text(CAST(name AS BLOB)) END,
            'data', CASE WHEN typeof(data) = 'text' THEN migration_text(CAST(data AS BLOB)) END))
        FROM lists;
        """;

    private const string ReadSubscribers = """
        SELECT json_group_array(json_object(
            'id', rowid,
            'user_id', CASE WHEN typeof(user_id) = 'text' THEN migration_text(CAST(user_id AS BLOB)) END))
        FROM subscribers;
        """;

    private const string ReadNotifications = """
        SELECT json_group_array(json_object(
            'id', rowid,
            'day', CASE WHEN typeof(day) = 'text' THEN migration_text(CAST(day AS BLOB)) END,
            'user_id', CASE WHEN typeof(user_id) = 'text' THEN migration_text(CAST(user_id AS BLOB)) END,
            'retry_key', CASE WHEN typeof(retry_key) = 'text' THEN migration_text(CAST(retry_key AS BLOB)) END,
            'sent', CASE WHEN typeof(sent) = 'integer' THEN sent END))
        FROM notifications;
        """;
}
