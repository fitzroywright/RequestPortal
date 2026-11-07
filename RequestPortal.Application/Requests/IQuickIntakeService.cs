using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Requests;

public sealed record QuickIntakeSubmission(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string Gender,
    string MaritalStatus,
    string Telephone,
    string? Email,
    string StreetAddress,
    string CommunityDistrict,
    string Parish,
    string EmploymentStatus,
    // Legacy storage/contract name: this value is weekly JMD.
    decimal MonthlyIncome,
    bool HasChildrenUnder18LivingFullTime,
    bool HasDisabilityOrSeriousMedicalCondition,
    IReadOnlyList<string> AssistancePrograms,
    string HelpCategory,
    string? SecondaryTelephone = null,
    IReadOnlyList<string>? VictimCircumstances = null);

public sealed record QuickIntakeResult(
    Guid RequestId,
    string PublicReference,
    string Telephone);

public sealed record QuickIntakeView(
    Guid RequestId,
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string Gender,
    string MaritalStatus,
    string Telephone,
    string? Email,
    string StreetAddress,
    string CommunityDistrict,
    string Parish,
    string EmploymentStatus,
    decimal MonthlyIncome,
    bool HasChildrenUnder18LivingFullTime,
    bool HasDisabilityOrSeriousMedicalCondition,
    IReadOnlyList<string> AssistancePrograms,
    string HelpCategory,
    DateTimeOffset CreatedUtc,
    Guid? ConvertedToUserId,
    DateTimeOffset? ConvertedUtc,
    string? SecondaryTelephone = null,
    string? VictimCircumstances = null)
{
    public bool IsConverted => ConvertedToUserId.HasValue;
}

public interface IQuickIntakeService
{
    Task<QuickIntakeView?> GetAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<QuickIntakeResult> CreateAsync(
        QuickIntakeSubmission submission,
        CancellationToken cancellationToken = default,
        Guid? requestId = null,
        Guid? applicantUserId = null);

    Task<Guid> GetAnonymousUploaderIdAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task ConvertToIndividualAsync(
        Guid requestId,
        Guid userId,
        Guid staffUserId,
        CancellationToken cancellationToken = default);
}
