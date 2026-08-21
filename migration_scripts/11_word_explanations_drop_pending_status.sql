USE NewWords;
-- Remove the pending-explanation mechanism from the schema (issue #52), reverting 09.
-- An add now returns a real explanation or fails, so no row can ever be Pending again, and no
-- placeholder markdown may survive this migration to later read as a real explanation.
--
-- ORDER MATTERS -- the schema drop comes FIRST, deliberately, and is the write barrier:
--   Deploys apply migrations while the OUTGOING service is still accepting requests and only
--   restart it afterwards (.github/workflows/deploy_to_production.yml). If pending rows were
--   deleted first, the old build could commit a fresh Status = 1 placeholder in the window
--   between that delete and the column drop, and the new build would then read it as a real
--   explanation. Dropping Status/RetryCount first closes that window without stopping the
--   service: every WordExplanations INSERT/UPDATE the outgoing build issues names those columns
--   (SqlSugar maps the whole entity), so from Step 4 on its writes fail and roll back and no new
--   placeholder can be created. Cleanup then runs behind that barrier.
--   The outgoing build's WordExplanations reads also fail for the seconds between Step 4 and the
--   service restart -- strictly less disruption than stopping the service across the whole
--   migration, and the deploy restarts into the new build immediately after.
--
-- After the drop, surviving placeholders are identified by their content instead of Status:
-- BuildPendingPlaceholder wrote exactly one shape ("**<word>**\n\n_The explanation is being
-- generated and will appear shortly._") and a pending row never had a provider model, so the
-- predicate is (ProviderModelName IS NULL AND markdown contains that sentence). Both conditions
-- must hold, so a genuine explanation with a NULL model is never touched.
--
-- MySQL 8 compatible, fully idempotent: once the columns are gone the drops are no-ops and the
-- deletes match nothing.

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
-- Step 2: Drop the pending-batch index added by 09
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
    'Index IX_WordExplanations_Status already gone') as Step2_Status;

-- ============================================================================
-- Step 3: Drop the RetryCount column
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
    'RetryCount column already gone') as Step3_Status;

-- ============================================================================
-- Step 4: Drop the Status column -- the write barrier. No new placeholder row can
--         be created from here on, so the cleanup below cannot lose a race.
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
    'Dropped Status column (writes by the outgoing build now fail; no new placeholders possible)',
    'Status column already gone') as Step4_Status;

-- ============================================================================
-- Step 5: Delete UserWords rows linked to a surviving placeholder explanation
-- ============================================================================
DELETE uw FROM UserWords uw
  JOIN WordExplanations we ON we.Id = uw.WordExplanationId
 WHERE we.ProviderModelName IS NULL
   AND we.MarkdownExplanation LIKE '%The explanation is being generated and will appear shortly.%';

SELECT ROW_COUNT() AS Step5_DeletedUserWords;

-- ============================================================================
-- Step 6: Delete the placeholder explanation rows themselves
-- ============================================================================
DELETE FROM WordExplanations
 WHERE ProviderModelName IS NULL
   AND MarkdownExplanation LIKE '%The explanation is being generated and will appear shortly.%';

SELECT ROW_COUNT() AS Step6_DeletedPlaceholderExplanations;

-- ============================================================================
-- Verification: neither column may remain, and no placeholder row may remain
-- (both result sets are expected to be empty / zero)
-- ============================================================================
SELECT
    COLUMN_NAME,
    COLUMN_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'WordExplanations'
  AND COLUMN_NAME IN ('Status', 'RetryCount')
ORDER BY COLUMN_NAME;

SELECT COUNT(*) AS RemainingPlaceholderRows
FROM WordExplanations
WHERE ProviderModelName IS NULL
  AND MarkdownExplanation LIKE '%The explanation is being generated and will appear shortly.%';

SELECT
    '✓ Migration 11_word_explanations_drop_pending_status.sql completed successfully' as Status,
    NOW() as CompletedAt;
