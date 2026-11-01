using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Persistence.Configurations;

public class ReservationItemConfiguration : IEntityTypeConfiguration<ReservationItem>
{
    public void Configure(EntityTypeBuilder<ReservationItem> builder)
    {
        builder.ToTable("ReservationItems", table =>
        {
            table.HasCheckConstraint(
                "CK_ReservationItems_Quantity_Positive",
                "[Quantity] > 0");
        });

        // Composite key: enforces one line per product per reservation, and clusters a
        // reservation's lines together - which is exactly how ReleaseStock reads them.
        builder.HasKey(e => new { e.ReservationId, e.ProductId });

        builder.Property(e => e.ReservationId)
            .HasMaxLength(ReservationConfiguration.ReservationIdMaxLength)
            .IsUnicode(false);

        builder.Property(e => e.ProductId)
            .HasColumnType("bigint");

        builder.Property(e => e.Quantity)
            .HasColumnType("int")
            .IsRequired();

        // Items belong to their reservation, so cascade is the right semantics. In practice
        // nothing deletes reservations: released rows are retained permanently.
        builder.HasOne(e => e.Reservation)
            .WithMany(r => r.Items)
            .HasForeignKey(e => e.ReservationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, deliberately. Deleting an inventory item that still has reservation history
        // must fail rather than quietly erase the lines that explain its reserved quantity.
        // Configured without navigations: nothing in this service needs to walk from a product
        // to every reservation that ever touched it, and leaving the navigation out keeps that
        // query from being written by accident.
        builder.HasOne<InventoryItem>()
            .WithMany()
            .HasForeignKey(e => e.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
