using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261105070000_IntegrationExceptionDetails")]
public sealed class IntegrationExceptionDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
        migrationBuilder.AddColumn<string>(
            name: "ExceptionDetails",
            table: "integration_attempts",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
        migrationBuilder.DropColumn(name: "ExceptionDetails", table: "integration_attempts");
    }
}
