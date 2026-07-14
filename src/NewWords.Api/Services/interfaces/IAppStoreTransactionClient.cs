using NewWords.Api.Services.AppleAppStore;

namespace NewWords.Api.Services.interfaces
{
    /// <summary>
    /// Thin boundary over the App Store Server API + JWS verification for a single environment.
    /// Kept behind an interface so the Production→Sandbox fallback in
    /// <see cref="IAppleTransactionVerifier"/> is unit-testable; the concrete implementation
    /// (network + Apple crypto) is covered by a manual sandbox run, not CI.
    /// </summary>
    public interface IAppStoreTransactionClient
    {
        /// <summary>
        /// Fetches the transaction from the given environment's App Store Server API and returns it
        /// only after its JWS signature validates against Apple's certificate chain and the
        /// bundleId matches.
        /// </summary>
        /// <exception cref="AppleTransactionNotFoundException">Not found in this environment (drives fallback).</exception>
        /// <exception cref="AppleReceiptVerificationException">Signature/bundleId/other verification failure.</exception>
        Task<AppleVerifiedTransaction> GetVerifiedTransactionAsync(AppleEnv env, string transactionId);
    }
}
