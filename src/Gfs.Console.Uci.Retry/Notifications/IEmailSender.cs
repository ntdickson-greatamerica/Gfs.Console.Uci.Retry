using System.Net;
using System.Net.Mail;

namespace Gfs.Console.Uci.Retry.Notifications;

/// <summary>Email delivery boundary; exists so tests never contact a mail server.</summary>
public interface IEmailSender
{
    /// <summary>Delivers the message or throws.</summary>
    void Send(EmailMessage message);
}

/// <summary>SMTP delivery using the built-in client. Writes nothing to stdout.</summary>
public sealed class SmtpEmailSender(NotificationOptions options) : IEmailSender
{
    private const int TimeoutMilliseconds = 30_000;

    public void Send(EmailMessage message)
    {
        using var mail = new MailMessage { From = new MailAddress(message.From), Subject = message.Subject, Body = message.Body };
        foreach (var recipient in message.To)
        {
            mail.To.Add(recipient);
        }

        using var stream = message.Attachment is null ? null : new MemoryStream(message.Attachment.Content, writable: false);
        if (message.Attachment is not null)
        {
            mail.Attachments.Add(new Attachment(stream!, message.Attachment.FileName, "text/plain"));
        }

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort) { EnableSsl = options.UseSsl, Timeout = TimeoutMilliseconds };
        if (!string.IsNullOrEmpty(options.SmtpUserName))
        {
            client.Credentials = new NetworkCredential(options.SmtpUserName, options.SmtpPassword);
        }
        client.Send(mail);
    }
}
