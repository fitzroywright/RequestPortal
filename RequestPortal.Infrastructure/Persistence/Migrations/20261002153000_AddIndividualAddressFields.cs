using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261002153000_AddIndividualAddressFields")]
public partial class AddIndividualAddressFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HomeAddressLine2",
            table: "AspNetUsers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Town",
            table: "AspNetUsers",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "HomeAddressLine2",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "Town",
            table: "AspNetUsers");
    }
}
