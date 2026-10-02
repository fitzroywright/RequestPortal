using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261002130000_AddIndividualIntakeProfile")]
public partial class AddIndividualIntakeProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AlternativePhoneNumber", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<int>("ChildrenUnder18", "AspNetUsers", type: "integer", nullable: true);
        migrationBuilder.AddColumn<DateOnly>("DateOfBirth", "AspNetUsers", type: "date", nullable: true);
        migrationBuilder.AddColumn<string>("DistrictArea", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("EmployerInstitution", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("EmploymentStatus", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("Gender", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("HomeAddress", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<int>("HouseholdSize", "AspNetUsers", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("HouseholdLivingSituation", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("HousingSituation", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("MaritalStatus", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<decimal>("MonthlyHouseholdIncome", "AspNetUsers", type: "numeric", nullable: true);
        migrationBuilder.AddColumn<string>("NationalIdTrn", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<int>("NumberOfDependants", "AspNetUsers", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("Parish", "AspNetUsers", type: "text", nullable: true);

        migrationBuilder.Sql(
            "UPDATE \"AspNetUsers\" SET \"IndividualProfileUpdateRequired\" = TRUE, \"IndividualProfileUpdateRequiredUtc\" = NOW() WHERE \"IsInternalStaff\" = FALSE;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AlternativePhoneNumber", "AspNetUsers");
        migrationBuilder.DropColumn("ChildrenUnder18", "AspNetUsers");
        migrationBuilder.DropColumn("DateOfBirth", "AspNetUsers");
        migrationBuilder.DropColumn("DistrictArea", "AspNetUsers");
        migrationBuilder.DropColumn("EmployerInstitution", "AspNetUsers");
        migrationBuilder.DropColumn("EmploymentStatus", "AspNetUsers");
        migrationBuilder.DropColumn("Gender", "AspNetUsers");
        migrationBuilder.DropColumn("HomeAddress", "AspNetUsers");
        migrationBuilder.DropColumn("HouseholdSize", "AspNetUsers");
        migrationBuilder.DropColumn("HouseholdLivingSituation", "AspNetUsers");
        migrationBuilder.DropColumn("HousingSituation", "AspNetUsers");
        migrationBuilder.DropColumn("MaritalStatus", "AspNetUsers");
        migrationBuilder.DropColumn("MonthlyHouseholdIncome", "AspNetUsers");
        migrationBuilder.DropColumn("NationalIdTrn", "AspNetUsers");
        migrationBuilder.DropColumn("NumberOfDependants", "AspNetUsers");
        migrationBuilder.DropColumn("Parish", "AspNetUsers");
    }
}
