using System.Net;
using Api.Framework.Exceptions;
using Api.Framework.Result;
using Microsoft.AspNetCore.Diagnostics;

namespace NewWords.Api.Exceptions;

/// <summary>
/// App-local exception handler that replaces the framework's GlobalExceptionHandler.
/// Unlike the framework handler, it never serializes raw exception messages or the
/// inner-exception chain for unexpected failures — those are genericized so SQL
/// fragments, connection strings, and provider URLs cannot leak to clients. Full
/// details are always logged. The framework CustomException&lt;T&gt; passthrough and
/// the HTTP-200 / ApiResult contract are preserved.
/// </summary>
public class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Always log the full exception (with inner chain) for Seq/NLog.
        logger.LogError(exception.ToString());

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.OK;

        // 1. Framework CustomException<T> — preserve its CustomData verbatim
        //    (used by SettingsController via CustomExceptionHelper).
        if (IsCustomException(exception))
        {
            var dataProperty = exception.GetType().GetProperty("CustomData");
            if (dataProperty != null)
            {
                var customData = dataProperty.GetValue(exception);
                await context.Response.WriteAsJsonAsync(customData, cancellationToken);
                return true;
            }
            await context.Response.WriteAsJsonAsync(new FailedResult(exception.Message), cancellationToken);
            return true;
        }

        // 2. Expected, user-facing business error — surface its message verbatim.
        if (exception is BusinessException businessException)
        {
            var businessResult = new FailedResult(businessException.ErrorCode, businessException.Message);
            await context.Response.WriteAsJsonAsync(businessResult, cancellationToken);
            return true;
        }

        // 3. Anything else is unexpected — never leak internal detail to the client.
        var genericResult = new FailedResult($"An unexpected error occurred. (ref: {context.TraceIdentifier})");
        await context.Response.WriteAsJsonAsync(genericResult, cancellationToken);
        return true;
    }

    // Mirrors Api.Framework's detection: walk the base-type chain for the open
    // generic CustomException<>.
    private static bool IsCustomException(Exception ex)
    {
        var customExceptionType = typeof(CustomException<>);
        var exType = ex.GetType();
        while (exType != null && exType != typeof(object))
        {
            if (exType.IsGenericType && exType.GetGenericTypeDefinition() == customExceptionType)
            {
                return true;
            }
            exType = exType.BaseType;
        }
        return false;
    }
}
