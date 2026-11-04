using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RequestPortal.Infrastructure.Persistence.Migrations;

[DbContext(typeof(RequestPortalDbContext))]
[Migration("20261104070000_ApplicantIdentityRecovery")]
public sealed class ApplicantIdentityRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE requestportal_identity_recoveries (
            id uuid PRIMARY KEY, applicant_id uuid NOT NULL REFERENCES "AspNetUsers"("Id"),
            provider text NOT NULL CHECK(provider IN ('Google','Microsoft','Yahoo','Apple','Facebook')),
            reason text NOT NULL, initiated_by uuid NOT NULL REFERENCES "AspNetUsers"("Id"),
            created_utc timestamptz NOT NULL, expires_utc timestamptz NOT NULL,
            status text NOT NULL CHECK(status IN ('Pending','Approved','Redeeming','Completed','Cancelled','Failed')),
            approved_by uuid NULL REFERENCES "AspNetUsers"("Id"), verification_reference text NULL,
            approved_utc timestamptz NULL, token_hash text NULL, consumed_utc timestamptz NULL,
            completed_utc timestamptz NULL,
            CHECK(approved_by IS NULL OR approved_by <> initiated_by),
            CHECK(token_hash IS NULL OR length(token_hash)=64)
        );
        CREATE INDEX ix_identity_recoveries_applicant ON requestportal_identity_recoveries(applicant_id,created_utc DESC);
        CREATE UNIQUE INDEX ix_identity_recoveries_open ON requestportal_identity_recoveries(applicant_id)
            WHERE status IN ('Pending','Approved','Redeeming');
        CREATE TABLE requestportal_identity_recovery_events (
            id uuid PRIMARY KEY, recovery_id uuid NOT NULL REFERENCES requestportal_identity_recoveries(id),
            actor_id text NOT NULL, action text NOT NULL, detail text NOT NULL, occurred_utc timestamptz NOT NULL
        );
        CREATE INDEX ix_identity_recovery_events ON requestportal_identity_recovery_events(recovery_id,occurred_utc);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Retain recovery audit records during application rollback. This additive schema is compatible with the previous application.");
}
