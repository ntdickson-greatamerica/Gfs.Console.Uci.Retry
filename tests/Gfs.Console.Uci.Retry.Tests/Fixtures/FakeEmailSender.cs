using Gfs.Console.Uci.Retry.Notifications;

namespace Gfs.Console.Uci.Retry.Tests.Fixtures;

/// <summary>In-memory email sender: records messages, can fail a set number of attempts or selected messages. Never touches the network.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private readonly int _failAttempts;
    private readonly Func<EmailMessage, bool>? _failWhen;

    /// <param name="failAttempts">How many initial attempts throw; use int.MaxValue to always fail.</param>
    public FakeEmailSender(int failAttempts = 0) => _failAttempts = failAttempts;

    /// <summary>Fails every attempt for which <paramref name="failWhen"/> is true.</summary>
    public FakeEmailSender(Func<EmailMessage, bool> failWhen) => _failWhen = failWhen;

    public List<EmailMessage> Sent { get; } = [];
    public List<EmailMessage> Attempted { get; } = [];
    public int Attempts { get; private set; }

    public static bool IsBusiness(EmailMessage m) => m.To.Contains("manual@example.invalid");
    public static bool IsTechnical(EmailMessage m) => m.To.Contains("tech@example.invalid");

    public int BusinessAttempts => Attempted.Count(IsBusiness);
    public int TechnicalAttempts => Attempted.Count(IsTechnical);
    public EmailMessage? TechnicalSent => Sent.SingleOrDefault(IsTechnical);

    public void Send(EmailMessage message)
    {
        Attempts++;
        Attempted.Add(message);
        if (Attempts <= _failAttempts || _failWhen?.Invoke(message) == true)
        {
            throw new IOException("simulated delivery failure");
        }
        Sent.Add(message);
    }
}