namespace InventoryService.Domain;


/// One stock reservation, identified by the key the caller supplied.


/// This row is the reservation lifecycle from the contract: no row means UNKNOWN,
/// ReservationStatus.Active means quantities are held, and
/// ReservationStatus.Released means they were given back.
///
/// Released rows are kept forever. That row is the only thing that lets a replayed
/// ReserveStock be rejected with FAILED_PRECONDITION instead of silently creating a
/// second reservation under an identifier that was already spent.

public class Reservation
{

    /// Idempotency key supplied by the caller, expected to be a UUID string. Opaque to this
    /// service: the contract requires only that it is non-empty, so no format is enforced here.
    
    public required string ReservationId { get; set; }

    public ReservationStatus Status { get; set; }

    /// UTC. SQL Server stores no offset, so the application is responsible for the kind.
    public DateTime CreatedAtUtc { get; set; }

    /// UTC. Null exactly when Status is ReservationStatus.Active.
    public DateTime? ReleasedAtUtc { get; set; }


    /// The lines this reservation holds. Also the only record of how much to give back on
    /// release, which is why the caller never supplies quantities when releasing.

    public ICollection<ReservationItem> Items { get; set; } = [];
}
