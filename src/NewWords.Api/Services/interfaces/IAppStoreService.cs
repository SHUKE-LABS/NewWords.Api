using NewWords.Api.Models.DTOs.Entitlement;

namespace NewWords.Api.Services.interfaces
{
    /// <summary>
    /// Orchestrates Apple App Store purchase verification and the resulting entitlement grant
    /// (issue #38). Verifies the submitted transaction, and on a valid active subscription upserts
    /// the #37 entitlement. Restore reuses the same path.
    /// </summary>
    public interface IAppStoreService
    {
        /// <summary>
        /// Verifies the transaction and, if it is an active subscription, grants/renews premium.
        /// Returns the user's fresh entitlement status. Invalid / expired / tampered / revoked
        /// transactions grant nothing and throw a <c>BusinessException</c> with
        /// <see cref="Constants.EntitlementConstants.AppleVerificationFailedErrorCode"/>.
        /// </summary>
        Task<EntitlementStatusDto> VerifyAndGrantAsync(int userId, string transactionId);
    }
}
