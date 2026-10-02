using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261002020000_AddPermanentRequestCodeSequence")]
public sealed class AddPermanentRequestCodeSequence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS request_code_sequence AS bigint START WITH 10000");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Issued references remain permanent even if this schema change is rolled back.
        // Preserve the sequence high-water mark so a subsequent upgrade cannot reuse it.
    }
}
