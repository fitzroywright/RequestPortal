using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261103160000_ConsolidateStaffRoles")]
public sealed class ConsolidateStaffRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(StaffRoleConsolidation.Sql);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Staff role consolidation is forward-only. Preserve current assignments and audit data during application rollback.");
}
