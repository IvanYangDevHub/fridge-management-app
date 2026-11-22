using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FreshDays.Tests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string schema = $"test_{Guid.NewGuid():N}";
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("FRESHDAYS_TEST_POSTGRES")
        ?? throw new InvalidOperationException("Set FRESHDAYS_TEST_POSTGRES to a disposable PostgreSQL database.");

    private readonly NpgsqlDataSource administrator = NpgsqlDataSource.Create(ConnectionString);

    public async Task Initialize()
    {
        await using var command = administrator.CreateCommand($"CREATE SCHEMA {schema}");
        await command.ExecuteNonQueryAsync();
    }

    public WebApplicationFactory<Program> CreateFactory(IHttpClientFactory? lineClient = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FreshDays"] = new NpgsqlConnectionStringBuilder(ConnectionString)
                    {
                        SearchPath = schema,
                        Pooling = false,
                    }.ConnectionString,
                    ["HouseholdCode"] = "test-code",
                    ["Line:ChannelSecret"] = "secret",
                    ["Line:AccessToken"] = "token",
                    ["DisableWorker"] = "true",
                }))
                .ConfigureTestServices(services =>
                {
                    if (lineClient is not null)
                        services.AddSingleton(lineClient);
                }));

    public async ValueTask DisposeAsync()
    {
        await using var command = administrator.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await command.ExecuteNonQueryAsync();
        await administrator.DisposeAsync();
    }
}
