using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryService.Persistence.Configurations;

public class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    /// Upper bound on the caller-supplied reservation id. The contract requires only that the
    /// id is non-empty, so this is a limit we impose rather than one it states; a UUID needs 36
    /// characters. Requests exceeding it must be rejected with INVALID_ARGUMENT in the service
    /// layer rather than silently truncated by the database.
    public const int ReservationIdMaxLength = 64;

    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations", table =>
        {
            table.HasCheckConstraint(
                "CK_Reservations_Status",
                "[Status] IN (1, 2)");

            // Keeps the lifecycle coherent: Active rows have no release timestamp and Released
            // rows always have one, so the two columns can never disagree about what happened.
            table.HasCheckConstraint(
                "CK_Reservations_ReleasedAtUtc_MatchesStatus",
                "([Status] = 1 AND [ReleasedAtUtc] IS NULL) OR ([Status] = 2 AND [ReleasedAtUtc] IS NOT NULL)");
        });

        // The primary key is what gives us idempotency: a replayed reservation collides here,
        // and a concurrent duplicate blocks on the key until the first transaction settles.
        builder.HasKey(e => e.ReservationId);

        // varchar, not nvarchar: a UUID is ASCII, and the default string key mapping of
        // nvarchar(450) wastes most of a key page for no benefit.
        builder.Property(e => e.ReservationId)
            .HasMaxLength(ReservationIdMaxLength)
            .IsUnicode(false)
            .ValueGeneratedNever();

        builder.Property(e => e.Status)
            .HasColumnType("tinyint")
            .HasConversion<byte>()
            .IsRequired();

        // Millisecond precision is ample for an audit timestamp; the default datetime2(7)
        // costs more bytes for resolution nothing here reads.
        builder.Property(e => e.CreatedAtUtc)
            .HasColumnType("datetime2(3)")
            .IsRequired();

        builder.Property(e => e.ReleasedAtUtc)
            .HasColumnType("datetime2(3)");
    }
}
