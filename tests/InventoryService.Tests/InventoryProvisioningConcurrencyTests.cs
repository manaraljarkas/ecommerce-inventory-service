using Xunit;
using InventoryService.Application;

namespace InventoryService.Tests;

/// Parallel provisioning of one product against a real SQL Server. The primary keys are the only
/// thing that can make these pass: nothing in the application serialises the calls.
[Collection(nameof(SqlServerCollection))]
public class InventoryProvisioningConcurrencyTests(SqlServerDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Racers = 12;

    private async Task ArrangeAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await fixture.ResetAsync();
    }

    /// Runs count operations at once, each with its own DbContext, released together so they
    /// genuinely contend rather than queueing behind one another.
    private static async Task<IReadOnlyList<T>> RunInParallelAsync<T>(
        SqlServerDatabaseFixture fixture, int count, Func<InventoryOperations, int, Task<T>> work)
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

    [Fact]
    public async Task Identical_parallel_requests_create_one_row_and_add_the_stock_once()
    {
        await ArrangeAsync();

        var results = await RunInParallelAsync(fixture, Racers, (ops, _) =>
            ops.CreateInventoryItemAsync(601, 20, Ct));

        Assert.Equal(1, results.Count(r => r.Outcome == CreateInventoryItemOutcome.Created));
        Assert.Equal(Racers - 1, results.Count(r => r.Outcome == CreateInventoryItemOutcome.AlreadyCreated));

        Assert.Equal(1, await fixture.CountInventoryRowsAsync(601));
        Assert.Equal(1, await fixture.CountProvisioningRowsAsync(601));
        Assert.Equal((20, 0), await fixture.ReadQuantitiesAsync(601));      // never 40, never 240
    }

    [Fact]
    public async Task Parallel_requests_with_different_quantities_have_one_winner_and_the_rest_conflict()
    {
        await ArrangeAsync();

        // Racer i asks for i + 1 units, so every request differs from every other.
        var results = await RunInParallelAsync(fixture, Racers, async (ops, i) =>
            (Quantity: i + 1, Result: await ops.CreateInventoryItemAsync(602, i + 1, Ct)));

        var winners = results.Where(r => r.Result.Outcome == CreateInventoryItemOutcome.Created).ToList();
        Assert.Single(winners);
        Assert.Equal(Racers - 1, results.Count(r => r.Result.Outcome == CreateInventoryItemOutcome.Conflict));

        // The stored stock is exactly what the winner asked for, and the record agrees.
        var winningQuantity = winners[0].Quantity;
        Assert.Equal((winningQuantity, 0), await fixture.ReadQuantitiesAsync(602));
        Assert.Equal(winningQuantity, await fixture.ReadInitialQuantityAsync(602));
        Assert.Equal(1, await fixture.CountInventoryRowsAsync(602));
    }

    [Fact]
    public async Task Parallel_provisioning_of_different_products_does_not_interfere()
    {
        await ArrangeAsync();

        var results = await RunInParallelAsync(fixture, Racers, (ops, i) =>
            ops.CreateInventoryItemAsync(700 + i, 10 + i, Ct));

        Assert.All(results, r => Assert.Equal(CreateInventoryItemOutcome.Created, r.Outcome));
        for (var i = 0; i < Racers; i++)
        {
            Assert.Equal((10 + i, 0), await fixture.ReadQuantitiesAsync(700 + i));
        }
    }

    [Fact]
    public async Task Reservations_and_replays_racing_together_never_change_the_stock_total()
    {
        await ArrangeAsync();
        await RunInParallelAsync(fixture, 1, (ops, _) => ops.CreateInventoryItemAsync(603, 10, Ct));

        // Half the racers replay the provisioning, half reserve one unit each.
        var results = await RunInParallelAsync(fixture, Racers, async (ops, i) =>
        {
            if (i % 2 == 0)
            {
                var replay = await ops.CreateInventoryItemAsync(603, 10, Ct);
                return replay.Outcome == CreateInventoryItemOutcome.AlreadyCreated;
            }

            var reserve = await ops.ReserveStockAsync($"mixed-{i}", [new RequestedItem(603, 1)], Ct);
            return reserve.Outcome == ReserveStockOutcome.Reserved;
        });

        Assert.All(results, succeeded => Assert.True(succeeded));
        var (onHand, reserved) = await fixture.ReadQuantitiesAsync(603);
        Assert.Equal(10, onHand);                       // replays added nothing
        Assert.Equal(Racers / 2, reserved);
        Assert.Equal(10, await fixture.ReadInitialQuantityAsync(603));
    }
}
