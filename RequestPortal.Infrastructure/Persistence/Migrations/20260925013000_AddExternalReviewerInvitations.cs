using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925013000_AddExternalReviewerInvitations")]
public partial class AddExternalReviewerInvitations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "external_reviewer_invitations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RedeemedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAccessUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_external_reviewer_invitations", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_external_reviewer_invitations_Email", table: "external_reviewer_invitations", column: "Email");
        migrationBuilder.CreateIndex(name: "IX_external_reviewer_invitations_ExpiresUtc", table: "external_reviewer_invitations", column: "ExpiresUtc");
        migrationBuilder.CreateIndex(name: "IX_external_reviewer_invitations_RevokedUtc", table: "external_reviewer_invitations", column: "RevokedUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "external_reviewer_invitations");
    }
}
