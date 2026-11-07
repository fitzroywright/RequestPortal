using System.Data;
using Microsoft.Data.SqlClient;
using PhoneNumbers;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed record ApplicantPhone(string Number, long NumberType, PhoneNumberType LineType);

public sealed record ResolvedApplicantPhone(string Number, long NumberType, long LineType);
public sealed record ApplicantProfilePlan(IReadOnlyList<ResolvedApplicantPhone> Phones, string? Email);

public static class BridgeApplicantProfile
{
    public const long ApplicantNumberType = 93;
    public const long SecondaryNumberType = 83;
    public const long UnknownNetwork = 5;

    // OrganizationPhone remains the legacy fallback. New child records are created
    // only for explicit applicant phone fields; regular organizations are unchanged.
    public static string? MainContact(ExternalApplicationSubmission submission) =>
        Normalize(submission.PrimaryTelephone) ?? submission.OrganizationPhone;

    public static string? Normalize(string? value)
    {
        return ExternalApplicantValidation.Telephone(TransferInput(value), "PrimaryTelephone")?[1..];
    }

    // Digits-only wire values include the country code. Restore the E.164 marker
    // for validation/classification, never for destination persistence. Retain the
    // existing normalizer for formatted input and international access prefixes.
    private static string? TransferInput(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit) && value[0] != '0' ? "+" + value : value;

    public static IReadOnlyList<ApplicantPhone> Phones(ExternalApplicationSubmission submission)
    {
        var result = new List<ApplicantPhone>();
        Add(submission.PrimaryTelephone, ApplicantNumberType);
        Add(submission.SecondaryTelephone, SecondaryNumberType);
        return result;

        void Add(string? value, long numberType)
        {
            string? number = ExternalApplicantValidation.Telephone(TransferInput(value),
                numberType == ApplicantNumberType ? "PrimaryTelephone" : "SecondaryTelephone");
            if (number is null) return;
            string digits = number[1..];
            if (result.Any(p => p.Number == digits)) return;
            var util = PhoneNumberUtil.GetInstance();
            result.Add(new(digits, numberType, QuickIntakeApplicationFields.AppliesTo(submission)
                ? PhoneNumberType.MOBILE : util.GetNumberType(util.Parse(number, "JM"))));
        }
    }

    public static int? Gender(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MALE" => 1,
        "FEMALE" => 2,
        "UNSPECIFIED" => 0,
        _ => null // Preserve unrecognized responses in Comments; never guess the enum.
    };

    public static void Validate(ExternalApplicationSubmission submission)
    {
        ExternalApplicantValidation.Validate(submission with
        {
            PrimaryTelephone = TransferInput(submission.PrimaryTelephone),
            SecondaryTelephone = TransferInput(submission.SecondaryTelephone)
        });
    }

    public static long ResolveLineType(PhoneNumberType kind, IReadOnlyList<(long Id, string? Name, string? Description)> rows)
    {
        // FFPJM ID 1's Name is corrupted ("4"), but Description is "Land Line".
        string[] labels = kind switch
        {
            PhoneNumberType.FIXED_LINE => ["LANDLINE", "FIXEDLINE"],
            PhoneNumberType.MOBILE => ["MOBILE", "CELLULARMOBILE"],
            PhoneNumberType.VOIP => ["INTERNETTELEPHONY", "VOIP"],
            _ => ["UNKNOWN", "UNSPECIFIED", "NOTSPECIFIED"]
        };
        static string Key(string? text) => new((text ?? "").Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        var matches = rows.Where(r => labels.Contains(Key(r.Name)) || labels.Contains(Key(r.Description)))
            .Select(r => r.Id).Distinct().ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"FFP Manager phone LineType mapping for {kind} is missing or ambiguous; no applicant records were committed.");
        return matches[0];
    }

