# Database Migration Scripts

This directory contains all database migration scripts for the NewWords.Api project.

## How migrations are applied (issue #41)

Production deploys apply pending migrations **automatically**. `apply_migrations.sh`
runs on the VPS during deploy — after the files are copied, before the service
restarts — via the *Apply DB migrations* step in
`.github/workflows/deploy_to_production.yml`. If it fails, the deploy fails
**before** the restart, so the new code never starts against a stale schema.

The runner is ledger-based and idempotent:

- A `schema_migrations` table records every applied filename.
- Only `NN_*.sql` files (numeric prefix) are considered, applied in numeric order.
- Each script runs **once**; re-deploys skip already-applied scripts, and each
  script is itself `INFORMATION_SCHEMA`-guarded / `CREATE TABLE IF NOT EXISTS`.
- DB credentials are read from the deployed `appsettings.json`
  `DatabaseConnectionOptions.ConnectionString` (override with
  `MYSQL_HOST/PORT/USER/DB/PASSWORD`); the password is passed via a `0600`
  `--defaults-file`, never on a process command line.
- `mysql` client is required on the VPS (the old manual `source` step already
  assumed it); the step fails loudly if it is absent.

Add a new migration by dropping a `NN_<name>.sql` file here (idempotent SQL, next
number in sequence). The next deploy applies and records it. Preview locally with:

```bash
DRY_RUN=1 APPSETTINGS=/path/to/appsettings.json bash migration_scripts/apply_migrations.sh
```

### ⚠️ One-time precondition before the FIRST automated deploy

The **first** run creates the `schema_migrations` ledger and **seeds every script
present as a baseline — recording them as applied WITHOUT executing them**, because
an existing production DB already contains 01-08 (pre-existing) and 09/10 (applied
by the manual incident mitigation below).

**Therefore any outstanding migration MUST be applied to the production DB by hand
before the first automated deploy lands.** If it is not, the baseline silently
records it as applied and the schema/code drift is baked in — the exact bug this
automation exists to prevent. For the #41 incident that means running, once, first:

```sql
source migration_scripts/09_word_explanations_pending_status.sql
source migration_scripts/10_user_entitlements.sql
```

(Both are idempotent; re-running is safe.) A fresh, empty database has no baseline
to preserve, so run all scripts manually once before the first deploy there too.

The manual `source NN.sql` steps documented per-migration below remain valid as an
**emergency / first-time bootstrap** path, but are no longer the normal route.

## Migration History

### 01-03: WordCollection Language Field Removal Migration
This migration removes the `Language` field from the `WordCollection` table to simplify the architecture and eliminate language detection ambiguity.

### 04-05: Per-User Story Read Tracking Migration
This migration implements per-user read tracking for stories, fixing the issue where `Stories.FirstReadAt` was story-level instead of per-user.

### 06: UserWords UpdatedAt Field Migration
This migration adds an `UpdatedAt` field to the `UserWords` table to enable sorting the user's vocabulary timeline by last interaction time instead of first addition time.

### 07: Multiple Explanations Support Migration
This migration adds `WordCollectionId` to the `UserWords` table and updates the unique constraint to enable multiple AI-generated explanations per word with user-specific default selection.

### 09: Pending Explanation Status Migration
This migration adds `Status` (`TINYINT NOT NULL DEFAULT 0`; `0 = Ready`, `1 = Pending`) and `RetryCount` (`INT NOT NULL DEFAULT 0`) to the `WordExplanations` table, plus an index on `Status`. When every LLM agent fails at add-word time, the word is persisted immediately with a `Pending` placeholder explanation instead of rolling back; inline re-add retry and the `ExplanationRetryBackgroundService` (every 30 min, batch 20, `RetryCount` cap 20) later fill it and flip it to `Ready`. Idempotent; existing rows read as `Ready`.

