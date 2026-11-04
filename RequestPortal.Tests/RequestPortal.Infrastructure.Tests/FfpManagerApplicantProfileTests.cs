using System.Text.Json;
using FfpManager.Bridge;
using PhoneNumbers;
using RequestPortal.Application.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerApplicantProfileTests
{
    private static ExternalApplicationSubmission Submission() => new(Guid.NewGuid(), Guid.NewGuid(), "RP-PROFILE-TEST",
        Guid.NewGuid(), "Applicant", null, null, "legacy organization number", null, Guid.NewGuid(), DateTimeOffset.UtcNow,
        "Food", "Normal", null, null, null, null, null, null, [], []);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void PrimaryOnly_OmitsEmptySecondaryAndKeepsLegacyCopy(string? secondary)
    {
        var s = Submission() with { PrimaryTelephone = "876 523-0123", SecondaryTelephone = secondary };
        var phone = Assert.Single(BridgeApplicantProfile.Phones(s));
        Assert.Equal("+18765230123",phone.Number);
        Assert.Equal(93,phone.NumberType);
        Assert.Equal(phone.Number,BridgeApplicantProfile.MainContact(s));
    }

    [Fact]
    public void TwoNumbers_KeepIndependentRolesAndClassifyLandlineSeparatelyFromMobile()
    {
        var util = PhoneNumberUtil.GetInstance();
        string mobile = util.Format(util.GetExampleNumberForType("JM",PhoneNumberType.MOBILE),PhoneNumberFormat.E164);
        string land = util.Format(util.GetExampleNumberForType("GB",PhoneNumberType.FIXED_LINE),PhoneNumberFormat.E164);
        var phones = BridgeApplicantProfile.Phones(Submission() with { PrimaryTelephone=mobile,SecondaryTelephone=land });
        Assert.Equal(2,phones.Count);
        Assert.Equal(PhoneNumberType.MOBILE,phones[0].LineType);
        Assert.Equal(PhoneNumberType.FIXED_LINE,phones[1].LineType);
        Assert.Equal(93,phones[0].NumberType);
        Assert.Equal(83,phones[1].NumberType);
        (long,string?,string?)[] lines=[(1,"4","Land Line"),(2,"Mobile","Cellular Mobile")];
        Assert.Equal(2,BridgeApplicantProfile.ResolveLineType(phones[0].LineType,lines));
        Assert.Equal(1,BridgeApplicantProfile.ResolveLineType(phones[1].LineType,lines));
    }

    [Fact]
    public void JamaicanFixedOrMobileNumberRequiresAnExplicitNeutralMapping()
    {
        var util=PhoneNumberUtil.GetInstance();
        var example=util.GetExampleNumberForType("JM",PhoneNumberType.FIXED_LINE);
        Assert.Equal(PhoneNumberType.FIXED_LINE_OR_MOBILE,util.GetNumberType(example));
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.ResolveLineType(util.GetNumberType(example),[(1,"4","Land Line"),(2,"Mobile","Cellular Mobile")]));
    }

    [Fact]
    public void EqualLocalAndInternationalNumbers_AreOneApplicantNumber()
    {
        var phones = BridgeApplicantProfile.Phones(Submission() with { PrimaryTelephone="(876) 523-0123",SecondaryTelephone="+1 876 523 0123" });
        Assert.Equal(93,Assert.Single(phones).NumberType);
    }

    [Fact]
    public void RegularOrganization_PreservesLegacyPhoneWithoutCreatingApplicantChildren()
    {
        var s=Submission();
        Assert.Empty(BridgeApplicantProfile.Phones(s));
        Assert.Equal(s.OrganizationPhone,BridgeApplicantProfile.MainContact(s));
    }

    [Theory]
    [InlineData("1234567890123456")]
    [InlineData("8765230123 ext 5")]
    [InlineData("8765230123/8765230124")]
    [InlineData("++18765230123")]
    public void UnsafeOrOversizedTelephoneFailsWithoutTruncation(string number) =>
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.Normalize(number));

    [Theory]
    [InlineData("876 523-0123", "+18765230123")]
    [InlineData("+1 (876) 523-0123", "+18765230123")]
    [InlineData("0018765230123", "+18765230123")]
    [InlineData("011442079460018", "+442079460018")]
    public void IntakeAndBridgeUseTheSameCanonicalTelephone(string input, string expected)
    {
        Assert.Equal(expected, RequestPortal.Common.Contacts.ApplicantTelephone.Normalize(input));
        Assert.Equal(expected, BridgeApplicantProfile.Normalize(input));
    }

    [Theory]
    [InlineData("+011442079460018")]
    [InlineData("+08765230123")]
    [InlineData("123")]
    [InlineData("8765230123 ext 5")]
    public void InvalidTelephoneIsRejectedWithoutGuessingOrLeakingItsValue(string input)
    {
        var error = Assert.Throws<ArgumentException>(() => RequestPortal.Common.Contacts.ApplicantTelephone.Normalize(input));
        Assert.DoesNotContain(input, error.Message);
        foreach (bool primary in new[] { true, false })
        {
            var submission = Submission() with {
                PrimaryTelephone = primary ? input : "8765230123",
                SecondaryTelephone = primary ? null : input };
            var failure = Assert.Throws<ExternalApplicationValidationException>(() => BridgeApplicantProfile.Validate(submission));
            Assert.Equal(primary ? "PrimaryTelephone" : "SecondaryTelephone", failure.Field);
            Assert.DoesNotContain(input, failure.Message);
        }
    }

    [Fact]
    public void UnclassifiedOrAmbiguousLineTypeFailsWithoutGuessing()
    {
        (long,string?,string?)[] lines=[(1,"4","Land Line"),(2,"Mobile","Cellular Mobile")];
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.ResolveLineType(PhoneNumberType.FIXED_LINE_OR_MOBILE,lines));
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.ResolveLineType(PhoneNumberType.MOBILE,[..lines,(20,"Mobile",null)]));
    }

    [Fact]
    public void NullableContract_RoundTripsIndependentDateAddressAndTelephoneFields()
    {
        var s=Submission() with { PrimaryTelephone="8765230123",SecondaryTelephone="8762101234",DateOfBirth=new(1980,2,3),StreetAddress="Street",CommunityDistrict="Town",ApplicantGender="Female",ApplicantEmail="test@example.invalid",ApplicantEmploymentStatus="Unemployed" };
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(s,JsonSerializer.Deserialize<ExternalApplicationSubmission>(JsonSerializer.Serialize(s,options),options)! with { Items=s.Items,Artifacts=s.Artifacts });
        BridgeApplicantProfile.Validate(s);
        Assert.Equal(2,BridgeApplicantProfile.Gender(s.ApplicantGender));
        Assert.Equal(1,BridgeApplicantProfile.Gender("Male"));
        Assert.Equal(0,BridgeApplicantProfile.Gender("Unspecified"));
        Assert.Null(BridgeApplicantProfile.Gender("Prefer not to say"));
    }

    [Fact]
    public void SqlFieldLimitsAreCheckedBeforeOpeningTheConnection()
    {
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.Validate(Submission() with { ApplicantEmail=new string('a',101) }));
        Assert.Throws<InvalidOperationException>(()=>BridgeApplicantProfile.Validate(Submission() with { DateOfBirth=new(1752,12,31) }));
    }
}
