using NewWords.Api.Models.DTOs.Entitlement;

namespace NewWords.Api.Services.interfaces
{
    /// <summary>
    /// Server-side subscription entitlement (issue #37). Authoritative premium check, the
    /// configurable free-word cap, the status read path, and an upsert seam the store-receipt
    /// verification ticket will call.
    /// </summary>
    public interface IEntitlementService
    {
        /// <summary>
        /// True iff the user has a non-null, unexpired <c>PremiumExpiresAt</c>.
        /// </summary>
        Task<bool> IsPremiumAsync(int userId);

        /// <summary>
        /// The free (non-premium) saved-word cap, re-read from config on every access so a
        /// Redis/ConfigManager change takes effect without a redeploy. Falls back to
        /// <see cref="Constants.EntitlementConstants.DefaultFreeWordCap"/> when unset or invalid.
        /// </summary>
        int FreeWordCap { get; }

        /// <summary>
        /// Plan, premium expiry, current saved-word count, and the configured cap.
        /// </summary>
        Task<EntitlementStatusDto> GetStatusAsync(int userId);

        /// <summary>
        /// Insert or update the user's entitlement row. Called by the store-receipt verification
        /// ticket once a purchase is validated; verification itself is out of scope here.
        /// </summary>
        Task UpsertAsync(int userId, long? premiumExpiresAt, string? store, string? originalTransactionId);
    }
}
