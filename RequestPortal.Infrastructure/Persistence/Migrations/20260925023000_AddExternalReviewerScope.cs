using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925023000_AddExternalReviewerScope")]
public partial class AddExternalReviewerScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "OrganizationId", table: "external_reviewer_invitations", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "RequestId", table: "external_reviewer_invitations", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "FromUtc", table: "external_reviewer_invitations", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ToUtc", table: "external_reviewer_invitations", type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateIndex(name: "IX_external_reviewer_invitations_OrganizationId", table: "external_reviewer_invitations", column: "OrganizationId");
        migrationBuilder.CreateIndex(name: "IX_external_reviewer_invitations_RequestId", table: "external_reviewer_invitations", column: "RequestId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_external_reviewer_invitations_OrganizationId", table: "external_reviewer_invitations");
        migrationBuilder.DropIndex(name: "IX_external_reviewer_invitations_RequestId", table: "external_reviewer_invitations");
        migrationBuilder.DropColumn(name: "OrganizationId", table: "external_reviewer_invitations");
        migrationBuilder.DropColumn(name: "RequestId", table: "external_reviewer_invitations");
        migrationBuilder.DropColumn(name: "FromUtc", table: "external_reviewer_invitations");
        migrationBuilder.DropColumn(name: "ToUtc", table: "external_reviewer_invitations");
    }
}
