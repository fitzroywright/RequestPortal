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
        var income = command.Parameters.Add("@Income", SqlDbType.Decimal);
        income.Precision = 18;
        income.Scale = 10;
        income.Value = submission.WeeklyIncome is { } amount ? amount : DBNull.Value;
        command.Parameters.Add("@PayFrequency", SqlDbType.Int).Value = 2;
        command.Parameters.Add("@MaritalStatus", SqlDbType.Int).Value =
            QuickIntakeApplicationFields.MaritalStatus(submission.ApplicantMaritalStatus) is { } marital ? marital : DBNull.Value;
        command.Parameters.Add("@Dependents", SqlDbType.NVarChar, 255).Value =
            (object?)QuickIntakeApplicationFields.Dependents(submission.HasChildrenUnder18LivingFullTime) ?? DBNull.Value;
        command.Parameters.Add("@Disabled", SqlDbType.Bit).Value =
            submission.HasDisabilityOrSeriousMedicalCondition is { } disabled ? disabled : DBNull.Value;
        command.Parameters.Add("@Circumstance", SqlDbType.NVarChar, 255).Value =
            (object?)QuickIntakeApplicationFields.Circumstance(submission) ?? DBNull.Value;
    }
}
