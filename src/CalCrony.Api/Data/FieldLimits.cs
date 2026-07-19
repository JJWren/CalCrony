namespace CalCrony.Api.Data;

/// <summary>Single source for user-writable field caps: the DbContext column configuration and
/// the endpoint validation both reference these, so a request that passes validation can never
/// hit a Postgres truncation error (SQLSTATE 22001 → opaque 500) — bad input gets a friendly
/// 400 instead.</summary>
public static class FieldLimits
{
    public const int EventTitle = 128;
    public const int EventDescription = 4096;
    public const int EventLocation = 256;
    public const int EventImageUrl = 512;
    public const int NotificationMessage = 1024;
    public const int NotificationMentions = 256;
    public const int ReminderText = 1024;

    /// <summary>Ceiling for durations and notification lead times: 4 weeks in minutes. A larger
    /// lead time would just fire immediately today, so nonsense becomes a friendly 400 instead.</summary>
    public const int MaxMinutes = 40320;
}
