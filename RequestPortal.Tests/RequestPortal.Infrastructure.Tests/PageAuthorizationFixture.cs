using Common.Security.Abstractions;
using Common.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RequestPortal.Web.Security;

namespace RequestPortal.Infrastructure.Tests;

// Existing page-only test hosts do not boot the application authorization store.
// Supply the layout's current policy/dependency without granting any test user a
// role. Dedicated settings/security tests continue to use their real stores.
internal static class PageAuthorizationFixture
{
    public static void AddPageAuthorizationFixture(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthorizationStore>(new EmptyRoles());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, WhatsAppSettingsAdministratorHandler>());
        services.AddAuthorization(options => options.AddPolicy(WhatsAppSettingsAdministratorRequirement.Policy,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new WhatsAppSettingsAdministratorRequirement())));
    }

    private sealed class EmptyRoles : IAuthorizationStore
    {
        public Task<IReadOnlyCollection<string>> GetDirectRoleKeysAsync(string subjectId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task<IReadOnlyCollection<string>> GetRoleKeysForGroupsAsync(IReadOnlyCollection<string> groups, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task<IReadOnlyCollection<RoleDefinition>> GetRolesAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<RoleDefinition>>([]);
    }
}
