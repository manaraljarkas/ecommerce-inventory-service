namespace InventoryService.Application;

/// Outcomes of InventoryOperations.GetStockAsync
public enum GetStockOutcome
{
    Success,
    InvalidArgument,
    ProductNotFound
}

public sealed record GetStockResult(
    GetStockOutcome Outcome,
    long ProductId,
    int AvailableQuantity,
    string? ValidationError = null);

/// Outcomes of InventoryOperations.ReserveStockAsync, mapping one-to-one onto the
/// cases the contract in Protos/inventory.proto enumerates for ReserveStock.

public enum ReserveStockOutcome
{
    /// Quantities were held by this call.
    Reserved,

    /// The id was already Active with the same items; nothing changed.
    AlreadyReserved,

    /// Nothing was held; at least one product was short. See Shortfalls.
    InsufficientStock,

    /// The id is Active but carries different items.
    IdReusedWithDifferentItems,

    /// The id is Released. Terminal: it can never hold stock again.
    IdAlreadyReleased,

    /// At least one product has no inventory row. See MissingProductIds.
    ProductNotFound,

    InvalidArgument
}

/// A product that blocked a reservation, with the quantity available at evaluation time.
public sealed record ShortfallItem(long ProductId, int RequestedQuantity, int AvailableQuantity);

public sealed record ReserveStockResult(
    ReserveStockOutcome Outcome,
    IReadOnlyList<ShortfallItem> Shortfalls,
    IReadOnlyList<long> MissingProductIds,
    string? ValidationError = null)
{
    public static ReserveStockResult Simple(ReserveStockOutcome outcome) =>
        new(outcome, [], []);

    public static ReserveStockResult Invalid(string message) =>
        new(ReserveStockOutcome.InvalidArgument, [], [], message);
}


/// Outcomes of InventoryOperations.ReleaseStockAsync
public enum ReleaseStockOutcome
{
    /// The reservation was Active; its quantities are now available again.
    Released,

    /// The reservation was already Released; nothing was restored a second time.
    AlreadyReleased,

    /// No reservation existed for this id when the call was processed.
    NotFound,

    InvalidArgument
}

public sealed record ReleaseStockResult(
    ReleaseStockOutcome Outcome,
    string? ValidationError = null);


/// One requested line, in the primitive form the application layer works with so that nothing
/// below the gRPC layer depends on the generated protobuf types.

public readonly record struct RequestedItem(long ProductId, int Quantity);
