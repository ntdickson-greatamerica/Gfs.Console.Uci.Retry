namespace Gfs.Console.Uci.Retry.Models;

/// <summary>How far the lifecycle got when a RemediationFailed result was produced. Used only to choose notification wording.</summary>
public enum FailureStage
{
    None,
    BeforeClaim,
    AfterClaim,
    AfterOutboundMove,
    ArchiveFailed
}
