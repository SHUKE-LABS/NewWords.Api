using Api.Framework;

namespace NewWords.Api.Exceptions;

/// <summary>
/// Thrown for expected, user-facing business/validation errors whose message is
/// intended to reach the client verbatim (e.g. "wrong password"). Unexpected
/// exceptions must NOT be this type — they are genericized by
/// <see cref="AppExceptionHandler"/> to avoid leaking internal detail.
/// </summary>
public class BusinessException : Exception
{
    /// <summary>Error code surfaced to the client in the FailedResult.</summary>
    public int ErrorCode { get; }

    public BusinessException(string message, int errorCode = FrameworkConstants.DefaultErrorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
