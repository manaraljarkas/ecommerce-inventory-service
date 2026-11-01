using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Persistence;

/// Configuration for the development-only stock seed, bound from the "InventorySeed" section.
public class InventorySeedOptions
{
    public const string SectionName = "InventorySeed";

    /// Off unless explicitly switched on, so seeding is never a surprise.
    public bool Enabled { get; set; }
    public List<InventorySeedItem> Items { get; set; } = [];
}

public class InventorySeedItem
{
    public long ProductId { get; set; }

    public int QuantityOnHand { get; set; }
}

/// Creates inventory rows for development and manual testing.


/// Laravel owns the product catalogue and the contract has no provisioning RPC, so stock rows
/// have to come from somewhere outside the three operations. This is the smallest safe
/// mechanism: a list of product ids and opening quantities in configuration, inserted once.
///
/// Three properties make it safe to leave in the codebase:

/// It only ever inserts. A product that already has a row is skipped, never updated, so
/// running it again cannot overwrite quantities that reservations depend on.
/// It runs only in the Development environment and only when explicitly enabled.
/// The product ids live in configuration, not in code, so nothing here assumes what
/// Laravel's ids will be.

/// It never creates or migrates a database; the schema must already exist.
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(
        InventoryDbContext db,
        InventorySeedOptions options,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!options.Enabled || options.Items.Count == 0)
        {
            return;
        }

        var invalid = options.Items
            .Where(i => i.ProductId <= 0 || i.QuantityOnHand < 0)
            .ToList();

        if (invalid.Count > 0)
        {
            logger.LogError(
                "Inventory seed skipped: {Count} entries have a non-positive ProductId or negative QuantityOnHand",
                invalid.Count);
            return;
        }

        var requestedIds = options.Items.Select(i => i.ProductId).ToList();

        var existingIds = await db.InventoryItems
            .Where(i => requestedIds.Contains(i.ProductId))
            .Select(i => i.ProductId)
            .ToListAsync(ct);

        var toInsert = options.Items
            .Where(i => !existingIds.Contains(i.ProductId))
            .Select(i => new InventoryItem
            {
                ProductId = i.ProductId,
                QuantityOnHand = i.QuantityOnHand,
                QuantityReserved = 0
            })
            .ToList();

        if (toInsert.Count == 0)
        {
            logger.LogInformation(
                "Inventory seed: all {Count} configured product(s) already have rows; nothing inserted",
                requestedIds.Count);
            return;
        }

        db.InventoryItems.AddRange(toInsert);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Inventory seed: inserted {Inserted} product(s), left {Skipped} existing row(s) untouched",
            toInsert.Count, existingIds.Count);
    }
}
