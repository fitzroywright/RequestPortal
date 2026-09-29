using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260929212500_StructureRequestItems")]
public partial class StructureRequestItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ItemName",
            table: "assistance_request_items",
            type: "character varying(250)",
            maxLength: 250,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Notes",
            table: "assistance_request_items",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE assistance_request_items
            SET "ItemName" = "Description"
            WHERE "ItemName" IS NULL OR BTRIM("ItemName") = '';
            """);

        migrationBuilder.AlterColumn<string>(
            name: "ItemName",
            table: "assistance_request_items",
            type: "character varying(250)",
            maxLength: 250,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(250)",
            oldMaxLength: 250,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Description",
            table: "assistance_request_items",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(1000)",
            oldMaxLength: 1000);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Description",
            table: "assistance_request_items",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(2000)",
            oldMaxLength: 2000);

        migrationBuilder.DropColumn(name: "ItemName", table: "assistance_request_items");
        migrationBuilder.DropColumn(name: "Notes", table: "assistance_request_items");
    }
}
