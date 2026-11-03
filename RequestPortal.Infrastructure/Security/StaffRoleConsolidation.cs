using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Security;

/// <summary>Renames staff-role bindings without changing active staff's direct effective grants.</summary>
public static class StaffRoleConsolidation
{
    public static async Task ApplyAsync(RequestPortalDbContext db, CancellationToken token = default)
    {
        if (db.Database.CurrentTransaction is not null) { await db.Database.ExecuteSqlRawAsync(Sql, token); return; }
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync(Sql, token);
        await transaction.CommitAsync(token);
    }

    public const string Sql = """
        SELECT pg_advisory_xact_lock(723901151);
        CREATE TABLE IF NOT EXISTS requestportal_permissions(permission_key text PRIMARY KEY,description text NOT NULL);
        CREATE TABLE IF NOT EXISTS requestportal_role_permissions(role_key text NOT NULL,permission_key text NOT NULL REFERENCES requestportal_permissions(permission_key),PRIMARY KEY(role_key,permission_key));
        CREATE TABLE IF NOT EXISTS requestportal_directory_group_roles(group_name text NOT NULL,role_key text NOT NULL,PRIMARY KEY(group_name,role_key));
        CREATE TABLE IF NOT EXISTS requestportal_staff_admin_events(
            id uuid PRIMARY KEY,subject_id text NOT NULL,actor_id text NOT NULL,action text NOT NULL,
            occurred_at timestamptz NOT NULL DEFAULT now(),previous_roles jsonb NOT NULL,new_roles jsonb NOT NULL,
            previous_status integer NULL,new_status integer NULL,detail text NULL);
        CREATE INDEX IF NOT EXISTS ix_staff_admin_subject_time ON requestportal_staff_admin_events(subject_id,occurred_at DESC);
        DO $$ BEGIN
            IF to_regclass('common_security_accounts') IS NOT NULL THEN
                LOCK TABLE common_security_accounts IN SHARE ROW EXCLUSIVE MODE;
            END IF;
        END $$;
        LOCK TABLE "AspNetRoles", "AspNetUserRoles", "AspNetRoleClaims", requestportal_role_permissions,requestportal_directory_group_roles IN SHARE ROW EXCLUSIVE MODE;
        DO $$ BEGIN
            IF EXISTS(SELECT lower("Name") FROM "AspNetRoles" WHERE lower("Name") IN ('administrator','reviewer') GROUP BY lower("Name") HAVING count(*)>1) THEN
                RAISE EXCEPTION 'RP-ROLE-001: Duplicate canonical role definitions require administrative reconciliation.';
            END IF;
        END $$;
        INSERT INTO "AspNetRoles"("Id","Name","NormalizedName","ConcurrencyStamp")
            SELECT gen_random_uuid(),name,upper(name),gen_random_uuid()::text FROM (VALUES('Administrator'),('Reviewer')) roles(name)
            WHERE NOT EXISTS(SELECT 1 FROM "AspNetRoles" WHERE lower("Name")=lower(name)) ON CONFLICT DO NOTHING;
        UPDATE "AspNetRoles" SET "Name"=CASE lower("Name") WHEN 'administrator' THEN 'Administrator' ELSE 'Reviewer' END,
            "NormalizedName"=upper("Name") WHERE lower("Name") IN ('administrator','reviewer');
        INSERT INTO requestportal_permissions(permission_key,description) VALUES
            ('requestportal.access','Access the staff experience.'),('requests.review','Review requests.'),
            ('ManagementDashboard.View','View management dashboards.'),('security.manage','Manage staff access.'),('*','All permissions.') ON CONFLICT DO NOTHING;
        INSERT INTO requestportal_role_permissions(role_key,permission_key) VALUES
            ('Reviewer','requestportal.access'),('Reviewer','requests.review'),('Reviewer','ManagementDashboard.View'),('Administrator','*') ON CONFLICT DO NOTHING;
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM requestportal_role_permissions WHERE lower(role_key)='reviewer' AND lower(permission_key) IN ('*','security.manage')) THEN
                RAISE EXCEPTION 'RP-ROLE-007: Reviewer has administrative grants. Reconcile the existing policy before consolidation.';
            END IF;
        END $$;
        DROP TABLE IF EXISTS pg_temp.rp_staff_role_map;
        CREATE TEMP TABLE rp_staff_role_map ON COMMIT DROP AS
            SELECT old."Id" old_id,old."Name" old_name,target."Id" new_id,target."Name" new_name
            FROM "AspNetRoles" old JOIN "AspNetRoles" target
              ON lower(target."Name")=CASE lower(old."Name") WHEN 'ffpadministrator' THEN 'administrator' WHEN 'ffpreviewer' THEN 'reviewer' END
            WHERE lower(old."Name") IN ('ffpadministrator','ffpreviewer');
        -- Empty legacy grants do not prove that activating a canonical role is safe.
        -- Directory mappings must be equivalent for every member, including future sign-ins.
        DO $$ BEGIN
            IF EXISTS(
                SELECT 1 FROM rp_staff_role_map m
                CROSS JOIN LATERAL (SELECT CASE WHEN bool_or(p.permission_key='*') THEN ARRAY['*']::text[] ELSE
                    coalesce(array_agg(DISTINCT lower(p.permission_key) ORDER BY lower(p.permission_key)),ARRAY[]::text[]) END grants
                    FROM requestportal_role_permissions p WHERE lower(p.role_key)=lower(m.old_name)) old
                CROSS JOIN LATERAL (SELECT CASE WHEN bool_or(p.permission_key='*') THEN ARRAY['*']::text[] ELSE
                    coalesce(array_agg(DISTINCT lower(p.permission_key) ORDER BY lower(p.permission_key)),ARRAY[]::text[]) END grants
                    FROM requestportal_role_permissions p WHERE lower(p.role_key)=lower(m.new_name)) target
                WHERE old.grants IS DISTINCT FROM target.grants
                  AND (EXISTS(SELECT 1 FROM requestportal_directory_group_roles g WHERE lower(btrim(g.role_key))=lower(m.old_name))
                    OR cardinality(old.grants)>0 AND EXISTS(SELECT 1 FROM "AspNetUserRoles" u WHERE u."RoleId"=m.old_id))) THEN
                RAISE EXCEPTION 'RP-ROLE-002: Legacy role grants differ from the canonical role. No assignments changed; reconcile the policy without broadening permissions.';
            END IF;
            IF EXISTS(SELECT 1 FROM rp_staff_role_map m
                WHERE (EXISTS(SELECT 1 FROM "AspNetUserRoles" u WHERE u."RoleId"=m.old_id)
                    OR EXISTS(SELECT 1 FROM requestportal_directory_group_roles g WHERE lower(btrim(g.role_key))=lower(m.old_name)))
                AND (EXISTS(SELECT 1 FROM "AspNetRoleClaims" c WHERE c."RoleId"=m.old_id
                    AND NOT EXISTS(SELECT 1 FROM "AspNetRoleClaims" target WHERE target."RoleId"=m.new_id AND target."ClaimType" IS NOT DISTINCT FROM c."ClaimType" AND target."ClaimValue" IS NOT DISTINCT FROM c."ClaimValue"))
                  OR EXISTS(SELECT 1 FROM "AspNetRoleClaims" target WHERE target."RoleId"=m.new_id
                    AND NOT EXISTS(SELECT 1 FROM "AspNetRoleClaims" c WHERE c."RoleId"=m.old_id AND target."ClaimType" IS NOT DISTINCT FROM c."ClaimType" AND target."ClaimValue" IS NOT DISTINCT FROM c."ClaimValue")))) THEN
                RAISE EXCEPTION 'RP-ROLE-003: Legacy role claims cannot be merged without changing access. No assignments changed.';
            END IF;
        END $$;
        DROP TABLE IF EXISTS pg_temp.rp_staff_role_before;
        CREATE TEMP TABLE rp_staff_role_before ON COMMIT DROP AS
            SELECT u."UserId" user_id,jsonb_agg(DISTINCT r."Name" ORDER BY r."Name") roles,
                CASE WHEN bool_or(p.permission_key='*') THEN ARRAY['*']::text[] ELSE
                coalesce(array_agg(DISTINCT lower(p.permission_key) ORDER BY lower(p.permission_key)) FILTER(WHERE p.permission_key IS NOT NULL),ARRAY[]::text[]) END grants
            FROM "AspNetUserRoles" u JOIN "AspNetRoles" r ON r."Id"=u."RoleId"
            LEFT JOIN requestportal_role_permissions p ON lower(p.role_key)=lower(r."Name")
            WHERE u."UserId" IN(SELECT bindings."UserId" FROM "AspNetUserRoles" bindings JOIN rp_staff_role_map m ON m.old_id=bindings."RoleId")
            GROUP BY u."UserId";
        INSERT INTO "AspNetUserRoles"("UserId","RoleId")
            SELECT u."UserId",m.new_id FROM "AspNetUserRoles" u JOIN rp_staff_role_map m ON m.old_id=u."RoleId" ON CONFLICT DO NOTHING;
        INSERT INTO requestportal_directory_group_roles(group_name,role_key)
            SELECT g.group_name,m.new_name FROM requestportal_directory_group_roles g JOIN rp_staff_role_map m ON lower(btrim(g.role_key))=lower(m.old_name) ON CONFLICT DO NOTHING;
        -- Verify each old binding has a matching target before removing any old binding.
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM "AspNetUserRoles" old JOIN rp_staff_role_map m ON old."RoleId"=m.old_id
                WHERE NOT EXISTS(SELECT 1 FROM "AspNetUserRoles" target WHERE target."UserId"=old."UserId" AND target."RoleId"=m.new_id))
                OR EXISTS(SELECT 1 FROM requestportal_directory_group_roles old JOIN rp_staff_role_map m ON lower(btrim(old.role_key))=lower(m.old_name)
                WHERE NOT EXISTS(SELECT 1 FROM requestportal_directory_group_roles target WHERE target.group_name=old.group_name AND target.role_key=m.new_name)) THEN
                RAISE EXCEPTION 'RP-ROLE-004: Assignment verification failed; obsolete roles retained.';
            END IF;
        END $$;
        INSERT INTO requestportal_staff_admin_events(id,subject_id,actor_id,action,previous_roles,new_roles,detail)
            SELECT gen_random_uuid(),'directory-group:'||g.group_name,'system:role-consolidation','RolesConsolidated',jsonb_build_array(m.old_name),jsonb_build_array(m.new_name),'Verified equivalent directory role grants.'
            FROM requestportal_directory_group_roles g JOIN rp_staff_role_map m ON lower(btrim(g.role_key))=lower(m.old_name);
        DELETE FROM "AspNetUserRoles" old USING rp_staff_role_map m WHERE old."RoleId"=m.old_id;
        DELETE FROM requestportal_directory_group_roles old USING rp_staff_role_map m WHERE lower(btrim(old.role_key))=lower(m.old_name);
        DROP TABLE IF EXISTS pg_temp.rp_staff_role_after;
        CREATE TEMP TABLE rp_staff_role_after ON COMMIT DROP AS
            SELECT u."UserId" user_id,jsonb_agg(DISTINCT r."Name" ORDER BY r."Name") roles,
                CASE WHEN bool_or(p.permission_key='*') THEN ARRAY['*']::text[] ELSE
                coalesce(array_agg(DISTINCT lower(p.permission_key) ORDER BY lower(p.permission_key)) FILTER(WHERE p.permission_key IS NOT NULL),ARRAY[]::text[]) END grants
            FROM "AspNetUserRoles" u JOIN "AspNetRoles" r ON r."Id"=u."RoleId"
            LEFT JOIN requestportal_role_permissions p ON lower(p.role_key)=lower(r."Name")
            WHERE u."UserId" IN(SELECT user_id FROM rp_staff_role_before) GROUP BY u."UserId";
        DO $$ BEGIN
            IF to_regclass('common_security_accounts') IS NOT NULL THEN
                IF EXISTS(SELECT 1 FROM rp_staff_role_before b JOIN rp_staff_role_after a USING(user_id)
                    JOIN common_security_accounts account ON account.account_id=b.user_id::text AND account.application_id='RequestPortal.Staff'
                    WHERE account.status=1 AND b.grants IS DISTINCT FROM a.grants) THEN
                    RAISE EXCEPTION 'RP-ROLE-005: Consolidation would change active staff permissions. All role changes rolled back.';
                END IF;
                UPDATE common_security_accounts SET session_version=session_version+1
                    WHERE application_id='RequestPortal.Staff' AND account_id IN(SELECT user_id::text FROM rp_staff_role_before);
            END IF;
        END $$;
        INSERT INTO requestportal_staff_admin_events(id,subject_id,actor_id,action,previous_roles,new_roles,detail)
            SELECT gen_random_uuid(),b.user_id::text,'system:role-consolidation','RolesConsolidated',b.roles,a.roles,'Canonical role assignments verified; account lifecycle unchanged.'
            FROM rp_staff_role_before b JOIN rp_staff_role_after a USING(user_id);
        DELETE FROM "AspNetRoleClaims" c USING rp_staff_role_map m WHERE c."RoleId"=m.old_id;
        DELETE FROM requestportal_role_permissions p USING rp_staff_role_map m WHERE lower(p.role_key)=lower(m.old_name);
        DO $$ BEGIN
            IF EXISTS(SELECT 1 FROM "AspNetUserRoles" u JOIN rp_staff_role_map m ON m.old_id=u."RoleId")
                OR EXISTS(SELECT 1 FROM requestportal_directory_group_roles g WHERE lower(btrim(g.role_key)) IN ('ffpadministrator','ffpreviewer'))
                OR EXISTS(SELECT 1 FROM requestportal_role_permissions p WHERE lower(p.role_key) IN ('ffpadministrator','ffpreviewer')) THEN
                RAISE EXCEPTION 'RP-ROLE-006: Obsolete role still has bindings; roles retained.';
            END IF;
        END $$;
        INSERT INTO requestportal_staff_admin_events(id,subject_id,actor_id,action,previous_roles,new_roles,detail)
            SELECT gen_random_uuid(),'staff-role:'||old_name,'system:role-consolidation','RoleDefinitionConsolidated',jsonb_build_array(old_name),jsonb_build_array(new_name),'All old bindings verified and removed.' FROM rp_staff_role_map;
        DELETE FROM "AspNetRoles" old USING rp_staff_role_map m WHERE old."Id"=m.old_id;
        """;
}
