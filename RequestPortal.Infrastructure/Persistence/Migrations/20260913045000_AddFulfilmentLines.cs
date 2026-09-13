using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

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
                table.ForeignKey(
                    name: "FK_fulfilment_lines_fulfilments_FulfilmentId",
                    column: x => x.FulfilmentId,
                    principalTable: "fulfilments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_fulfilment_lines_assistance_request_items_RequestItemId",
                    column: x => x.RequestItemId,
                    principalTable: "assistance_request_items",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
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

        migrationBuilder.Sql("""
            INSERT INTO fulfilment_lines
                ("Id", "FulfilmentId", "RequestItemId", "RequestedQuantity", "FulfilledQuantity", "FulfilledValue")
            SELECT
                md5(f."Id"::text || '|' || i."Id"::text)::uuid,
                f."Id",
                i."Id",
                i."Quantity",
                CASE WHEN f."Status" = 'Fulfilled' THEN i."Quantity" ELSE 0 END,
                0
            FROM fulfilments f
            INNER JOIN assistance_request_items i ON i."RequestId" = f."RequestId"
            ON CONFLICT ("FulfilmentId", "RequestItemId") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "fulfilment_lines");
    }
}
