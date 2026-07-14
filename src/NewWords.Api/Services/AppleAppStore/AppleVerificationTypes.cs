namespace NewWords.Api.Services.AppleAppStore
{
    /// <summary>Which App Store Server API environment to query.</summary>
    public enum AppleEnv
    {
        Production,
        Sandbox
    }

    /// <summary>
    /// The subset of a verified Apple transaction the entitlement grant needs. Produced only after
    /// the App Store Server API returned the transaction and its JWS signature was validated
    /// against Apple's certificate chain. All timestamps are Apple's UNIX time in milliseconds.
    /// </summary>
    /// <param name="OriginalTransactionId">Correlates renewals/refunds; stored on the entitlement.</param>
    /// <param name="ProductId">The purchased subscription product id.</param>
    /// <param name="ExpiresDateMs">When the subscription expires or renews (UNIX ms).</param>
    /// <param name="RevocationDateMs">
    /// When Apple refunded/revoked the transaction (UNIX ms), or <c>0</c> when not revoked. Mimo's
    /// <c>JwsTransactionDecodedPayload.RevocationDate</c> is a non-nullable <c>long</c> that
    /// defaults to 0 when the field is absent, so "not revoked" is 0, not null.
    /// </param>
    public record AppleVerifiedTransaction(
        string OriginalTransactionId,
        string ProductId,
        long ExpiresDateMs,
        long RevocationDateMs);

    /// <summary>
    /// The transaction id was not found in the queried environment. Drives the Production→Sandbox
    /// fallback (Apple returns TransactionIdNotFound / error code 4040010 when a sandbox
    /// transaction is looked up in Production).
    /// </summary>
    public class AppleTransactionNotFoundException : Exception
    {
        public AppleTransactionNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// The transaction could not be verified: JWS signature/cert-chain validation failed
    /// (tampered), a bundleId mismatch, or an unexpected App Store Server API error. Treated as a
    /// rejection — nothing is granted.
    /// </summary>
    public class AppleReceiptVerificationException : Exception
    {
        public AppleReceiptVerificationException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }
}
