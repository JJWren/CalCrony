using NodaTime;

namespace CalCrony.Api.Data;

/// <summary>A bot credential; only the SHA-256 hash of the key is stored.</summary>
public class ApiKey
{
    public Guid Id { get; set; }

    public required string Label { get; set; }

    /// <summary>Lowercase hex SHA-256 of the raw key. Raw keys are never stored.</summary>
    public required string KeyHash { get; set; }

    public Instant CreatedAt { get; set; }

    public bool Revoked { get; set; }
}
