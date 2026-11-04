using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveSubmittedIntakeTelephones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '60s';");
            migrationBuilder.AddColumn<string>(
                name: "OriginalSecondaryTelephone",
                table: "quick_intakes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTelephone",
                table: "quick_intakes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Retain original submitted telephones during application rollback; do not drop captured source data.");
        }
    }
}
