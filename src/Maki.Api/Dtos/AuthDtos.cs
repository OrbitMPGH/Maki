using Maki.Core.Security;
using Maki.Data.Identity;

namespace Maki.Api.Dtos;

public record LoginRequest(string? Username, string? Password);

public record TwoFactorRequest(string? Code, bool RememberMachine);

public record SetupRequest(string? Username, string? Password, string? DisplayName);

public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record EnableTwoFactorRequest(string? Code);

public record DisableTwoFactorRequest(string? Password);

public record CreateApiKeyRequest(string? Name, UserApiKeyScope Scope);

/// <summary>
/// Who the caller is and what they may do. The SPA drives every permission-dependent control off
/// this, so it is fetched once on load and refetched after anything that could change it.
/// <para>
/// <c>PermissionNames</c> is the flag set flattened to strings so the client tests membership
/// without reimplementing bit arithmetic (and without having to know that Admin implies the rest).
/// </para>
/// </summary>
public record MeDto(
    int Id,
    string UserName,
    string? DisplayName,
    MakiPermission Permissions,
    IReadOnlyList<string> PermissionNames,
    bool IsAdmin,
    string MaxContentRating,
    bool AllRootFolders,
    IReadOnlyList<int> RootFolderIds,
    bool TwoFactorEnabled);

public record UserSummaryDto(
    int Id,
    string UserName,
    string? DisplayName,
    MakiPermission Permissions,
    IReadOnlyList<string> PermissionNames,
    bool IsAdmin,
    string MaxContentRating,
    bool AllRootFolders,
    IReadOnlyList<int> RootFolderIds,
    bool Disabled,
    bool PendingSetup,
    bool TwoFactorEnabled,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

/// <summary>
/// Admin-side create/update. Every field is optional on update so a partial edit does not have to
/// round-trip values it is not changing; <c>Password</c> null on update means "leave it alone".
/// </summary>
public record SaveUserRequest(
    string? Username,
    string? Password,
    string? DisplayName,
    MakiPermission? Permissions,
    string? MaxContentRating,
    bool? AllRootFolders,
    IReadOnlyList<int>? RootFolderIds,
    bool? Disabled);

public record ApiKeyDto(
    int Id,
    string Name,
    string Prefix,
    UserApiKeyScope Scope,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt);

/// <summary>
/// The one and only time the plaintext key is returned. Nothing stores it — only its SHA-256 digest
/// is persisted — so a client that loses this response cannot recover the key and must create another.
/// </summary>
public record CreatedApiKeyDto(ApiKeyDto Key, string Secret);

public record TwoFactorSetupDto(string SharedKey, string AuthenticatorUri);

public record AuthEventDto(
    DateTime Timestamp,
    AuthEventType Type,
    int? UserId,
    string UserName,
    string? ClientIp,
    string? UserAgent,
    string? Detail);
