using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Countries;

public sealed record CountryOption(
    string CountryCode,
    string DisplayName,
    string TimeZoneId,
    string DefaultCulture,
    bool OrganizationRegistrationEnabled,
    bool RequestSubmissionEnabled);

public interface ICountryPolicyService
{
    Task<IReadOnlyList<CountryOption>> GetActiveCountriesAsync(CancellationToken cancellationToken = default);

    Task EnsureOrganizationRegistrationAllowedAsync(
        string countryCode,
        CancellationToken cancellationToken = default);

    Task EnsureRequestSubmissionAllowedAsync(
        string countryCode,
        CancellationToken cancellationToken = default);
}
