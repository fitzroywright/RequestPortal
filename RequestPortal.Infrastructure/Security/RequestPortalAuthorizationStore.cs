using System.Data;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Security;

public static class RequestPortalRoles
{
    public const string Reviewer = "Reviewer";
    public const string Administrator = "Administrator";
}

public static class RequestPortalPermissions
{
    public const string Access = "requestportal.access";
    public const string ReviewRequests = "requests.review";
    public const string ViewManagementDashboard = "ManagementDashboard.View";
    public const string ManageSecurity = "security.manage";
}

public sealed class RequestPortalAuthorizationStore : IAuthorizationStore
{
    private readonly RequestPortalDbContext dbContext;
    private readonly ActiveDirectoryOptions directoryOptions;
    private bool initialized;

    public RequestPortalAuthorizationStore(
        RequestPortalDbContext dbContext,
        IOptions<ActiveDirectoryOptions> directoryOptions)
    {
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.directoryOptions = directoryOptions?.Value ?? throw new ArgumentNullException(nameof(directoryOptions));
    }

    public async Task<IReadOnlyCollection<string>> GetDirectRoleKeysAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        const string sql = """
            SELECT r."Name"
            FROM "AspNetUsers" u
            INNER JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id"
            INNER JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE u."Id"::text = @subject
            ORDER BY r."Name";
            """;

