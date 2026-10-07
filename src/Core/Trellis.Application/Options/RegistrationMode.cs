namespace Trellis.Application.Options;

/// <summary>Self-registration policy. <see cref="Closed"/> is zero, so an unset value is the safe one.</summary>
public enum RegistrationMode
{
    /// <summary><c>POST /auth/register</c> answers 403 <c>Auth.RegistrationClosed</c>; accounts come from an administrator or the seeder.</summary>
    Closed = 0,

    /// <summary>Anyone can create a Member account.</summary>
    Open = 1,
}
