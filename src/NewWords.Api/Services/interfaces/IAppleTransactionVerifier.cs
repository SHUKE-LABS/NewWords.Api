using NewWords.Api.Services.AppleAppStore;

namespace NewWords.Api.Services.interfaces
{
    /// <summary>
    /// Verifies an Apple transaction id via the App Store Server API, resolving the correct
    /// environment (Production/Sandbox). The mockable seam <see cref="AppStoreService"/> depends
    /// on, so grant/reject logic is unit-testable without Apple credentials or network.
    /// </summary>
    public interface IAppleTransactionVerifier
    {
        /// <summary>
        /// Resolves and verifies the transaction. When <c>AppStore:Environment</c> is unset, tries
        /// Production then falls back to Sandbox on <see cref="AppleTransactionNotFoundException"/>.
        /// </summary>
        /// <exception cref="AppleTransactionNotFoundException">Not found in any tried environment.</exception>
        /// <exception cref="AppleReceiptVerificationException">Verification failed (tampered/mismatch/API error).</exception>
        Task<AppleVerifiedTransaction> VerifyAsync(string transactionId);
    }
}
