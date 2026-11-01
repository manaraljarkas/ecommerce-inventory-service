namespace InventoryService.Domain;


/// One product line within a reservation.

/// The key is (ReservationId, ProductId), which makes the contract's "a product must not
/// appear twice in one request" rule a property of the table rather than a validation step
/// that could be skipped.

public class ReservationItem
{
    public required string ReservationId { get; set; }

    /// The Laravel product id; must already exist in InventoryItems.
    public long ProductId { get; set; }

    /// Quantity held for this product by this reservation. Always greater than zero.
    public int Quantity { get; set; }

    public Reservation? Reservation { get; set; }
}
