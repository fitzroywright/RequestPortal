using System;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Reviewers;
using Xunit;

namespace RequestPortal.Domain.Tests;

public sealed class DemoCompletionDomainTests
{
    [Fact]
    public void DeliveryAcknowledgementPreservesImmutableSnapshotAndPartialOutcome()
    {
        const string snapshot = "{\"DocumentReference\":\"DN-100\",\"Lines\":[{\"Description\":\"Rice\",\"Quantity\":10}]}";
        DeliveryRecord delivery = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Delivery Note",
            "DN-100",
            snapshot,
            new string('A', 64),
            "Recipient",
            "Driver",
            DateTimeOffset.UtcNow);

        delivery.Acknowledge(
            DeliveryStatus.PartiallyDelivered,
            "Recipient",
            DateTimeOffset.UtcNow,
            "Recipient",
            null,
            "Two items remain outstanding.");

        Assert.Equal(DeliveryStatus.PartiallyDelivered, delivery.Status);
        Assert.Equal(snapshot, delivery.SnapshotJson);
        Assert.NotNull(delivery.AcknowledgedUtc);
        Assert.Throws<InvalidOperationException>(() =>
            delivery.Acknowledge(
                DeliveryStatus.Delivered,
                "Recipient",
                DateTimeOffset.UtcNow,
                "Recipient",
                null,
                null));
    }

    [Fact]
    public void UnacknowledgedDeliveryCannotBeMarkedSynced()
    {
        DeliveryRecord delivery = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Invoice",
            "INV-1",
            "{}",
            new string('B', 64),
            "Recipient",
            null,
            DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => delivery.MarkSynced(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReviewerMagicLinkCanOnlyBeRedeemedOnce()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExternalReviewerInvitation invitation = new(
            Guid.NewGuid(),
            "reviewer@example.org",
            "Reviewer",
            "ManagementDashboard",
            new string('C', 64),
            Guid.NewGuid(),
            now,
            now.AddHours(24));

        invitation.RecordRedemption(now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            invitation.RecordRedemption(now.AddMinutes(2)));
    }

    [Fact]
    public void ReviewerScopeIsPreservedAndInvalidDateRangeIsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid organizationId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();

        ExternalReviewerInvitation invitation = new(
            Guid.NewGuid(),
            "reviewer@example.org",
            "Reviewer",
            "Request",
            new string('E', 64),
            Guid.NewGuid(),
            now,
            now.AddHours(24),
            organizationId,
            requestId,
            now.AddDays(-7),
            now);

        Assert.Equal(organizationId, invitation.OrganizationId);
        Assert.Equal(requestId, invitation.RequestId);
        Assert.Equal(now.AddDays(-7), invitation.FromUtc);
        Assert.Equal(now, invitation.ToUtc);

        Assert.Throws<ArgumentException>(() => new ExternalReviewerInvitation(
            Guid.NewGuid(),
            "reviewer@example.org",
            "Reviewer",
            "DateRange",
            new string('F', 64),
            Guid.NewGuid(),
            now,
            now.AddHours(24),
            null,
            null,
            now,
            now.AddDays(-1)));
    }

    [Fact]
    public void RevokedReviewerSessionIsInactive()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExternalReviewerInvitation invitation = new(
            Guid.NewGuid(),
            "reviewer@example.org",
            "Reviewer",
            "ManagementDashboard",
            new string('D', 64),
            Guid.NewGuid(),
            now,
            now.AddHours(24));

        invitation.Revoke(now.AddMinutes(1));

        Assert.False(invitation.IsActive(now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => invitation.RecordAccess(now.AddMinutes(2)));
    }
}
