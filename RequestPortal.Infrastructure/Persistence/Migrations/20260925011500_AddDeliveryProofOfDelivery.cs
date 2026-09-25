using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925011500_AddDeliveryProofOfDelivery")]
public partial class AddDeliveryProofOfDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "deliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DocumentReference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                AccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RecipientName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                DeliveryPerson = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                AcknowledgedBy = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AcknowledgedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                SignatureData = table.Column<string>(type: "text", nullable: true),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                SyncedToFfpManagerUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_deliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_deliveries_assistance_requests_RequestId",
                    column: x => x.RequestId,
                    principalTable: "assistance_requests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_deliveries_RequestId", table: "deliveries", column: "RequestId");
        migrationBuilder.CreateIndex(name: "IX_deliveries_DocumentReference", table: "deliveries", column: "DocumentReference");
        migrationBuilder.CreateIndex(name: "IX_deliveries_Status_SyncedToFfpManagerUtc", table: "deliveries", columns: new[] { "Status", "SyncedToFfpManagerUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "deliveries");
    }
}
