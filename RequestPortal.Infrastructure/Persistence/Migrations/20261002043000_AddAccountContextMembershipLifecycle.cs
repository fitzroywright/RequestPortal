using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

public partial class AddAccountContextMembershipLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IndividualProfileUpdateRequired",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "IndividualProfileUpdateRequiredUtc",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "StatusBeforeRepresentativeReview",
            table: "assistance_requests",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IndividualProfileUpdateRequired", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "IndividualProfileUpdateRequiredUtc", table: "AspNetUsers");
        migrationBuilder.DropColumn(name: "StatusBeforeRepresentativeReview", table: "assistance_requests");
    }
}
