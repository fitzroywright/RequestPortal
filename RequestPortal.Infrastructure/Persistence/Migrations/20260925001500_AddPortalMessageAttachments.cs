using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260925001500_AddPortalMessageAttachments")]
public partial class AddPortalMessageAttachments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "message_attachments",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_message_attachments", x => new { x.MessageId, x.DocumentId });
                table.ForeignKey(
                    name: "FK_message_attachments_portal_messages_MessageId",
                    column: x => x.MessageId,
                    principalTable: "portal_messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_message_attachments_DocumentId",
            table: "message_attachments",
            column: "DocumentId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "message_attachments");
    }
}
