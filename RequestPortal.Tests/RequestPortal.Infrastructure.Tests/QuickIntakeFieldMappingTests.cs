using System.Data;
using System.Globalization;
using System.Text.Json;
using FfpManager.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PhoneNumbers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Web.Pages;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeFieldMappingTests
{
    static ExternalApplicationSubmission Submission(bool quick = true) => new(Guid.NewGuid(), Guid.NewGuid(), "RP-MAPPING", Guid.NewGuid(),
        "Applicant", null, null, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow, "Individual", "Normal", null,
        "Food", "Housing", "REQUESTED ASSISTANCE MUST NOT BECOME PROGRAMME", null, null, [], [], "Kingston",
        quick ? "Request Portal - Quick Intake" : "Request Portal");

    static QuickIntakeRecord Intake(decimal income = 1234.56m, bool children = true, bool disability = true, bool legacy = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test", "Applicant", legacy ? new(1900, 1, 1) : new(1980, 1, 1),
        "Male", "Married", "18765230123", null, legacy ? "Not captured" : "Street", "Town", "Kingston", "Employed",
        income, children, disability, "Social Pension|PATH|PATH", "Food", DateTimeOffset.UtcNow, victimCircumstances:
        "Flooding; House Fire; Flooding");

    [Fact]
    public void PersistedFieldsSurviveWireRoundtripAndUseVerifiedSqlTypesWithoutMonthlyConversion()
    {
        var source = Intake();
        var submission = QuickIntakeSubmissionMapping.Apply(Submission(), source);
        submission = JsonSerializer.Deserialize<ExternalApplicationSubmission>(JsonSerializer.Serialize(submission))!;
        BridgeApplicantProfile.Validate(submission);
        using var sql = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(sql, submission);
        Assert.Equal(source.MonthlyIncome, submission.WeeklyIncome); // legacy member name, confirmed weekly amount
        Assert.Equal(1234.56m, sql.Parameters["@Income"].Value);
        Assert.Equal(SqlDbType.Decimal, sql.Parameters["@Income"].SqlDbType);
        Assert.Equal((byte)18, sql.Parameters["@Income"].Precision);
        Assert.Equal((byte)10, sql.Parameters["@Income"].Scale);
        Assert.Equal(2, sql.Parameters["@PayFrequency"].Value);
        Assert.Equal(SqlDbType.Int, sql.Parameters["@PayFrequency"].SqlDbType);
        Assert.Equal(1, sql.Parameters["@MaritalStatus"].Value);
        Assert.Equal("One or More", sql.Parameters["@Dependents"].Value);
        Assert.Equal(true, sql.Parameters["@Disabled"].Value);
        Assert.Equal(SqlDbType.Bit, sql.Parameters["@Disabled"].SqlDbType);
        Assert.Equal("PATH, Social Pension", sql.Parameters["@Programme"].Value);
        Assert.Equal("House Fire, Flooding", sql.Parameters["@Circumstance"].Value);
        foreach (string field in new[] { "@Programme", "@Dependents", "@Circumstance" })
        {
            Assert.Equal(SqlDbType.NVarChar, sql.Parameters[field].SqlDbType);
            Assert.Equal(255, sql.Parameters[field].Size);
        }
        Assert.Contains(",Circumstance", BridgeQuickIntakeFields.Columns(submission));
        Assert.DoesNotContain("Circomstance", BridgeQuickIntakeFields.Columns(submission));
        Assert.Equal("Social Pension|PATH|PATH", source.AssistancePrograms);
        Assert.Equal(1234.56m, source.MonthlyIncome);
    }

    [Theory]
    [InlineData(" Single ", 0)] [InlineData("mArRiEd", 1)]
    [InlineData(" divorced ", 2)] [InlineData("WIDOWED", 3)]
    [InlineData("Unknown", null)] [InlineData(null, null)]
    public void MaritalCodesAreExactAndUnknownDoesNotBecomeSingle(string? source, int? expected)
        => Assert.Equal(expected, QuickIntakeApplicationFields.MaritalStatus(source));

    [Fact]
    public void GenuineZeroNoAndNoneRemainDistinctFromHistoricalUnknown()
    {
        var known = QuickIntakeSubmissionMapping.Apply(Submission(), Intake(0, false, false)) with
        { CurrentAssistancePrograms = ["None"], VictimCircumstances = ["None of the Above"] };
        using var sql = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(sql, known);
        Assert.Equal(0m, sql.Parameters["@Income"].Value);
        Assert.Equal("None", sql.Parameters["@Dependents"].Value);
        Assert.Equal(false, sql.Parameters["@Disabled"].Value);
        Assert.Equal("None of the Above", sql.Parameters["@Programme"].Value);
        Assert.Equal("None of the Above", sql.Parameters["@Circumstance"].Value);
        var missing = QuickIntakeSubmissionMapping.Apply(Submission(), Intake(0, false, false, true)) with { VictimCircumstances = null };
        using var unknown = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(unknown, missing);
        foreach (string field in new[] { "@Income", "@MaritalStatus", "@Dependents", "@Disabled", "@Programme", "@Circumstance" })
            Assert.Same(DBNull.Value, unknown.Parameters[field].Value);
    }

    [Fact]
    public void ActualSelectionsAreCanonicalCompleteDeterministicAndNeverRequestedAssistance()
    {
        var first = Submission() with { CurrentAssistancePrograms = ["Social Pension", "path", "PATH"],
            VictimCircumstances = ["Flooding", "House Fire", "house fire"] };
        var second = first with { CurrentAssistancePrograms = ["PATH", "social pension"], VictimCircumstances = ["House Fire", "Flooding"] };
        Assert.Equal("PATH, Social Pension", QuickIntakeApplicationFields.Programme(first));
        Assert.Equal(QuickIntakeApplicationFields.Programme(first), QuickIntakeApplicationFields.Programme(second));
        Assert.Equal(QuickIntakeApplicationFields.Circumstance(first), QuickIntakeApplicationFields.Circumstance(second));
        Assert.Equal("Social Pension", QuickIntakeApplicationFields.Programme(first with { CurrentAssistancePrograms = ["Solidarity Program"] }));
        Assert.Equal("Abuse (Physical, Emotional, Sexual, Verbal, or Substance)",
            QuickIntakeApplicationFields.Circumstance(first with { VictimCircumstances = ["Domestic Abuse"] }));
        Assert.Null(QuickIntakeApplicationFields.Programme(Submission()));
        Assert.Null(QuickIntakeApplicationFields.Programme(Submission() with { CurrentAssistancePrograms = [] }));
        Assert.Null(QuickIntakeApplicationFields.Circumstance(Submission()));
    }

    [Fact]
    public void OlderMessagesAndRegularSubmissionsRetainCompatibilityWithoutQuickIntakeFallbacks()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var oldJson = JsonSerializer.Serialize(Submission(), options);
        using var parsed = JsonDocument.Parse(oldJson);
        var oldFields = parsed.RootElement.EnumerateObject().Where(p => p.Name is not
            ("weeklyIncome" or "applicantMaritalStatus" or "hasChildrenUnder18LivingFullTime" or
             "hasDisabilityOrSeriousMedicalCondition" or "currentAssistancePrograms" or "victimCircumstances"))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var old = JsonSerializer.Deserialize<ExternalApplicationSubmission>(JsonSerializer.Serialize(oldFields), options)!;
        using var quick = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(quick, old);
        Assert.Same(DBNull.Value, quick.Parameters["@Programme"].Value);
        Assert.Same(DBNull.Value, quick.Parameters["@Income"].Value);
        var regular = Submission(false);
        using var normal = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(normal, regular);
        Assert.Equal(regular.ProgramCategory, normal.Parameters["@Programme"].Value);
        Assert.Single(normal.Parameters.Cast<SqlParameter>());
        Assert.Equal("", BridgeQuickIntakeFields.Columns(regular));
        Assert.Equal("", BridgeQuickIntakeFields.Values(regular));
        Assert.Same(regular, QuickIntakeSubmissionMapping.Apply(regular, null));
    }

    [Theory]
    [InlineData("income")] [InlineData("precision")] [InlineData("negative")]
    [InlineData("programmes")] [InlineData("circumstances")] [InlineData("none")]
    public async Task BothGatewaysRejectInvalidMappingBeforeSqlWithoutTruncation(string invalid)
    {
        var s = invalid switch
        {
            "income" => Submission() with { WeeklyIncome = 100000000m },
            "precision" => Submission() with { WeeklyIncome = 1.001m },
            "negative" => Submission() with { WeeklyIncome = -1m },
            "programmes" => Submission() with { CurrentAssistancePrograms = [new string('x', 256)] },
            "circumstances" => Submission() with { VictimCircumstances = [new string('x', 256)] },
            _ => Submission() with { CurrentAssistancePrograms = ["None", "PATH"] }
        };
        foreach (bool synced in new[] { false, true })
        {
            IFfpManagerGateway gateway = synced ? new SyncedSqlFfpManagerGateway(Options.Create(new BridgeOptions()), null!)
                : new SqlFfpManagerGateway(Options.Create(new BridgeOptions()));
            await Assert.ThrowsAsync<ExternalApplicationValidationException>(() => gateway.CreateApplicationAsync(s, TestContext.Current.CancellationToken));
        }
        ExternalApplicantValidation.Validate(Submission() with { WeeklyIncome = 99999999.99m });
    }

    [Fact]
    public void ProfileKeepsMonthlyMeaningAndDoesNotOverwriteExistingIncome()
    {
        var intake = Intake(1200);
        var profile = new RequestPortal.Infrastructure.Identity.ApplicationUser();
        RequestPortal.Infrastructure.Identity.IntakeProfilePromotion.FillMissing(profile, intake);
        Assert.Equal(5200m, profile.MonthlyHouseholdIncome);
        Assert.Equal(1200m, intake.MonthlyIncome);
        profile.MonthlyHouseholdIncome = 7500m;
        RequestPortal.Infrastructure.Identity.IntakeProfilePromotion.FillMissing(profile, intake);
        Assert.Equal(7500m, profile.MonthlyHouseholdIncome);
    }

    [Fact]
    public void TransferOptionsDefaultOnInputAndSurviveSavedSourceAndWireMapping()
    {
        var input = new QuickIntakeModel.InputModel();
        Assert.Equal(2, input.PayFrequency);
        Assert.Equal(2, input.PhoneLineType);
        var intake = new QuickIntakeRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Test", "Applicant", new(1980, 1, 1), "Male", "Single", "18765230123", null,
            "Street", "Town", "Kingston", "Employed", 100, false, false, "PATH", "Food", DateTimeOffset.UtcNow,
            payFrequency: input.PayFrequency, phoneLineType: input.PhoneLineType);
        var submission = QuickIntakeSubmissionMapping.Apply(Submission(), intake);
        submission = JsonSerializer.Deserialize<ExternalApplicationSubmission>(JsonSerializer.Serialize(submission))!;
        Assert.Equal(intake.PayFrequency, submission.PayFrequency);
        Assert.Equal(intake.PhoneLineType, submission.PhoneLineType);
        using var sql = new SqlCommand();
        BridgeQuickIntakeFields.AddParameters(sql, submission);
        Assert.Equal(submission.PayFrequency, sql.Parameters["@PayFrequency"].Value);
        var phone = Assert.Single(BridgeApplicantProfile.Phones(submission with { PrimaryTelephone = "18765230123" }));
        Assert.Equal((long)submission.PhoneLineType!.Value, BridgeApplicantProfile.ResolveLineType(submission, phone, [(2, "Mobile", "Cellular Mobile")]));
        Assert.Throws<InvalidOperationException>(() => BridgeApplicantProfile.ResolveLineType(submission, phone, []));
    }

    [Fact]
    public void LegacyNullOptionsAndOlderMessagesKeepAgreedDefaults()
    {
        var intake = new QuickIntakeRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Test", "Applicant", new(1980, 1, 1), "Male", "Single", "18765230123", null,
            "Street", "Town", "Kingston", "Employed", 100, false, false, "PATH", "Food", DateTimeOffset.UtcNow,
            payFrequency: null, phoneLineType: null);
        var mapped = QuickIntakeSubmissionMapping.Apply(Submission(), intake);
        Assert.Equal(2, mapped.PayFrequency);
        Assert.Equal(2, mapped.PhoneLineType);
        Assert.Equal(2, QuickIntakeApplicationFields.MappedValues(Submission()).PayFrequency);
        var util = PhoneNumberUtil.GetInstance();
        string fixedOrMobile = util.Format(util.GetExampleNumberForType("JM", PhoneNumberType.FIXED_LINE), PhoneNumberFormat.E164);
        var phone = Assert.Single(BridgeApplicantProfile.Phones(Submission() with { PrimaryTelephone = fixedOrMobile }));
        Assert.Equal(2, BridgeApplicantProfile.ResolveLineType(Submission(), phone, [(2, "Mobile", "Cellular Mobile")]));
        // A regular request ignores Quick Intake options and retains number classification.
        Assert.Throws<InvalidOperationException>(() => BridgeApplicantProfile.ResolveLineType(Submission(false) with { PhoneLineType = 2 }, phone, [(2, "Mobile", "Cellular Mobile")]));
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(2, 0)] [InlineData(3, 2)] [InlineData(2, 3)]
    public void UnsupportedHiddenOptionsFailValidation(int payFrequency, int phoneLineType)
    {
        var input = new QuickIntakeModel.InputModel { PayFrequency = payFrequency, PhoneLineType = phoneLineType };
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(input, new(input), errors, true));
        Assert.Contains(errors, error => error.MemberNames.Contains(payFrequency != 2 ? "PayFrequency" : "PhoneLineType"));
        Assert.Throws<ExternalApplicationValidationException>(() => QuickIntakeApplicationFields.Validate(
            Submission() with { PayFrequency = payFrequency, PhoneLineType = phoneLineType }));
    }

    [Fact]
    public async Task QuickIntakeRendersBothTransferOptionsOnlyAsHiddenDefaults()
    {
        var assembly = typeof(QuickIntakeModel).Assembly;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath("../../../../../RequestPortal.Web", AppContext.BaseDirectory),
            EnvironmentName = "Test"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityCore<RequestPortal.Infrastructure.Identity.ApplicationUser>();
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<IUserStore<RequestPortal.Infrastructure.Identity.ApplicationUser>>(
            (name, _) => name == "Dispose" ? null : throw new InvalidOperationException("No authenticated user expected.")));
        builder.Services.AddSingleton<IQuickIntakeService>(new Capture());
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<RequestPortal.Application.Documents.IDocumentService>(
            (_, _) => throw new InvalidOperationException("No document access expected.")));
        builder.Services.AddRazorPages().AddApplicationPart(assembly);
        builder.Services.AddPageAuthorizationFixture();
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapRazorPages();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/QuickIntake", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string html = await response.Content.ReadAsStringAsync(ct);
        foreach (string field in new[] { "PayFrequency", "PhoneLineType" })
        {
            string input = Assert.Single(System.Text.RegularExpressions.Regex.Matches(html,
                "<input[^>]*name=\"Input\\." + field + "\"[^>]*>").Cast<System.Text.RegularExpressions.Match>()).Value;
            Assert.Contains("type=\"hidden\"", input);
            Assert.Contains("value=\"2\"", input);
            Assert.DoesNotContain("for=\"Input_" + field + "\"", html);
        }
    }

    [Fact]
    public void PersistenceMigrationOnlyAddsTwoNullableOptionsWithoutBackfillOrDefaults()
    {
        var migration = new RequestPortal.Infrastructure.Persistence.Migrations.CaptureQuickIntakeTransferOptions();
        var columns = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>().ToArray();
        Assert.Equal(new[] { "PayFrequency", "PhoneLineType" }, columns.Select(column => column.Name));
        Assert.All(columns, column =>
        {
            Assert.Equal("quick_intakes", column.Table);
            Assert.Equal("integer", column.ColumnType);
            Assert.True(column.IsNullable);
            Assert.Null(column.DefaultValue);
            Assert.Null(column.DefaultValueSql);
        });
        Assert.Equal(3, migration.UpOperations.Count); // Two columns and bounded local DDL timeouts.
        using var db = new RequestPortal.Infrastructure.Persistence.RequestPortalDesignTimeDbContextFactory().CreateDbContext([]);
        var entity = db.Model.FindEntityType(typeof(QuickIntakeRecord))!;
        foreach (string name in new[] { "PayFrequency", "PhoneLineType" })
        {
            var property = entity.FindProperty(name)!;
            Assert.True(property.IsNullable);
            Assert.Equal(typeof(int?), property.ClrType);
        }
    }

    sealed class Capture : IQuickIntakeService
    {
        public QuickIntakeSubmission? Submitted;
        public Task<QuickIntakeResult> CreateAsync(QuickIntakeSubmission submission, CancellationToken ct = default, Guid? requestId = null, Guid? applicantUserId = null)
        { Submitted = submission; return Task.FromResult(new QuickIntakeResult(requestId ?? Guid.NewGuid(), "RP-CAPTURE", "18765230123")); }
        public Task<QuickIntakeView?> GetAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Guid> GetAnonymousUploaderIdAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ConvertToIndividualAsync(Guid requestId, Guid userId, Guid staffUserId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false, "1250.50")] [InlineData(true, "1250.50")]
    [InlineData(false, "0")] [InlineData(true, "0")]
    public async Task BothQuickIntakePostPathsPreserveEnteredWeeklyAmount(bool start, string amount)
    {
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["Input.MonthlyIncome"] = amount });
        var capture = new Capture();
        var model = new QuickIntakeModel(capture, null!, null!) { SubmissionId = Guid.NewGuid(), PageContext = new PageContext { HttpContext = context }, Input = new()
        {
            FirstName = "Test", LastName = "Applicant", DateOfBirth = new(1980, 1, 1), Gender = "Male", MaritalStatus = "Single",
            Telephone = "8765230123", StreetAddress = "Street", CommunityDistrict = "Town", Parish = "Kingston", EmploymentStatus = "Employed",
            HasChildrenUnder18LivingFullTime = "No", HasDisabilityOrSeriousMedicalCondition = "No", AssistancePrograms = ["PATH", "Social Pension"],
            HelpCategory = "Food", VictimCircumstances = ["None of the Above"]
        }};
        var result = start ? await model.OnPostStartAsync() : await model.OnPostAsync();
        Assert.True(model.ModelState.IsValid);
        Assert.NotNull(capture.Submitted);
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), capture.Submitted.MonthlyIncome);
        Assert.Equal(model.Input.PayFrequency, capture.Submitted.PayFrequency);
        Assert.Equal(model.Input.PhoneLineType, capture.Submitted.PhoneLineType);
        Assert.True(result is PageResult or JsonResult);
    }
}
