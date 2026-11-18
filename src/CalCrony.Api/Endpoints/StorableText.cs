using System.Text.Json;
using System.Text.Json.Serialization;
using CalCrony.Contracts;

namespace CalCrony.Api.Endpoints;

/// <summary>Request text the database cannot store is refused where it comes in — the path and the
/// query string of an address, and every string a JSON body binds to — with one sentence, so no
/// route checks its own fields for it and a route added later is covered as it is.</summary>
internal static class StorableText
{
    /// <summary>The sentence every such refusal carries.</summary>
    public const string Refusal = "The text contains a character that can't be saved.";

    /// <summary>Whether the database can store the text as it is.</summary>
    /// <param name="text">The text to check.</param>
    /// <returns>True when the text can be stored.</returns>
    public static bool Allows(string text) => !text.Contains('\0');

    /// <summary>Checks the path and the query string here, before the route runs, and answers the
    /// refusal <see cref="StorableTextConverter"/> raises while a route reads its JSON body. Both get
    /// a 400 with <see cref="Refusal"/>. Placed after authorization, so a request without
    /// credentials is answered 401 as before.</summary>
    /// <param name="app">The application's pipeline.</param>
    /// <returns>The same pipeline.</returns>
    public static IApplicationBuilder UseStorableText(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!Allows(context.Request.Path.Value ?? string.Empty) || !AllowsQuery(context.Request.Query))
            {
                await RefuseAsync(context);
                return;
            }

            try
            {
                await next(context);
            }
            catch (UnstorableTextException) when (!context.Response.HasStarted)
            {
                await RefuseAsync(context);
            }
        });

    /// <summary>Whether every name and value in a query string can be stored.</summary>
    /// <param name="query">The parsed query string.</param>
    /// <returns>True when nothing in it needs refusing.</returns>
    private static bool AllowsQuery(IQueryCollection query)
    {
        foreach (var (name, values) in query)
        {
            if (!Allows(name))
            {
                return false;
            }

            foreach (var value in values)
            {
                if (value is not null && !Allows(value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Task RefuseAsync(HttpContext context) =>
        Results.BadRequest(new ErrorResponse(Refusal)).ExecuteAsync(context);
}

/// <summary>Reads JSON text as the serializer does by default, refusing text
/// <see cref="StorableText.Allows"/> does not; writes it exactly as the serializer does by default.
/// The refusal is a type of its own, not a <see cref="JsonException"/>, so it reaches
/// <see cref="StorableText.UseStorableText"/> instead of being answered as unreadable JSON.</summary>
internal sealed class StorableTextConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is not null && !StorableText.Allows(text))
        {
            throw new UnstorableTextException();
        }

        return text;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Raised while a JSON body is read when it carries text that cannot be stored; answered
/// with a 400 by <see cref="StorableText.UseStorableText"/>.</summary>
internal sealed class UnstorableTextException() : Exception(StorableText.Refusal);
