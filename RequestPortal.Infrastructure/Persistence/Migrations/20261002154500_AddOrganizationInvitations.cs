using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261002154500_AddOrganizationInvitations")]
public partial class AddOrganizationInvitations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_invitations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                AcceptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_organization_invitations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_organization_invitations_Email",
            table: "organization_invitations",
            column: "Email");

        migrationBuilder.CreateIndex(
            name: "IX_organization_invitations_ExpiresUtc",
            table: "organization_invitations",
            column: "ExpiresUtc");

        migrationBuilder.CreateIndex(
            name: "IX_organization_invitations_OrganizationId",
            table: "organization_invitations",
            column: "OrganizationId");

        migrationBuilder.CreateIndex(
            name: "IX_organization_invitations_OrganizationId_Email_Status",
            table: "organization_invitations",
            columns: new[] { "OrganizationId", "Email", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "organization_invitations");
    }
}
