namespace ReelGuo.Data.Entities;

public enum UserRole
{
    User,
    Creator,
    Admin,
}

public enum UserStatus
{
    Active,
    Disabled,
}

public sealed class User
{
    public long Id { get; set; }
    public required string Username { get; set; }
    /// <summary>Lower-cased username; the unique index is on this column so 'Abc' and 'abc' cannot both register.</summary>
    public required string NormalizedUsername { get; set; }
    public required string PasswordHash { get; set; }
    public required string Nickname { get; set; }
    public string? AvatarUrl { get; set; }
    public string AvatarColor { get; set; } = "#888888";
    public string? Sign { get; set; }
    public UserRole Role { get; set; }
    public UserStatus Status { get; set; }
    public DateTimeOffset? AgeVerifiedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>When the user confirmed being 18+ and accepted the terms at sign-up.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
