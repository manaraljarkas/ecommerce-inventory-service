using System.Data;
using InventoryService.Domain;
using InventoryService.Persistence;
using InventoryService.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Application;


/// The three inventory operations defined by Protos/inventory.proto, implemented against
/// SQL Server. This is the business layer: it knows nothing about gRPC, and returns outcome
/// records that the transport layer maps to status codes.

/// Every guarantee the contract makes is enforced by the database rather than by the order of
/// statements in C#. Three mechanisms do the work:


/// The Reservations primary key. A replayed or concurrent duplicate id collides
/// there, and a concurrent insert blocks until the first transaction settles, which is what
/// makes "the same reservation_id is serialized" true.
/// WITH (UPDLOCK, ROWLOCK) on inventory rows. The database has
/// READ_COMMITTED_SNAPSHOT enabled (EF Core turns it on when it creates a database), so a plain
/// SELECT returns a snapshot and blocks nobody. Read-then-update without an explicit lock would
/// silently oversell.
/// Guarded UPDATE statements with a rows-affected check, backed by the table's CHECK
/// constraints. Even if the locking were wrong, SQL Server rejects a write that would push
/// QuantityReserved above QuantityOnHand or below zero.


/// Rows are locked one at a time in ascending ProductId order. A single statement with an
/// ORDER BY does not promise the engine acquires locks in that order, so the ordering is made
/// real by issuing the locking reads sequentially. This is what keeps two multi-item orders
/// that share products from deadlocking. ReleaseStock follows the same order.
public class InventoryOperations(InventoryDbContext db, ILogger<InventoryOperations> logger)
{
    private const int SqlDuplicateKey = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    // ---------------------------------------------------------------- GetStock

    public async Task<GetStockResult> GetStockAsync(long productId, CancellationToken ct)
    {
        if (productId <= 0)
        {
            return new GetStockResult(GetStockOutcome.InvalidArgument, productId, 0,
                "product_id must be greater than zero.");
        }

        var item = await db.InventoryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ProductId == productId, ct);

        if (item is null)
        {
            return new GetStockResult(GetStockOutcome.ProductNotFound, productId, 0);
        }

