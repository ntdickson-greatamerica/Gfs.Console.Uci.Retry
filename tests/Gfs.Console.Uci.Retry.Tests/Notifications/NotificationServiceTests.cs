using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Notifications;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Notifications;

public sealed class NotificationServiceTests
{
    private static NotificationOptions Options(Action<NotificationOptions>? configure = null)
    {
        var options = new NotificationOptions
        {
            Enabled = true, SmtpHost = "smtp.invalid", SmtpPort = 25, FromAddress = "from@example.invalid",
            ManualReviewRecipients = ["manual@example.invalid"], TechnicalRecipients = ["tech@example.invalid"]
        };
        configure?.Invoke(options);
        return options;
    }

    private static NotificationService Service(NotificationOptions options, FakeEmailSender sender) =>
        new(options, sender, NullLogger.Instance) { Delay = _ => { } };

    private static ProcessingResult Result(ProcessingOutcome outcome, bool claimed = false) =>
        new() { Outcome = outcome, Message = "synthetic reason", RetryClaimed = claimed, FailureStage = claimed ? FailureStage.AfterClaim : FailureStage.None };

    [Fact]
    public void AutoRetrySubmitted_SendsNothing_AndIsUnchanged()
    {
        var sender = new FakeEmailSender();
        var result = Result(ProcessingOutcome.AutoRetrySubmitted);
        Assert.Same(result, Service(Options(), sender).Notify(result));
        Assert.Equal(0, sender.Attempts);
    }

    [Theory]
    [InlineData(ProcessingOutcome.ManualReview)]
    [InlineData(ProcessingOutcome.AlreadyRetried)]
    [InlineData(ProcessingOutcome.RemediationFailed)]
    [InlineData(ProcessingOutcome.InvalidInput)]
    [InlineData(ProcessingOutcome.ProcessingError)]
    public void Delivered_SendsExactlyOne_AndKeepsResult(ProcessingOutcome outcome)
    {
        var sender = new FakeEmailSender();
        var result = Result(outcome);
        Assert.Same(result, Service(Options(), sender).Notify(result));
        Assert.Single(sender.Sent);
        Assert.Equal(1, sender.Attempts);
    }

    [Theory]
    [InlineData(ProcessingOutcome.ManualReview)]
    [InlineData(ProcessingOutcome.AlreadyRetried)]
    public void Disabled_BusinessNotification_FailsSafeToProcessingError_WithoutDelivery(ProcessingOutcome outcome)
    {
        var sender = new FakeEmailSender();
        var result = Service(Options(o => o.Enabled = false), sender).Notify(Result(outcome));

        Assert.Equal(ProcessingOutcome.ProcessingError, result.Outcome);
        Assert.Equal(50, result.ExitCode);
        Assert.Contains("requires manual handling", result.Message);
        Assert.Equal(0, sender.Attempts);
    }

    [Theory]
    [InlineData(ProcessingOutcome.InvalidInput)]
    [InlineData(ProcessingOutcome.ProcessingError)]
    public void Disabled_TechnicalNotification_KeepsOriginalResult(ProcessingOutcome outcome)
    {
        var sender = new FakeEmailSender();
        var result = Result(outcome);
        Assert.Same(result, Service(Options(o => o.Enabled = false), sender).Notify(result));
        Assert.Equal(0, sender.Attempts);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("from")]
    [InlineData("recipients")]
    public void IncompleteConfiguration_IsNotDelivered(string missing)
    {
        var sender = new FakeEmailSender();
        var options = Options(o =>
        {
            switch (missing)
            {
                case "host": o.SmtpHost = ""; break;
                case "port": o.SmtpPort = 0; break;
                case "from": o.FromAddress = " "; break;
                default: o.ManualReviewRecipients = []; break;
            }
        });

        var result = Service(options, sender).Notify(Result(ProcessingOutcome.ManualReview));
        Assert.Equal(ProcessingOutcome.ProcessingError, result.Outcome);
        Assert.Equal(0, sender.Attempts);
    }

    [Theory]
    [InlineData(ProcessingOutcome.ManualReview)]
    [InlineData(ProcessingOutcome.AlreadyRetried)]
    public void DeliveryFailure_BusinessNotification_BecomesProcessingError_AfterBoundedAttempts(ProcessingOutcome outcome)
    {
        var sender = new FakeEmailSender(int.MaxValue);
        var result = Service(Options(), sender).Notify(Result(outcome));

        Assert.Equal(ProcessingOutcome.ProcessingError, result.Outcome);
        Assert.Equal(NotificationService.MaxAttempts, sender.Attempts);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void DeliveryFailure_AlreadyRetried_MentionsMarkerStillBlocksRetry()
    {
        var result = Service(Options(), new FakeEmailSender(int.MaxValue)).Notify(Result(ProcessingOutcome.AlreadyRetried, claimed: true));
        Assert.Contains("no further automatic retry", result.Message);
    }

    [Fact]
    public void DeliveryFailure_AfterClaim_KeepsRemediationFailed_AndStatesRetryConsumed()
    {
        var sender = new FakeEmailSender(int.MaxValue);
        var result = Service(Options(), sender).Notify(Result(ProcessingOutcome.RemediationFailed, claimed: true));

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Contains("after the retry was claimed", result.Message);
        Assert.Contains("remains consumed", result.Message);
        Assert.Equal(NotificationService.MaxAttempts, sender.Attempts);
    }

    [Theory]
    [InlineData(ProcessingOutcome.InvalidInput)]
    [InlineData(ProcessingOutcome.ProcessingError)]
    public void DeliveryFailure_Technical_ReturnsOriginalResult_WithoutRecursion(ProcessingOutcome outcome)
    {
        var sender = new FakeEmailSender(int.MaxValue);
        var result = Result(outcome);
        Assert.Same(result, Service(Options(), sender).Notify(result));
        Assert.Equal(NotificationService.MaxAttempts, sender.Attempts);
    }

    [Fact]
    public void TransientFailure_ThenSuccess_SendsOneMessage()
    {
        var sender = new FakeEmailSender(failAttempts: 1);
        var result = Result(ProcessingOutcome.ManualReview);
        Assert.Same(result, Service(Options(), sender).Notify(result));
        Assert.Equal(2, sender.Attempts);
        Assert.Single(sender.Sent);
    }
}
