using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261007140000_AddAnonymousQuickIntake")]
public partial class AddAnonymousQuickIntake : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "quick_intakes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                AnonymousUserId = table.Column<Guid>(type: "uuid", nullable: false),
                IndividualContextId = table.Column<Guid>(type: "uuid", nullable: false),
                FirstName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                LastName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                MobileNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Parish = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Town = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConvertedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                ConvertedByStaffUserId = table.Column<Guid>(type: "uuid", nullable: true),
                ConvertedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quick_intakes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_quick_intakes_RequestId",
            table: "quick_intakes",
            column: "RequestId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_quick_intakes_MobileNumber",
            table: "quick_intakes",
            column: "MobileNumber");

        migrationBuilder.CreateIndex(
            name: "IX_quick_intakes_ConvertedToUserId",
            table: "quick_intakes",
            column: "ConvertedToUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "quick_intakes");
    }
}
