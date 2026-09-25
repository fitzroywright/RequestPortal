using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925014500_StrengthenAudit")]
public partial class StrengthenAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ApplicationContext",
            table: "audit_entries",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            defaultValue: "RequestPortal");

        migrationBuilder.AddColumn<string>(
            name: "Outcome",
            table: "audit_entries",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "Succeeded");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ApplicationContext", table: "audit_entries");
        migrationBuilder.DropColumn(name: "Outcome", table: "audit_entries");
    }
}
