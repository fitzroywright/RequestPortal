using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261014170000_AddIntakeApplicantProfileFields")]
public partial class AddIntakeApplicantProfileFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("FirstName", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("LastName", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>("HasChildrenUnder18LivingFullTime", "AspNetUsers", type: "boolean", nullable: true);
        migrationBuilder.AddColumn<bool>("HasDisabilityOrSeriousMedicalCondition", "AspNetUsers", type: "boolean", nullable: true);
        migrationBuilder.AddColumn<string>("AssistancePrograms", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("VictimCircumstances", "AspNetUsers", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("VictimCircumstances", "quick_intakes", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (string column in new[] { "FirstName", "LastName", "HasChildrenUnder18LivingFullTime",
            "HasDisabilityOrSeriousMedicalCondition", "AssistancePrograms", "VictimCircumstances" })
            migrationBuilder.DropColumn(column, "AspNetUsers");
        migrationBuilder.DropColumn("VictimCircumstances", "quick_intakes");
    }
}
