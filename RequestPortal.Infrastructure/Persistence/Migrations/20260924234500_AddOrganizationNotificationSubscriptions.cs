using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260924234500_AddOrganizationNotificationSubscriptions")]
public partial class AddOrganizationNotificationSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ReceiveMembershipNotifications",
            table: "organization_memberships",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ReceiveOrganizationNotifications",
            table: "organization_memberships",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ReceiveRequestNotifications",
            table: "organization_memberships",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE organization_memberships
            SET "ReceiveOrganizationNotifications" = TRUE,
                "ReceiveRequestNotifications" = TRUE,
                "ReceiveMembershipNotifications" = TRUE
            WHERE "Status" = 'Approved'
              AND "Role" = 'Administrator';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ReceiveMembershipNotifications",
            table: "organization_memberships");

        migrationBuilder.DropColumn(
            name: "ReceiveOrganizationNotifications",
            table: "organization_memberships");

        migrationBuilder.DropColumn(
            name: "ReceiveRequestNotifications",
            table: "organization_memberships");
    }
}
