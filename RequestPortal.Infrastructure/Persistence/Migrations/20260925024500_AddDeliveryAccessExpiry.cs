using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925024500_AddDeliveryAccessExpiry")]
public partial class AddDeliveryAccessExpiry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AccessExpiresUtc",
            table: "deliveries",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE deliveries
            SET "AccessExpiresUtc" = "CreatedUtc" + INTERVAL '30 days'
            WHERE "AccessExpiresUtc" IS NULL;
            """);

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "AccessExpiresUtc",
            table: "deliveries",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "timestamp with time zone",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AccessExpiresUtc", table: "deliveries");
    }
}
