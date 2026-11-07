using FfpManager.Bridge;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ApplicationFieldUpdateTests
{
    internal static ApplicationFieldPreview Preview(Guid? requestId = null)
    {
        var id = requestId ?? Guid.NewGuid();
        var identity = new ExternalApplicationReceipt(id, "123", "KGN/26/GR/0100", DateTimeOffset.UnixEpoch, "JMDB01", "FFPJM");
        return new(id, "RP-PREVIEW", new(identity, new(null,null,null,null,null,"Existing staff programme",null), new DateTime(2026,11,7), 5),
            new(1250.50m,2,1,"One or More",true,"PATH, Social Pension","House Fire"));
    }

    [Fact]
    public void PreviewUsesDeployedMappingAndDoesNotReplaceUnknownAnswers()
    {
        var source = new ExternalApplicationSubmission(Guid.NewGuid(),Guid.NewGuid(),"RP-MAPPING",Guid.NewGuid(),"Applicant",null,null,null,null,
            Guid.NewGuid(),DateTimeOffset.UtcNow,"Individual","Normal",null,"Food",null,"Food",null,null,[],[],
            DocumentSource:"Request Portal - Quick Intake", WeeklyIncome:0,ApplicantMaritalStatus:" mArRiEd ",
            HasChildrenUnder18LivingFullTime:true,HasDisabilityOrSeriousMedicalCondition:false,
            CurrentAssistancePrograms:["Social Pension","PATH"],VictimCircumstances:["House Fire"]);
        var mapped = QuickIntakeApplicationFields.MappedValues(source);
        Assert.Equal(new ApplicationMappedFields(0m,2,1,"One or More",false,"PATH, Social Pension","House Fire"),mapped);
        using var command = new Microsoft.Data.SqlClient.SqlCommand();
        BridgeQuickIntakeFields.AddParameters(command,source);
        foreach (string name in ApplicationMappedFields.Names)
            Assert.Equal(mapped.Value(name), new ApplicationFieldPreview(source.PortalRequestId,"RP",new(Preview().Destination.Identity,mapped,null,null),mapped)
                .Differences.Single(d=>d.Name==name).Current);
        var unknown = Preview() with { Proposed = new(null,2,null,null,null,null,null) };
        Assert.False(unknown.Differences.Single(d=>d.Name=="Programme").Different);
        Assert.Null(QuickIntakeApplicationFields.MappedValues(source with { WeeklyIncome=null }).Income);
    }

    [Fact]
    public void ExistingValuesIncludingZeroAndFalseAreConflictsAndCannotBeApproved()
    {
        var preview = Preview();
        Assert.True(preview.Differences.Single(d=>d.Name=="Income").CanApply);
        Assert.True(preview.Differences.Single(d=>d.Name=="Programme").Conflict);
        Assert.Throws<InvalidOperationException>(()=>preview.ValidateApproval(["Programme"]));
        var current = preview.Destination.Fields with { Income=0m,Disabled=false,MaritalStatus=0 };
        preview=preview with { Destination=preview.Destination with { Fields=current } };
        foreach (string name in new[]{"Income","Disabled","MaritalStatus"})
        {
            Assert.True(preview.Differences.Single(d=>d.Name==name).Conflict);
            Assert.Throws<InvalidOperationException>(()=>preview.ValidateApproval([name]));
        }
        Assert.Throws<InvalidOperationException>(()=>preview.ValidateApproval([]));
        Assert.Throws<InvalidOperationException>(()=>Preview().ValidateApproval(["Income","Income"]));
        Assert.Throws<InvalidOperationException>(()=>Preview().ValidateApproval(["Income]; DELETE FROM dbo.ApplicationLetters;--"]));
    }

    [Theory]
    [InlineData("Income")][InlineData("PayFrequency")][InlineData("MaritalStatus")][InlineData("Dependents")]
    [InlineData("Disabled")][InlineData("Programme")][InlineData("Circumstance")]
    [InlineData("Identity")][InlineData("Reference")][InlineData("Workflow")][InlineData("LastUpdated")]
    public void AnyMappedValueIdentityReferenceWorkflowOrTimestampChangeInvalidatesPreview(string changed)
    {
        var p=Preview();
        var current=p.Destination;
        current=changed switch {
            "Income"=>current with{Fields=current.Fields with{Income=1}},
            "PayFrequency"=>current with{Fields=current.Fields with{PayFrequency=3}},
            "MaritalStatus"=>current with{Fields=current.Fields with{MaritalStatus=0}},
            "Dependents"=>current with{Fields=current.Fields with{Dependents="None"}},
            "Disabled"=>current with{Fields=current.Fields with{Disabled=false}},
            "Programme"=>current with{Fields=current.Fields with{Programme="Staff edit"}},
            "Circumstance"=>current with{Fields=current.Fields with{Circumstance="Staff edit"}},
            "Identity"=>current with{Identity=current.Identity with{ApplicationId="456"}},
            "Reference"=>current with{Identity=current.Identity with{ApplicationNumber="OTHER"}},
            "Workflow"=>current with{WorkflowStatus=7},
            _=>current with{LastUpdated=current.LastUpdated!.Value.AddSeconds(1)}
        };
        var error=Assert.Throws<IntegrationTransferException>(()=>p.ValidateCurrent(current));
        Assert.Equal("FIELD_PREVIEW_STALE",error.Failure.Code);
        Assert.False(error.Failure.Transient);
    }

    [Fact]
    public void ReadbackMustMatchExactlyApprovedFieldsAndPreserveUnselectedValuesAndStatus()
    {
        var preview=Preview();
        var roundtrip=System.Text.Json.JsonSerializer.Deserialize<ApplicationFieldPreview>(System.Text.Json.JsonSerializer.Serialize(preview))!;
        roundtrip.ValidateCurrent(preview.Destination);
        string[] approved=["Income","PayFrequency"];
        var correct=preview.Destination with{Fields=preview.Destination.Fields.WithApproved(preview.Proposed,approved)};
        preview.ValidateReadback(correct,approved);
        Assert.Equal(preview.Destination.Fields.Programme,correct.Fields.Programme);
        Assert.Equal(preview.Destination.WorkflowStatus,correct.WorkflowStatus);
        Assert.Throws<IntegrationTransferException>(()=>preview.ValidateReadback(correct with{WorkflowStatus=9},approved));
        Assert.Throws<IntegrationTransferException>(()=>preview.ValidateReadback(correct with{Fields=correct.Fields with{Income=999}},approved));
        Assert.Throws<IntegrationTransferException>(()=>preview.ValidateReadback(correct with{Fields=correct.Fields with{Disabled=true}},approved));
    }

    sealed class Deny : IIntegrationOperatorAuthorization
    { public Task DemandAsync(Guid actor,CancellationToken ct=default)=>throw new UnauthorizedAccessException(); }
    [Fact]
    public async Task ServerAuthorizationPrecedesAnySourceOrDestinationReadOrWrite()
    {
        var service=new IntegrationStatusService(null!,new SystemClock(),null!,Options.Create(new FfpManagerBridgeOptions()),null!,null!,new Deny());
        var actor=Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.PreviewFieldsAsync(actor,Guid.NewGuid(),TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ApplyFieldsAsync(actor,Preview(),["Income"],TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.RetryAsync(actor,Guid.NewGuid(),TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SynchronizationStatus.Synchronized,true)]
    [InlineData(SynchronizationStatus.ReconciliationRequired,true)]
    [InlineData(SynchronizationStatus.Failed,false)]
    [InlineData(SynchronizationStatus.Pending,false)]
    public void FieldActionRequiresPositiveIdentityAndSupportedQuickIntakeStatus(SynchronizationStatus status,bool allowed)
    {
        var row=new IntegrationTransferRow(Guid.NewGuid(),Guid.NewGuid(),"RP","Applicant","Quick Intake","FFPManager","JMDB01 / FFPJM",status,
            null,null,null,null,DateTimeOffset.UtcNow,null,0,null,null,DateTimeOffset.UtcNow,"123","REF",true);
        Assert.Equal(allowed,row.CanUpdateFields);
        Assert.False((row with{VerifiedUtc=null}).CanUpdateFields);
        Assert.False((row with{ApplicationId=null}).CanUpdateFields);
        Assert.False((row with{ApplicationNumber=""}).CanUpdateFields);
        Assert.False((row with{Source="Request Portal"}).CanUpdateFields);
    }
}
