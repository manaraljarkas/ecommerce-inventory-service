using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Persistence.Configurations;

public class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> builder)
    {
        builder.ToTable("InventoryItems", table =>
        {
            // The first two constraints keep each column sane on its own. The third is the one
            // that makes overselling structurally impossible: whatever the reservation code
            // does, SQL Server rejects a write that would hold more than is on hand.
            table.HasCheckConstraint(
                "CK_InventoryItems_QuantityOnHand_NonNegative",
                "[QuantityOnHand] >= 0");

            table.HasCheckConstraint(
                "CK_InventoryItems_QuantityReserved_NonNegative",
                "[QuantityReserved] >= 0");

            table.HasCheckConstraint(
                "CK_InventoryItems_QuantityReserved_NotAboveOnHand",
                "[QuantityReserved] <= [QuantityOnHand]");
        });

        builder.HasKey(e => e.ProductId);

        // The id comes from Laravel, so SQL Server must not make it an IDENTITY column.
        // Without this, EF treats an integer key as store-generated and inserts of explicit
        // ids fail.
        builder.Property(e => e.ProductId)
            .HasColumnType("bigint")
            .ValueGeneratedNever();

        builder.Property(e => e.QuantityOnHand)
            .HasColumnType("int")
            .IsRequired();

        builder.Property(e => e.QuantityReserved)
            .HasColumnType("int")
            .IsRequired();

        // Derived from the two columns above; nothing to store.
        builder.Ignore(e => e.AvailableQuantity);
    }
}
