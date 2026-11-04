namespace RequestPortal.Application.Integration;

public sealed record TransferFailure(string Stage, string Classification, string Code, string Explanation, bool Transient);

public static class TransferFailures
{
    public static TransferFailure For(string stage, string code) => code switch
    {
        "VALIDATION" => new(stage, "PermanentValidation", code, "Required fields or mapping values are invalid. Correct the source or mapping before an authorized retry.", false),
        "VALIDATION_PRIMARY_TELEPHONE" => new(stage, "PermanentValidation", code, "The primary applicant telephone cannot be transferred. Verify the original submission and correct the source before an authorized reconciled retry.", false),
        "VALIDATION_SECONDARY_TELEPHONE" => new(stage, "PermanentValidation", code, "The secondary applicant telephone cannot be transferred. Verify the original submission and correct the source before an authorized reconciled retry.", false),
        "VALIDATION_PARISH" => new(stage, "PermanentValidation", code, "The explicitly selected parish is missing or has no approved FFP Manager mapping. Correct the intake/profile or mapping before retrying; address text cannot substitute for a parish.", false),
        "SQL_CONSTRAINT" => new(stage, "PermanentSql", code, "SQL Server rejected a constraint or field value. Review the mapping before retrying.", false),
        "SQL_AUTH" => new(stage, "Authentication", code, "SQL Server rejected the bridge service identity. Verify its approved credentials and database access.", false),
        "BRIDGE_AUTH" => new(stage, "Authentication", code, "The bridge rejected authentication. Verify the existing protected integration credential.", false),
        "CONFIGURATION" => new(stage, "Configuration", code, "The integration or destination configuration is unavailable or inconsistent. Commissioning is required.", false),
        "DESTINATION_CONFLICT" => new(stage, "Ambiguous", code, "The destination identity, reference or target could not be verified. Reconcile before retrying.", false),
        "SQL_CONNECTION" or "SQL_TIMEOUT" or "CONNECTION" => new(stage, "TransientConnection", code, "A connection or timeout failure interrupted the transfer. The next attempt will verify the destination before inserting.", true),
        "ARTIFACT" => new(stage, "Artifacts", code, "The application exists but supporting document transfer did not complete. Reconciliation and document retry are required.", false),
        _ when code.StartsWith("SQL_",StringComparison.Ordinal) && int.TryParse(code.AsSpan(4),out var number) => Sql(stage,number),
        _ => new(stage, "Ambiguous", "UNCLASSIFIED", "The transfer did not complete. Verify the destination and correlation history before recovery.", false)
    };

    public static TransferFailure Sql(string stage,int number)
    {
        string category = number switch {
            2601 or 2627 or 547 or 515 or 2628 or 8152 => "SQL_CONSTRAINT",
            18456 or 4060 or 229 => "SQL_AUTH",
            -2 or 1205 => "SQL_TIMEOUT",
            53 or 64 or 233 or 10053 or 10054 or 10060 or 11001 => "SQL_CONNECTION",
            51050 => "CONFIGURATION", _ => "UNCLASSIFIED"
        };
        var failure = For(stage,category);
        return failure with { Code = $"SQL_{number}", Explanation = failure.Explanation + $" SQL error number: {number}." };
    }
}

public sealed class IntegrationTransferException(TransferFailure failure, Exception? inner = null)
    : Exception(failure.Explanation, inner)
{
    public TransferFailure Failure { get; } = failure;
}

public interface IIntegrationOperatorAuthorization
{
    Task DemandAsync(Guid actorId, CancellationToken cancellationToken = default);
}
