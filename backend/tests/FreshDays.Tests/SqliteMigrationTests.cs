using FreshDays.Migration;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace FreshDays.Tests;

public sealed class SqliteMigrationTests
{
    private const string Subscriber = "U-existing";
    private const string RetryKey = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public async Task TransferPreservesListOrderUnknownFieldsAndLineHistory(string encoding)
    {
        await using var sqlite = await LegacyDatabase.Create(encoding);
        await sqlite.Execute("""
            INSERT INTO lists VALUES
                ('inventory', '[{"id":"b","name":"梨","expiresAt":null,"legacy":{"x":[1,null]}},{"id":"a","name":"Apple"}]'),
                ('shopping', '[{"id":"a","name":"Rice","completed":false,"quantity":2}]');
            INSERT INTO subscribers VALUES ('U-existing');
            INSERT INTO notifications VALUES
                ('2026-11-21', 'U-existing', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 1),
                ('2026-11-22', 'U-previous', 'aaaaaaaa-bbbb-cccc-dddd-ffff00000000', 0);
            """);
        await using var postgres = new PostgresTestDatabase();
        await postgres.Initialize();

        var result = await Run(sqlite, postgres);

        Assert.Equal(0, result.Code);
        Assert.Equal("", result.Error);
        using var report = JsonDocument.Parse(result.Output);
        AssertGroup(report, "inventory", 2);
        AssertGroup(report, "shopping", 1);
        AssertGroup(report, "subscribers", 1);
        AssertGroup(report, "notifications", 2);
        await using var source = NpgsqlDataSource.Create(postgres.IsolatedConnectionString);
        var store = new PostgresStore(source);
        using var inventoryDocument = JsonDocument.Parse(await store.ReadList("inventory"));
        var inventory = inventoryDocument.RootElement;
        Assert.Equal("b", inventory[0].GetProperty("id").GetString());
        Assert.Equal("梨", inventory[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, inventory[0].GetProperty("expiresAt").ValueKind);
        Assert.Equal(1, inventory[0].GetProperty("legacy").GetProperty("x")[0].GetInt32());
        Assert.Equal("a", inventory[1].GetProperty("id").GetString());
        Assert.False(inventory[1].TryGetProperty("expiresAt", out _));
        using var shoppingDocument = JsonDocument.Parse(await store.ReadList("shopping"));
        var shopping = shoppingDocument.RootElement;
        Assert.False(shopping[0].GetProperty("completed").GetBoolean());
        Assert.Equal(2, shopping[0].GetProperty("quantity").GetInt32());
        Assert.Equal([Subscriber], await store.Subscribers());
        Assert.Equal((RetryKey, true), await store.Notification(new DateOnly(2026, 11, 21), Subscriber));
        Assert.Equal(("aaaaaaaa-bbbb-cccc-dddd-ffff00000000", false),
            await store.Notification(new DateOnly(2026, 11, 22), "U-previous"));
    }

    [Fact]
    public async Task DryRunReportsProposedRowsAndRollsBackSchemaThenRerunsWithoutChangingOwnership()
    {
        await using var sqlite = await LegacyDatabase.Create();
        await sqlite.Execute("""
            INSERT INTO lists VALUES ('inventory', '[{"id":"milk","name":"Milk"}]');
            INSERT INTO subscribers VALUES ('U-existing');
            """);
        await using var postgres = new PostgresTestDatabase();
        await postgres.Initialize();
        await using var source = NpgsqlDataSource.Create(postgres.IsolatedConnectionString);

        var dryRun = await Run(sqlite, postgres, "--dry-run");
        Assert.Equal(0, dryRun.Code);
        using var proposed = JsonDocument.Parse(dryRun.Output);
        Assert.True(proposed.RootElement.GetProperty("dryRun").GetBoolean());
        AssertGroup(proposed, "inventory", 1);
        AssertGroup(proposed, "shopping", 0);
        AssertGroup(proposed, "subscribers", 1);
        AssertGroup(proposed, "notifications", 0);
        Assert.Equal(DBNull.Value, await Execute(source, "SELECT to_regclass('households')::text"));

        var first = await Run(sqlite, postgres);
        Assert.Equal(0, first.Code);
        await Execute(source, """
            INSERT INTO households VALUES ('00000000-0000-0000-0000-000000000002', 'Other household');
            INSERT INTO inventory_items (household_id, id, name, position)
            VALUES ('00000000-0000-0000-0000-000000000002', 'milk', 'Other milk', 1);
            """);
        var second = await Run(sqlite, postgres);
        Assert.Equal(0, second.Code);
        Assert.Equal(first.Output, second.Output);
        using var committed = JsonDocument.Parse(second.Output);
        Assert.False(committed.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(PostgresStore.LegacyHouseholdId, committed.RootElement.GetProperty("householdId").GetGuid());
        Assert.Equal(
            "00000000-0000-0000-0000-000000000001=Milk,00000000-0000-0000-0000-000000000002=Other milk",
            await Execute(source, "SELECT string_agg(household_id::text || '=' || name, ',' ORDER BY household_id) FROM inventory_items"));
    }

    [Fact]
    public async Task TransferReportsEveryInvalidGroupAndLeavesDestinationUntouched()
    {
        await using var sqlite = await LegacyDatabase.Create();
        await sqlite.Execute("""
            INSERT INTO lists(rowid, name, data) VALUES
                (1, 'inventory', '[{"id":"dup","name":"Banana"},{"id":"dup","name":"Papaya"},{"id":"bad-date","name":"Fish","expiresAt":"2026-02-30"},{}]'),
                (2, 'shopping', '[{"id":"a","name":"Rice","unknown":{"key":1,"key":2}}]'),
                (3, 'unrecognized', '{}');
            INSERT INTO subscribers(rowid, user_id) VALUES (7, ''), (8, x'FF'), (9, 'NULL' || char(0)), (14, CAST(x'FF' AS TEXT));
            INSERT INTO notifications(rowid, day, user_id, retry_key, sent) VALUES
                (10, '2026-02-30', 'U-existing', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 1),
                (11, '2026-11-22', 'U-existing', 'invalid', 0),
                (12, '2026-11-23', 'U-existing', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 2),
                (13, '2026-11-24', 'U-existing', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 'invalid');
            """);
        await using var postgres = new PostgresTestDatabase();
        await postgres.Initialize();
        await using var source = NpgsqlDataSource.Create(postgres.IsolatedConnectionString);

        var result = await Run(sqlite, postgres);

        Assert.Equal(1, result.Code);
        Assert.Equal("", result.Output);
        Assert.Contains("lists rowid 1 (inventory), item 1 (id dup)", result.Error);
        Assert.Contains("lists rowid 1 (inventory), item 2 (id dup)", result.Error);
        Assert.Contains("lists rowid 1 (inventory), item 3 (id bad-date)", result.Error);
        Assert.Contains("lists rowid 1 (inventory), item 4", result.Error);
        Assert.Contains("lists rowid 2 (shopping)", result.Error);
        Assert.Contains("lists rowid 3 (unrecognized)", result.Error);
        Assert.Contains("subscribers rowid 7", result.Error);
        Assert.Contains("subscribers rowid 8", result.Error);
        Assert.Contains("subscribers rowid 9", result.Error);
        Assert.Contains("subscribers rowid 14", result.Error);
        Assert.Contains("notifications rowid 10 (day 2026-02-30)", result.Error);
        Assert.Contains("notifications rowid 11 (day 2026-11-22)", result.Error);
        Assert.Contains("notifications rowid 12 (day 2026-11-23)", result.Error);
        Assert.Contains("notifications rowid 13 (day 2026-11-24)", result.Error);
        Assert.Equal(DBNull.Value, await Execute(source, "SELECT to_regclass('households')::text"));
    }

    [Fact]
    public async Task ExistingConflictingContentFailsVerificationAndRollsBackOtherGroups()
    {
        await using var sqlite = await LegacyDatabase.Create();
        await sqlite.Execute("""
            INSERT INTO lists VALUES ('inventory', '[{"id":"milk","name":"Milk"}]');
            INSERT INTO subscribers VALUES ('U-existing');
            """);
        await using var postgres = new PostgresTestDatabase();
        await postgres.Initialize();
        await using var source = NpgsqlDataSource.Create(postgres.IsolatedConnectionString);
        await PostgresMigrations.Apply(source);
        var store = new PostgresStore(source);
        await store.ReplaceList("inventory", """[{"id":"milk","name":"Edited milk"}]""");

        var result = await Run(sqlite, postgres);

        Assert.Equal(1, result.Code);
        Assert.Contains("rolled back", result.Error);
        using var report = JsonDocument.Parse(result.Output);
        var inventory = report.RootElement.GetProperty("groups").GetProperty("inventory");
        Assert.Equal(1, inventory.GetProperty("source").GetInt32());
        Assert.Equal(1, inventory.GetProperty("destination").GetInt32());
        Assert.False(inventory.GetProperty("contentMatches").GetBoolean());
        Assert.Contains("Edited milk", await store.ReadList("inventory"));
        Assert.Empty(await store.Subscribers());
    }

    [Fact]
    public async Task LostDestinationConnectionRollsBackEveryGroupAndSameBackupCanBeRetried()
    {
        await using var sqlite = await LegacyDatabase.Create();
        await sqlite.Execute("""
            INSERT INTO lists VALUES ('shopping', '[{"id":"rice","name":"Rice","completed":true}]');
            INSERT INTO subscribers VALUES ('U-existing');
            INSERT INTO notifications VALUES ('2026-11-21', 'U-existing', 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', 1);
            """);
        await using var postgres = new PostgresTestDatabase();
        await postgres.Initialize();
        await using var source = NpgsqlDataSource.Create(postgres.IsolatedConnectionString);
        await PostgresMigrations.Apply(source);
        await Execute(source, """
            CREATE FUNCTION pause_notification() RETURNS trigger AS $$
            BEGIN PERFORM pg_sleep(60); RETURN NEW; END
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER pause_notification BEFORE INSERT ON line_notifications
            FOR EACH ROW EXECUTE FUNCTION pause_notification();
            """);
        var interruptedConnection = new NpgsqlConnectionStringBuilder(postgres.IsolatedConnectionString)
        {
            ApplicationName = $"migration_{Guid.NewGuid():N}",
        };

        using var output = new StringWriter();
        using var error = new StringWriter();
        var migration = MigrationCommand.Run([sqlite.Path], interruptedConnection.ConnectionString, output, error);
        await TerminatePausedTransfer(source, interruptedConnection.ApplicationName!);
        Assert.Equal(1, await migration);
        Assert.Equal("", output.ToString());
        Assert.Contains("PostgreSQL transfer did not complete", error.ToString());

        var store = new PostgresStore(source);
        Assert.Equal("[]", await store.ReadList("shopping"));
        Assert.Empty(await store.Subscribers());
        Assert.Equal(0L, await Execute(source, "SELECT count(*) FROM line_notifications"));
        await Execute(source, "DROP TRIGGER pause_notification ON line_notifications");
        var retry = await Run(sqlite, postgres);
        Assert.Equal(0, retry.Code);
        using var report = JsonDocument.Parse(retry.Output);
        AssertGroup(report, "shopping", 1);
        AssertGroup(report, "subscribers", 1);
        AssertGroup(report, "notifications", 1);
        Assert.Equal((RetryKey, true), await store.Notification(new DateOnly(2026, 11, 21), Subscriber));
    }

    [Fact]
    public async Task MissingBackupDoesNotCreateAnEmptyDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"missing-freshdays-{Guid.NewGuid():N}.db");
        using var error = new StringWriter();

        Assert.Equal(1, await MigrationCommand.Run([path], "Host=localhost", TextWriter.Null, error));
        Assert.Contains("Cannot read the SQLite backup", error.ToString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task InvalidDestinationConfigurationProducesAnOperatorError()
    {
        await using var sqlite = await LegacyDatabase.Create();
        using var error = new StringWriter();

        Assert.Equal(2, await MigrationCommand.Run([sqlite.Path], "invalid-connection-string", TextWriter.Null, error));
        Assert.Contains("PostgreSQL connection configuration", error.ToString());
    }

    [Fact]
    public async Task UsageAndMissingConnectionProduceOperatorErrors()
    {
        Assert.Equal(2, await MigrationCommand.Main([]));
        using var error = new StringWriter();

        Assert.Equal(2, await MigrationCommand.Run(["backup.db", "--invalid"], null, TextWriter.Null, error));
        Assert.Contains("Usage:", error.ToString());
        Assert.Equal(2, await MigrationCommand.Run(["backup.db"], null, TextWriter.Null, error));
        Assert.Contains("ConnectionStrings__FreshDays is required", error.ToString());
    }

    private static async Task TerminatePausedTransfer(NpgsqlDataSource source, string name)
    {
        await using var command = source.CreateCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE application_name = @name AND wait_event = 'PgSleep'
            """);
        command.Parameters.AddWithValue("name", name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await command.ExecuteScalarAsync(timeout.Token) is not true)
            await Task.Delay(25, timeout.Token);
    }

    private static async Task<object?> Execute(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }


    private static void AssertGroup(JsonDocument report, string name, int count)
    {
        var group = report.RootElement.GetProperty("groups").GetProperty(name);
        Assert.Equal(count, group.GetProperty("source").GetInt32());
        Assert.Equal(count, group.GetProperty("destination").GetInt32());
        Assert.True(group.GetProperty("contentMatches").GetBoolean());
    }

    private static async Task<(int Code, string Output, string Error)> Run(
        LegacyDatabase sqlite, PostgresTestDatabase postgres, params string[] extra)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await MigrationCommand.Run([sqlite.Path, .. extra], postgres.IsolatedConnectionString, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private sealed class LegacyDatabase(string path) : IAsyncDisposable
    {
        public string Path => path;

        public static async Task<LegacyDatabase> Create(string encoding = "UTF-8")
        {
            var database = new LegacyDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freshdays-{Guid.NewGuid():N}.db"));
            await database.Execute($"""
                PRAGMA encoding = '{encoding}';
                CREATE TABLE lists (name TEXT PRIMARY KEY, data TEXT NOT NULL);
                CREATE TABLE subscribers (user_id TEXT PRIMARY KEY);
                CREATE TABLE notifications (day TEXT NOT NULL, user_id TEXT NOT NULL,
                    retry_key TEXT NOT NULL, sent INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (day, user_id));
                """);
            return database;
        }

        public async Task Execute(string sql)
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
