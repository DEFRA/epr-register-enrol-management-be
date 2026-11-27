using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.Test.WorkItems.Core;

public sealed class WorkItemSubmissionResultTests
{
    [Fact]
    public void A_wrapped_action_failure_keeps_its_code_message_and_error_code()
    {
        var failure = WorkItemActionResult.Failure(
            WorkItemActionFailureCode.InvalidEffectiveDate,
            "No date.",
            "effective-date-required"
        );

        var result = WorkItemSubmissionResult.Failure(failure);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkItemActionFailureCode.InvalidEffectiveDate, result.FailureCode);
        Assert.Equal("No date.", result.Message);
        Assert.Equal("effective-date-required", result.ErrorCode);
    }

    [Fact]
    public void A_wrapped_action_failure_without_an_error_code_has_none()
    {
        var failure = WorkItemActionResult.Failure(WorkItemActionFailureCode.MissingActorIdentity, "Who?");

        Assert.Null(WorkItemSubmissionResult.Failure(failure).ErrorCode);
    }
}
