using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    QuantityOnHand = table.Column<int>(type: "int", nullable: false),
                    QuantityReserved = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.ProductId);
                    table.CheckConstraint("CK_InventoryItems_QuantityOnHand_NonNegative", "[QuantityOnHand] >= 0");
                    table.CheckConstraint("CK_InventoryItems_QuantityReserved_NonNegative", "[QuantityReserved] >= 0");
                    table.CheckConstraint("CK_InventoryItems_QuantityReserved_NotAboveOnHand", "[QuantityReserved] <= [QuantityOnHand]");
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    ReservationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.ReservationId);
                    table.CheckConstraint("CK_Reservations_ReleasedAtUtc_MatchesStatus", "([Status] = 1 AND [ReleasedAtUtc] IS NULL) OR ([Status] = 2 AND [ReleasedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_Reservations_Status", "[Status] IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "ReservationItems",
                columns: table => new
                {
                    ReservationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationItems", x => new { x.ReservationId, x.ProductId });
                    table.CheckConstraint("CK_ReservationItems_Quantity_Positive", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_ReservationItems_InventoryItems_ProductId",
                        column: x => x.ProductId,
                        principalTable: "InventoryItems",
                        principalColumn: "ProductId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReservationItems_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "ReservationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservationItems_ProductId",
                table: "ReservationItems",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReservationItems");

            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "Reservations");
        }
    }
}
