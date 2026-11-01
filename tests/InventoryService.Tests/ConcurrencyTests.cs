using Xunit;
using InventoryService.Application;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Tests;

/// The tests that justify the locking strategy. Each runs genuinely parallel calls against a
/// real SQL Server and then asserts the stored quantities, because a correct-looking set of
/// response codes can still sit on top of a corrupted counter.

/// These are the tests that fail if the <c>UPDLOCK</c> hint, the ascending ProductId lock order
/// or the guarded <c>UPDATE ... WHERE Status = Active</c> is removed. The database has
/// READ_COMMITTED_SNAPSHOT on, so unhinted reads would not block and overselling would go
/// unnoticed without them.

[Collection(nameof(SqlServerCollection))]
public class ConcurrencyTests(SqlServerDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Racers = 12;

    private async Task ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
    }


    /// Runs count operations at once, each with its own DbContext, released
    /// together so they genuinely contend rather than queueing behind one another.

    private async Task<IReadOnlyList<T>> RunInParallelAsync<T>(int count, Func<InventoryOperations, int, Task<T>> work)
    {
        using var gate = new Barrier(count);

        var tasks = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            var ops = fixture.CreateOperations(db);
            gate.SignalAndWait();
            return await work(ops, index);
        })).ToArray();

        return await Task.WhenAll(tasks);
    }

    [Fact] // 14. Two customers reserve the last available item concurrently: exactly one succeeds
    public async Task Only_one_reservation_can_take_the_last_item()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 1, onHand: 1);

        var results = await RunInParallelAsync(Racers, (ops, i) =>
            ops.ReserveStockAsync($"race-{i}", [new RequestedItem(1, 1)], Ct));

        Assert.Equal(1, results.Count(r => r.Outcome == ReserveStockOutcome.Reserved));
        Assert.Equal(Racers - 1, results.Count(r => r.Outcome == ReserveStockOutcome.InsufficientStock));

        var (onHand, reserved) = await fixture.ReadQuantitiesAsync(1);
        Assert.Equal(1, onHand);
        Assert.Equal(1, reserved);      // never 2, never 12
    }

    [Fact] // 15. Multiple concurrent reservations with the same reservation id cannot double-reserve
    public async Task Same_reservation_id_in_parallel_holds_stock_once()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 1, onHand: 100);

        var results = await RunInParallelAsync(Racers, (ops, _) =>
            ops.ReserveStockAsync("same-id", [new RequestedItem(1, 3)], Ct));

        // Exactly one call creates the reservation; the rest observe it.
        Assert.Equal(1, results.Count(r => r.Outcome == ReserveStockOutcome.Reserved));
        Assert.Equal(Racers - 1, results.Count(r => r.Outcome == ReserveStockOutcome.AlreadyReserved));

        Assert.Equal(3, (await fixture.ReadQuantitiesAsync(1)).Reserved);   // 3, not 36

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Reservations.CountAsync(r => r.ReservationId == "same-id", Ct));
        Assert.Equal(1, await db.ReservationItems.CountAsync(i => i.ReservationId == "same-id", Ct));
    }

    [Fact] // 16. Multiple concurrent releases cannot double-release
    public async Task Parallel_releases_restore_stock_once()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 1, onHand: 50);

        await using (var setup = fixture.CreateContext())
        {
            await fixture.CreateOperations(setup)
                .ReserveStockAsync("rel-race", [new RequestedItem(1, 10)], Ct);
        }

        var results = await RunInParallelAsync(Racers, (ops, _) =>
            ops.ReleaseStockAsync("rel-race", Ct));

        Assert.Equal(1, results.Count(r => r.Outcome == ReleaseStockOutcome.Released));
        Assert.Equal(Racers - 1, results.Count(r => r.Outcome == ReleaseStockOutcome.AlreadyReleased));

        var (onHand, reserved) = await fixture.ReadQuantitiesAsync(1);
        Assert.Equal(50, onHand);
        Assert.Equal(0, reserved);      // restored once; never negative
    }

    [Fact] // 17. Concurrent multi-item reservations: no overselling, no partial reservation
    public async Task Concurrent_multi_item_reservations_never_oversell_or_partially_apply()
    {
        await ArrangeAsync();
        // Two products, each able to satisfy exactly 4 of the 12 attempts.
        await fixture.SeedProductAsync(productId: 1, onHand: 8);
        await fixture.SeedProductAsync(productId: 2, onHand: 8);

        // Half the callers list the products in the opposite order. If the implementation took
        // locks in request order rather than ascending ProductId, this is where it would deadlock.
        var results = await RunInParallelAsync(Racers, (ops, i) =>
        {
            List<RequestedItem> items = i % 2 == 0
                ? [new RequestedItem(1, 2), new RequestedItem(2, 2)]
                : [new RequestedItem(2, 2), new RequestedItem(1, 2)];
            return ops.ReserveStockAsync($"multi-{i}", items, Ct);
        });

        var reserved = results.Count(r => r.Outcome == ReserveStockOutcome.Reserved);
        var rejected = results.Count(r => r.Outcome == ReserveStockOutcome.InsufficientStock);

        Assert.Equal(Racers, reserved + rejected);      // nothing errored or deadlocked out
        Assert.Equal(4, reserved);                      // 8 / 2 per product

        var product1 = await fixture.ReadQuantitiesAsync(1);
        var product2 = await fixture.ReadQuantitiesAsync(2);
        Assert.Equal(8, product1.Reserved);
        Assert.Equal(8, product2.Reserved);

        // Partial application would show as the two products disagreeing.
        Assert.Equal(product1.Reserved, product2.Reserved);
    }

    [Fact] // 18. QuantityReserved matches the sum of Active reservation items after concurrent work
    public async Task Reserved_counter_matches_active_reservations_after_mixed_load()
    {
        await ArrangeAsync();
        await fixture.SeedProductAsync(productId: 1, onHand: 60);
        await fixture.SeedProductAsync(productId: 2, onHand: 60);

        // Reserve a batch that some of the mixed load will later release.
        var seeded = await RunInParallelAsync(10, (ops, i) =>
            ops.ReserveStockAsync($"mix-pre-{i}", [new RequestedItem(1, 2), new RequestedItem(2, 1)], Ct));
        Assert.All(seeded, r => Assert.Equal(ReserveStockOutcome.Reserved, r.Outcome));

        // Now interleave fresh reservations, replays and releases against the same rows.
        await RunInParallelAsync(24, async (ops, i) =>
        {
            switch (i % 4)
            {
                case 0:
                    await ops.ReserveStockAsync($"mix-new-{i}", [new RequestedItem(1, 1)], Ct);
                    break;
                case 1:
                    await ops.ReserveStockAsync($"mix-pre-{i % 10}", [new RequestedItem(1, 2), new RequestedItem(2, 1)], Ct);
                    break;
                case 2:
                    await ops.ReleaseStockAsync($"mix-pre-{i % 10}", Ct);
                    break;
                default:
                    await ops.ReserveStockAsync($"mix-new-{i}", [new RequestedItem(2, 1)], Ct);
                    break;
            }
            return true;
        });

        // The invariant: the counter equals what the Active reservations say it should be.
        foreach (var productId in new long[] { 1, 2 })
        {
            var (counter, derived) = await fixture.ReadReservedConsistencyAsync(productId);
            Assert.Equal(derived, counter);

            var (onHand, reserved) = await fixture.ReadQuantitiesAsync(productId);
            Assert.InRange(reserved, 0, onHand);    // the CHECK constraint, re-asserted
        }
    }
}
