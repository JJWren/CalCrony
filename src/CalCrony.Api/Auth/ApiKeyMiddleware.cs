namespace CalCrony.Api.Auth;

/// <summary>
/// Requires a valid <c>X-Api-Key</c> header on every request except the anonymous prefixes:
/// health checks, token-authenticated ICS feeds, and the browser-facing OAuth redirect routes
/// (which authenticate via single-use CalendarLinkTokens instead).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Api-Key";

    private static readonly PathString[] AnonymousPrefixes = ["/health", "/feeds", "/oauth"];

    public async Task InvokeAsync(HttpContext context, ApiKeyValidator validator)
    {
        if (Array.Exists(AnonymousPrefixes, p => context.Request.Path.StartsWithSegments(p)))
        {
            await next(context);
            return;
        }

        var rawKey = context.Request.Headers[HeaderName].ToString();
        if (!await validator.IsValidAsync(rawKey, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }

        await next(context);
    }
}
