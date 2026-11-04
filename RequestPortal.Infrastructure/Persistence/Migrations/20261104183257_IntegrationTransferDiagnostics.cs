using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationTransferDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
            migrationBuilder.AddColumn<bool>(
                name: "IsTerminal",
                table: "outbox_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptUtc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArtifactsSynchronizedUtc",
                table: "integration_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "integration_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                table: "integration_links",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureClassification",
                table: "integration_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureStage",
                table: "integration_links",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryUtc",
                table: "integration_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDatabase",
                table: "integration_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetServer",
                table: "integration_links",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VerifiedUtc",
                table: "integration_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "integration_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Classification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Explanation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integration_attempts_integration_links_LinkId",
                        column: x => x.LinkId,
                        principalTable: "integration_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Correlate existing durable messages without replaying or rewriting their payload/outcome.
            migrationBuilder.Sql("""
                UPDATE outbox_messages SET "RequestId"=COALESCE("Payload"->>'RequestId',"Payload"->>'requestId')::uuid
                WHERE COALESCE("Payload"->>'RequestId',"Payload"->>'requestId') ~* '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$';
                UPDATE outbox_messages SET "NextAttemptUtc"=split_part(substring("LastError" FROM 10),'|',1)::timestamptz
                WHERE "ProcessedUtc" IS NULL AND "LastError" ~ '^RETRY_AT=[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+([+]00:00|Z)[|]';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedUtc_NextAttemptUtc_OccurredUtc",
                table: "outbox_messages",
                columns: new[] { "ProcessedUtc", "NextAttemptUtc", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_RequestId",
                table: "outbox_messages",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_integration_links_Status_NextRetryUtc",
                table: "integration_links",
                columns: new[] { "Status", "NextRetryUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_attempts_LinkId_StartedUtc",
                table: "integration_attempts",
                columns: new[] { "LinkId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
            "Retain integration attempt history during application rollback. This additive schema supports the previous application.");
    }
}
