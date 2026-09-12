using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations;

public partial class AddProjectProposalRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "project_proposal_revisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                RichContentHtml = table.Column<string>(type: "text", nullable: false),
                EditedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                SavedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Automatic = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_project_proposal_revisions", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_project_proposal_revisions_ProposalId_Version",
            table: "project_proposal_revisions",
            columns: new[] { "ProposalId", "Version" });

        migrationBuilder.CreateIndex(
            name: "IX_project_proposal_revisions_RequestId_SavedUtc",
            table: "project_proposal_revisions",
            columns: new[] { "RequestId", "SavedUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "project_proposal_revisions");
    }
}
