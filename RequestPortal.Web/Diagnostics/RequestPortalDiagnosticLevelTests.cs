using System.Net.Sockets;
using Common.Diagnostics;
using Common.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Diagnostics;

public sealed class BasicRuntimeIdentityDiagnosticLevelTest(
    IConfiguration configuration,
    IHostEnvironment environment) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.RUNTIME.IDENTITY";
    public string Name => "Runtime identity";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string instanceId =
            configuration["Service:Identity"]?.Trim()
            ?? configuration["Client:InstallationId"]?.Trim()
            ?? Environment.MachineName;

        return Task.FromResult(string.IsNullOrWhiteSpace(instanceId)
            ? EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "No stable runtime identity could be resolved.")
            : EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Runtime identity is available.",
                $"Instance={instanceId}; Environment={environment.EnvironmentName}; Machine={Environment.MachineName}"));
    }
}

public sealed class BasicControlPlaneConfigurationDiagnosticLevelTest(
    IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.CONTROLPLANE.CONFIG";
    public string Name => "Control Plane configuration";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] required =
        [
            "Aegis:Configuration:Url",
            "Aegis:Diagnostics:Url",
            "Aegis:Operations:Url",
            "Aegis:Diagnostics:RequestCredentialSecretName"
        ];

        string[] missing = required
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();

        return Task.FromResult(missing.Length == 0
            ? EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Required Control Plane settings are configured.")
            : EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "One or more Control Plane settings are missing.",
                $"Missing={string.Join(",", missing)}"));
    }
}

public sealed class PostgreSqlConnectivityDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.POSTGRES.CONNECTIVITY";
    public string Name => "PostgreSQL connectivity";
    public string Owner => "RequestPortal.Infrastructure";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();
            bool connected = await db.Database.CanConnectAsync(cancellationToken);
            return connected
                ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "PostgreSQL connection succeeded.")
                : EngineeringDiagnosticPolicy.Failed(TestId, Name, "PostgreSQL connection failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId,
                Name,
                "PostgreSQL connectivity check failed.",
                $"FailureType={exception.GetType().Name}");
        }
    }
}

public sealed class MalwareScannerConnectivityDiagnosticLevelTest(
    IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L5.CLAMAV.CONNECTIVITY";
    public string Name => "Upload malware scanner connectivity";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Uploads:MalwareScanning:Enabled", false))
        {
            return EngineeringDiagnosticPolicy.Warning(TestId, Name, "Upload malware scanning is disabled.");
        }

        string host = configuration["Uploads:MalwareScanning:Host"]?.Trim() ?? string.Empty;
        int port = configuration.GetValue("Uploads:MalwareScanning:Port", 3310);
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Malware scanner endpoint is not configured correctly.");
        }

        try
        {
            using TcpClient client = new();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            return EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Malware scanner TCP endpoint is reachable.",
                $"Host={host}; Port={port}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return EngineeringDiagnosticPolicy.Failed(
                TestId,
                Name,
                "Malware scanner TCP endpoint is unreachable.",
                $"Host={host}; Port={port}; FailureType={exception.GetType().Name}");
        }
    }
}

