using System.Text;
using CalCrony.Api.Auth;
using CalCrony.Contracts;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace CalCrony.Api.Endpoints;

/// <summary>Two checks on a request to a route that reads a JSON body, made before the route runs:
/// a JSON body of more than <see cref="MaxBodyBytes"/> from any caller but the bot is refused with
/// a 413 — with a sentence when the request states its length — and a body in a character set that
/// can't be read with a 415 and a sentence. A route that reads no body, and a body the route would
/// not read as JSON, are answered as they would be without them.</summary>
internal static class RequestBodyChecks
{
    /// <summary>The largest body any caller but the bot may send: 1 MB. The bot keeps the server's
    /// own limit, since its snapshot of a large server's members can be bigger.</summary>
    public const long MaxBodyBytes = 1_048_576;

    /// <summary>The sentence of the refusal of a body that states a length over the limit.</summary>
    public const string TooLarge = "The request body must be at most 1 MB.";

    /// <summary>The sentence of the refusal of a body in a character set that can't be read.</summary>
    public const string UnreadableCharset =
        "The request body is in a character set that can't be read — send it as UTF-8.";

    /// <summary>Adds both checks to the pipeline. Placed after authorization, so a request without
    /// credentials is answered 401, and a web caller of a route only the bot may use 403, as
    /// before.</summary>
    /// <param name="app">The application's pipeline.</param>
    /// <returns>The same pipeline.</returns>
    public static IApplicationBuilder UseRequestBodyChecks(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!ReadsJsonBody(context))
            {
                await next(context);
                return;
            }

            // The size rule is for a JSON body: one that isn't JSON is never read, and the framework
            // answers it 415 whatever its size.
            if (!context.User.IsBot() && context.Request.HasJsonContentType())
            {
                // A body that states a length over the limit is refused before anything of it is read.
                if (context.Request.ContentLength > MaxBodyBytes)
                {
                    await Results.Json(new ErrorResponse(TooLarge), statusCode: StatusCodes.Status413PayloadTooLarge)
                        .ExecuteAsync(context);
                    return;
                }

                // One that does not state its length is read until what has been sent of it, the
                // framing of its chunks included, passes the limit; the server then answers 413
                // itself. The limit is only ever lowered here, never raised.
                if (context.Request.ContentLength is null
                    && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size
                    && (size.MaxRequestBodySize is null || size.MaxRequestBodySize > MaxBodyBytes))
                {
                    size.MaxRequestBodySize = MaxBodyBytes;
                }
            }

            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                && HasUnreadableCharset(context.Request.ContentType))
            {
                await Results.Json(new ErrorResponse(UnreadableCharset), statusCode: StatusCodes.Status415UnsupportedMediaType)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    /// <summary>Whether the route reads its body as JSON: such a route declares that it accepts
    /// <c>application/json</c>.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>True when the matched route reads a JSON body.</returns>
    private static bool ReadsJsonBody(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAcceptsMetadata>()
            .Any(accepts => accepts.ContentTypes.Contains("application/json", StringComparer.OrdinalIgnoreCase)) == true;

    /// <summary>Whether a JSON content type names a character set that can't be read, decided as the
    /// framework decides it when it reads the body: <c>utf-8</c> in any case, or no character set at
    /// all, is read; any other name is read when .NET has an encoding by that name. A content type
    /// that is not JSON is left to the framework, which answers 415 itself.</summary>
    /// <param name="contentType">The request's Content-Type header.</param>
    /// <returns>True when the body would be refused for its character set.</returns>
    private static bool HasUnreadableCharset(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !(mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                 || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var charset = mediaType.Charset;
        if (!charset.HasValue || charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            _ = Encoding.GetEncoding(charset.Value!);
            return false;
        }
        catch (Exception)
        {
            // The framework takes any failure of this lookup to mean a character set it can't read.
            return true;
        }
    }
}
