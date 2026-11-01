using Xunit;
using InventoryService.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryService.Tests;

/// Creates one throwaway SQL Server database for the whole test run and drops it afterwards.

/// These tests exist to prove things the database does - CHECK constraints, row locks,
/// transaction rollback, duplicate-key serialization - so they run against a real SQL Server.
/// The EF Core in-memory provider enforces none of it, so a suite built on it could pass while
/// the real system oversells.
///
/// Safety: the server comes from the same ConnectionStrings:InventoryDb user secret as the
/// application, but the database name is replaced with a generated one
/// ("ECommerceInventoryDb_Test_&lt;guid&gt;"). Nothing here ever opens, alters or drops the
/// development database or any other database on the instance; the only database touched is
/// the one this fixture created.
public sealed class SqlServerDatabaseFixture : IAsyncLifetime
{
    private const string DevelopmentDatabaseName = "ECommerceInventoryDb";

    public string ConnectionString { get; private set; } = string.Empty;

    public string DatabaseName { get; private set; } = string.Empty;

    /// Set when the environment cannot host the tests, so they can skip with a reason.
    public string? SkipReason { get; private set; }

    private string _masterConnectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var configured = new ConfigurationBuilder()
            .AddUserSecrets<SqlServerDatabaseFixture>(optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetConnectionString("InventoryDb");

        if (string.IsNullOrWhiteSpace(configured))
        {
            SkipReason = "ConnectionStrings:InventoryDb is not configured (user secrets or environment).";
            return;
        }

        DatabaseName = $"ECommerceInventoryDb_Test_{Guid.NewGuid():N}";

        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = DatabaseName };
        ConnectionString = builder.ConnectionString;

        _masterConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }
            .ConnectionString;

        // Guard against ever pointing the destructive parts of this fixture at a real database.
        if (string.Equals(DatabaseName, DevelopmentDatabaseName, StringComparison.OrdinalIgnoreCase)
            || !DatabaseName.StartsWith("ECommerceInventoryDb_Test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to run tests against database '{DatabaseName}': not a generated test database.");
        }

        try
        {
            await using var connection = new SqlConnection(_masterConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            SkipReason =
                $"Could not create an isolated test database (SQL error {ex.Number}). " +
                "The SQL Server login needs permission to create databases.";
            return;
        }

        // Schema comes from the reviewed migration, never from EnsureCreated, so the tests
        // exercise the same DDL that was applied to the development database.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public InventoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new InventoryDbContext(options);
    }

    public InventoryService.Application.InventoryOperations CreateOperations(InventoryDbContext db) =>
        new(db, NullLogger<InventoryService.Application.InventoryOperations>.Instance);

    /// Removes all rows so each test starts from a known state, without touching the schema.
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ReservationItems");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Reservations");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM InventoryItems");
    }

    public async Task SeedProductAsync(long productId, int onHand, int reserved = 0)
    {
        await using var db = CreateContext();
        db.InventoryItems.Add(new InventoryService.Domain.InventoryItem
        {
            ProductId = productId,
            QuantityOnHand = onHand,
            QuantityReserved = reserved
        });
        await db.SaveChangesAsync();
    }

    public async Task<(int OnHand, int Reserved)> ReadQuantitiesAsync(long productId)
    {
        await using var db = CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.ProductId == productId);
        return (item.QuantityOnHand, item.QuantityReserved);
    }

    /// Re-derives reserved stock from the Active reservation rows and compares it with the
    /// counter, which is the strongest check available: it catches drift no single-case
    /// assertion would.
    public async Task<(int Counter, int DerivedFromReservations)> ReadReservedConsistencyAsync(long productId)
    {
        await using var db = CreateContext();

        var counter = await db.InventoryItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => i.QuantityReserved)
            .SingleAsync();

        var derived = await db.ReservationItems.AsNoTracking()
            .Where(ri => ri.ProductId == productId
                         && db.Reservations.Any(r => r.ReservationId == ri.ReservationId
                                                     && r.Status == InventoryService.Domain.ReservationStatus.Active))
            .Select(ri => (int?)ri.Quantity)
            .SumAsync() ?? 0;

        return (counter, derived);
    }

    public async ValueTask DisposeAsync()
    {
        if (string.IsNullOrEmpty(DatabaseName) || SkipReason is not null)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_masterConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Only ever the database this fixture generated.
            command.CommandText =
                $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN " +
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{DatabaseName}]; END";
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Leaving a test database behind is untidy but harmless; failing teardown would
            // hide the real test result.
        }
    }
}

[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerDatabaseFixture>;