### 10: User Entitlements Table Migration
This migration creates the `UserEntitlements` table backing server-side subscription state (issue #37): `Id` (PK), `UserId` (unique), `PremiumExpiresAt` (`BIGINT NULL`, unix seconds), `Store` (`VARCHAR(32) NULL`), `OriginalTransactionId` (`VARCHAR(255) NULL`), `CreatedAt`/`UpdatedAt` (`BIGINT NOT NULL`). A user is premium iff `PremiumExpiresAt` is non-null and greater than now. The table powers the `GET /Entitlement/Status` endpoint and the non-premium 500-word free cap enforced in `AddUserWordAsync` before any LLM call. Store receipt verification (which populates `Store` / `OriginalTransactionId`) is a separate ticket. Fully idempotent (`CREATE TABLE IF NOT EXISTS` + existence-checked unique index).

## Migration Steps

### Phase 1: Analysis
1. **Run analysis script:**
   ```sql
   source 01_analyze_duplicates.sql
   ```
   - Review the duplicate analysis results
   - Note which words have multiple language entries
   - Understand the scope of data consolidation needed

### Phase 2: Data Consolidation
2. **Run merge script:**
   ```sql
   source 02_merge_duplicates.sql
   ```
   - **CRITICAL:** This script modifies data. Ensure you have a backup.
   - Updates `WordExplanation` foreign key references
   - Merges duplicate `WordCollection` records by summing `QueryCount`
   - Soft-deletes duplicate records (keeps earliest `CreatedAt`)
   - Creates audit log in temporary `merge_log` table

3. **Verify merge results:**
   - Check that `Orphaned WordExplanations` count is 0
   - Check that `Remaining Duplicates` count is 0
   - Review `merge_log` table for audit trail

### Phase 3: Code Deployment
4. **Deploy updated codebase:**
   - The code changes have already been made
   - Deploy the updated application with removed `Language` field references
   - Test that word addition and explanation generation still work

### Phase 4: Schema Update
5. **Run final migration:**
   ```sql
   source 03_drop_language_column.sql
   ```
   - **IMPORTANT:** Permanently deletes soft-deleted records first (required for unique index)
   - Drops old unique index `WordText` (WordText + Language)
   - Creates new unique index `UQ_WordCollection_WordText` (WordText only)
   - Drops the `Language` column
   - Verifies no duplicates remain

**Note:** The cleanup of soft-deleted records is now integrated into step 3 since it's required before creating the new unique index.

## Rollback Plan

If you need to rollback:

1. **Before running merge script:** Simply restore from backup
2. **After running merge script:**
   - Use the `merge_log` table to identify what was changed
   - Restore from backup (recommended)
   - Or manually recreate duplicate records if backup is not available

## Key Changes Made

### Database Schema:
- **Before:** `WordCollection` had `Language` field with compound unique key
- **After:** `WordCollection` has only `WordText` with simple unique key

### Code Logic:
- **Before:** Complex language-based matching in `_HandleWordCollection`
- **After:** Simple text-based lookup, language-agnostic storage

### WordExplanation:
- **No changes** - Still maintains `LearningLanguage` from user profile
- Foreign key integrity preserved through merge script

## Benefits

1. **Simplified Logic:** No more language detection for storage
2. **Better Accuracy:** User's `LearningLanguage` always correct from profile
3. **Unified Tracking:** `QueryCount` represents total popularity across languages
4. **Reduced Complexity:** One record per unique word text

## Testing Checklist

After WordCollection migration:
- [ ] Can add new words via API
- [ ] Word explanations generate correctly
- [ ] `QueryCount` increments properly
- [ ] No duplicate `WordText` entries exist
- [ ] `WordExplanations` foreign keys are valid
- [ ] Bulk explanation generation works (`FillWordExplanationsTable`)
- [ ] Duplicate records permanently removed

---

## Per-User Story Read Tracking Migration (04-05)

### Overview
Fixes the design issue where `Stories.FirstReadAt` was story-level instead of per-user, preventing proper read tracking when users discover stories created by others.

### Migration Steps

#### Phase 1: Create Infrastructure & Migrate Data
1. **Run migration script:**
   ```sql
   source 04_user_story_reads_migration.sql
   ```
   - Creates `UserStoryReads` table with proper indexes (re-runnable)
   - Migrates existing `Stories.FirstReadAt` data to preserve read status
   - Skips duplicate entries if script is run multiple times
   - Verifies migration completed successfully

#### Phase 2: Code Deployment
2. **Deploy updated codebase:**
   - Updated service layer with optimized SqlSugar joins
   - Removed ownership restriction from `MarkStoryAsReadAsync`
   - `Story.FirstReadAt` becomes computed property (user-context-aware)

#### Phase 3: Schema Cleanup (Optional)
3. **Run cleanup script after verification:**
   ```sql
   source 05_cleanup_stories_firstreadat.sql
   ```
   - **Optional:** Removes old `FirstReadAt` column from `Stories` table (re-runnable)
   - Safely checks if column exists before dropping
   - Run only after confirming everything works correctly

### Key Changes

#### Database Schema:
- **Before:** `Stories.FirstReadAt` (story-level, single timestamp)
- **After:** `UserStoryReads` table (per-user read tracking)

#### API Behavior:
- **Before:** `MarkStoryAsReadAsync` only worked for story owners
- **After:** Any user can mark any story as read
- **Frontend:** Zero changes needed - API contracts unchanged

#### Performance:
- **Before:** N+1 queries in `ConvertToStoryDtosAsync`
- **After:** Single query with LEFT JOINs for favorites and read status

### Benefits

1. **Proper Read Tracking:** Users can read stories from Story Square
2. **Zero Frontend Changes:** `StoryDto.FirstReadAt` still works as expected
3. **Better Performance:** Single optimized query instead of multiple lookups
4. **Data Preservation:** All existing read data migrated safely

### Testing Checklist

After migration:
- [ ] Story Square shows stories from other users
- [ ] Users can mark any story as read (not just their own)
- [ ] Read status persists correctly per user
- [ ] Stories show proper read status in all endpoints
- [ ] Favorite functionality still works
- [ ] Migration preserved all existing read data

---

## UserWords UpdatedAt Field Migration (06)

### Overview
Adds `UpdatedAt` field to `UserWords` table to support re-adding words to bump them to the front of the user's vocabulary timeline. Previously, words were sorted only by `CreatedAt` (first addition), but the design requires sorting by last interaction (`UpdatedAt`).

### Migration Steps

#### Phase 1: Run Migration Script
1. **Run migration:**
   ```sql
   source 06_userwords_updatedat.sql
   ```
   - Adds `UpdatedAt` column to `UserWords` table
   - Initializes existing records with `UpdatedAt = CreatedAt`
   - Makes column NOT NULL with default value
   - Verifies all records have `UpdatedAt` set

#### Phase 2: Code Already Deployed
2. **Code changes:**
   - `UserWord` entity: Added `UpdatedAt` property
   - `VocabularyService.GetUserWordsAsync`: Changed sorting from `CreatedAt` to `UpdatedAt`
   - `VocabularyService._HandleUserWord`: Updates `UpdatedAt` when user re-adds existing word
   - `WordExplanation` entity: Added ignored `UpdatedAt` property for API response

### Key Changes

#### Database Schema:
- **Before:** `UserWords` had only `CreatedAt` (first addition timestamp)
- **After:** `UserWords` has both `CreatedAt` and `UpdatedAt` (last interaction timestamp)

#### API Behavior:
- **Before:** Re-adding an existing word did nothing (word stayed at original position)
- **After:** Re-adding a word updates `UpdatedAt` and moves it to front of timeline
- **Frontend:** Zero changes needed - sorting now reflects last interaction

### Benefits

1. **Better UX:** Re-adding a word bumps it to the front of the timeline
2. **Data Preservation:** All existing words keep their original `CreatedAt`
3. **Backward Compatible:** Existing records initialized with `UpdatedAt = CreatedAt`
4. **Performance:** Same query performance with indexed sorting

### Testing Checklist

After migration:
- [ ] Existing words show in timeline (sorted by original creation date initially)
- [ ] Re-adding an existing word moves it to the front of the timeline
- [ ] New words appear at the front of the timeline
- [ ] API returns both `CreatedAt` and `UpdatedAt` in response
- [ ] All UserWords records have non-null `UpdatedAt` values
- [ ] Performance improved (check query execution plans)

---

## Multiple Explanations Support Migration (07)

### Overview
Adds `WordCollectionId` to `UserWords` table and updates the unique constraint from `(UserId, WordExplanationId)` to `(UserId, WordCollectionId)`. This enables the system to store multiple AI-generated explanations per word (from different models) while allowing each user to choose their preferred default explanation.

### Design Principles
- **Shared Explanations**: All users benefit from AI-generated explanations (no per-user generation)
- **Only Add, Never Delete**: `RefreshExplanation` creates new records, preserving history
- **Smart Model Selection**: Automatically uses next unused AI model when refreshing
- **User-Specific Default**: Each user can select their preferred explanation version

### Migration Steps

#### Phase 1: Run Migration Script
1. **Run migration:**
   ```sql
   source 07_multiple_explanations.sql
   ```
   - Adds `WordCollectionId` column to `UserWords`
   - Populates from existing `WordExplanation` relationships
   - **Step 4**: Dynamically finds and drops FK constraints (no hardcoded names)
   - **Step 5**: Drops old unique index `UQ_UserWords_UserId_WordId`
   - **Step 6**: Creates new unique index `UQ_UserWords_UserId_WordCollectionId` (ASC)
   - **Step 7**: Adds performance index on `WordCollectionId`
   - **Step 8**: Recreates FK constraints (checks existence first)
   - Fully idempotent - safe to run multiple times
   - Environment-agnostic - works with any FK naming convention

#### Phase 2: Code Already Deployed
2. **Backend changes:**
   - `UserWord`: Added `WordCollectionId` field
   - `ExplanationsResponse`: New DTO for multiple explanations
   - `VocabularyService.RefreshUserWordExplanationAsync`: Rewritten to create new records
   - `VocabularyService.GetAllExplanationsForWordAsync`: New method
   - `VocabularyService.SwitchUserDefaultExplanationAsync`: New method
   - New API endpoints for browsing and switching explanations

3. **Frontend changes:**
   - `ExplanationsResponse` entity
   - API and service layer methods
   - `VocabularyProvider`: Caching and switching logic
   - `WordDetailScreen`: Version navigator, default indicator, browsing UI

### Key Changes

#### Database Schema:
- **Before:** `UserWords` uniquely identified by `(UserId, WordExplanationId)`
- **After:** `UserWords` uniquely identified by `(UserId, WordCollectionId)`
- **Migration Process:** Drops FK → Drops old index → Creates new index → Recreates FK
- **Impact:** One UserWord per user per word, but WordExplanationId can change

#### API Behavior:
- **Before:** `RefreshExplanation` updated existing record in-place
- **After:** `RefreshExplanation` creates new `WordExplanation` record with different model
- **New Endpoints:**
  - `GET /vocabulary/explanations/{wordCollectionId}/{learningLanguage}/{explanationLanguage}` - Get all versions
  - `PUT /vocabulary/switch-explanation/{wordCollectionId}/{explanationId}` - Switch default

#### User Experience:
- **Before:** One explanation per word, refresh overwrites
- **After:** Multiple explanations browsable, user picks favorite

### Benefits

1. **Diverse Perspectives**: Users see explanations from multiple AI models
2. **No Data Loss**: Historical explanations preserved
3. **User Choice**: Each user picks their preferred explanation style
4. **Shared Resources**: All users benefit from generated explanations
5. **Smart Generation**: Automatically uses next unused model

### Data Integrity

The migration ensures:
- All existing `UserWords` get valid `WordCollectionId` from their current `WordExplanation`
- Unique constraint prevents duplicate entries
- Foreign key relationships remain valid
- No data loss during constraint change

### Migration Robustness

Key improvements for production safety:
- **Dynamic FK Discovery**: Queries actual FK names from `INFORMATION_SCHEMA` instead of hardcoding
- **No sql_notes Suppression**: Clean error handling without hiding warnings
- **Environment Agnostic**: Works with any FK naming convention (ORM-generated or custom)
- **Index Optimization**: Uses default ASC for simplicity and consistency
- **Full Idempotency**: Every step checks existence before action

### Testing Checklist

After migration:
- [ ] All UserWords have valid `WordCollectionId` set
- [ ] Unique constraint `UQ_UserWords_UserId_WordCollectionId` exists
- [ ] Old index `UQ_UserWords_UserId_WordId` removed
- [ ] Foreign key constraints properly recreated
- [ ] No data inconsistencies (check verification query)
- [ ] Can browse multiple explanations in word detail screen
- [ ] Can switch default explanation
- [ ] Refresh creates new explanation (doesn't overwrite)
- [ ] Refresh uses next unused AI model
- [ ] UI shows version navigator (1/3, 2/3, etc)
- [ ] Default explanation marked with star
- [ ] Cache invalidation works after refresh/switch