public sealed class ExternalIdentityProvidersDiagnosticLevelTest(
    IAuthenticationSchemeProvider schemeProvider) : IDiagnosticLevelLocalTest
{
    private static readonly string[] supported = ["Google", "Microsoft", "Facebook", "Yahoo", "Apple"];

    public string TestId => "REQUESTPORTAL.WEB.L5.AUTH.EXTERNAL_PROVIDERS";
    public string Name => "Representative identity providers";
    public string Owner => "RequestPortal.Web";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticationScheme[] schemes = (await schemeProvider.GetAllSchemesAsync()).ToArray();
        string[] configured = supported
            .Where(name => schemes.Any(scheme => string.Equals(scheme.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return configured.Length == 0
            ? EngineeringDiagnosticPolicy.Failed(
                TestId,
                Name,
                "No representative external identity provider is configured.")
            : EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                $"{configured.Length} representative identity provider(s) are configured.",
                $"Configured={string.Join(",", configured)}");
    }
}

public sealed class OrganizationAdministratorIntegrityDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L4.ORGANIZATION.ADMIN_INTEGRITY";
    public string Name => "Approved organizations retain an administrator";
    public string Owner => "RequestPortal.Domain";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();

        int invalid = await db.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.Status == OrganizationStatus.Approved &&
                !db.OrganizationMemberships.Any(membership =>
                    membership.OrganizationId == organization.Id &&
                    membership.Status == MembershipStatus.Approved &&
                    membership.Role == OrganizationRole.Administrator))
            .CountAsync(cancellationToken);

        return invalid == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Every approved organization has at least one approved administrator.")
            : EngineeringDiagnosticPolicy.Failed(
                TestId,
                Name,
                "One or more approved organizations have no approved administrator.",
                $"InvalidOrganizations={invalid}");
    }
}

public sealed class MembershipStateIntegrityDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L4.MEMBERSHIP.STATE_INTEGRITY";
    public string Name => "Organization membership state integrity";
    public string Owner => "RequestPortal.Domain";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();

        int pendingWithTerminalData = await db.OrganizationMemberships.AsNoTracking().CountAsync(
            membership =>
                membership.Status == MembershipStatus.Pending &&
                (membership.ApprovedUtc != null || membership.EndedUtc != null),
            cancellationToken);

        int approvedWithoutApproval = await db.OrganizationMemberships.AsNoTracking().CountAsync(
            membership =>
                membership.Status == MembershipStatus.Approved &&
                (membership.ApprovedUtc == null || membership.ApprovedByUserId == null),
            cancellationToken);

        int endedWithoutAudit = await db.OrganizationMemberships.AsNoTracking().CountAsync(
            membership =>
                (membership.Status == MembershipStatus.Revoked || membership.Status == MembershipStatus.Rejected) &&
                (membership.EndedUtc == null || membership.EndedByUserId == null || membership.EndReason == null),
            cancellationToken);

        int invalid = pendingWithTerminalData + approvedWithoutApproval + endedWithoutAudit;
        string evidence =
            $"PendingWithTerminalData={pendingWithTerminalData}; ApprovedWithoutApproval={approvedWithoutApproval}; EndedWithoutAudit={endedWithoutAudit}";

        return invalid == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Organization membership state is internally consistent.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Inconsistent organization membership records were found.", evidence);
    }
}

public sealed class RequestRelationshipIntegrityDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L4.REQUEST.RELATIONSHIP_INTEGRITY";
    public string Name => "Request relationship integrity";
    public string Owner => "RequestPortal.Domain";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();

        int orphanRequests = await db.AssistanceRequests
            .AsNoTracking()
            .Where(request => !db.Organizations.Any(organization => organization.Id == request.OrganizationId))
            .CountAsync(cancellationToken);

        int submittedWithoutTimestamp = await db.AssistanceRequests
            .AsNoTracking()
            .Where(request =>
                (request.Status == RequestStatus.Submitted ||
                 request.Status == RequestStatus.MoreInformationRequired ||
                 request.Status == RequestStatus.Approved ||
                 request.Status == RequestStatus.Rejected ||
                 request.Status == RequestStatus.Duplicate ||
                 request.Status == RequestStatus.PendingFulfilment ||
                 request.Status == RequestStatus.Fulfilled ||
                 request.Status == RequestStatus.Closed) &&
                request.SubmittedUtc == null)
            .CountAsync(cancellationToken);

        int invalid = orphanRequests + submittedWithoutTimestamp;
        string evidence = $"OrphanRequests={orphanRequests}; NonDraftWithoutSubmittedUtc={submittedWithoutTimestamp}";

        return invalid == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Request ownership and submission relationships are consistent.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Inconsistent request relationship records were found.", evidence);
    }
}

