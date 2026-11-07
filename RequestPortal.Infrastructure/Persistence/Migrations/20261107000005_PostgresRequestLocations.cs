using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostgresRequestLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
            migrationBuilder.CreateTable(
                name: "request_location_import_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerifiedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ImportedLocations = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_location_import_state", x => x.Id);
                    table.CheckConstraint("CK_request_location_import_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "request_locations",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceText = table.Column<string>(type: "text", nullable: true),
                    Parish = table.Column<string>(type: "text", nullable: false),
                    Community = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    Precision = table.Column<int>(type: "integer", nullable: false),
                    ResolvedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ImportedStorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ImportedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImportedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_locations", x => x.RequestId);
                    table.CheckConstraint("CK_request_location_coordinates", "(\"Latitude\" IS NULL) = (\"Longitude\" IS NULL) AND (\"Latitude\" IS NULL OR \"Latitude\" BETWEEN -90 AND 90) AND (\"Longitude\" IS NULL OR \"Longitude\" BETWEEN -180 AND 180)");
                    table.CheckConstraint("CK_request_location_parish", "length(btrim(\"Parish\")) > 0");
                    table.CheckConstraint("CK_request_location_precision", "\"Precision\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_request_locations_assistance_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "assistance_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.Sql("CREATE TRIGGER requestportal_dashboard_refresh AFTER INSERT OR UPDATE OR DELETE ON request_locations FOR EACH STATEMENT EXECUTE FUNCTION requestportal_queue_dashboard_refresh();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Request locations are authoritative PostgreSQL data. Application rollback must retain these tables.");
        }
    }
}
