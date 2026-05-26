using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace MultiRepoMcp.Logging;

/// <summary>
/// Reads or generates an <c>X-Correlation-Id</c> header value, makes it
/// available on the response and as a Serilog scope property for every log
/// entry produced while handling the request.
/// </summary>
internal sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxIncomingLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string correlationId;
        if (context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && incoming.Count > 0
            && IsValidIncomingValue(incoming[0]))
        {
            correlationId = incoming[0]!;
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        context.Items[HeaderName] = correlationId;
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    private static bool IsValidIncomingValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIncomingLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            // Sanitised character class: [A-Za-z0-9._-]
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
