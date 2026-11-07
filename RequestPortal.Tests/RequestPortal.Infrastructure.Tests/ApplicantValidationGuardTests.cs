using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FfpManager.Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Web.Pages;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ApplicantValidationGuardTests
{
    static ExternalApplicationSubmission Submission() => new(Guid.NewGuid(), Guid.NewGuid(), "RP-VALIDATION", Guid.NewGuid(),
        "Applicant", null, null, null, null, Guid.NewGuid(), DateTimeOffset.UtcNow, "Individual", "Normal", null, null, null, null, null, null, [], [], "Kingston");

    static ExternalApplicationSubmission Invalid(string field) => field switch
    {
        "PrimaryTelephone" => Submission() with { PrimaryTelephone = "PRIVATE_INVALID" },
        "SecondaryTelephone" => Submission() with { SecondaryTelephone = "PRIVATE_INVALID" },
        "DateOfBirth" => Submission() with { DateOfBirth = new(1752, 12, 31) },
        "ApplicantEmail" => Submission() with { ApplicantEmail = "PRIVATE_INVALID" },
        _ => Submission() with { ApplicantEmploymentStatus = new string('x', 256) }
    };

    [Theory]
    [InlineData("PrimaryTelephone", "VALIDATION_PRIMARY_TELEPHONE")]
    [InlineData("SecondaryTelephone", "VALIDATION_SECONDARY_TELEPHONE")]
    [InlineData("DateOfBirth", "VALIDATION_DATE_OF_BIRTH")]
    [InlineData("ApplicantEmail", "VALIDATION_APPLICANT_EMAIL")]
    [InlineData("ApplicantEmploymentStatus", "VALIDATION_EMPLOYMENT_STATUS")]
    public async Task BothGatewaysRejectBeforeSqlAndEndpointReturnsSafe422(string field, string code)
    {
        foreach (bool synced in new[] { false, true })
        {
            IFfpManagerGateway gateway = synced ? new SyncedSqlFfpManagerGateway(Options.Create(new BridgeOptions()), null!) : new SqlFfpManagerGateway(Options.Create(new BridgeOptions()));
            var result = await ApplicationSubmissionEndpoint.HandleAsync(Invalid(field), gateway, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
            Assert.Equal(422, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            var failure = Assert.IsType<TransferFailure>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.Equal(code, failure.Code); Assert.Equal(field, failure.Field); Assert.False(failure.Transient);
            Assert.NotNull(failure.ValidationRule); Assert.NotNull(failure.CorrectiveAction);
            Assert.DoesNotContain("PRIVATE_INVALID", JsonSerializer.Serialize(failure));
            Assert.DoesNotContain(new string('x', 256), JsonSerializer.Serialize(failure));
        }
    }

    [Fact]
    public void OptionalValuesSqlBoundaryFutureDatesAndEmailLimitAreEnforcedWithoutChangingValues()
    {
        ExternalApplicantValidation.Validate(Submission());
        ExternalApplicantValidation.Validate(Submission() with { DateOfBirth = new(1753, 1, 1), ApplicantEmploymentStatus = new string('x', 255) });
        Assert.Equal("VALIDATION_DATE_OF_BIRTH", Assert.Throws<ExternalApplicationValidationException>(() => ExternalApplicantValidation.DateOfBirth(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))).Code);
        string validEmail = new string('a', 60) + "@" + new string('b', 35) + ".com";
        Assert.Equal(100, validEmail.Length); ExternalApplicantValidation.Email(validEmail);
        Assert.Equal("VALIDATION_APPLICANT_EMAIL", Assert.Throws<ExternalApplicationValidationException>(() => ExternalApplicantValidation.Email("a" + validEmail)).Code);
        Assert.Null(TransferFailures.For("Preparation", "VALIDATION").Field);
    }

    [Theory]
    [InlineData("DateOfBirth")]
    [InlineData("Email")]
    [InlineData("Telephone")]
    [InlineData("SecondaryTelephone")]
    [InlineData("EmploymentStatus")]
    public async Task QuickIntakeBothPostPathsReturnErrorsBesideInvalidField(string field)
    {
        foreach (bool start in new[] { false, true })
        {
            var context = new DefaultHttpContext();
            context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["Input.MonthlyIncome"] = "0" });
            var model = new QuickIntakeModel(null!, null!, null!) { SubmissionId = Guid.NewGuid(), PageContext = new PageContext { HttpContext = context }, Input = new()
            {
                FirstName = "Test", LastName = "Applicant", DateOfBirth = new(1980, 1, 1), Gender = "Male", MaritalStatus = "Single",
                Telephone = "8765230123", StreetAddress = "Street", CommunityDistrict = "Town", Parish = "Kingston", EmploymentStatus = "Employed",
                HasChildrenUnder18LivingFullTime = "No", HasDisabilityOrSeriousMedicalCondition = "No", AssistancePrograms = ["None of the Above"], HelpCategory = "Food"
            }};
            switch (field)
            {
                case "DateOfBirth": model.Input.DateOfBirth = new(1752, 12, 31); break;
                case "Email": model.Input.Email = new string('x', 101); break;
                case "Telephone": model.Input.Telephone = "PRIVATE_INVALID"; break;
                case "SecondaryTelephone": model.Input.SecondaryTelephone = "PRIVATE_INVALID"; break;
                default: model.Input.EmploymentStatus = "Unsupported"; break;
            }
            var errors = new List<ValidationResult>();
            Validator.TryValidateObject(model.Input, new ValidationContext(model.Input), errors, true);
            foreach (var error in errors) foreach (string member in error.MemberNames) model.ModelState.AddModelError("Input." + member, error.ErrorMessage!);
            IActionResult result = start ? await model.OnPostStartAsync() : await model.OnPostAsync();
            Assert.NotEmpty(model.ModelState["Input." + field]!.Errors);
            Assert.True(result is BadRequestObjectResult or PageResult);
        }
    }
}
