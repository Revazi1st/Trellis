using System.ComponentModel.DataAnnotations;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// Which adapter delivers outbound e-mail, bound from the "Email" section. Checked when the sender is
/// first built and, in the host that sends (the API), at boot (<c>AddOutboundIntegrations</c>).
/// <para>
/// The template ships no real provider. <see cref="EmailProvider.Logging"/> writes a masked line instead
/// of sending, which is right in Development and a silent loss anywhere else — so the API logs a
/// Warning at boot when it is active outside Development. <see cref="EmailProvider.None"/> says "discard"
/// on purpose.
/// </para>
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    [EnumDataType(typeof(EmailProvider))]
    public EmailProvider Provider { get; init; } = EmailProvider.Logging;
}
