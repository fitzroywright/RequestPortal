namespace RequestPortal.Domain.Countries;

public sealed class CountryConfiguration
{
    private CountryConfiguration()
    {
    }

    public CountryConfiguration(string countryCode, string displayName, string timeZoneId, string defaultCulture)
    {
        CountryCode = countryCode.Trim().ToUpperInvariant();
        DisplayName = displayName.Trim();
        TimeZoneId = timeZoneId.Trim();
        DefaultCulture = defaultCulture.Trim();
        IsActive = true;
    }

    public string CountryCode { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = string.Empty;
    public string DefaultCulture { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public bool OrganizationRegistrationEnabled { get; private set; } = true;
    public bool RequestSubmissionEnabled { get; private set; } = true;
}
