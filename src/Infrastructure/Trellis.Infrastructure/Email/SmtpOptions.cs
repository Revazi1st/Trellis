using System.ComponentModel.DataAnnotations;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// The relay <see cref="SmtpEmailSender"/> submits to, bound from "Email:Smtp". Read and checked only when
/// <c>Email:Provider</c> is <see cref="EmailProvider.Smtp"/>: <see cref="SmtpOptionsValidator"/> applies
/// the annotations below itself, because <c>ValidateDataAnnotations()</c> would apply them whatever the
/// provider, and a deployment that keeps the logging stand-in or discards mail owes this section nothing.
/// <para>
/// What the BCL client speaks, and so what this adapter supports: SMTP submission upgraded with STARTTLS
/// (port 587) and username/password authentication. Not supported: implicit TLS ("SMTPS", port 465 — the
/// client would wait for a greeting the relay never sends in clear, so that port is refused at boot), OAuth2
/// (XOAUTH2, which Microsoft 365 and Gmail now push accounts towards) and HTTP-API providers. Each of those
/// is a new adapter beside this one, not a setting.
/// </para>
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    /// <summary>Implicit TLS, which <see cref="System.Net.Mail.SmtpClient"/> never speaks.</summary>
    public const int ImplicitTlsPort = 465;

    /// <summary>The relay's host name, e.g. <c>smtp.example.com</c>.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Email:Smtp:Host is required when Email:Provider is Smtp.")]
    public string Host { get; init; } = string.Empty;

    /// <summary>The submission port: 587 with STARTTLS is the standard; 25 is for relay-to-relay traffic and often blocked.</summary>
    [Range(1, 65535, ErrorMessage = "Email:Smtp:Port must be between {1} and {2}.")]
    public int Port { get; init; } = 587;

    /// <summary>
    /// <see langword="true"/> (the default): upgrade the connection with STARTTLS before anything else is
    /// sent, and fail rather than fall back to plaintext when the relay does not offer it.
    /// <see langword="false"/> sends in clear, which is for a local mail catcher (Mailpit, smtp4dev) only:
    /// it is refused together with credentials, and the API logs a Warning at boot when it is set outside
    /// Development.
    /// </summary>
    public bool UseStartTls { get; init; } = true;

    /// <summary>
    /// The sender every message carries: <c>billing@example.com</c>, or <c>Acme Billing &lt;billing@example.com&gt;</c>
    /// for a display name. Most relays reject a sender the account may not send as.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Email:Smtp:FromAddress is required when Email:Provider is Smtp.")]
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>The relay account. Leave it and <see cref="Password"/> unset for a relay that accepts unauthenticated submission.</summary>
    public string? UserName { get; init; }

    /// <summary>The relay account's password: user secrets in development, the secret store in production, never appsettings.json.</summary>
    public string? Password { get; init; }
}
