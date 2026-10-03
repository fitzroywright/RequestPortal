using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261003224500_AddApplicantProfilePhoto")]
public partial class AddApplicantProfilePhoto : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfilePhotoContentType",
            table: "AspNetUsers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfilePhotoStorageKey",
            table: "AspNetUsers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ProfilePhotoUpdatedUtc",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfilePhotoContentType",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "ProfilePhotoStorageKey",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "ProfilePhotoUpdatedUtc",
            table: "AspNetUsers");
    }
}
