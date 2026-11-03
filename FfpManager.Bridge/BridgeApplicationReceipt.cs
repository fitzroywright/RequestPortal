using System.Data;
using Microsoft.Data.SqlClient;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

/// <summary>Maps receipt fields to the legacy ApplicationLetters schema.</summary>
public static class BridgeApplicationReceipt
{
    // Verified on JMDB01/FFP: nullable nvarchar(255), max_length 510 bytes.
    public const int OccupationMaxLength = 255;

    public static void Validate(string? employmentStatus)
    {
        if (employmentStatus?.Trim().Length > OccupationMaxLength)
            throw new InvalidOperationException("FFP Manager ApplicationLetters.Occupation permits at most 255 characters.");
    }

    public static void AddParameters(SqlCommand command, ExternalApplicationSubmission submission)
    {
        Validate(submission.ApplicantEmploymentStatus);
        command.Parameters.Add("@Occupation", SqlDbType.NVarChar, OccupationMaxLength).Value =
            string.IsNullOrWhiteSpace(submission.ApplicantEmploymentStatus) ? DBNull.Value : submission.ApplicantEmploymentStatus.Trim();
        DateTime received = JamaicaBusinessTime.ToLocal(submission.SubmittedUtc);
        command.Parameters.Add("@ApplicationDate", SqlDbType.DateTime).Value = received.Date;
        command.Parameters.Add("@DateReceived", SqlDbType.DateTime).Value = received;
    }
}
