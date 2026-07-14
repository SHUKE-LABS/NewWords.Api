using NewWords.Api.Constants;
using NewWords.Api.Exceptions;
using NewWords.Api.Models.DTOs.Entitlement;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services.AppleAppStore
{
    /// <summary>
    /// Verifies an Apple transaction and grants premium (issue #38). Verification I/O lives behind
    /// <see cref="IAppleTransactionVerifier"/>; this class owns the active-subscription decision
    /// and the entitlement upsert, and is fully unit-tested via the mocked verifier.
    /// </summary>
    public class AppStoreService(
        IAppleTransactionVerifier verifier,
        IEntitlementService entitlementService)
        : IAppStoreService
    {
        public async Task<EntitlementStatusDto> VerifyAndGrantAsync(int userId, string transactionId)
        {
            AppleVerifiedTransaction transaction;
            try
            {
                transaction = await verifier.VerifyAsync(transactionId);
            }
            catch (AppleTransactionNotFoundException)
            {
                // Unknown after the Production→Sandbox fallback: treat as an invalid transaction.
                throw new BusinessException(
                    "Could not verify the Apple transaction.",
                    EntitlementConstants.AppleVerificationFailedErrorCode);
            }
            catch (AppleReceiptVerificationException)
            {
                // Tampered / bundleId mismatch / API error: reject without leaking detail.
                throw new BusinessException(
                    "Could not verify the Apple transaction.",
                    EntitlementConstants.AppleVerificationFailedErrorCode);
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var isActive = transaction.RevocationDateMs == 0 && transaction.ExpiresDateMs > nowMs;
            if (!isActive)
            {
                // Expired or revoked/refunded: grant nothing.
                throw new BusinessException(
                    "The Apple subscription is not active.",
                    EntitlementConstants.AppleVerificationFailedErrorCode);
            }

            // The entitlement stores premium expiry in UNIX seconds; Apple reports milliseconds.
            var premiumExpiresAtSeconds = transaction.ExpiresDateMs / 1000;
            await entitlementService.UpsertAsync(
                userId,
                premiumExpiresAtSeconds,
                EntitlementConstants.AppleStore,
                transaction.OriginalTransactionId);

            return await entitlementService.GetStatusAsync(userId);
        }
    }
}
