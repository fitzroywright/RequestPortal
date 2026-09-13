using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260913045000_AddFulfilmentLines")]
public partial class AddFulfilmentLines : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "fulfilment_lines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FulfilmentId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestItemId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                FulfilledQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                FulfilledValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                LastProgressUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_fulfilment_lines", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_fulfilment_lines_FulfilmentId_RequestItemId",
            table: "fulfilment_lines",
            columns: new[] { "FulfilmentId", "RequestItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_fulfilment_lines_RequestItemId",
            table: "fulfilment_lines",
            column: "RequestItemId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "fulfilment_lines");
    }
}
