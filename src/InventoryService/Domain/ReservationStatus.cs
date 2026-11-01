namespace InventoryService.Domain;


/// Lifecycle state of a reservation, matching the state machine documented in
/// Protos/inventory.proto. A reservation with no row at all is the contract's
/// UNKNOWN state; it is not represented here.


/// Values start at 1 on purpose: 0 is left unused so that a default(ReservationStatus)
/// written by accident violates the CK_Reservations_Status check constraint instead of
/// silently persisting as a valid state.

public enum ReservationStatus : byte
{
    /// Quantities are currently held for this reservation.
    Active = 1,

    /// Quantities were returned to stock. Terminal: a released reservation is never reactivated.
    Released = 2
}
