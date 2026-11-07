using System.Data;
using Microsoft.Data.SqlClient;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public static class BridgeQuickIntakeFields
{
    // Fixed identifiers only; regular applications retain their existing insert columns/defaults.
    public static string Columns(ExternalApplicationSubmission submission) => QuickIntakeApplicationFields.AppliesTo(submission)
        ? ",Income,PayFrequency,MaritalStatus,Dependents,Disabled,Circumstance" : "";
    public static string Values(ExternalApplicationSubmission submission) => QuickIntakeApplicationFields.AppliesTo(submission)
        ? ",@Income,@PayFrequency,@MaritalStatus,@Dependents,@Disabled,@Circumstance" : "";

    public static void AddParameters(SqlCommand command, ExternalApplicationSubmission submission)
    {
        QuickIntakeApplicationFields.Validate(submission);
        string? programme = QuickIntakeApplicationFields.Programme(submission);
        command.Parameters.Add("@Programme", SqlDbType.NVarChar, 255).Value =
            string.IsNullOrWhiteSpace(programme) ? DBNull.Value : programme;
        if (!QuickIntakeApplicationFields.AppliesTo(submission)) return;
        var mapped = QuickIntakeApplicationFields.MappedValues(submission);
        var income = command.Parameters.Add("@Income", SqlDbType.Decimal);
        income.Precision = 18;
        income.Scale = 10;
        income.Value = mapped.Income is { } amount ? amount : DBNull.Value;
        command.Parameters.Add("@PayFrequency", SqlDbType.Int).Value = mapped.PayFrequency;
        command.Parameters.Add("@MaritalStatus", SqlDbType.Int).Value =
            mapped.MaritalStatus is { } marital ? marital : DBNull.Value;
        command.Parameters.Add("@Dependents", SqlDbType.NVarChar, 255).Value =
            (object?)mapped.Dependents ?? DBNull.Value;
        command.Parameters.Add("@Disabled", SqlDbType.Bit).Value =
            mapped.Disabled is { } disabled ? disabled : DBNull.Value;
        command.Parameters.Add("@Circumstance", SqlDbType.NVarChar, 255).Value =
            (object?)mapped.Circumstance ?? DBNull.Value;
    }
}
