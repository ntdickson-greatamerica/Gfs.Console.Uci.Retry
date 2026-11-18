using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Notifications;

namespace Gfs.Console.Uci.Retry.Tests.Notifications;

public sealed class NotificationMessageBuilderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-notify-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static NotificationOptions Options(Action<NotificationOptions>? configure = null)
    {
        var options = new NotificationOptions
        {
            Enabled = true,
            ManualReviewRecipients = ["manual@example.invalid", " ", "MANUAL@example.invalid"],
            TechnicalRecipients = ["tech@example.invalid"]
        };
        configure?.Invoke(options);
        return options;
    }

    private static ProcessingResult Result(ProcessingOutcome outcome, string? file = null, Func<ProcessingResult, ProcessingResult>? with = null)
    {
        var result = new ProcessingResult { Outcome = outcome, Message = "synthetic reason", OriginalErrorFile = file, ErrorType = UciErrorType.GeoCode };
        return with is null ? result : with(result);
    }

    private string ErrorFile(int size = 10)
    {
        var path = Path.Combine(_dir, "ex10000001.txt");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void AutoRetrySubmitted_HasNoNotification() =>
        Assert.Null(new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.AutoRetrySubmitted)));

    [Theory]
    [InlineData(ProcessingOutcome.ManualReview, NotificationType.ManualReview)]
    [InlineData(ProcessingOutcome.AlreadyRetried, NotificationType.AlreadyRetried)]
    [InlineData(ProcessingOutcome.RemediationFailed, NotificationType.Technical)]
    [InlineData(ProcessingOutcome.InvalidInput, NotificationType.Technical)]
    [InlineData(ProcessingOutcome.ProcessingError, NotificationType.Technical)]
    public void OutcomeSelectsTypeAndRecipients(ProcessingOutcome outcome, NotificationType type)
    {
        var request = new NotificationMessageBuilder(Options()).Build(Result(outcome))!;
        Assert.Equal(type, request.Type);
        Assert.Equal(type == NotificationType.Technical ? ["tech@example.invalid"] : ["manual@example.invalid"], request.Recipients);
    }

    [Fact]
    public void ManualReview_AttachesOriginalErrorFile()
    {
        var path = ErrorFile();
        var request = new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.ManualReview, path))!;

        Assert.Equal("ex10000001.txt", request.Attachment!.FileName);
        Assert.Equal(File.ReadAllBytes(path), request.Attachment.Content);
        Assert.Contains("is attached", request.Body);
    }

    [Fact]
    public void MissingAttachment_StillBuilds_AndSaysSo()
    {
        var request = new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.AlreadyRetried, Path.Combine(_dir, "gone.txt")))!;
        Assert.Null(request.Attachment);
        Assert.Contains("could not be attached", request.Body);
    }

    [Fact]
    public void OversizedAttachment_IsNotAttached()
    {
        var request = new NotificationMessageBuilder(Options(o => o.MaxAttachmentBytes = 5)).Build(Result(ProcessingOutcome.ManualReview, ErrorFile(10)))!;
        Assert.Null(request.Attachment);
        Assert.Contains("exceeds the configured size limit", request.Body);
    }

    [Fact]
    public void TechnicalNotifications_NeverAttach()
    {
        var request = new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.RemediationFailed, ErrorFile()))!;
        Assert.Null(request.Attachment);
    }

    [Theory]
    [InlineData(FailureStage.BeforeClaim, "before the retry marker was claimed", "has not been consumed")]
    [InlineData(FailureStage.AfterClaim, "after the retry marker was claimed", "considered consumed")]
    [InlineData(FailureStage.AfterOutboundMove, "may already have observed", "Nothing was rolled back")]
    [InlineData(FailureStage.ArchiveFailed, "archiving the original error file failed", "may already have observed")]
    public void RemediationFailed_DescribesStage(FailureStage stage, string expected1, string expected2)
    {
        var request = new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.RemediationFailed, with: r => r with { FailureStage = stage, RetryClaimed = stage != FailureStage.BeforeClaim }))!;
        Assert.Contains(expected1, request.Body);
        Assert.Contains(expected2, request.Body);
    }

    [Fact]
    public void UciIdentifier_IsOmittedUnlessApproved()
    {
        var result = Result(ProcessingOutcome.ManualReview, with: r => r with { UciIdentifier = "900-1234567-000" });
        Assert.DoesNotContain("900-1234567-000", new NotificationMessageBuilder(Options()).Build(result)!.Body);
        Assert.Contains("900-1234567-000", new NotificationMessageBuilder(Options(o => o.IncludeUciIdentifier = true)).Build(result)!.Body);
    }

    [Fact]
    public void Subject_UsesPrefix_AndStripsControlCharacters()
    {
        var request = new NotificationMessageBuilder(Options(o => o.SubjectPrefix = "[X]\r\nBcc: a@b.c")).Build(Result(ProcessingOutcome.ManualReview, "C:\\x\\e\r\nx.txt"))!;
        Assert.DoesNotContain('\r', request.Subject);
        Assert.DoesNotContain('\n', request.Subject);
        Assert.StartsWith("[X]", request.Subject);
    }

    [Fact]
    public void Body_ContainsFileNameButNoDirectories()
    {
        var request = new NotificationMessageBuilder(Options()).Build(Result(ProcessingOutcome.ManualReview, ErrorFile()))!;
        Assert.Contains("ex10000001.txt", request.Body);
        Assert.DoesNotContain(_dir, request.Body);
    }
}
