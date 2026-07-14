namespace NewWords.Api.Models.DTOs.Entitlement
{
    /// <summary>
    /// Entitlement status for the authenticated user, so the client can render the plan and an
    /// "X/cap" saved-word indicator.
    /// </summary>
    public class EntitlementStatusDto
    {
        /// <summary>"free" or "premium".</summary>
        public string Plan { get; set; } = "free";

        /// <summary>
        /// Unix timestamp (seconds) when premium expires, or null for a free user / never granted.
        /// </summary>
        public long? PremiumExpiresAt { get; set; }

        /// <summary>The user's current number of saved words.</summary>
        public int SavedWordCount { get; set; }

        /// <summary>The configured free saved-word cap. Informational for premium users.</summary>
        public int WordCap { get; set; }
    }
}
