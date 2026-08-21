USE NewWords;
-- Remove the pending-explanation mechanism from the schema (issue #52), reverting 09.
-- An add now returns a real explanation or fails, so no row can ever be Pending again, and no
-- placeholder row may survive this migration to later read as a real explanation.
--
-- Order: every remaining Status = 1 (Pending) row -- and the UserWords rows pointing at it -- is
-- deleted FIRST, then the index and the two columns are dropped.
--
-- The whole sequence runs while holding an explicit write lock, because deploys apply migrations
-- while the OUTGOING service is still accepting requests and only restart it afterwards
-- (.github/workflows/deploy_to_production.yml). Without the lock the old build could commit a
-- fresh placeholder in the window between the delete and the column drop, and the new build would
-- read that row as a real explanation. LOCK TABLES ... WRITE is held across both the deletes and
-- the DDL in this one session, so a concurrent add blocks until the lock is released and then
-- fails against the new schema (its INSERT names Status/RetryCount, which SqlSugar maps from the
-- entity) -- verified: a writer that arrives mid-migration blocks, then errors with
-- "Unknown column 'Status' in 'field list'". Nothing is persisted; no placeholder can slip in.
--
-- The DB user therefore needs LOCK TABLES in addition to DELETE and ALTER. All
-- INFORMATION_SCHEMA probes are evaluated BEFORE the lock, because locked-tables mode restricts
-- the session to the locked tables.
--
-- MySQL 8 compatible, fully idempotent: once the columns are gone, every step is a no-op.

-- ============================================================================
-- Probes (must run before LOCK TABLES)
-- ============================================================================
SET @status_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'Status'
);

SET @retry_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND COLUMN_NAME = 'RetryCount'
);

SET @status_index_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'WordExplanations'
      AND INDEX_NAME = 'IX_WordExplanations_Status'
);

-- ============================================================================
-- Barrier: block concurrent writes from the outgoing build for the whole sequence
-- ============================================================================
-- Locked-tables mode requires every alias used in a statement to be locked by name too,
-- hence the aliased entries for the Step 2 join.
LOCK TABLES WordExplanations WRITE, WordExplanations AS we WRITE, UserWords WRITE, UserWords AS uw WRITE;

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
    'DELETE uw FROM UserWords uw JOIN WordExplanations we ON we.Id = uw.WordExplanationId WHERE we.Status = 1',
    'SELECT 0'  -- No-op statement
);

PREPARE stmt FROM @del_userwords_sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SELECT IF(@status_exists = 1,
    'Deleted UserWords rows linked to pending explanations',
    'Status column already gone; no UserWords cleanup needed') as Step2_Status;

-- ============================================================================
-- Step 3: Delete the pending (placeholder) explanation rows themselves
-- ============================================================================
SET @del_pending_sql = IF(
    @status_exists = 1,
    'DELETE FROM WordExplanations WHERE Status = 1',
    'SELECT 0'  -- No-op statement
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
-- Release the barrier. Queued writes from the outgoing build now fail against the
-- new schema instead of committing a placeholder.
-- ============================================================================
UNLOCK TABLES;

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
