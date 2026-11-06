using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagementDashboardAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
            migrationBuilder.CreateTable(
                name: "management_dashboard_aggregate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false),
                    AggregateData = table.Column<string>(type: "jsonb", nullable: true),
                    LastUpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReconciledUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefreshStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextRefreshUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_management_dashboard_aggregate", x => x.Id);
                    table.CheckConstraint("CK_management_dashboard_singleton", "\"Id\" = 1");
                });
            migrationBuilder.Sql("""
                INSERT INTO management_dashboard_aggregate
                ("Id","SnapshotVersion","AggregateData","RefreshStatus","AttemptCount","NextRefreshUtc","CreatedUtc")
                VALUES (1,0,NULL,'Pending',0,now(),now());
                CREATE FUNCTION requestportal_queue_dashboard_refresh() RETURNS trigger
                LANGUAGE plpgsql SET search_path FROM CURRENT AS $body$
                BEGIN
                    INSERT INTO outbox_messages
                    ("Id","MessageType","Payload","OccurredUtc","AttemptCount","IsTerminal")
                    VALUES (md5('RequestPortal.Dashboard:' || txid_current()::text)::uuid,
                            'RequestPortal.ManagementDashboardRefreshRequested.v1',
                            jsonb_build_object('Reason',TG_TABLE_NAME),clock_timestamp(),0,false)
                    ON CONFLICT ("Id") DO NOTHING;
                    RETURN NULL;
                END;
                $body$;
                CREATE INDEX "IX_outbox_dashboard_refresh" ON outbox_messages ("OccurredUtc")
                WHERE "MessageType"='RequestPortal.ManagementDashboardRefreshRequested.v1' AND "ProcessedUtc" IS NULL;
                """);
            foreach(string table in SourceTables)
                migrationBuilder.Sql($"CREATE TRIGGER requestportal_dashboard_refresh AFTER INSERT OR UPDATE OR DELETE ON \"{table}\" FOR EACH STATEMENT EXECUTE FUNCTION requestportal_queue_dashboard_refresh();");
        }

        private static readonly string[] SourceTables = ["assistance_requests","request_status_history","quick_intakes","documents","deliveries","delivery_returns","organizations","request_categories","program_classifications","integration_links"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s';");
            foreach(string table in SourceTables)
                migrationBuilder.Sql($"DROP TRIGGER requestportal_dashboard_refresh ON \"{table}\";");
            migrationBuilder.Sql("DROP FUNCTION requestportal_queue_dashboard_refresh(); DROP INDEX \"IX_outbox_dashboard_refresh\";");
            migrationBuilder.DropTable(
                name: "management_dashboard_aggregate");
        }
    }
}
