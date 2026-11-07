using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaptureQuickIntakeTransferOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '60s';");
            migrationBuilder.AddColumn<int>(
                name: "PayFrequency",
                table: "quick_intakes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhoneLineType",
                table: "quick_intakes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Retain captured Quick Intake transfer options during application rollback.");
        }
    }
}
