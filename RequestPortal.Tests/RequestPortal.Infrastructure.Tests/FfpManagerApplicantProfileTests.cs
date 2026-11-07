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
        Assert.Equal("18765230123",phone.Number);
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

    [Theory]
    [InlineData("Request Portal - Quick Intake")]
    [InlineData(" request portal - quick intake ")]
    public void QuickIntakePhonesUseLineTypeTwoForBothApplicantAndSecondary(string source)
    {
        var util = PhoneNumberUtil.GetInstance();
        string primary = util.Format(util.GetExampleNumberForType("JM", PhoneNumberType.FIXED_LINE), PhoneNumberFormat.E164);
        string secondary = util.Format(util.GetExampleNumberForType("GB", PhoneNumberType.FIXED_LINE), PhoneNumberFormat.E164);
        Assert.Equal(PhoneNumberType.FIXED_LINE_OR_MOBILE, util.GetNumberType(util.Parse(primary, "JM")));
        var phones = BridgeApplicantProfile.Phones(Submission() with
        {
            DocumentSource = source, PrimaryTelephone = primary, SecondaryTelephone = secondary
        });
        Assert.Equal(new long[] { 93, 83 }, phones.Select(phone => phone.NumberType));
        Assert.Equal(new[] { primary[1..], secondary[1..] }, phones.Select(phone => phone.Number));
        (long, string?, string?)[] lines = [(1, "4", "Land Line"), (2, "Mobile", "Cellular Mobile"), (3, "Skype", "Internet Telephony")];
        foreach (var phone in phones)
        {
            Assert.Equal(PhoneNumberType.MOBILE, phone.LineType);
            long lineType = BridgeApplicantProfile.ResolveLineType(phone.LineType, lines);
            Assert.Equal(2, lineType);
            using var command = new Microsoft.Data.SqlClient.SqlCommand();
            BridgeApplicantProfile.AddPhoneParameters(command, new(phone.Number, phone.NumberType, lineType));
            Assert.Equal(2L, command.Parameters["@LineType"].Value);
        }
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
        Assert.Throws<ExternalApplicationValidationException>(()=>BridgeApplicantProfile.Normalize(number));

    [Theory]
    [InlineData("876 523-0123", "+18765230123")]
    [InlineData("+1 (876) 523-0123", "+18765230123")]
    [InlineData("0018765230123", "+18765230123")]
    [InlineData("011442079460018", "+442079460018")]
    public void IntakeKeepsE164AndBridgeWritesOnlyCanonicalDigits(string input, string expected)
    {
        Assert.Equal(expected, RequestPortal.Common.Contacts.ApplicantTelephone.Normalize(input));
        Assert.Equal(expected[1..], BridgeApplicantProfile.Normalize(input));
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
                PrimaryTelephone = primary ? input : "18765230123",
                SecondaryTelephone = primary ? null : input };
            var failure = Assert.Throws<ExternalApplicationValidationException>(() => BridgeApplicantProfile.Validate(submission));
            Assert.Equal(primary ? "PrimaryTelephone" : "SecondaryTelephone", failure.Field);
            Assert.DoesNotContain(input, failure.Message);
        }
    }

    [Theory]
    [InlineData("(876) 523-0123", "+44 20 7946 0018", "18765230123", "442079460018")]
    [InlineData("18765230123", "442079460018", "18765230123", "442079460018")]
    [InlineData("0018765230123", "011442079460018", "18765230123", "442079460018")]
    [InlineData("+1.876.523.0123", "+65 8123 4567", "18765230123", "6581234567")]
    public void ActualPhoneInsertParametersContainOnlyValidatedDigits(string primary, string secondary, string expectedPrimary, string expectedSecondary)
    {
        var submission = Submission() with { PrimaryTelephone = primary, SecondaryTelephone = secondary };
        BridgeApplicantProfile.Validate(submission);
        var phones = BridgeApplicantProfile.Phones(submission);
        Assert.Equal(2, phones.Count);
        Assert.Equal(expectedPrimary, BridgeApplicantProfile.MainContact(submission));
        foreach (var pair in phones.Zip(new[] { expectedPrimary, expectedSecondary }))
        {
            using var command = new Microsoft.Data.SqlClient.SqlCommand();
            BridgeApplicantProfile.AddPhoneParameters(command, new(pair.First.Number, pair.First.NumberType, 2));
            var parameter = command.Parameters["@Number"];
            Assert.Equal(pair.Second, Assert.IsType<string>(parameter.Value));
            Assert.All((string)parameter.Value, digit => Assert.True(char.IsAsciiDigit(digit)));
            Assert.Equal(System.Data.SqlDbType.NVarChar, parameter.SqlDbType); Assert.Equal(15, parameter.Size);
            Assert.Equal(pair.First.NumberType, command.Parameters["@NumberType"].Value);
        }
        // Digits-only international values are interpreted with their country code,
        // including values which resemble a Jamaican local number.
        var roundTrip = BridgeApplicantProfile.Phones(submission with { PrimaryTelephone = expectedPrimary, SecondaryTelephone = expectedSecondary });
        Assert.Equal(phones, roundTrip);
    }

    [Theory]
    [InlineData("8761234567")]
    [InlineData("123")]
    [InlineData("+1 876 523 0123 ext 5")]
    [InlineData("18765230123;5")]
    [InlineData("１２３４５６７８９０")]
    [InlineData("1234567890123456")]
    public void InvalidValuesCannotReachPhoneInsertParameters(string value)
    {
        using var command = new Microsoft.Data.SqlClient.SqlCommand();
        Assert.Throws<ExternalApplicationValidationException>(() => BridgeApplicantProfile.AddPhoneParameters(command, new(value, 83, 2)));
        Assert.Empty(command.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>());
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
        var s=Submission() with { PrimaryTelephone="18765230123",SecondaryTelephone="18762101234",DateOfBirth=new(1980,2,3),StreetAddress="Street",CommunityDistrict="Town",ApplicantGender="Female",ApplicantEmail="test@example.invalid",ApplicantEmploymentStatus="Unemployed" };
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
        Assert.Throws<ExternalApplicationValidationException>(()=>BridgeApplicantProfile.Validate(Submission() with { ApplicantEmail=new string('a',101) }));
        Assert.Throws<ExternalApplicationValidationException>(()=>BridgeApplicantProfile.Validate(Submission() with { DateOfBirth=new(1752,12,31) }));
    }
}
