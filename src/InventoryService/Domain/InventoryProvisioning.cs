namespace InventoryService.Domain;


/// The permanent record that a product's inventory row was created by CreateInventoryItem, and
/// with what initial quantity.

/// It exists because a replay has to be told apart from a conflict, and the live
/// InventoryItem cannot do that: its QuantityOnHand changes with every restock, so comparing the
/// requested quantity with it would call a legitimate retry a conflict after any stock movement,
/// and a different request a replay whenever the numbers happened to match. This row is written
/// once, in the same transaction as the inventory row, and never updated.
///
/// An inventory row with no provisioning row was not created through this operation (a
/// development seed, or entered by hand), so a provisioning request for it is a conflict.

public class InventoryProvisioning
{
    /// The Laravel product id; also the primary key, so a product is provisioned at most once.
    public long ProductId { get; set; }

    /// What the caller asked the product to start with. Immutable: never the current stock.
    public int InitialQuantity { get; set; }

    /// UTC. SQL Server stores no offset, so the application is responsible for the kind.
    public DateTime CreatedAtUtc { get; set; }
}