        // A snapshot, as the contract states. No lock is taken: only ReserveStock decides
        // whether quantities can actually be held.
        return new GetStockResult(GetStockOutcome.Success, productId, item.AvailableQuantity);
    }

    // ------------------------------------------------------------ ReserveStock

    public async Task<ReserveStockResult> ReserveStockAsync(
        string reservationId,
        IReadOnlyList<RequestedItem> items,
        CancellationToken ct)
    {
        var validationError = ValidateReservationId(reservationId) ?? ValidateItems(items);
        if (validationError is not null)
        {
            return ReserveStockResult.Invalid(validationError);
        }

        // Deterministic ordering, applied once and reused for every lock and update below.
        var ordered = items.OrderBy(i => i.ProductId).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Claim the identifier first. Doing this before touching inventory is what serializes
        // concurrent calls that share a reservation_id: the second insert waits on the key.
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO Reservations (ReservationId, Status, CreatedAtUtc)
                   VALUES ({reservationId}, {(byte)ReservationStatus.Active}, {DateTime.UtcNow})",
                ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
            when (ex.Number is SqlDuplicateKey or SqlUniqueIndexViolation)
        {
            // The id already exists. Which of the three replay answers applies depends on the
            // stored reservation, not on anything this request carries.
            var replay = await ResolveReplayAsync(reservationId, ordered, ct);
            await transaction.RollbackAsync(ct);
            return replay;
        }

        // Lock every inventory row this reservation touches, in ascending ProductId order.
        var locked = new Dictionary<long, InventoryItem>();
        var missing = new List<long>();

        foreach (var item in ordered)
        {
            var row = await LockInventoryRowAsync(item.ProductId, ct);
            if (row is null)
            {
                missing.Add(item.ProductId);
            }
            else
            {
                locked[item.ProductId] = row;
            }
        }

        if (missing.Count > 0)
        {
            // The catalogue and the inventory database disagree. Nothing is held, and the
            // rollback removes the reservation row too.
            await transaction.RollbackAsync(ct);
            return new ReserveStockResult(ReserveStockOutcome.ProductNotFound, [], missing);
        }

        // Every row is locked, so these numbers cannot move under us.
        var shortfalls = ordered
            .Where(i => locked[i.ProductId].AvailableQuantity < i.Quantity)
            .Select(i => new ShortfallItem(i.ProductId, i.Quantity, locked[i.ProductId].AvailableQuantity))
            .ToList();

        if (shortfalls.Count > 0)
        {
            // All-or-nothing: the rollback discards the reservation row as well, so an
            // insufficient-stock attempt leaves no record, exactly as the contract specifies.
            await transaction.RollbackAsync(ct);
            return new ReserveStockResult(ReserveStockOutcome.InsufficientStock, shortfalls, []);
        }

        foreach (var item in ordered)
        {
            // The guard is redundant while the row is locked, and deliberately kept: it means a
            // flaw in the locking above cannot oversell, it can only fail the reservation.
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE InventoryItems
                   SET QuantityReserved = QuantityReserved + {item.Quantity}
                   WHERE ProductId = {item.ProductId}
                     AND QuantityOnHand - QuantityReserved >= {item.Quantity}",
                ct);

            if (affected != 1)
            {
                logger.LogWarning(
                    "Guarded reservation update affected {Affected} rows for product {ProductId} in reservation {ReservationId}; rolling back",
                    affected, item.ProductId, reservationId);

                await transaction.RollbackAsync(ct);
                return new ReserveStockResult(
                    ReserveStockOutcome.InsufficientStock,
                    [new ShortfallItem(item.ProductId, item.Quantity, locked[item.ProductId].AvailableQuantity)],
                    []);
            }
        }

        db.ReservationItems.AddRange(ordered.Select(i => new ReservationItem
        {
            ReservationId = reservationId,
            ProductId = i.ProductId,
            Quantity = i.Quantity
        }));
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Reserved {ItemCount} item(s) for reservation {ReservationId}", ordered.Count, reservationId);

        return ReserveStockResult.Simple(ReserveStockOutcome.Reserved);
    }

    /// Decides what a duplicate reservation id means. Released is checked before the item
    /// comparison, so a replay of a spent id is always reported as released rather than as a
    /// conflicting reuse.
    private async Task<ReserveStockResult> ResolveReplayAsync(
        string reservationId,
        IReadOnlyList<RequestedItem> ordered,
        CancellationToken ct)
    {
        var existing = await db.Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReservationId == reservationId, ct);

        if (existing is null)
        {
            // The holder rolled back between our failed insert and this read. Nothing is held
            // under this id; the caller can retry.
            return ReserveStockResult.Simple(ReserveStockOutcome.InsufficientStock);
        }

        if (existing.Status == ReservationStatus.Released)
        {
            return ReserveStockResult.Simple(ReserveStockOutcome.IdAlreadyReleased);
        }

        var storedItems = await db.ReservationItems
            .AsNoTracking()
            .Where(i => i.ReservationId == reservationId)
            .OrderBy(i => i.ProductId)
            .Select(i => new { i.ProductId, i.Quantity })
            .ToListAsync(ct);

        var sameItems =
            storedItems.Count == ordered.Count &&
            storedItems.Zip(ordered).All(pair =>
                pair.First.ProductId == pair.Second.ProductId &&
                pair.First.Quantity == pair.Second.Quantity);

        return ReserveStockResult.Simple(sameItems
            ? ReserveStockOutcome.AlreadyReserved
            : ReserveStockOutcome.IdReusedWithDifferentItems);
    }

    // ------------------------------------------------------------ ReleaseStock

    public async Task<ReleaseStockResult> ReleaseStockAsync(string reservationId, CancellationToken ct)
    {
        var validationError = ValidateReservationId(reservationId);
        if (validationError is not null)
        {
            return new ReleaseStockResult(ReleaseStockOutcome.InvalidArgument, validationError);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // The whole double-release defence is this one guarded statement. Two concurrent
        // releases both reach it, the row lock serializes them, and exactly one sees a row
        // affected. Only that one goes on to restore quantities.
        var flipped = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE Reservations
               SET Status = {(byte)ReservationStatus.Released}, ReleasedAtUtc = {DateTime.UtcNow}
               WHERE ReservationId = {reservationId}
                 AND Status = {(byte)ReservationStatus.Active}",
            ct);

        if (flipped == 0)
        {
            var exists = await db.Reservations
                .AsNoTracking()
                .AnyAsync(r => r.ReservationId == reservationId, ct);

            await transaction.RollbackAsync(ct);

            return new ReleaseStockResult(exists
                ? ReleaseStockOutcome.AlreadyReleased
                : ReleaseStockOutcome.NotFound);
        }

        var items = await db.ReservationItems
            .AsNoTracking()
            .Where(i => i.ReservationId == reservationId)
            .OrderBy(i => i.ProductId)        // same lock order as ReserveStock
            .Select(i => new { i.ProductId, i.Quantity })
            .ToListAsync(ct);

        foreach (var item in items)
        {
            // Guarded so that a release can never drive QuantityReserved negative, which the
            // CHECK constraint would reject anyway.
            var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE InventoryItems
                   SET QuantityReserved = QuantityReserved - {item.Quantity}
                   WHERE ProductId = {item.ProductId}
                     AND QuantityReserved >= {item.Quantity}",
                ct);

            if (affected != 1)
            {
                logger.LogError(
                    "Release of reservation {ReservationId} could not restore {Quantity} of product {ProductId}; rolling back",
                    reservationId, item.Quantity, item.ProductId);

                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    $"Releasing reservation '{reservationId}' would have driven reserved stock negative for product {item.ProductId}.");
            }
        }

        await transaction.CommitAsync(ct);

        logger.LogInformation("Released reservation {ReservationId}", reservationId);
        return new ReleaseStockResult(ReleaseStockOutcome.Released);
    }

    // ---------------------------------------------------------------- helpers

    /// Reads one inventory row under an update lock held to the end of the transaction.
    /// The hint is what makes this a real read: with READ_COMMITTED_SNAPSHOT on, an unhinted
    /// SELECT would return a snapshot and take no lock at all.
    private async Task<InventoryItem?> LockInventoryRowAsync(long productId, CancellationToken ct)
    {
        var rows = await db.InventoryItems
            .FromSql($@"SELECT ProductId, QuantityOnHand, QuantityReserved
                        FROM InventoryItems WITH (UPDLOCK, ROWLOCK)
                        WHERE ProductId = {productId}")
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Count > 0 ? rows[0] : null;
    }

    private static string? ValidateReservationId(string? reservationId)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            return "reservation_id is required and must not be blank.";
        }

        // Rejected rather than trimmed. Trimming would make two different identifiers behave as
        // one, which silently changes what idempotency means for the caller.
        if (reservationId.Trim() != reservationId)
        {
            return "reservation_id must not have leading or trailing whitespace.";
        }

        if (reservationId.Length > ReservationConfiguration.ReservationIdMaxLength)
        {
            return $"reservation_id must be at most {ReservationConfiguration.ReservationIdMaxLength} characters.";
        }

        return null;
    }

    private static string? ValidateItems(IReadOnlyList<RequestedItem> items)
    {
        if (items.Count == 0)
        {
            return "items must contain at least one entry.";
        }

        if (items.Any(i => i.ProductId <= 0))
        {
            return "every item product_id must be greater than zero.";
        }

        if (items.Any(i => i.Quantity <= 0))
        {
            return "every item quantity must be greater than zero.";
        }

        if (items.Select(i => i.ProductId).Distinct().Count() != items.Count)
        {
            return "a product must not appear more than once in the same reservation.";
        }

        return null;
    }
}
