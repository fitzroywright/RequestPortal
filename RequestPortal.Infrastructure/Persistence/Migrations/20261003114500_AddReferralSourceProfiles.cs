using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261003114500_AddReferralSourceProfiles")]
public partial class AddReferralSourceProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReferralSource",
            table: "AspNetUsers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReferralSource",
            table: "organizations",
            type: "character varying(150)",
            maxLength: 150,
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE \"AspNetUsers\" SET \"IndividualProfileUpdateRequired\" = TRUE, \"IndividualProfileUpdateRequiredUtc\" = NOW() WHERE \"IsInternalStaff\" = FALSE AND (\"ReferralSource\" IS NULL OR btrim(\"ReferralSource\") = '');");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReferralSource",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "ReferralSource",
            table: "organizations");
    }
}
