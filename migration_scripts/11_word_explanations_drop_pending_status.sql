USE NewWords;
-- Remove the pending-explanation mechanism from the schema (issue #52), reverting 09.
-- An add now returns a real explanation or fails, so no row can ever be Pending again:
-- every surviving Status = 1 (Pending) placeholder row is deleted -- together with the
-- UserWords rows pointing at it -- BEFORE the columns are dropped, so no placeholder
-- markdown survives to later read as Ready.
--
-- The final best-effort fill pass is an operator step taken BEFORE this deploy is
-- triggered (see migration_scripts/README.md): deploys apply migrations before the
-- service restart, so only the outgoing build's retry worker can still fill rows.
--
-- MySQL 8 compatible, fully idempotent: once Status is gone every step is a no-op.

SET @status_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'Status'
);

-- ============================================================================
-- Step 1: Report what is about to be deleted (audit trail in the deploy log)
-- ============================================================================
SET @count_sql = IF(
    @status_exists = 1,
    'SELECT COUNT(*) AS PendingExplanationsToDelete FROM WordExplanations WHERE Status = 1',
    'SELECT 0 AS PendingExplanationsToDelete'
);

PREPARE stmt FROM @count_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ============================================================================
-- Step 2: Delete UserWords rows linked to a pending explanation
-- ============================================================================
SET @del_userwords_sql = IF(
    @status_exists = 1,
    'DELETE uw FROM UserWords uw
       JOIN WordExplanations we ON we.Id = uw.WordExplanationId
      WHERE we.Status = 1',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @del_userwords_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_exists = 1,
    'Deleted UserWords rows linked to pending explanations',
    'Status column already gone; no UserWords cleanup needed') as Step2_Status;

-- ============================================================================
-- Step 3: Delete the pending explanation rows themselves
-- ============================================================================
SET @del_pending_sql = IF(
    @status_exists = 1,
    'DELETE FROM WordExplanations WHERE Status = 1',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @del_pending_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_exists = 1,
    'Deleted pending (placeholder) WordExplanations rows',
    'Status column already gone; no pending rows to delete') as Step3_Status;

-- ============================================================================
-- Step 4: Drop the pending-batch index added by 09
-- ============================================================================
SET @status_index_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND INDEX_NAME = 'IX_WordExplanations_Status'
);

SET @drop_index_sql = IF(
    @status_index_exists = 1,
    'ALTER TABLE WordExplanations DROP INDEX IX_WordExplanations_Status',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @drop_index_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_index_exists = 1,
    'Dropped index: IX_WordExplanations_Status',
    'Index IX_WordExplanations_Status already gone') as Step4_Status;

-- ============================================================================
-- Step 5: Drop the RetryCount column
-- ============================================================================
SET @retry_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'RetryCount'
);

SET @drop_retry_sql = IF(
    @retry_exists = 1,
    'ALTER TABLE WordExplanations DROP COLUMN RetryCount',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @drop_retry_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@retry_exists = 1,
    'Dropped RetryCount column',
    'RetryCount column already gone') as Step5_Status;

-- ============================================================================
-- Step 6: Drop the Status column
-- ============================================================================
SET @drop_status_sql = IF(
    @status_exists = 1,
    'ALTER TABLE WordExplanations DROP COLUMN Status',
    'SELECT 1'  -- No-op statement
);

PREPARE stmt FROM @drop_status_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_exists = 1,
    'Dropped Status column',
    'Status column already gone') as Step6_Status;

-- ============================================================================
-- Verification: neither column may remain (expect an empty result set)
-- ============================================================================
SELECT
    COLUMN_NAME,
    COLUMN_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'WordExplanations'
  AND COLUMN_NAME IN ('Status', 'RetryCount')
ORDER BY COLUMN_NAME;

SELECT
    '✓ Migration 11_word_explanations_drop_pending_status.sql completed successfully' as Status,
    NOW() as CompletedAt;
