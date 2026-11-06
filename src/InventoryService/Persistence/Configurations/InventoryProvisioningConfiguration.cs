using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Persistence.Configurations;

public class InventoryProvisioningConfiguration : IEntityTypeConfiguration<InventoryProvisioning>
{
    public void Configure(EntityTypeBuilder<InventoryProvisioning> builder)
    {
        builder.ToTable("InventoryProvisionings", table =>
        {
            table.HasCheckConstraint(
                "CK_InventoryProvisionings_InitialQuantity_NonNegative",
                "[InitialQuantity] >= 0");
        });

        // The key is the product id: a second provisioning of the same product collides here,
        // in the database, whatever the application code did or did not check first.
        builder.HasKey(e => e.ProductId);

        builder.Property(e => e.ProductId)
            .HasColumnType("bigint")
            .ValueGeneratedNever();

        builder.Property(e => e.InitialQuantity)
            .HasColumnType("int")
            .IsRequired();

        builder.Property(e => e.CreatedAtUtc)
            .HasColumnType("datetime2(3)")
            .IsRequired();

        // The row can only describe an inventory row that exists, and Restrict means deleting that
        // row cannot silently erase the record of how it was created. No navigations: nothing in
        // this service walks from one to the other.
        builder.HasOne<InventoryItem>()
            .WithOne()
            .HasForeignKey<InventoryProvisioning>(e => e.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
