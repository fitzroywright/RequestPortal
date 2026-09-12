using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260912223000_AddManagementDashboardGeo")]
public partial class AddManagementDashboardGeo : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "request_geo_locations",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                Parish = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Community = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                Latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                Longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                Precision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ResolvedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_request_geo_locations", x => x.RequestId);
                table.ForeignKey(
                    name: "FK_request_geo_locations_assistance_requests_RequestId",
                    column: x => x.RequestId,
                    principalTable: "assistance_requests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_request_geo_locations_Parish",
            table: "request_geo_locations",
            column: "Parish");

        migrationBuilder.CreateIndex(
            name: "IX_request_geo_locations_Parish_Community",
            table: "request_geo_locations",
            columns: new[] { "Parish", "Community" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "request_geo_locations");
    }
}
