using System.Globalization;

namespace RequestPortal.Application.Integration;

public sealed record ApplicationMappedFields(decimal? Income, int? PayFrequency, int? MaritalStatus,
    string? Dependents, bool? Disabled, string? Programme, string? Circumstance)
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] { "Income", "PayFrequency", "MaritalStatus", "Dependents", "Disabled", "Programme", "Circumstance" });
    public string? Value(string name) => name switch
    {
        "Income" => Income?.ToString("G29", CultureInfo.InvariantCulture),
        "PayFrequency" => PayFrequency?.ToString(CultureInfo.InvariantCulture),
        "MaritalStatus" => MaritalStatus?.ToString(CultureInfo.InvariantCulture),
        "Dependents" => string.IsNullOrWhiteSpace(Dependents) ? null : Dependents,
        "Disabled" => Disabled?.ToString(),
        "Programme" => string.IsNullOrWhiteSpace(Programme) ? null : Programme,
        "Circumstance" => string.IsNullOrWhiteSpace(Circumstance) ? null : Circumstance,
        _ => throw new InvalidOperationException("Unsupported destination field.")
    };

    public ApplicationMappedFields WithApproved(ApplicationMappedFields proposed, IReadOnlyCollection<string> approved) => this with
    {
        Income = approved.Contains("Income") ? proposed.Income : Income,
        PayFrequency = approved.Contains("PayFrequency") ? proposed.PayFrequency : PayFrequency,
        MaritalStatus = approved.Contains("MaritalStatus") ? proposed.MaritalStatus : MaritalStatus,
        Dependents = approved.Contains("Dependents") ? proposed.Dependents : Dependents,
        Disabled = approved.Contains("Disabled") ? proposed.Disabled : Disabled,
        Programme = approved.Contains("Programme") ? proposed.Programme : Programme,
        Circumstance = approved.Contains("Circumstance") ? proposed.Circumstance : Circumstance
    };
}

public sealed record ApplicationFieldSnapshot(ExternalApplicationReceipt Identity, ApplicationMappedFields Fields,
    DateTime? LastUpdated, long? WorkflowStatus);
public sealed record ApplicationFieldDifference(string Name, string? Current, string? Proposed)
{
    public bool Different => Proposed is not null && Current != Proposed;
    public bool Conflict => Different && Current is not null;
    public bool CanApply => Different && !Conflict;
}
public sealed record ApplicationFieldPreview(Guid RequestId, string PublicReference, ApplicationFieldSnapshot Destination,
    ApplicationMappedFields Proposed)
{
    public IReadOnlyList<ApplicationFieldDifference> Differences => ApplicationMappedFields.Names
        .Select(name => new ApplicationFieldDifference(name, Destination.Fields.Value(name), Proposed.Value(name))).ToArray();
    public void ValidateApproval(IReadOnlyList<string> approved)
    {
        if (approved.Count == 0 || approved.Count > ApplicationMappedFields.Names.Count || approved.Distinct(StringComparer.Ordinal).Count() != approved.Count ||
            approved.Any(name => !Differences.Any(d => d.Name == name && d.CanApply)))
            throw new InvalidOperationException("Select only missing, nonconflicting fields from this preview.");
    }

    public void ValidateCurrent(ApplicationFieldSnapshot current)
    {
        if (current != Destination)
            throw new IntegrationTransferException(TransferFailures.For("FieldUpdate", "FIELD_PREVIEW_STALE"));
    }

    public void ValidateReadback(ApplicationFieldSnapshot readback, IReadOnlyList<string> approved)
    {
        ValidateApproval(approved);
        var expected = Destination with { Fields = Destination.Fields.WithApproved(Proposed, approved) };
        if (readback != expected)
            throw new IntegrationTransferException(TransferFailures.For("FieldUpdate", "FIELD_READBACK"));
    }
}
public sealed record ApplicationFieldUpdate(ExternalApplicationSubmission Source, ApplicationFieldSnapshot Expected,
    IReadOnlyList<string> ApprovedFields);
public sealed class ExistingDestinationPreviewRequiredException(Guid requestId) : InvalidOperationException("An existing FFP Manager application was verified. Review its mapped fields before any field update.")
{ public Guid RequestId { get; } = requestId; }
