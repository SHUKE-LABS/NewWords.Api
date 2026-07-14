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
    }
}
