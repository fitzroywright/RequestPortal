using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20260924235900_AddPortalMessaging")]
public partial class AddPortalMessaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "message_threads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                StaffVisible = table.Column<bool>(type: "boolean", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ClosedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_message_threads", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "portal_messages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                SenderUserId = table.Column<Guid>(type: "uuid", nullable: true),
                SenderKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SenderDisplayName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                SentUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_portal_messages", x => x.Id);
                table.ForeignKey(
                    name: "FK_portal_messages_message_threads_ThreadId",
                    column: x => x.ThreadId,
                    principalTable: "message_threads",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "message_recipients",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ReadUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ArchivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_message_recipients", x => new { x.MessageId, x.UserId });
                table.ForeignKey(
                    name: "FK_message_recipients_portal_messages_MessageId",
                    column: x => x.MessageId,
                    principalTable: "portal_messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_message_threads_OrganizationId",
            table: "message_threads",
            column: "OrganizationId");

        migrationBuilder.CreateIndex(
            name: "IX_message_threads_RequestId",
            table: "message_threads",
            column: "RequestId");

        migrationBuilder.CreateIndex(
            name: "IX_message_threads_StaffVisible_CreatedUtc",
            table: "message_threads",
            columns: new[] { "StaffVisible", "CreatedUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_portal_messages_ThreadId_SentUtc",
            table: "portal_messages",
            columns: new[] { "ThreadId", "SentUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_message_recipients_UserId_ReadUtc",
            table: "message_recipients",
            columns: new[] { "UserId", "ReadUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "message_recipients");
        migrationBuilder.DropTable(name: "portal_messages");
        migrationBuilder.DropTable(name: "message_threads");
    }
}
