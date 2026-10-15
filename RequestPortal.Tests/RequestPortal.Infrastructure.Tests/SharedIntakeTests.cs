using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class SharedIntakeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedIntake_PersistsAnswers_AndLinksSignedInApplicant(bool signedIn)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        Guid applicantId = Guid.NewGuid();
        if (signedIn)
            database.Db.Users.Add(new ApplicationUser
            {
                Id = applicantId, UserName = "existing@example.com", Email = "existing@example.com",
                NormalizedUserName = "EXISTING@EXAMPLE.COM", NormalizedEmail = "EXISTING@EXAMPLE.COM",
                DisplayName = "Test Applicant", CreatedUtc = DateTimeOffset.UtcNow
            });
        database.Db.UnitsOfMeasure.Add(new UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        await database.Db.SaveChangesAsync(token);
        QuickIntakeService service = new(database.Db);
        QuickIntakeSubmission submission = new(
            "Test", "Applicant", new DateOnly(1980, 1, 1), "Male", "Single",
            "8765550123", null, "1 Test Road", "Kingston", "Kingston", "Unemployed",
            0, false, false, ["None"], "Food", "8765550124", ["House Fire", "Flooding"]);
        Guid requestId = Guid.NewGuid();
        QuickIntakeResult result = await service.CreateAsync(submission, token, requestId, signedIn ? applicantId : null);
        var request = await database.Db.AssistanceRequests.SingleAsync(item => item.Id == result.RequestId, token);
        var intake = await database.Db.QuickIntakes.SingleAsync(item => item.RequestId == result.RequestId, token);
        Assert.Contains("House Fire; Flooding", request.Circumstances);
        Assert.Equal("+18765550124", intake.SecondaryTelephone);
        Assert.Equal(signedIn, intake.IsConverted);
        if (signedIn)
        {
            Assert.Equal(applicantId, request.CreatedByUserId);
            Assert.Equal(applicantId, request.SubmittedByUserId);
            Assert.Equal(applicantId, intake.ConvertedToUserId);
        }
        ApplicationUser profile = await database.Db.Users.SingleAsync(token);
        Assert.Equal("Test", profile.FirstName);
        Assert.Equal("Applicant", profile.LastName);
        Assert.Equal("+18765550124", profile.AlternativePhoneNumber);
        Assert.False(profile.HasChildrenUnder18LivingFullTime);
        Assert.False(profile.HasDisabilityOrSeriousMedicalCondition);
        Assert.Equal("None of the Above", profile.AssistancePrograms);
        Assert.Equal("House Fire; Flooding", profile.VictimCircumstances);
        Assert.Equal("House Fire; Flooding", intake.VictimCircumstances);
        Assert.Equal(1, await database.Db.Users.CountAsync(token));
        Assert.Equal(result, await service.CreateAsync(submission, token, requestId, signedIn ? applicantId : null));
        Assert.Equal(1, await database.Db.AssistanceRequests.CountAsync(token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(submission, token, requestId, Guid.NewGuid()));
        if (signedIn)
        {
            await service.CreateAsync(submission, token, Guid.NewGuid(), applicantId);
            Assert.Equal(1, await database.Db.Organizations.CountAsync(token));
            Assert.Equal(1, await database.Db.NotificationPreferences.CountAsync(token));
        }
    }
}
