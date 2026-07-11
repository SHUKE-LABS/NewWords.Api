USE NewWords;
-- Add Status and RetryCount to WordExplanations to support the pending-explanation flow (issue #18).
-- When all LLM agents fail at add-word time, the word is persisted with Status = 1 (Pending) and a
-- placeholder explanation; inline re-add retry and a background worker later fill it (Status = 0, Ready).
-- MySQL 8 compatible, fully idempotent (safe to run multiple times). Existing rows read as Ready (0).

-- ============================================================================
-- Step 1: Add Status column (TINYINT NOT NULL DEFAULT 0) if it doesn't exist
-- ============================================================================
SET @status_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'Status'
);

SET @add_status_sql = IF(
    @status_exists = 0,
    'ALTER TABLE WordExplanations ADD COLUMN Status TINYINT NOT NULL DEFAULT 0 AFTER ProviderModelName',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @add_status_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_exists = 0,
    'Added Status column (default 0 = Ready)',
    'Status column already exists') as Step1_Status;

-- ============================================================================
-- Step 2: Add RetryCount column (INT NOT NULL DEFAULT 0) if it doesn't exist
-- ============================================================================
SET @retry_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'RetryCount'
);

SET @add_retry_sql = IF(
    @retry_exists = 0,
    'ALTER TABLE WordExplanations ADD COLUMN RetryCount INT NOT NULL DEFAULT 0 AFTER Status',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @add_retry_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@retry_exists = 0,
    'Added RetryCount column (default 0)',
    'RetryCount column already exists') as Step2_Status;

-- ============================================================================
-- Step 3: Index Status for the background worker's pending-batch scan
-- ============================================================================
SET @status_index_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND INDEX_NAME = 'IX_WordExplanations_Status'
);

SET @create_status_index_sql = IF(
    @status_index_exists = 0,
    'ALTER TABLE WordExplanations ADD INDEX IX_WordExplanations_Status (Status)',
    'SELECT 1'  -- No-op
);

PREPARE stmt FROM @create_status_index_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_index_exists = 0,
    'Created index: IX_WordExplanations_Status',
    'Index IX_WordExplanations_Status already exists') as Step3_Status;

-- ============================================================================
-- Verification
-- ============================================================================
SELECT
    COLUMN_NAME,
    COLUMN_TYPE,
    IS_NULLABLE,
    COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'WordExplanations'
  AND COLUMN_NAME IN ('Status', 'RetryCount')
ORDER BY COLUMN_NAME;

SELECT
    '✓ Migration 09_word_explanations_pending_status.sql completed successfully' as Status,
    NOW() as CompletedAt;
