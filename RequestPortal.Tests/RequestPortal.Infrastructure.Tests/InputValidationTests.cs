using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Contacts;
using RequestPortal.Common.Validation;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;
public sealed class InputValidationTests
{
    [Theory]
    [InlineData("0",true)][InlineData("5000",true)][InlineData("12500.50",true)][InlineData("25000",true)]
    [InlineData("-5000",false)][InlineData("abc",false)][InlineData("12e3",false)][InlineData("0005000",false)]
    [InlineData("5000.999",false)][InlineData("9999999999999999999999999999999999999",false)]
    [InlineData("1,000",false)][InlineData("1.00\n",false)]
    public void IncomeFormatAndOverflow(string input,bool valid)=>Assert.Equal(valid,WeeklyIncome.TryParse(input,out _));
    [Fact]public void DecimalMonthlyConversionRetainsExistingContract()
    {Assert.Equal(21666.67m,WeeklyIncome.ToMonthly(5000m));Assert.Equal(0m,WeeklyIncome.ToMonthly(0m));Assert.Throws<ArgumentException>(()=>WeeklyIncome.ToMonthly(0.001m));}
    [Theory]
    [InlineData("8765230123","+18765230123")][InlineData("(876) 523-0123","+18765230123")]
    [InlineData("+1 876 523 0123","+18765230123")][InlineData("+44 20 7946 0018","+442079460018")]
    public void PhoneNormalizesUsingApprovedLibrary(string value,string expected)=>Assert.Equal(expected,ApplicantTelephone.NormalizeNew(value));
    [Fact]public void JamaicanOverlayAndEquivalentSecondary()
    {
        var util=PhoneNumbers.PhoneNumberUtil.GetInstance();
        var overlay=util.GetExampleNumberForType("JM",PhoneNumbers.PhoneNumberType.MOBILE);
        Assert.NotNull(overlay);Assert.NotNull(ApplicantTelephone.NormalizeNew(util.Format(overlay,PhoneNumbers.PhoneNumberFormat.E164)));
        Assert.Equal(ApplicantTelephone.NormalizeNew("8765230123"),ApplicantTelephone.NormalizeNew("+18765230123"));
        string? valid658=Enumerable.Range(200,800).Select(prefix=>"658"+prefix+"0123").FirstOrDefault(number=>{
            try {return ApplicantTelephone.NormalizeNew(number) is not null;}catch(ArgumentException){return false;}});
        Assert.NotNull(valid658);Assert.StartsWith("+1658",ApplicantTelephone.NormalizeNew(valid658));
        Assert.Null(ApplicantTelephone.NormalizeNew(null));
    }
    [Theory][InlineData("123")][InlineData("8761234567")][InlineData("+08765230123")][InlineData("8765230123ext123")]
    public void InvalidTelephone(string value)=>Assert.Throws<ArgumentException>(()=>ApplicantTelephone.NormalizeNew(value));
    [Theory][InlineData("User@example.com","User@example.com")][InlineData(" User+tag@Sub.Example.COM ","User+tag@sub.example.com")]
    public void EmailPreservesLocalPart(string input,string expected)=>Assert.Equal(expected,ContactEmail.Normalize(input));
    [Theory][InlineData("no-at")][InlineData("a@@example.com")][InlineData("a@example.com\r\nBcc:other@example.com")]
    [InlineData("<script>alert(1)</script>@example.com")][InlineData("a@-bad.com")]
    public void InvalidEmail(string value)=>Assert.Throws<ArgumentException>(()=>ContactEmail.Normalize(value));
    [Fact]public void LongEmailIsRejectedAndMailtoEscapesLocalPart()
    {Assert.Throws<ArgumentException>(()=>ContactEmail.Normalize(new string('a',321)+"@example.com"));Assert.Equal("mailto:User%2Btag%40example.com",ContactEmail.Mailto("User+tag@example.com"));}
    [Fact]
    public void NormalizedFormsKeepAttemptedValuesAndReportInlineErrors()
    {
        var input=new RequestPortal.Web.Pages.QuickIntakeModel.InputModel {Telephone="8765230123",Email=" User+tag@Example.COM ",SecondaryTelephone="123"};
        var state=new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary();
        state.SetModelValue("Input.Email",new Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult(input.Email));
        InputContactNormalization.Apply(input,state,"Input");
        Assert.Equal("+18765230123",input.Telephone);Assert.Equal("User+tag@example.com",input.Email);
        Assert.Equal(" User+tag@Example.COM ",state["Input.Email"]!.AttemptedValue);
        Assert.Single(state["Input.SecondaryTelephone"]!.Errors);
    }
    [Fact]
    public async Task HistoricalDataAuditCountsButNeverRewritesContactValues()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var user=new RequestPortal.Infrastructure.Identity.ApplicationUser {Id=Guid.NewGuid(),UserName="quality-fixture",Email=" User+tag@Example.COM ",PhoneNumber="8765230123",AlternativePhoneNumber="123"};
        fixture.Db.Users.Add(user);await fixture.Db.SaveChangesAsync(ct);
        var report=await new RequestPortal.Infrastructure.Management.InputDataQualityAudit(fixture.Db).RunAsync(ct);
        Assert.True(report.ReadOnly);Assert.Equal(1,report.Fields.Single(x=>x.Field=="AspNetUsers.Email").NonCanonical);
        Assert.Equal(1,report.Fields.Single(x=>x.Field=="AspNetUsers.AlternativePhoneNumber").InvalidUnderNewRules);
        Assert.DoesNotContain("User+tag",System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(" User+tag@Example.COM ",await fixture.Db.Users.AsNoTracking().Where(x=>x.Id==user.Id).Select(x=>x.Email).SingleAsync(ct));
    }

