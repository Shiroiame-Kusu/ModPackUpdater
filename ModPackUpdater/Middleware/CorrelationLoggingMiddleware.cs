using System.Diagnostics;
using Serilog.Context;

namespace ModPackUpdater.Middleware;

public class CorrelationLoggingMiddleware
{
    private const string HeaderName = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        var corr = context.Request.Headers.TryGetValue(HeaderName, out var requested) && !string.IsNullOrWhiteSpace(requested)
            ? requested.ToString()
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("n");

        context.Items["CorrelationId"] = corr;
        context.Response.Headers[HeaderName] = corr;

        using (LogContext.PushProperty("CorrelationId", corr))
        using (LogContext.PushProperty("Component", "HTTP"))
        {
            await _next(context);
        }
    }
}
