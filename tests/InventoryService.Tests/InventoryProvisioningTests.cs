using Xunit;
using InventoryService.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Tests;

/// Functional coverage of CreateInventoryItem against a real SQL Server database. Assertions
/// check the stored rows and quantities, not only the returned outcome.
[Collection(nameof(SqlServerCollection))]
public class InventoryProvisioningTests(SqlServerDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<InventoryOperations> ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
        return fixture.CreateOperations(fixture.CreateContext());
    }

    [Fact]
    public async Task Creates_the_inventory_row_with_the_initial_quantity()
    {
        var ops = await ArrangeAsync();

        var result = await ops.CreateInventoryItemAsync(501, 20, Ct);

        Assert.Equal(CreateInventoryItemOutcome.Created, result.Outcome);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(501));
        Assert.Equal(20, await fixture.ReadInitialQuantityAsync(501));
        Assert.Equal(20, (await ops.GetStockAsync(501, Ct)).AvailableQuantity);
    }

    [Fact]
    public async Task Accepts_a_zero_initial_quantity()
    {
        var ops = await ArrangeAsync();

        var result = await ops.CreateInventoryItemAsync(502, 0, Ct);

        Assert.Equal(CreateInventoryItemOutcome.Created, result.Outcome);
        Assert.Equal((0, 0), await fixture.ReadQuantitiesAsync(502));
        Assert.Equal(0, (await ops.GetStockAsync(502, Ct)).AvailableQuantity);
    }

    [Fact]
    public async Task Rejects_a_negative_initial_quantity_and_stores_nothing()
    {
        var ops = await ArrangeAsync();

        var result = await ops.CreateInventoryItemAsync(503, -1, Ct);

        Assert.Equal(CreateInventoryItemOutcome.InvalidArgument, result.Outcome);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(503));
        Assert.Equal(0, await fixture.CountProvisioningRowsAsync(503));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task Rejects_a_product_id_that_is_not_positive(long productId)
    {
        var ops = await ArrangeAsync();

        var result = await ops.CreateInventoryItemAsync(productId, 5, Ct);

        Assert.Equal(CreateInventoryItemOutcome.InvalidArgument, result.Outcome);
        Assert.Equal(0, await fixture.CountInventoryRowsAsync(productId));
    }

    [Fact]
    public async Task Repeating_the_same_request_is_a_replay_that_adds_no_stock()
    {
        var ops = await ArrangeAsync();
        await ops.CreateInventoryItemAsync(504, 20, Ct);

        var replay = await ops.CreateInventoryItemAsync(504, 20, Ct);

        Assert.Equal(CreateInventoryItemOutcome.AlreadyCreated, replay.Outcome);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(504));      // not 40
        Assert.Equal(1, await fixture.CountInventoryRowsAsync(504));
        Assert.Equal(1, await fixture.CountProvisioningRowsAsync(504));
    }

    [Fact]
    public async Task A_replay_is_judged_against_the_recorded_quantity_not_the_current_stock()
    {
        var ops = await ArrangeAsync();
        await ops.CreateInventoryItemAsync(505, 10, Ct);
        await ops.ReserveStockAsync("prov-held", [new RequestedItem(505, 3)], Ct);
        await fixture.SetQuantityOnHandAsync(505, 25);          // a restock after provisioning

        // Stock now reads 25 on hand with 3 reserved, yet this is still the request that
        // created the product, so a timed-out caller retrying it must get a success.
        var replay = await ops.CreateInventoryItemAsync(505, 10, Ct);

        Assert.Equal(CreateInventoryItemOutcome.AlreadyCreated, replay.Outcome);
        Assert.Equal((25, 3), await fixture.ReadQuantitiesAsync(505));  // nothing overwritten
        Assert.Equal(10, await fixture.ReadInitialQuantityAsync(505));
    }

    [Fact]
    public async Task A_different_quantity_for_a_provisioned_product_is_a_conflict_and_changes_nothing()
    {
        var ops = await ArrangeAsync();
        await ops.CreateInventoryItemAsync(506, 20, Ct);

        var conflict = await ops.CreateInventoryItemAsync(506, 99, Ct);

        Assert.Equal(CreateInventoryItemOutcome.Conflict, conflict.Outcome);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(506));
        Assert.Equal(20, await fixture.ReadInitialQuantityAsync(506));
    }

    [Fact]
    public async Task A_row_that_was_never_provisioned_is_a_conflict_even_when_the_numbers_match()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 507, onHand: 20);     // as the development seeder would

        var result = await ops.CreateInventoryItemAsync(507, 20, Ct);

        // Matching quantities prove nothing: this request was never applied.
        Assert.Equal(CreateInventoryItemOutcome.Conflict, result.Outcome);
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(507));
        Assert.Null(await fixture.ReadInitialQuantityAsync(507));
    }

    [Fact]
    public async Task A_seeded_product_with_different_stock_is_never_overwritten()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 508, onHand: 7, reserved: 2);

        var result = await ops.CreateInventoryItemAsync(508, 500, Ct);

        Assert.Equal(CreateInventoryItemOutcome.Conflict, result.Outcome);
        Assert.Equal((7, 2), await fixture.ReadQuantitiesAsync(508));
    }

    [Fact]
    public async Task A_provisioned_product_works_with_reserve_and_release()
    {
        var ops = await ArrangeAsync();
        await ops.CreateInventoryItemAsync(509, 5, Ct);

        var reserved = await ops.ReserveStockAsync("prov-order", [new RequestedItem(509, 3)], Ct);
        Assert.Equal(ReserveStockOutcome.Reserved, reserved.Outcome);
        Assert.Equal(2, (await ops.GetStockAsync(509, Ct)).AvailableQuantity);

        var tooMany = await ops.ReserveStockAsync("prov-order-2", [new RequestedItem(509, 3)], Ct);
        Assert.Equal(ReserveStockOutcome.InsufficientStock, tooMany.Outcome);

        var released = await ops.ReleaseStockAsync("prov-order", Ct);
        Assert.Equal(ReleaseStockOutcome.Released, released.Outcome);
        Assert.Equal(5, (await ops.GetStockAsync(509, Ct)).AvailableQuantity);
        Assert.Equal((5, 0), await fixture.ReadQuantitiesAsync(509));
    }

    [Fact]
    public async Task An_unprovisioned_product_still_has_no_stock_to_read_or_reserve()
    {
        var ops = await ArrangeAsync();

        Assert.Equal(GetStockOutcome.ProductNotFound, (await ops.GetStockAsync(510, Ct)).Outcome);
        Assert.Equal(ReserveStockOutcome.ProductNotFound,
            (await ops.ReserveStockAsync("prov-none", [new RequestedItem(510, 1)], Ct)).Outcome);
    }

    [Fact]
    public async Task The_database_itself_rejects_a_negative_recorded_quantity()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 511, onHand: 1);

        await using var db = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO InventoryProvisionings (ProductId, InitialQuantity, CreatedAtUtc) VALUES ({511L}, {-5}, {DateTime.UtcNow})",
            Ct));

        Assert.Equal(547, ex.Number);       // CHECK constraint violation
    }

    [Fact]
    public async Task The_database_itself_refuses_a_provisioning_record_without_an_inventory_row()
    {
        await ArrangeAsync();

        await using var db = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO InventoryProvisionings (ProductId, InitialQuantity, CreatedAtUtc) VALUES ({512L}, {5}, {DateTime.UtcNow})",
            Ct));

        Assert.Equal(547, ex.Number);       // FOREIGN KEY violation
    }
}
