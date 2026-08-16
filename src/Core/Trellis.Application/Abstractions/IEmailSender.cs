namespace Trellis.Application.Abstractions;

/// <summary>Outbound email port; Infrastructure provides the adapter (a structured-logging stub in development).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}
