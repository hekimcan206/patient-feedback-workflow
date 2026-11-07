namespace PatientFeedback.Web.Infrastructure;

internal static class SecurityHeaders
{
    /// <summary>
    /// Strict defaults for every response. Two matter specifically here:
    /// <c>Referrer-Policy: no-referrer</c> keeps the survey token (in the URL path) out of Referer headers, and the CSP
    /// allows scripts only from this origin (all JavaScript is in wwwroot; no inline script).
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next();
        });
}
