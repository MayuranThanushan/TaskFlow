using TaskFlow.Application.Features.Activity;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using System.Diagnostics;

namespace TaskFlow.Tests;

public sealed class AuditMetadataTests
{
    [Fact]
    public async Task RequestCorrelationFlowsAcrossAsyncScopesWhenTracingIsDisabled()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            using var request = AuditCorrelation.Begin(id);
            await Task.Yield();
            var record = await Task.Run(() => SecurityRecords.Create(SecurityAction.Login, SecurityOutcome.Denied, null));
            Assert.Equal(id, record.CorrelationId);
        }
        finally { Activity.Current = previous; }
    }
    [Fact]
    public void UnknownSchemaDoesNotInterpretOrExposeAnUnreviewedPayload()
    {
        var record = new ActivityLog { SchemaVersion = 99, ChangesJson = "UNREVIEWED_PRIVATE_PAYLOAD", SubjectId = "UNREVIEWED_SUBJECT", Outcome = "Succeeded" };
        var metadata = AuditMetadata.Map(record);
        Assert.Equal("UnsupportedVersion", metadata.Outcome); Assert.Empty(metadata.Changes); Assert.Null(metadata.SubjectId); Assert.False(metadata.IsLegacy);
    }
}
