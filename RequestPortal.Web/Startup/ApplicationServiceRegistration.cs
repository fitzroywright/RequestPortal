using RequestPortal.Web.Security;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Management;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Reviewers;
using RequestPortal.Application.Reporting;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Configuration;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Administration;
using RequestPortal.Infrastructure.Auditing;
using RequestPortal.Infrastructure.Beneficiaries;
using RequestPortal.Infrastructure.Countries;
using RequestPortal.Infrastructure.Diagnostics;
using RequestPortal.Infrastructure.Documents;
using RequestPortal.Infrastructure.Delivery;
using RequestPortal.Infrastructure.Fulfilment;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Portal;
using RequestPortal.Infrastructure.ReferenceData;
using RequestPortal.Infrastructure.Reviewers;
using RequestPortal.Infrastructure.Reporting;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Messaging;
using RequestPortal.Web.Services;

namespace RequestPortal.Web.Startup;

internal static class ApplicationServiceRegistration
{
    public static void Add(WebApplicationBuilder builder)
    {
        AddMessagingAndReferenceServices(builder);
        AddOrganizationServices(builder);
        AddStaffAndManagementServices(builder);
        AddRequestServices(builder);
        AddIntegrationAndRuntimeServices(builder);
    }

    private static void AddMessagingAndReferenceServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddSignalR();

        builder.Services.AddSingleton<
            IPortalMessageRealtimeNotifier
            , SignalRPortalMessageRealtimeNotifier>();

        builder.Services.AddScoped<
            IPortalMessageService
            , PortalMessageService>();

        builder.Services.AddScoped<RequestPortal.Application.Management.IQuickIntakeDashboardService, RequestPortal.Infrastructure.Management.QuickIntakeDashboardService>();
        builder.Services.AddScoped<MessageArchiveService>();
        builder.Services.AddScoped<IStaffNotificationAccess, StaffNotificationAccess>();
        builder.Services.AddScoped<StaffNotificationRouting>();
        builder.Services.AddScoped<NotificationRoutingAdministration>();

        builder.Services.AddScoped<
            INotificationPreferenceService
            , NotificationPreferenceService>();

        builder.Services.AddScoped<
            ICountryPolicyService
            , CountryPolicyService>();

        builder.Services.AddScoped<
            IRequestReferenceDataService
            , RequestReferenceDataService>();
    }

    private static void AddOrganizationServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<
            IOrganizationMembershipGovernanceService
            , OrganizationMembershipGovernanceService>();

        builder.Services.AddScoped<
            IOrganizationInvitationService
            , OrganizationInvitationService>();

        builder.Services.AddScoped<
            IOrganizationAccessService
            , OrganizationAccessService>();

        builder.Services.AddScoped<
            IOrganizationProfileService
            , OrganizationProfileService>();

        builder.Services.AddScoped<PortalWorkflowService>();
    }

    private static void AddStaffAndManagementServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<
            IStaffOrganizationDirectoryService
            , StaffOrganizationDirectoryService>();

        builder.Services.AddScoped<
            IStaffOrganizationService
            , StaffOrganizationService>();

        builder.Services.AddScoped<
            IStaffOperationsService
            , StaffOperationsService>();
        builder.Services.AddScoped<IStaffRequestSearchService, StaffRequestSearchService>();
        builder.Services.AddScoped<IStaffDocumentContextService, StaffDocumentContextService>();

        builder.Services.AddScoped<
            IStaffReportingService
            , StaffReportingService>();

        builder.Services.AddScoped<
            IExternalReviewerService
            , ExternalReviewerService>();

        builder.Services
            .AddOptions<ManagementDashboardOptions>()
            .Bind(
                builder.Configuration.GetSection(
                    ManagementDashboardOptions.SectionName));

        builder.Services.AddScoped<
            IManagementDashboardService
            , ManagementDashboardService>();

        builder.Services.AddScoped<
            IRequestLocationService
            , RequestLocationService>();

        builder.Services.AddScoped<
            IBusinessAuditWriter
            , BusinessAuditWriter>();
    }

    private static void AddRequestServices(
        WebApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<RequestSubmissionPolicyOptions>()
            .Bind(builder.Configuration.GetSection(RequestSubmissionPolicyOptions.SectionName));

        builder.Services.AddScoped<
            IRequestSubmissionPolicy
            , RequestSubmissionPolicy>();

        builder.Services.AddScoped<
            IDocumentService
            , DocumentService>();

        builder.Services.AddScoped<
            IDocumentPreviewRenderer
            , OpenXmlDocumentPreviewRenderer>();

        builder.Services.AddScoped<
            IDeliveryService
            , DeliveryService>();

        builder.Services.AddScoped<
            IBeneficiaryService
            , BeneficiaryService>();

        builder.Services.AddScoped<
            IRequestDraftService
            , RequestDraftService>();

        builder.Services.AddScoped<
            IQuickIntakeService
            , QuickIntakeService>();

        builder.Services.AddScoped<
            IProjectProposalService
            , ProjectProposalService>();

        builder.Services.AddScoped<
            IProjectPlanningService
            , ProjectPlanningService>();

        builder.Services.AddScoped<
            IRequestLifecycleService
            , RequestLifecycleService>();

        builder.Services.AddScoped<
            IFulfilmentQueueService
            , FulfilmentQueueService>();
    }

    private static void AddIntegrationAndRuntimeServices(
        WebApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IIntegrationOperatorAuthorization, IntegrationOperatorAuthorization>();
        builder.Services.AddScoped<
            IIntegrationStatusService
            , IntegrationStatusService>();

        builder.Services.AddFfpManagerIntegration(
            builder.Configuration);

        builder.Services.AddScoped<StaffOidcPrincipalService>();
        builder.Services.AddScoped<OutboxDispatcher>();
        builder.Services.AddHostedService<OutboxHostedService>();
        builder.Services.AddHostedService<ConfigurationContractPublisher>();
        builder.Services.AddScoped<DemoDatabaseBootstrapper>();
        builder.Services.AddScoped<DemoDataSeeder>();
        builder.Services.AddScoped<DemoReadinessValidator>();
        builder.Services.AddEngineeringDiagnostics();

        builder.Services.AddScoped<
            IStartupDiagnostic
            , PostgreSqlStartupDiagnostic>();

        builder.Services.AddScoped<StartupDiagnosticRunner>();
    }
}