    [Theory]
    [InlineData(false,"12e3")][InlineData(true,"12e3")][InlineData(false,"0005000")][InlineData(true,"5000.999")]
    public async Task BothQuickIntakeHandlersValidateTheRawNumericInput(bool uploadAssisted,string raw)
    {
        var page=new RequestPortal.Web.Pages.QuickIntakeModel(null!,null!,null!) {
            PageContext=new Microsoft.AspNetCore.Mvc.RazorPages.PageContext {HttpContext=new Microsoft.AspNetCore.Http.DefaultHttpContext()},SubmissionId=Guid.NewGuid()};
        page.Request.Form=new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string,Microsoft.Extensions.Primitives.StringValues> {{"Input.MonthlyIncome",raw}});
        page.Input.DateOfBirth=new(1980,1,1);page.Input.AssistancePrograms=["None of the Above"];
        if(uploadAssisted)Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await page.OnPostStartAsync());
        else Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await page.OnPostAsync());
        Assert.NotEmpty(page.ModelState["Input.MonthlyIncome"]!.Errors);
    }
    [Fact]
    public async Task ValidIncomeIsPreservedWhenAnotherFieldFails()
    {
        var page=new RequestPortal.Web.Pages.QuickIntakeModel(null!,null!,null!) {
            PageContext=new Microsoft.AspNetCore.Mvc.RazorPages.PageContext {HttpContext=new Microsoft.AspNetCore.Http.DefaultHttpContext()}};
        page.Request.Form=new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string,Microsoft.Extensions.Primitives.StringValues> {{"Input.MonthlyIncome","12500.50"}});
        page.Input.DateOfBirth=new(1980,1,1);page.Input.AssistancePrograms=["None of the Above"];
        page.ModelState.AddModelError("Input.LastName","Fixture validation failure.");
        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await page.OnPostAsync());Assert.Equal(12500.50m,page.Input.MonthlyIncome);
        Assert.Equal(54168.83m,WeeklyIncome.ToMonthly(page.Input.MonthlyIncome));
        Assert.True(WeeklyIncome.ToMonthly(WeeklyIncome.MaximumWeekly)<=WeeklyIncome.MaximumMonthly);
    }

}
