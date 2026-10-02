using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261002133000_AddOrganizationIntakeProfile")]
public partial class AddOrganizationIntakeProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AlternativePhone", "organizations", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<decimal>("AnnualOperatingBudget", "organizations", type: "numeric(18,2)", precision: 18, scale: 2, nullable: true);
        migrationBuilder.AddColumn<int>("ApproximateBeneficiariesServed", "organizations", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("AuthorizedSignatoryName", "organizations", type: "character varying(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<string>("AuthorizedSignatoryTitle", "organizations", type: "character varying(150)", maxLength: 150, nullable: true);
        migrationBuilder.AddColumn<string>("CountryOfRegistration", "organizations", type: "character varying(100)", maxLength: 100, nullable: true, defaultValue: "Jamaica");
        migrationBuilder.AddColumn<DateOnly>("DateEstablished", "organizations", type: "date", nullable: true);
        migrationBuilder.AddColumn<string>("DistrictArea", "organizations", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<int>("EmployeeCount", "organizations", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("GeographicServiceArea", "organizations", type: "character varying(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>("MailingAddress", "organizations", type: "character varying(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>("MainActivities", "organizations", type: "character varying(4000)", maxLength: 4000, nullable: true);
        migrationBuilder.AddColumn<string>("Parish", "organizations", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("PopulationGroupsServed", "organizations", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>("PremisesSituation", "organizations", type: "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<bool>("PreviouslyReceivedFfpAssistance", "organizations", type: "boolean", nullable: true);
        migrationBuilder.AddColumn<string>("PrimaryContactTitle", "organizations", type: "character varying(150)", maxLength: 150, nullable: true);
        migrationBuilder.AddColumn<string>("PrimaryFundingSources", "organizations", type: "character varying(2000)", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<string>("SecondaryContactEmail", "organizations", type: "character varying(320)", maxLength: 320, nullable: true);
        migrationBuilder.AddColumn<string>("SecondaryContactName", "organizations", type: "character varying(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<string>("SecondaryContactPhone", "organizations", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("TaxRegistrationNumber", "organizations", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("TradingName", "organizations", type: "character varying(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<int>("VolunteerCount", "organizations", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("Website", "organizations", type: "character varying(500)", maxLength: 500, nullable: true);

        migrationBuilder.Sql("UPDATE organizations SET \"CountryOfRegistration\" = 'Jamaica' WHERE \"CountryOfRegistration\" IS NULL OR btrim(\"CountryOfRegistration\") = '';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AlternativePhone", "organizations");
        migrationBuilder.DropColumn("AnnualOperatingBudget", "organizations");
        migrationBuilder.DropColumn("ApproximateBeneficiariesServed", "organizations");
        migrationBuilder.DropColumn("AuthorizedSignatoryName", "organizations");
        migrationBuilder.DropColumn("AuthorizedSignatoryTitle", "organizations");
        migrationBuilder.DropColumn("CountryOfRegistration", "organizations");
        migrationBuilder.DropColumn("DateEstablished", "organizations");
        migrationBuilder.DropColumn("DistrictArea", "organizations");
        migrationBuilder.DropColumn("EmployeeCount", "organizations");
        migrationBuilder.DropColumn("GeographicServiceArea", "organizations");
        migrationBuilder.DropColumn("MailingAddress", "organizations");
        migrationBuilder.DropColumn("MainActivities", "organizations");
        migrationBuilder.DropColumn("Parish", "organizations");
        migrationBuilder.DropColumn("PopulationGroupsServed", "organizations");
        migrationBuilder.DropColumn("PremisesSituation", "organizations");
        migrationBuilder.DropColumn("PreviouslyReceivedFfpAssistance", "organizations");
        migrationBuilder.DropColumn("PrimaryContactTitle", "organizations");
        migrationBuilder.DropColumn("PrimaryFundingSources", "organizations");
        migrationBuilder.DropColumn("SecondaryContactEmail", "organizations");
        migrationBuilder.DropColumn("SecondaryContactName", "organizations");
        migrationBuilder.DropColumn("SecondaryContactPhone", "organizations");
        migrationBuilder.DropColumn("TaxRegistrationNumber", "organizations");
        migrationBuilder.DropColumn("TradingName", "organizations");
        migrationBuilder.DropColumn("VolunteerCount", "organizations");
        migrationBuilder.DropColumn("Website", "organizations");
    }
}
