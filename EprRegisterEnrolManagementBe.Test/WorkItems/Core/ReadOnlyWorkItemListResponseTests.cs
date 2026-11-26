using EprRegisterEnrolManagementBe.Utils.Logging;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

/// <summary>
/// RA-751 / ADR-0010: the projection a read-only caller (the Admin UI) gets
/// from <c>GET /work-items</c>. A stored payload value that is not a string
/// is corrupt data: that one field comes back as null with an Error log, and
/// the rest of the page still returns.
/// </summary>
public class ReadOnlyWorkItemListResponseTests
{
    private static readonly Guid s_workItemId = Guid.Parse("00000751-0000-4000-8000-0000000000aa");

    private readonly IStructuredLogger<WorkItemEndpointsLogger> _log =
        Substitute.For<IStructuredLogger<WorkItemEndpointsLogger>>();

    private static WorkItem ItemWith(BsonDocument payload) =>
        new()
        {
            Id = s_workItemId,
            TypeId = "re-accreditation",
            StateId = "submitted",
            LastModifiedAt = new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
            Payload = payload,
        };

    [Fact]
    public void String_fields_are_projected_without_logging()
    {
        var item = ItemWith(
            new BsonDocument
            {
                ["applicationReference"] = "APEPL5005001",
                ["operatorOrganisationId"] = "org-1",
                ["organisationName"] = "Acme Plastics Ltd",
            }
        );

        var projected = ReadOnlyWorkItemListItem.From(item, _log);

        Assert.Equal(
            new ReadOnlyWorkItemPayload("APEPL5005001", "org-1", "Acme Plastics Ltd"),
            projected.Payload
        );
        _log.DidNotReceiveWithAnyArgs().Log(default, default!, default!);
    }

    [Fact]
    public void Absent_and_bson_null_fields_are_null_without_logging()
    {
        // Optional on stored items: applicationReference predates RA-219.
        var item = ItemWith(new BsonDocument { ["organisationName"] = BsonNull.Value });

        var projected = ReadOnlyWorkItemListItem.From(item, _log);

        Assert.Equal(new ReadOnlyWorkItemPayload(null, null, null), projected.Payload);
        _log.DidNotReceiveWithAnyArgs().Log(default, default!, default!);
    }

    public static TheoryData<BsonValue> NonStringValues() =>
        new()
        {
            new BsonInt32(42),
            BsonBoolean.True,
            new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new BsonDocument { ["name"] = "Acme Plastics Ltd" },
            new BsonArray { "Acme Plastics Ltd" },
        };

    [Theory]
    [MemberData(nameof(NonStringValues))]
    public void A_non_string_field_is_null_with_an_error_log_and_the_other_fields_still_project(
        BsonValue corrupt
    )
    {
        var item = ItemWith(
            new BsonDocument
            {
                ["applicationReference"] = "APEPL5005001",
                ["operatorOrganisationId"] = "org-1",
                ["organisationName"] = corrupt,
            }
        );

        var projected = ReadOnlyWorkItemListItem.From(item, _log);

        Assert.Equal(new ReadOnlyWorkItemPayload("APEPL5005001", "org-1", null), projected.Payload);
        _log.Received(1)
            .Log(
                LogLevel.Error,
                Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, object?>>(p =>
                    (string?)p["work_item.id"] == s_workItemId.ToString()
                    && (string?)p["work_item.payload_field"] == "organisationName"
                    && (string?)p["work_item.payload_field_bson_type"] == corrupt.BsonType.ToString()
                    && p.Count == 3
                )
            );
    }

    [Fact]
    public void The_error_log_never_carries_the_stored_value()
    {
        // The value may be personal data (an organisation or person's name).
        var item = ItemWith(new BsonDocument { ["organisationName"] = new BsonArray { "Siân O'Brien" } });
        IReadOnlyDictionary<string, object?>? logged = null;
        string? message = null;
        _log.Log(
            LogLevel.Error,
            Arg.Do<string>(m => message = m),
            Arg.Do<IReadOnlyDictionary<string, object?>>(p => logged = p)
        );

        ReadOnlyWorkItemListItem.From(item, _log);

        Assert.NotNull(logged);
        Assert.DoesNotContain("Siân", message, StringComparison.Ordinal);
        Assert.All(
            logged!.Values,
            v => Assert.DoesNotContain("Siân", v?.ToString() ?? "", StringComparison.Ordinal)
        );
    }
}
