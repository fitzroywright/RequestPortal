using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261007204500_ExpandQuickIntake")]
public partial class ExpandQuickIntake : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "Town",
            table: "quick_intakes",
            newName: "CommunityDistrict");

        migrationBuilder.AddColumn<string>(
            name: "AssistancePrograms",
            table: "quick_intakes",
            type: "character varying(500)",
            maxLength: 500,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<DateOnly>(
            name: "DateOfBirth",
            table: "quick_intakes",
            type: "date",
            nullable: false,
            defaultValue: new DateOnly(1900, 1, 1));

        migrationBuilder.AddColumn<string>(
            name: "Email",
            table: "quick_intakes",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EmploymentStatus",
            table: "quick_intakes",
            type: "character varying(50)",
            maxLength: 50,
            nullable: false,
            defaultValue: "Unemployed");

        migrationBuilder.AddColumn<string>(
            name: "Gender",
            table: "quick_intakes",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Non-Binary");

        migrationBuilder.AddColumn<bool>(
            name: "HasChildrenUnder18LivingFullTime",
            table: "quick_intakes",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "HasDisabilityOrSeriousMedicalCondition",
            table: "quick_intakes",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "HelpCategory",
            table: "quick_intakes",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            defaultValue: "Food");

        migrationBuilder.AddColumn<string>(
            name: "MaritalStatus",
            table: "quick_intakes",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Single");

        migrationBuilder.AddColumn<decimal>(
            name: "MonthlyIncome",
            table: "quick_intakes",
            type: "numeric(14,2)",
            precision: 14,
            scale: 2,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<string>(
            name: "StreetAddress",
            table: "quick_intakes",
            type: "character varying(250)",
            maxLength: 250,
            nullable: false,
            defaultValue: "Not captured");

        migrationBuilder.AlterColumn<string>(
            name: "CommunityDistrict",
            table: "quick_intakes",
            type: "character varying(150)",
            maxLength: 150,
            nullable: false,
            defaultValue: "Not captured",
            oldClrType: typeof(string),
            oldType: "character varying(150)",
            oldMaxLength: 150,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AssistancePrograms", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "DateOfBirth", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "Email", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "EmploymentStatus", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "Gender", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "HasChildrenUnder18LivingFullTime", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "HasDisabilityOrSeriousMedicalCondition", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "HelpCategory", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "MaritalStatus", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "MonthlyIncome", table: "quick_intakes");
        migrationBuilder.DropColumn(name: "StreetAddress", table: "quick_intakes");

        migrationBuilder.AlterColumn<string>(
            name: "CommunityDistrict",
            table: "quick_intakes",
            type: "character varying(150)",
            maxLength: 150,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(150)",
            oldMaxLength: 150);

        migrationBuilder.RenameColumn(
            name: "CommunityDistrict",
            table: "quick_intakes",
            newName: "Town");
    }
}
