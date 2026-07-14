namespace NewWords.Api.Constants
{
    /// <summary>
    /// Constants for the subscription entitlement / free-word-cap feature (issue #37).
    /// </summary>
    public static class EntitlementConstants
    {
        /// <summary>
        /// Distinct business error code returned when a free user hits the saved-word cap. The
        /// client maps this to the paywall. Deliberately not the framework default (1) so it is
        /// unambiguously the cap, never a generic failure.
        /// </summary>
        public const int FreeWordCapReachedErrorCode = 42901;

        /// <summary>
        /// Default number of saved words a free (non-premium) user may keep, used when the
        /// config key is unset or invalid.
        /// </summary>
        public const int DefaultFreeWordCap = 500;

        /// <summary>
        /// Configuration key (Redis/ConfigManager or appsettings) for the free saved-word cap.
        /// Tunable without redeploy.
        /// </summary>
        public const string FreeWordCapConfigKey = "Subscription:FreeWordCap";

        /// <summary>
        /// Distinct business error code returned when an Apple App Store transaction cannot be
        /// verified or is not an active subscription (invalid / expired / tampered / revoked), or
        /// when server-side Apple verification is not configured. Kept separate from the cap code
        /// so the client can tell "verification failed" from "cap reached".
        /// </summary>
        public const int AppleVerificationFailedErrorCode = 42902;

        /// <summary>
        /// The <see cref="Entities.UserEntitlement.Store"/> discriminator value for grants that
        /// come from the Apple App Store. Pairs with a future "playstore" value for the Google
        /// Play follow-up (#24). Internal only — not surfaced in <c>EntitlementStatusDto</c>.
        /// </summary>
        public const string AppleStore = "appstore";
    }
}
