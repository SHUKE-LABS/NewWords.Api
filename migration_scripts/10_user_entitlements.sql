USE NewWords;
-- Create the UserEntitlements table backing server-side subscription state (issue #37).
-- A user is premium iff PremiumExpiresAt is non-null and greater than the current unix time.
-- This ticket adds the store, the read path, and an upsert seam; store receipt verification is
-- a separate ticket. MySQL 8 compatible and fully idempotent (safe to run multiple times).

-- ============================================================================
-- Step 1: Create the table if it doesn't exist
-- ============================================================================
CREATE TABLE IF NOT EXISTS UserEntitlements (
    Id                    BIGINT       NOT NULL AUTO_INCREMENT,
    UserId                INT          NOT NULL,
    PremiumExpiresAt      BIGINT       NULL,
    Store                 VARCHAR(32)  NULL,
    OriginalTransactionId VARCHAR(255) NULL,
    CreatedAt             BIGINT       NOT NULL,
    UpdatedAt             BIGINT       NOT NULL,
    PRIMARY KEY (Id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ============================================================================
-- Step 2: Add the unique index on UserId (one entitlement row per user) if absent
-- ============================================================================
SET @uq_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'UserEntitlements'
      AND INDEX_NAME = 'UQ_UserEntitlements_UserId'
);

SET @add_uq_sql = IF(
    @uq_exists = 0,
    'ALTER TABLE UserEntitlements ADD UNIQUE INDEX UQ_UserEntitlements_UserId (UserId)',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @add_uq_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@uq_exists = 0,
    'Added unique index UQ_UserEntitlements_UserId',
    'Unique index UQ_UserEntitlements_UserId already exists') as Step2_Status;
