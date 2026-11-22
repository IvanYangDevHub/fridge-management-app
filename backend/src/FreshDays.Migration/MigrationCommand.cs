using Microsoft.Data.Sqlite;
using Npgsql;

namespace FreshDays.Migration;

public static class MigrationCommand
{
    public static Task<int> Main(string[] args) =>
        Run(args, Environment.GetEnvironmentVariable("ConnectionStrings__FreshDays"), Console.Out, Console.Error);

    public static async Task<int> Run(string[] args, string? connectionString, TextWriter output, TextWriter error)
    {
        if (args is not [_] and not [_, "--dry-run"])
        {
            error.WriteLine("Usage: FreshDays.Migration <sqlite-backup-path> [--dry-run]");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error.WriteLine("ConnectionStrings__FreshDays is required.");
            return 2;
        }

        bool contentMatches;
        string report;
        try
        {
            (contentMatches, report) = await SqliteTransfer.Run(args[0], connectionString, args.Length == 2);
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (SqliteException)
        {
            error.WriteLine("Cannot read the SQLite backup or its legacy lists, subscribers and notifications tables.");
            return 1;
        }
        catch (NpgsqlException)
        {
            error.WriteLine("The PostgreSQL transfer did not complete. Check the destination and rerun with the same backup.");
            return 1;
        }
        catch (ArgumentException)
        {
            error.WriteLine("Check the SQLite backup path and PostgreSQL connection configuration.");
            return 2;
        }

        output.WriteLine(report);
        if (contentMatches)
            return 0;

        error.WriteLine("Destination content differs from the SQLite source. The transfer was rolled back.");
        return 1;
    }
}