    public static async Task<ApplicantProfilePlan> PrepareAsync(SqlConnection connection, SqlTransaction transaction,
        ExternalApplicationSubmission submission, CancellationToken token)
    {
        var phones = Phones(submission);
        string? email = string.IsNullOrWhiteSpace(submission.ApplicantEmail) ? null : submission.ApplicantEmail.Trim();
        if (phones.Count == 0 && email is null) return new([], null);
        // Validate approved reference IDs inside the same transaction as the new letter.
        await using (var lookup = new SqlCommand("SELECT ID,Name FROM dbo.AddressTypes WHERE ID IN (93,83); SELECT ID,Name FROM dbo.TelephoneNetworks WHERE ID=5;", connection, transaction))
        await using (var reader = await lookup.ExecuteReaderAsync(token))
        {
            var types = new Dictionary<long,string>();
            while (await reader.ReadAsync(token)) types[reader.GetInt64(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
            if (!types.TryGetValue(93, out var primary) || !primary.Equals("Applicant", StringComparison.OrdinalIgnoreCase) ||
                (phones.Any(p => p.NumberType == 83) && (!types.TryGetValue(83, out var secondary) || !secondary.Equals("Secondary Number", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Approved applicant telephone/address type metadata is missing or changed.");
            await reader.NextResultAsync(token);
            if (phones.Count > 0 && (!await reader.ReadAsync(token) || !reader.GetString(1).Trim().Equals("No Carrier Assigned", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Approved unknown telephone network metadata is missing or changed.");
        }
        var lines = new List<(long, string?, string?)>();
        if (phones.Count > 0)
        {
            await using var lookup = new SqlCommand("SELECT ID,Name,Description FROM dbo.PhoneLineTypes;", connection, transaction);
            await using var reader = await lookup.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) lines.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return new(phones.Select(phone => new ResolvedApplicantPhone(phone.Number,phone.NumberType,ResolveLineType(phone.LineType,lines))).ToArray(),email);
    }

    public static async Task InsertChildrenAsync(SqlConnection connection, SqlTransaction transaction,
        long applicationId, ApplicantProfilePlan plan, ExternalApplicationSubmission submission, BridgeOptions options, CancellationToken token)
    {
        // Only called on the new-letter path, after the serialized idempotency check.
        foreach (var phone in plan.Phones)
        {
            await using var command = new SqlCommand("INSERT INTO dbo.PhoneNumbers (Number,NumberType,Network,LineType,ApplicationLetter,CreatedBy,DateCreated,UserName,DateUpdated) VALUES (@Number,@NumberType,5,@LineType,@ApplicationLetter,@CreatedBy,@DateCreated,@UserName,@DateUpdated);", connection, transaction);
            AddPhoneParameters(command, phone);
            Audit(command, applicationId, submission, options);
            await command.ExecuteNonQueryAsync(token);
        }
        if (plan.Email is not null)
        {
            await using var command = new SqlCommand("INSERT INTO dbo.EmailAddresses (MailboxName,AddressType,ApplicationLetter,CreatedBy,DateCreated,UserName,DateUpdated) VALUES (@Email,93,@ApplicationLetter,@CreatedBy,@DateCreated,@UserName,@DateUpdated);", connection, transaction);
            command.Parameters.Add("@Email", SqlDbType.NVarChar, 100).Value = plan.Email;
            Audit(command, applicationId, submission, options);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public static void AddPhoneParameters(SqlCommand command, ResolvedApplicantPhone phone)
    {
        string field = phone.NumberType == SecondaryNumberType ? "SecondaryTelephone" : "PrimaryTelephone";
        string? number = ExternalApplicantValidation.Telephone(TransferInput(phone.Number), field);
        command.Parameters.Add("@Number", SqlDbType.NVarChar, 15).Value = number is null ? DBNull.Value : number[1..];
        command.Parameters.Add("@NumberType", SqlDbType.BigInt).Value = phone.NumberType;
        command.Parameters.Add("@LineType", SqlDbType.BigInt).Value = phone.LineType;
    }

    private static void Audit(SqlCommand command, long applicationId, ExternalApplicationSubmission submission, BridgeOptions options)
    {
        command.Parameters.Add("@ApplicationLetter", SqlDbType.BigInt).Value = applicationId;
        command.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 50).Value = options.CreatedBy;
        command.Parameters.Add("@UserName", SqlDbType.NVarChar, 50).Value = options.UserName;
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submission.SubmittedUtc);
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submission.SubmittedUtc);
    }
}