        return await QueryStringsAsync(sql, [new NpgsqlParameter("subject", subjectId.Trim())], cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> GetRoleKeysForGroupsAsync(
        IReadOnlyCollection<string> groupNames,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (groupNames.Count == 0) return Array.Empty<string>();

        await using NpgsqlCommand command = await CreateCommandAsync(
            "SELECT DISTINCT role_key FROM requestportal_directory_group_roles WHERE lower(group_name) = ANY(@groups) ORDER BY role_key;",
            cancellationToken);
        command.Parameters.AddWithValue("groups", groupNames.Select(group => group.Trim().ToLowerInvariant()).ToArray());

        List<string> results = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyCollection<RoleDefinition>> GetRolesAsync(
        IReadOnlyCollection<string> roleKeys,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (roleKeys.Count == 0) return Array.Empty<RoleDefinition>();

        await using NpgsqlCommand command = await CreateCommandAsync(
            """
            SELECT r."Name", rp.permission_key
            FROM "AspNetRoles" r
            LEFT JOIN requestportal_role_permissions rp ON lower(rp.role_key) = lower(r."Name")
            WHERE lower(r."Name") = ANY(@roles)
            ORDER BY r."Name", rp.permission_key;
            """,
            cancellationToken);
        command.Parameters.AddWithValue("roles", roleKeys.Select(role => role.Trim().ToLowerInvariant()).ToArray());

        Dictionary<string, List<string>> permissions = new(StringComparer.OrdinalIgnoreCase);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string roleName = reader.GetString(0);
            if (!permissions.TryGetValue(roleName, out List<string>? values))
            {
                values = [];
                permissions[roleName] = values;
            }
            if (!reader.IsDBNull(1)) values.Add(reader.GetString(1));
        }

        return permissions.Select(pair => new RoleDefinition(pair.Key, pair.Key, pair.Value)).ToArray();
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (initialized) return;

        const string schemaSql = """
            CREATE TABLE IF NOT EXISTS requestportal_staff_provisioning (
                singleton integer PRIMARY KEY CHECK(singleton=1),
                account_id text NOT NULL, actor_id text NOT NULL, completed_at timestamptz NOT NULL
            );
            CREATE TABLE IF NOT EXISTS requestportal_permissions (
                permission_key text PRIMARY KEY,
                description text NOT NULL
            );

            CREATE TABLE IF NOT EXISTS requestportal_role_permissions (
                role_key text NOT NULL,
                permission_key text NOT NULL REFERENCES requestportal_permissions(permission_key) ON DELETE CASCADE,
                PRIMARY KEY (role_key, permission_key)
            );

            CREATE TABLE IF NOT EXISTS requestportal_directory_group_roles (
                group_name text NOT NULL,
                role_key text NOT NULL,
                PRIMARY KEY (group_name, role_key)
            );
            """;

        await dbContext.Database.ExecuteSqlRawAsync(schemaSql, cancellationToken);

        await EnsureRoleAsync(RequestPortalRoles.Reviewer, cancellationToken);
        await EnsureRoleAsync(RequestPortalRoles.Administrator, cancellationToken);
        await EnsurePermissionAsync(RequestPortalPermissions.Access, "Access the Request Portal staff experience.", cancellationToken);
        await EnsurePermissionAsync(RequestPortalPermissions.ReviewRequests, "Review and process assistance requests.", cancellationToken);
        await EnsurePermissionAsync(RequestPortalPermissions.ViewManagementDashboard, "View management dashboards and organization-wide service metrics.", cancellationToken);
        await EnsurePermissionAsync(RequestPortalPermissions.ManageSecurity, "Manage Request Portal roles and security policy.", cancellationToken);

        await EnsureRolePermissionAsync(RequestPortalRoles.Reviewer, RequestPortalPermissions.Access, cancellationToken);
        await EnsureRolePermissionAsync(RequestPortalRoles.Reviewer, RequestPortalPermissions.ReviewRequests, cancellationToken);
        await EnsureRolePermissionAsync(RequestPortalRoles.Reviewer, RequestPortalPermissions.ViewManagementDashboard, cancellationToken);
        await EnsureRolePermissionAsync(RequestPortalRoles.Administrator, PermissionAuthorizer.AllPermissions, cancellationToken, definePermission: true);

        foreach (string group in directoryOptions.ReviewerGroups.Where(group => !string.IsNullOrWhiteSpace(group)))
            await EnsureGroupRoleMappingAsync(group, RequestPortalRoles.Reviewer, cancellationToken);

        foreach (string group in directoryOptions.AdministratorGroups.Where(group => !string.IsNullOrWhiteSpace(group)))
            await EnsureGroupRoleMappingAsync(group, RequestPortalRoles.Administrator, cancellationToken);

        initialized = true;
    }

    private async Task EnsureRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(
            """
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            SELECT @id, @name, upper(@name), @stamp
            WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE lower("Name") = lower(@name));
            """,
            cancellationToken);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("name", roleName);
        command.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsurePermissionAsync(string permission, string description, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(
            "INSERT INTO requestportal_permissions(permission_key, description) VALUES (@permission, @description) ON CONFLICT (permission_key) DO NOTHING;",
            cancellationToken);
        command.Parameters.AddWithValue("permission", permission);
        command.Parameters.AddWithValue("description", description);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureRolePermissionAsync(string role, string permission, CancellationToken cancellationToken, bool definePermission = false)
    {
        if (definePermission) await EnsurePermissionAsync(permission, "Explicit all-permissions grant.", cancellationToken);
        await using NpgsqlCommand command = await CreateCommandAsync(
            "INSERT INTO requestportal_role_permissions(role_key, permission_key) VALUES (@role, @permission) ON CONFLICT DO NOTHING;",
            cancellationToken);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("permission", permission);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureGroupRoleMappingAsync(string groupName, string roleName, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(
            "INSERT INTO requestportal_directory_group_roles(group_name, role_key) VALUES (@group, @role) ON CONFLICT DO NOTHING;",
            cancellationToken);
        command.Parameters.AddWithValue("group", groupName.Trim());
        command.Parameters.AddWithValue("role", roleName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> QueryStringsAsync(string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = await CreateCommandAsync(sql, cancellationToken);
        foreach (NpgsqlParameter parameter in parameters) command.Parameters.Add(parameter);
        List<string> results = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) if (!reader.IsDBNull(0)) results.Add(reader.GetString(0));
        return results;
    }

    private async Task<NpgsqlCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        return new NpgsqlCommand(sql, connection);
    }
}
