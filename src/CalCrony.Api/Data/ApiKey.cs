using NodaTime;

namespace CalCrony.Api.Data;

public class ApiKey
{
    public Guid Id { get; set; }

    public required string Label { get; set; }

    /// <summary>Lowercase hex SHA-256 of the raw key. Raw keys are never stored.</summary>
    public required string KeyHash { get; set; }

    public Instant CreatedAt { get; set; }

    public bool Revoked { get; set; }
}
