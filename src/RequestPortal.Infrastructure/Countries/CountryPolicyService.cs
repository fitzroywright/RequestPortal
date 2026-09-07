using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Countries;
using RequestPortal.Domain.Countries;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Countries;

public sealed class CountryPolicyService : ICountryPolicyService
{
    private readonly RequestPortalDbContext dbContext;

    public CountryPolicyService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CountryOption>> GetActiveCountriesAsync(CancellationToken cancellationToken = default)
    {
        List<CountryOption> countries = await dbContext.CountryConfigurations
            .AsNoTracking()
            .Where(country => country.IsActive)
            .OrderBy(country => country.DisplayName)
            .Select(country => new CountryOption(
                country.CountryCode,
                country.DisplayName,
                country.TimeZoneId,
                country.DefaultCulture,
                country.OrganizationRegistrationEnabled,
                country.RequestSubmissionEnabled))
            .ToListAsync(cancellationToken);

        return countries;
    }

    public async Task EnsureOrganizationRegistrationAllowedAsync(
        string countryCode,
        CancellationToken cancellationToken = default)
    {
        CountryConfiguration country = await GetCountryAsync(countryCode, cancellationToken);
        if (!country.IsActive || !country.OrganizationRegistrationEnabled)
        {
            throw new InvalidOperationException("Organization registration is not currently enabled for the selected country.");
        }
    }

    public async Task EnsureRequestSubmissionAllowedAsync(
        string countryCode,
        CancellationToken cancellationToken = default)
    {
        CountryConfiguration country = await GetCountryAsync(countryCode, cancellationToken);
        if (!country.IsActive || !country.RequestSubmissionEnabled)
        {
            throw new InvalidOperationException("Request submission is not currently enabled for the selected country.");
        }
    }

    private async Task<CountryConfiguration> GetCountryAsync(
        string countryCode,
        CancellationToken cancellationToken)
    {
        string normalizedCountryCode = countryCode.Trim().ToUpperInvariant();
        CountryConfiguration? country = await dbContext.CountryConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CountryCode == normalizedCountryCode, cancellationToken);

        return country
            ?? throw new InvalidOperationException($"Country '{normalizedCountryCode}' is not configured for Request Portal.");
    }
}
