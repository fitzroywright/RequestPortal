using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261001173000_AddMessageThreadAssignment")]
public partial class AddMessageThreadAssignment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AssignedToStaffUserId",
            table: "message_threads",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AssignedUtc",
            table: "message_threads",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_message_threads_AssignedToStaffUserId",
            table: "message_threads",
            column: "AssignedToStaffUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_message_threads_AssignedToStaffUserId",
            table: "message_threads");

        migrationBuilder.DropColumn(
            name: "AssignedToStaffUserId",
            table: "message_threads");

        migrationBuilder.DropColumn(
            name: "AssignedUtc",
            table: "message_threads");
    }
}
