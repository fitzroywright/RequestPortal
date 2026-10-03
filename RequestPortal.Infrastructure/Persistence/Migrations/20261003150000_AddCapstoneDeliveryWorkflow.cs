using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261003150000_AddCapstoneDeliveryWorkflow")]
public partial class AddCapstoneDeliveryWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FfpDeliveryOrderId",
            table: "deliveries",
            type: "character varying(160)",
            maxLength: 160,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql("""
            UPDATE deliveries
            SET "FfpDeliveryOrderId" = "DocumentReference"
            WHERE "FfpDeliveryOrderId" = '';
            """);

        migrationBuilder.AddColumn<bool>(
            name: "ReturnsPermitted",
            table: "deliveries",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<Guid>(
            name: "ConfirmedByUserId",
            table: "deliveries",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "RecipientConfirmedUtc",
            table: "deliveries",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RecipientConfirmationJson",
            table: "deliveries",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RecipientDiscrepancyJson",
            table: "deliveries",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "RecipientConfirmationSyncedUtc",
            table: "deliveries",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "delivery_returns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                FfpDeliveryItemId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                ItemDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                FfpReturnId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                StatusDetail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                StatusUpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SyncedToFfpManagerUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_delivery_returns", x => x.Id);
                table.ForeignKey(
                    name: "FK_delivery_returns_assistance_requests_RequestId",
                    column: x => x.RequestId,
                    principalTable: "assistance_requests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_delivery_returns_deliveries_DeliveryId",
                    column: x => x.DeliveryId,
                    principalTable: "deliveries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_deliveries_RequestId_FfpDeliveryOrderId",
            table: "deliveries",
            columns: new[] { "RequestId", "FfpDeliveryOrderId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_deliveries_RecipientConfirmationSyncedUtc",
            table: "deliveries",
            column: "RecipientConfirmationSyncedUtc");

        migrationBuilder.CreateIndex(name: "IX_delivery_returns_RequestId", table: "delivery_returns", column: "RequestId");
        migrationBuilder.CreateIndex(name: "IX_delivery_returns_DeliveryId", table: "delivery_returns", column: "DeliveryId");
        migrationBuilder.CreateIndex(name: "IX_delivery_returns_SyncedToFfpManagerUtc", table: "delivery_returns", column: "SyncedToFfpManagerUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "delivery_returns");
        migrationBuilder.DropIndex(name: "IX_deliveries_RequestId_FfpDeliveryOrderId", table: "deliveries");
        migrationBuilder.DropIndex(name: "IX_deliveries_RecipientConfirmationSyncedUtc", table: "deliveries");
        migrationBuilder.DropColumn(name: "FfpDeliveryOrderId", table: "deliveries");
        migrationBuilder.DropColumn(name: "ReturnsPermitted", table: "deliveries");
        migrationBuilder.DropColumn(name: "ConfirmedByUserId", table: "deliveries");
        migrationBuilder.DropColumn(name: "RecipientConfirmedUtc", table: "deliveries");
        migrationBuilder.DropColumn(name: "RecipientConfirmationJson", table: "deliveries");
        migrationBuilder.DropColumn(name: "RecipientDiscrepancyJson", table: "deliveries");
        migrationBuilder.DropColumn(name: "RecipientConfirmationSyncedUtc", table: "deliveries");
    }
}