public sealed class DocumentStorageReferenceDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory,
    IFileStorage storage) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L4.DOCUMENT.STORAGE_REFERENCES";
    public string Name => "Document storage references";
    public string Owner => "RequestPortal.Infrastructure";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();

        var documents = await db.Documents
            .AsNoTracking()
            .Where(document => !document.IsArchived)
            .OrderByDescending(document => document.UploadedUtc)
            .Take(50)
            .Select(document => new { document.Id, document.StorageKey })
            .ToListAsync(cancellationToken);

        int missing = 0;
        foreach (var document in documents)
        {
            StoredFile? metadata = await storage.GetMetadataAsync(document.StorageKey, cancellationToken);
            if (metadata is null || metadata.Status != StorageStatus.Stored)
            {
                missing++;
            }
        }

        string evidence = $"Sampled={documents.Count}; MissingOrUnavailable={missing}";
        return missing == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Sampled document storage references resolved successfully.", evidence)
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "One or more sampled document storage references could not be resolved.", evidence);
    }
}

public sealed class ExpiredOrganizationRegistrationDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L3.ORGANIZATION.REGISTRATION_EXPIRY";
    public string Name => "Organization registration expiry";
    public string Owner => "RequestPortal.Domain";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level3Verification;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        int expiredApproved = await db.Organizations
            .AsNoTracking()
            .CountAsync(
                organization =>
                    organization.Status == OrganizationStatus.Approved &&
                    organization.RegistrationExpiryDate != null &&
                    organization.RegistrationExpiryDate < today,
                cancellationToken);

        return expiredApproved == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "No approved organizations have expired registration.")
            : EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "Approved organizations with expired registration require reverification.",
                $"ExpiredApprovedOrganizations={expiredApproved}");
    }
}

public sealed class FulfilmentIntegrityDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L3.FULFILMENT.INTEGRITY";
    public string Name => "Fulfilment state integrity";
    public string Owner => "RequestPortal.Domain";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level3Verification;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();

        int fulfilledWithoutTimestamp = await db.Fulfilments
            .AsNoTracking()
            .CountAsync(
                fulfilment =>
                    fulfilment.Status == FulfilmentStatus.Fulfilled &&
                    fulfilment.FulfilledUtc == null,
                cancellationToken);

        int timestampOnNonFulfilled = await db.Fulfilments
            .AsNoTracking()
            .CountAsync(
                fulfilment =>
                    fulfilment.Status != FulfilmentStatus.Fulfilled &&
                    fulfilment.FulfilledUtc != null,
                cancellationToken);

        int invalid = fulfilledWithoutTimestamp + timestampOnNonFulfilled;
        string evidence =
            $"FulfilledWithoutTimestamp={fulfilledWithoutTimestamp}; NonFulfilledWithTimestamp={timestampOnNonFulfilled}";

        return invalid == 0
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Fulfilment records satisfy core state and quantity invariants.")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Inconsistent fulfilment records were found.", evidence);
    }
}

public sealed class FfpManagerBridgeReadinessDiagnosticLevelTest(
    IServiceScopeFactory scopeFactory) : IDiagnosticLevelLocalTest
{
    public string TestId => "REQUESTPORTAL.WEB.L2.FFPMANAGER.BRIDGE_READINESS";
    public string Name => "FFP Manager bridge readiness";
    public string Owner => "RequestPortal.Infrastructure";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level2Repair;
    public bool IsDestructive => false;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IStartupDiagnostic[] diagnostics = scope.ServiceProvider.GetServices<IStartupDiagnostic>().ToArray();
        IStartupDiagnostic? bridge = diagnostics.FirstOrDefault(item =>
            string.Equals(item.Name, "FFP Manager Bridge", StringComparison.OrdinalIgnoreCase));

        if (bridge is null)
        {
            return EngineeringDiagnosticPolicy.Warning(TestId, Name, "FFP Manager bridge startup diagnostic is not registered.");
        }

        DiagnosticCheckResult result = await bridge.CheckAsync(cancellationToken);
        return result.IsHealthy
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, result.Detail)
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "FFP Manager bridge readiness check failed.", result.Detail);
    }
}
