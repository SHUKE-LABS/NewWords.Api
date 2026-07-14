using SqlSugar;

namespace NewWords.Api.Entities
{
    /// <summary>
    /// Server-side subscription entitlement for a user. Authoritative source of premium
    /// status: a user is premium iff <see cref="PremiumExpiresAt"/> is set and still in the
    /// future. Client-reported purchase state is never trusted. This ticket (#37) creates the
    /// store, the read path, and the upsert seam; store receipt verification is a separate ticket.
    /// </summary>
    [SugarTable("UserEntitlements")]
    // One entitlement row per user.
    [SugarIndex("UQ_UserEntitlements_UserId", nameof(UserId), OrderByType.Asc, true)]
    public class UserEntitlement
    {
        /// <summary>
        /// Unique identifier for this entitlement row (Primary Key, Auto-Increment).
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public long Id { get; set; }

        /// <summary>
        /// Foreign key referencing the User. Required and unique.
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public int UserId { get; set; }

        /// <summary>
        /// Unix timestamp (seconds) when premium access expires. Null means the user has never
        /// been granted premium. A user is premium iff this is non-null and greater than now.
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public long? PremiumExpiresAt { get; set; }

        /// <summary>
        /// Which store granted the entitlement (e.g. "appstore", "playstore"). Nullable until
        /// the verification ticket populates it.
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 32)]
        public string? Store { get; set; }

        /// <summary>
        /// The store's original transaction id, used to correlate renewals/refunds. Nullable
        /// until the verification ticket populates it.
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 255)]
        public string? OriginalTransactionId { get; set; }

        /// <summary>
        /// Timestamp when this entitlement row was created (Unix seconds). Required.
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public long CreatedAt { get; set; }

        /// <summary>
        /// Timestamp when this entitlement row was last updated (Unix seconds). Required.
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public long UpdatedAt { get; set; }
    }
}
