using Xunit;
using InventoryService.Application;
using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Tests;

/// Functional coverage of the three operations against a real SQL Server database.
/// Assertions check the stored quantities, not only the returned outcome.
[Collection(nameof(SqlServerCollection))]
public class InventoryOperationsTests(SqlServerDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<InventoryOperations> ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
        return fixture.CreateOperations(fixture.CreateContext());
    }

    private static List<RequestedItem> Items(params (long ProductId, int Quantity)[] items) =>
        items.Select(i => new RequestedItem(i.ProductId, i.Quantity)).ToList();

    // ---------------------------------------------------------------- GetStock

    [Fact] // 1. GetStock returns the correct quantity
    public async Task GetStock_returns_on_hand_minus_reserved()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 10, onHand: 7, reserved: 2);

        var result = await ops.GetStockAsync(10, Ct);

        Assert.Equal(GetStockOutcome.Success, result.Outcome);
        Assert.Equal(10, result.ProductId);
        Assert.Equal(5, result.AvailableQuantity);
    }

    [Fact] // 2. Unknown product returns NOT_FOUND
    public async Task GetStock_unknown_product_is_not_found()
    {
        var ops = await ArrangeAsync();

        var result = await ops.GetStockAsync(999, Ct);

        Assert.Equal(GetStockOutcome.ProductNotFound, result.Outcome);
    }

    [Fact] // 3. Invalid arguments are rejected
    public async Task GetStock_rejects_non_positive_product_id()
    {
        var ops = await ArrangeAsync();

        Assert.Equal(GetStockOutcome.InvalidArgument, (await ops.GetStockAsync(0, Ct)).Outcome);
        Assert.Equal(GetStockOutcome.InvalidArgument, (await ops.GetStockAsync(-5, Ct)).Outcome);
    }

    [Theory] // 3. Invalid arguments are rejected
    [InlineData("", "blank id")]
    [InlineData("   ", "whitespace id")]
    [InlineData(" leading-space", "leading whitespace")]
    [InlineData("trailing-space ", "trailing whitespace")]
    public async Task ReserveStock_rejects_malformed_reservation_ids(string reservationId, string because)
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        var result = await ops.ReserveStockAsync(reservationId, Items((1, 1)), Ct);

        Assert.Equal(ReserveStockOutcome.InvalidArgument, result.Outcome);
        Assert.NotNull(result.ValidationError);
        Assert.Equal(0, (await fixture.ReadQuantitiesAsync(1)).Reserved);
        _ = because;
    }

    [Fact] // 3. Invalid arguments are rejected
    public async Task ReserveStock_rejects_oversized_reservation_id()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        var result = await ops.ReserveStockAsync(new string('x', 65), Items((1, 1)), Ct);

        Assert.Equal(ReserveStockOutcome.InvalidArgument, result.Outcome);
    }

    [Fact] // 3. Invalid arguments are rejected
    public async Task ReserveStock_rejects_bad_item_collections()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        Assert.Equal(ReserveStockOutcome.InvalidArgument,
            (await ops.ReserveStockAsync("r-empty", [], Ct)).Outcome);

        Assert.Equal(ReserveStockOutcome.InvalidArgument,
            (await ops.ReserveStockAsync("r-zero-product", Items((0, 1)), Ct)).Outcome);

        Assert.Equal(ReserveStockOutcome.InvalidArgument,
            (await ops.ReserveStockAsync("r-zero-qty", Items((1, 0)), Ct)).Outcome);

        Assert.Equal(ReserveStockOutcome.InvalidArgument,
            (await ops.ReserveStockAsync("r-duplicate", Items((1, 1), (1, 2)), Ct)).Outcome);
    }

    // ----------------------------------------------------------- ReserveStock

    [Fact] // 4. Reservation succeeds when stock is sufficient
    public async Task ReserveStock_holds_quantities_when_stock_is_sufficient()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        var result = await ops.ReserveStockAsync("res-ok", Items((1, 3)), Ct);

        Assert.Equal(ReserveStockOutcome.Reserved, result.Outcome);
        Assert.Equal((10, 3), await fixture.ReadQuantitiesAsync(1));
    }

    [Fact] // 5. Reservation fails when stock is insufficient
    public async Task ReserveStock_rejects_when_stock_is_short_and_stores_nothing()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 2);

        var result = await ops.ReserveStockAsync("res-short", Items((1, 5)), Ct);

        Assert.Equal(ReserveStockOutcome.InsufficientStock, result.Outcome);
        Assert.Equal((2, 0), await fixture.ReadQuantitiesAsync(1));

        // The contract requires an insufficient attempt to leave no reservation record.
        await using var db = fixture.CreateContext();
        Assert.False(await db.Reservations.AnyAsync(r => r.ReservationId == "res-short", Ct));
        Assert.False(await db.ReservationItems.AnyAsync(i => i.ReservationId == "res-short", Ct));
    }

    [Fact] // 6. Multi-item reservation is atomic
    public async Task ReserveStock_is_all_or_nothing_across_items()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);
        await fixture.SeedProductAsync(2, 1);   // the one that will block the order
        await fixture.SeedProductAsync(3, 10);

        var result = await ops.ReserveStockAsync("res-atomic", Items((1, 2), (2, 5), (3, 2)), Ct);

        Assert.Equal(ReserveStockOutcome.InsufficientStock, result.Outcome);

        // Not one quantity moved, including for the products that had plenty.
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(1));
        Assert.Equal((1, 0), await fixture.ReadQuantitiesAsync(2));
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(3));
    }

    [Fact] // 7. Insufficient-stock response identifies failing items
    public async Task ReserveStock_reports_every_failing_item()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);
        await fixture.SeedProductAsync(2, 1);
        await fixture.SeedProductAsync(3, 0);

        var result = await ops.ReserveStockAsync("res-detail", Items((1, 2), (2, 5), (3, 1)), Ct);

        Assert.Equal(ReserveStockOutcome.InsufficientStock, result.Outcome);

        // Both short products, not just the first one encountered.
        Assert.Equal([2L, 3L], result.Shortfalls.Select(s => s.ProductId).OrderBy(id => id));

        var product2 = result.Shortfalls.Single(s => s.ProductId == 2);
        Assert.Equal(5, product2.RequestedQuantity);
        Assert.Equal(1, product2.AvailableQuantity);
    }

    [Fact] // 8. Repeated reservation with the same id and items does not reserve twice
    public async Task ReserveStock_replay_with_same_items_is_idempotent()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        var first = await ops.ReserveStockAsync("res-replay", Items((1, 4)), Ct);
        var second = await ops.ReserveStockAsync("res-replay", Items((1, 4)), Ct);

        Assert.Equal(ReserveStockOutcome.Reserved, first.Outcome);
        Assert.Equal(ReserveStockOutcome.AlreadyReserved, second.Outcome);
        Assert.Equal((10, 4), await fixture.ReadQuantitiesAsync(1));   // held once, not twice
    }

    [Fact] // 9. Reused reservation id with different items is rejected
    public async Task ReserveStock_replay_with_different_items_is_rejected()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        await ops.ReserveStockAsync("res-conflict", Items((1, 2)), Ct);
        var conflict = await ops.ReserveStockAsync("res-conflict", Items((1, 3)), Ct);

        Assert.Equal(ReserveStockOutcome.IdReusedWithDifferentItems, conflict.Outcome);
        Assert.Equal((10, 2), await fixture.ReadQuantitiesAsync(1));
    }

    [Fact] // 13 (reserve side). Unknown product returns NOT_FOUND and changes nothing
    public async Task ReserveStock_unknown_product_is_not_found()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);

        var result = await ops.ReserveStockAsync("res-missing", Items((1, 1), (404, 1)), Ct);

        Assert.Equal(ReserveStockOutcome.ProductNotFound, result.Outcome);
        Assert.Contains(404L, result.MissingProductIds);
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(1));
    }

    // ----------------------------------------------------------- ReleaseStock

    [Fact] // 10. Releasing a reservation restores stock exactly once
    public async Task ReleaseStock_restores_quantities()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);
        await ops.ReserveStockAsync("rel-1", Items((1, 4)), Ct);

        var result = await ops.ReleaseStockAsync("rel-1", Ct);

        Assert.Equal(ReleaseStockOutcome.Released, result.Outcome);
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(1));
    }

    [Fact] // 11. Repeated release does not restore stock twice
    public async Task ReleaseStock_second_call_restores_nothing()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);
        await ops.ReserveStockAsync("rel-2", Items((1, 4)), Ct);

        var first = await ops.ReleaseStockAsync("rel-2", Ct);
        var second = await ops.ReleaseStockAsync("rel-2", Ct);

        Assert.Equal(ReleaseStockOutcome.Released, first.Outcome);
        Assert.Equal(ReleaseStockOutcome.AlreadyReleased, second.Outcome);
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(1));   // not -4
    }

    [Fact] // 12. Released reservation cannot be reactivated
    public async Task ReserveStock_after_release_is_rejected_and_the_record_remains()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 10);
        await ops.ReserveStockAsync("rel-3", Items((1, 4)), Ct);
        await ops.ReleaseStockAsync("rel-3", Ct);

        var reuse = await ops.ReserveStockAsync("rel-3", Items((1, 4)), Ct);

        Assert.Equal(ReserveStockOutcome.IdAlreadyReleased, reuse.Outcome);
        Assert.Equal((10, 0), await fixture.ReadQuantitiesAsync(1));

        await using var db = fixture.CreateContext();
        var stored = await db.Reservations.AsNoTracking().SingleAsync(r => r.ReservationId == "rel-3", Ct);
        Assert.Equal(ReservationStatus.Released, stored.Status);   // still recorded
        Assert.NotNull(stored.ReleasedAtUtc);
    }

    [Fact] // 13. Unknown reservation returns NOT_FOUND
    public async Task ReleaseStock_unknown_reservation_is_not_found()
    {
        var ops = await ArrangeAsync();

        var result = await ops.ReleaseStockAsync("never-existed", Ct);

        Assert.Equal(ReleaseStockOutcome.NotFound, result.Outcome);
    }

    [Fact] // An insufficient attempt stores nothing, so releasing it finds nothing.
    public async Task ReleaseStock_after_insufficient_reserve_is_not_found()
    {
        var ops = await ArrangeAsync();
        await fixture.SeedProductAsync(1, 1);
        await ops.ReserveStockAsync("rel-none", Items((1, 5)), Ct);

        var result = await ops.ReleaseStockAsync("rel-none", Ct);

        Assert.Equal(ReleaseStockOutcome.NotFound, result.Outcome);
    }
}
