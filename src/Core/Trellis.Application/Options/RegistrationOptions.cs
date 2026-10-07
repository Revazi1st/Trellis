using System.ComponentModel.DataAnnotations;

namespace Trellis.Application.Options;

/// <summary>
/// Who may create an account through <c>POST /auth/register</c>, bound from the "Registration"
/// configuration section and validated at API startup (<c>RequireValidTokenOptions</c>).
/// <para>
/// Closed is the default because a self-registered account is a Member, and a Member can read
/// business data: an open sign-up endpoint is an open read endpoint. Development turns it on in
/// <c>appsettings.Development.json</c>; any other environment opts in explicitly with
/// <c>Registration__Mode=Open</c>.
/// </para>
/// </summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    [EnumDataType(typeof(RegistrationMode))]
    public RegistrationMode Mode { get; init; } = RegistrationMode.Closed;
}
