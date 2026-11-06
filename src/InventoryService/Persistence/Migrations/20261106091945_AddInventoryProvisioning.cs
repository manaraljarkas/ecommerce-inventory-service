using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryProvisionings",
                columns: table => new
                {
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    InitialQuantity = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryProvisionings", x => x.ProductId);
                    table.CheckConstraint("CK_InventoryProvisionings_InitialQuantity_NonNegative", "[InitialQuantity] >= 0");
                    table.ForeignKey(
                        name: "FK_InventoryProvisionings_InventoryItems_ProductId",
                        column: x => x.ProductId,
                        principalTable: "InventoryItems",
                        principalColumn: "ProductId",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryProvisionings");
        }
    }
}
