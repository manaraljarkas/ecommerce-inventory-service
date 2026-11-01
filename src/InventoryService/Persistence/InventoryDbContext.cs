using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Persistence;

/// EF Core context for the inventory database, which this service owns exclusively.

/// Used directly by the gRPC services. There is no repository layer: the context is already
/// an abstraction over persistence, and the behaviour that needs testing here - row locking,
/// check constraints, transaction boundaries - only exists against a real SQL Server, so a
/// mockable interface in front of it would add indirection without adding coverage.
public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ReservationItem> ReservationItems => Set<ReservationItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
    }
}
