using Maki.Api.Localization;
using Microsoft.AspNetCore.Identity;

namespace Maki.Api.Auth;

/// <summary>
/// Words ASP.NET Identity's errors in the request's language. Identity only ever hands controllers a
/// rendered <see cref="IdentityError.Description"/>, so this is the one place they can be translated.
/// Role errors and the password composition rules are left to the base class: Maki has no roles and
/// switches those rules off.
/// </summary>
public sealed class LocalizedIdentityErrorDescriber(ILocalizer localizer) : IdentityErrorDescriber
{
    private IdentityError Error(string code, string key, object? args = null) =>
        new() { Code = code, Description = localizer.Get(key, args) };

    public override IdentityError DefaultError() =>
        Error(nameof(DefaultError), "error.identity.defaultError");

    public override IdentityError ConcurrencyFailure() =>
        Error(nameof(ConcurrencyFailure), "error.identity.concurrencyFailure");

    public override IdentityError PasswordMismatch() =>
        Error(nameof(PasswordMismatch), "error.identity.passwordMismatch");

    public override IdentityError InvalidToken() =>
        Error(nameof(InvalidToken), "error.identity.invalidToken");

    public override IdentityError RecoveryCodeRedemptionFailed() =>
        Error(nameof(RecoveryCodeRedemptionFailed), "error.identity.recoveryCodeRedemptionFailed");

    public override IdentityError LoginAlreadyAssociated() =>
        Error(nameof(LoginAlreadyAssociated), "error.identity.loginAlreadyAssociated");

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), "error.identity.invalidUserName", new { userName = userName ?? "" });

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), "error.identity.invalidEmail", new { email = email ?? "" });

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), "error.identity.duplicateUserName", new { userName });

    public override IdentityError DuplicateEmail(string email) =>
        Error(nameof(DuplicateEmail), "error.identity.duplicateEmail", new { email });

    public override IdentityError UserAlreadyHasPassword() =>
        Error(nameof(UserAlreadyHasPassword), "error.identity.userAlreadyHasPassword");

    public override IdentityError UserLockoutNotEnabled() =>
        Error(nameof(UserLockoutNotEnabled), "error.identity.userLockoutNotEnabled");

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), "error.identity.passwordTooShort", new { length });
}
