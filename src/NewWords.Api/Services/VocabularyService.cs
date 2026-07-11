using Api.Framework.Models;
using Api.Framework.Extensions;
using NewWords.Api.Entities;
using NewWords.Api.Repositories;
using SqlSugar;
using Api.Framework;
using LLM;
using LLM.Models;
using LLM.Services;
using NewWords.Api.Services.interfaces;
using System.Globalization;
using System.Diagnostics;
using NewWords.Api.Exceptions;

namespace NewWords.Api.Services
{
    public class VocabularyService(
        ISqlSugarClient db,
        ILanguageService languageService,
        IConfigurationService configurationService,
        ILogger<VocabularyService> logger,
        IRepositoryBase<WordCollection> wordCollectionRepository,
        IRepositoryBase<WordExplanation> wordExplanationRepository,
        IRepositoryBase<QueryHistory> queryHistoryRepository,
        IUserWordRepository userWordRepository)
        : IVocabularyService
    {
        // Handles WordExplanation entities

        public async Task<PageData<WordExplanation>> GetUserWordsAsync(int userId, int pageSize, int pageNumber)
        {
            RefAsync<int> totalCount = 0;
            var pagedWords = await db.Queryable<WordExplanation>()
                .RightJoin<UserWord>((we, uw) => we.Id == uw.WordExplanationId)
                .Where((we, uw) => uw.UserId == userId)
                .OrderBy((we, uw) => uw.UpdatedAt, OrderByType.Desc)
                .Select((we, uw) => new WordExplanation()
                {
                    CreatedAt = uw.CreatedAt,
                    UpdatedAt = uw.UpdatedAt,
                }, true)
                .ToPageListAsync(pageNumber, pageSize, totalCount);

            return new PageData<WordExplanation>
            {
                DataList = pagedWords,
                TotalCount = totalCount,
                PageIndex = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<WordExplanation> AddUserWordAsync(int userId, string wordText, string learningLanguageCode, string explanationLanguageCode)
        {
            var overallStopwatch = Stopwatch.StartNew();
            var normalizeMs = 0d;
            var localWordLookupMs = 0d;
            var localExplanationLookupMs = 0d;
            var llmTotalMs = 0d;
            var canonicalExtractMs = 0d;
            var transactionMs = 0d;
            var userWordMs = 0d;
            var modelName = string.Empty;
            var llmSuccess = false;
            var usedLocalExplanation = false;
            logger.LogInformation("Starting AddUserWordAsync for user {UserId}, word '{WordText}', learning: {LearningLanguage}, explanation: {ExplanationLanguage}",
                userId, wordText, learningLanguageCode, explanationLanguageCode);

            try
            {
                var normalizeStopwatch = Stopwatch.StartNew();
                var wordTextTrimmed = NormalizeWord(wordText);
                var learningLanguageName = configurationService.GetLanguageName(learningLanguageCode)!;
                var explanationLanguageName = configurationService.GetLanguageName(explanationLanguageCode)!;
                normalizeStopwatch.Stop();
                normalizeMs = Math.Round(normalizeStopwatch.Elapsed.TotalMilliseconds, 1);

                var localWordLookupStopwatch = Stopwatch.StartNew();
                var localWord = await wordCollectionRepository.GetFirstOrDefaultAsync(wc => wc.WordText == wordTextTrimmed && wc.DeletedAt == null);
                localWordLookupStopwatch.Stop();
                localWordLookupMs = Math.Round(localWordLookupStopwatch.Elapsed.TotalMilliseconds, 1);

                WordExplanation? explanation = null;
                WordExplanation? pendingRow = null;
                long wordCollectionId = 0;
                string canonicalWord = wordTextTrimmed;

                if (localWord != null)
                {
                    var localExplanationLookupStopwatch = Stopwatch.StartNew();
                    explanation = await wordExplanationRepository.GetFirstOrDefaultAsync(we =>
                        we.WordCollectionId == localWord.Id &&
                        we.LearningLanguage == learningLanguageCode &&
                        we.ExplanationLanguage == explanationLanguageCode);
                    localExplanationLookupStopwatch.Stop();
                    localExplanationLookupMs = Math.Round(localExplanationLookupStopwatch.Elapsed.TotalMilliseconds, 1);

                    if (explanation != null)
                    {
                        if (explanation.Status == ExplanationStatus.Pending)
                        {
                            // A placeholder left by an earlier all-agents-failed add. Treat it as missing so
                            // control enters the AI path for one inline retry that fills this same row.
                            pendingRow = explanation;
                            explanation = null;
                        }
                        else
                        {
                            wordCollectionId = localWord.Id;
                            usedLocalExplanation = true;
                        }
                    }
                }

                ExplanationResult? aiResult = null;
                if (explanation == null)
                {
                    logger.LogInformation("No local explanation found for word '{WordText}', calling AI service", wordTextTrimmed);

                    var llmStopwatch = Stopwatch.StartNew();
                    try
                    {
                        aiResult = await InvokeAiServiceAsync(wordTextTrimmed, explanationLanguageName, learningLanguageName);
                        llmStopwatch.Stop();
                        llmTotalMs = Math.Round(llmStopwatch.Elapsed.TotalMilliseconds, 1);
                        llmSuccess = aiResult?.IsSuccess ?? false;
                        modelName = aiResult?.ModelName ?? string.Empty;
                        logger.LogInformation("AI call completed in {ElapsedMs}ms for word '{WordText}', success: {Success}",
                            llmStopwatch.ElapsedMilliseconds, wordTextTrimmed, llmSuccess);
                    }
                    catch (Exception ex)
                    {
                        llmStopwatch.Stop();
                        llmTotalMs = Math.Round(llmStopwatch.Elapsed.TotalMilliseconds, 1);
                        logger.LogWarning(ex, "AI call failed after {ElapsedMs}ms for word '{WordText}'; will fallback to user input as canonical word",
                            llmStopwatch.ElapsedMilliseconds, wordTextTrimmed);
                        aiResult = new ExplanationResult { IsSuccess = false, ErrorMessage = ex.Message };
                    }

                    var aiSuccess = aiResult != null && aiResult.IsSuccess && !string.IsNullOrWhiteSpace(aiResult.Markdown);

                    if (pendingRow != null)
                    {
                        // Inline retry of an existing pending explanation: one LLM attempt. On success fill
                        // that same row in place (canonical extraction + typo merge); on failure return it
                        // unchanged so the word still shows up while the background worker keeps retrying.
                        var fillStopwatch = Stopwatch.StartNew();
                        if (aiSuccess)
                        {
                            await FillPendingExplanationAsync(pendingRow, aiResult!);
                        }
                        else
                        {
                            logger.LogWarning("Inline retry for pending word '{WordText}' failed; returning pending row unchanged",
                                wordTextTrimmed);
                        }
                        fillStopwatch.Stop();
                        transactionMs = Math.Round(fillStopwatch.Elapsed.TotalMilliseconds, 1);

                        explanation = pendingRow;
                        wordCollectionId = pendingRow.WordCollectionId;
                    }
                    else
                    {
                        if (!aiSuccess)
                        {
                            // Do not throw: persist the user's word now as a pending explanation instead of
                            // rolling back the whole transaction. The word is the valuable signal.
                            logger.LogWarning("AI explanation unavailable for '{WordText}': {ErrorMessage}. Persisting as pending",
                                wordTextTrimmed, aiResult?.ErrorMessage ?? "empty response");
                            canonicalWord = wordTextTrimmed;
                        }
                        else
                        {
                            var canonicalExtractStopwatch = Stopwatch.StartNew();
                            canonicalWord = ExtractCanonicalWordFromMarkdown(aiResult!.Markdown!);
                            if (string.IsNullOrWhiteSpace(canonicalWord)) canonicalWord = wordTextTrimmed;
                            canonicalExtractStopwatch.Stop();
                            canonicalExtractMs = Math.Round(canonicalExtractStopwatch.Elapsed.TotalMilliseconds, 1);
                        }

                        var transactionStopwatch = Stopwatch.StartNew();
                        try
                        {
                            await TransactionHelper.ExecuteInTransactionAsync(db, async () =>
                            {
                                wordCollectionId = await _HandleWordCollection(wordTextTrimmed, canonicalWord);
                                explanation = await _HandleExplanation(canonicalWord, learningLanguageCode, explanationLanguageCode, wordCollectionId, aiResult);
                            }, logger);
                            transactionStopwatch.Stop();
                            transactionMs = Math.Round(transactionStopwatch.Elapsed.TotalMilliseconds, 1);
                        }
                        catch (Exception ex)
                        {
                            transactionStopwatch.Stop();
                            transactionMs = Math.Round(transactionStopwatch.Elapsed.TotalMilliseconds, 1);
                            logger.LogError(ex, "Database transaction block failed for word '{WordText}'", wordTextTrimmed);
                            throw;
                        }
                    }
                }

                if (explanation == null)
                {
                    logger.LogError("Explanation unexpectedly null after DB transaction for word '{WordText}'", wordTextTrimmed);
                    throw new InvalidOperationException("Explanation is null after expected creation");
                }

                var userWordStopwatch = Stopwatch.StartNew();
                var userWord = await _HandleUserWord(userId, explanation);
                userWordStopwatch.Stop();
                userWordMs = Math.Round(userWordStopwatch.Elapsed.TotalMilliseconds, 1);

                explanation.CreatedAt = userWord.CreatedAt;
                explanation.UpdatedAt = userWord.UpdatedAt;

                overallStopwatch.Stop();
                logger.LogInformation(
                    "AddUserWordAsync timing for user {UserId}, word '{WordText}': normalize_ms={NormalizeMs}, local_word_lookup_ms={LocalWordLookupMs}, local_explanation_lookup_ms={LocalExplanationLookupMs}, llm_total_ms={LlmTotalMs}, canonical_extract_ms={CanonicalExtractMs}, transaction_ms={TransactionMs}, userword_ms={UserWordMs}, total_ms={TotalMs}, model_name={ModelName}, success={Success}, llm_success={LlmSuccess}, used_local_explanation={UsedLocalExplanation}",
                    userId,
                    wordTextTrimmed,
                    normalizeMs,
                    localWordLookupMs,
                    localExplanationLookupMs,
                    llmTotalMs,
                    canonicalExtractMs,
                    transactionMs,
                    userWordMs,
                    Math.Round(overallStopwatch.Elapsed.TotalMilliseconds, 1),
                    modelName,
                    true,
                    llmSuccess,
                    usedLocalExplanation);
                logger.LogInformation("AddUserWordAsync completed successfully in {ElapsedMs}ms for user {UserId}, word '{WordText}'",
                    overallStopwatch.ElapsedMilliseconds, userId, wordTextTrimmed);

                return explanation;
            }
            catch (Exception ex) // Catch exceptions from ExecuteInTransactionAsync or input validation
            {
                overallStopwatch.Stop();
                logger.LogInformation(
                    "AddUserWordAsync timing for user {UserId}, word '{WordText}': normalize_ms={NormalizeMs}, local_word_lookup_ms={LocalWordLookupMs}, local_explanation_lookup_ms={LocalExplanationLookupMs}, llm_total_ms={LlmTotalMs}, canonical_extract_ms={CanonicalExtractMs}, transaction_ms={TransactionMs}, userword_ms={UserWordMs}, total_ms={TotalMs}, model_name={ModelName}, success={Success}, llm_success={LlmSuccess}, used_local_explanation={UsedLocalExplanation}",
                    userId,
                    wordText,
                    normalizeMs,
                    localWordLookupMs,
                    localExplanationLookupMs,
                    llmTotalMs,
                    canonicalExtractMs,
                    transactionMs,
                    userWordMs,
                    Math.Round(overallStopwatch.Elapsed.TotalMilliseconds, 1),
                    modelName,
                    false,
                    llmSuccess,
                    usedLocalExplanation);
                logger.LogError(ex, "AddUserWordAsync failed after {ElapsedMs}ms for user {UserId}, word '{WordText}'",
                    overallStopwatch.ElapsedMilliseconds, userId, wordText);
                throw;
            }
        }

    /// <summary>
    /// Extract the canonical word from the first line of AI markdown (e.g. "**apple**" -> "apple")
    /// </summary>
    internal string ExtractCanonicalWordFromMarkdown(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
            var firstLine = markdown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => !string.IsNullOrEmpty(l));
            if (!string.IsNullOrEmpty(firstLine))
            {
                // Prefer the first bold block **...** on the first non-empty line
                var start = firstLine.IndexOf("**", StringComparison.Ordinal);
                if (start >= 0)
                {
                    var end = firstLine.IndexOf("**", start + 2, StringComparison.Ordinal);
                    if (end > start + 1)
                    {
                        var candidate = firstLine.Substring(start + 2, end - (start + 2)).Trim();
                        if (!string.IsNullOrEmpty(candidate)) return candidate;
                    }
                }

                // Fallback: strip basic markdown characters and take the first token
                var cleaned = firstLine.Replace("**", "").Replace("*", "").Replace("`", "").Replace("#", "").Trim();
                var tokens = cleaned.Split(new[] { ' ', '\t', '-', '—', '\u2014', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0) return tokens[0].Trim();
            }

            // As a last resort, return trimmed whole markdown
            return markdown.Trim();
        }

        public async Task DelUserWordAsync(int userId, long wordExplanationId)
        {
            var userWord = await userWordRepository.GetFirstOrDefaultAsync(uw =>
                uw.UserId == userId && uw.WordExplanationId == wordExplanationId);

            if (userWord != null)
            {
                await _DeleteUserWordWithCleanup(userWord);
            }
            else
            {
                logger.LogWarning($"UserWord not found for deletion - UserId: {userId}, WordExplanationId: {wordExplanationId}");
            }
        }

        public async Task<WordExplanation> RefreshUserWordExplanationAsync(int userId, long wordExplanationId)
        {
            // 1. Get current explanation
            var currentExplanation = await wordExplanationRepository.GetFirstOrDefaultAsync(we => we.Id == wordExplanationId);
            if (currentExplanation == null)
            {
                logger.LogWarning($"Word explanation not found for refresh - WordExplanationId: {wordExplanationId}");
                throw new BusinessException("Word explanation not found");
            }

            // 1b. Verify the caller actually owns this word before doing any LLM work.
            var userWord = await userWordRepository.GetFirstOrDefaultAsync(uw =>
                uw.UserId == userId &&
                uw.WordCollectionId == currentExplanation.WordCollectionId);
            if (userWord == null)
            {
                logger.LogWarning($"User word not found for refresh - UserId: {userId}, WordCollectionId: {currentExplanation.WordCollectionId}");
                throw new BusinessException("User word not found");
            }

            // 2. Get all existing explanations for this word
            var existingExplanations = await wordExplanationRepository.GetListAsync(we =>
                we.WordCollectionId == currentExplanation.WordCollectionId &&
                we.LearningLanguage == currentExplanation.LearningLanguage &&
                we.ExplanationLanguage == currentExplanation.ExplanationLanguage);

            // 3. Extract used model names
            var usedModels = existingExplanations
                .Select(e => e.ProviderModelName)
                .Where(m => !string.IsNullOrEmpty(m))
                .ToHashSet();

            logger.LogInformation($"Found {usedModels.Count} existing models for word '{currentExplanation.WordText}': {string.Join(", ", usedModels)}");

            // 4. Find all suitable unused agents
            var availableAgents = configurationService.Agents;

            // Filter to get only agents that haven't been used yet
            var unusedAgents = availableAgents
                .Where(agent => !usedModels.Contains($"{agent.Provider}:{agent.ModelName}"))
                .ToList();

            if (!unusedAgents.Any())
            {
                logger.LogWarning($"All available models have been used for word '{currentExplanation.WordText}'");
                throw new InvalidOperationException("All available models have been used for this word");
            }

            // 5. Get language names for AI prompt
            var learningLangName = configurationService.GetLanguageName(currentExplanation.LearningLanguage);
            var explanationLangName = configurationService.GetLanguageName(currentExplanation.ExplanationLanguage);

            if (learningLangName == null || explanationLangName == null)
            {
                logger.LogError($"Language names not found - LearningLanguage: {currentExplanation.LearningLanguage}, ExplanationLanguage: {currentExplanation.ExplanationLanguage}");
                throw new InvalidOperationException("Language names not found");
            }

            // 6. Try to generate explanation with unused agents one by one
            string newMarkdown = string.Empty;
            Agent? successfullyUsedAgent = null;

            foreach (var agent in unusedAgents)
            {
                try
                {
                    logger.LogInformation($"Attempting refresh with agent: {agent.Provider}:{agent.ModelName}");

                    newMarkdown = await languageService.GetMarkdownExplanationAsync(
                        agent,
                        currentExplanation.WordText,
                        learningLangName,
                        explanationLangName);

                    if (!string.IsNullOrWhiteSpace(newMarkdown))
                    {
                        successfullyUsedAgent = agent;
                        break; // Success!
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh explanation with {Provider}:{Model}. Will try next available agent.",
                        agent.Provider, agent.ModelName);
                    // Continue to next agent
                }
            }

            if (successfullyUsedAgent == null || string.IsNullOrWhiteSpace(newMarkdown))
            {
                logger.LogError($"Failed to refresh explanation for '{currentExplanation.WordText}' with any of the {unusedAgents.Count} unused agents.");
                throw new InvalidOperationException("Failed to refresh explanation with any available unused agent.");
            }

            // 7. Create new explanation record
            var newExplanation = new WordExplanation
            {
                WordCollectionId = currentExplanation.WordCollectionId,
                WordText = currentExplanation.WordText,
                LearningLanguage = currentExplanation.LearningLanguage,
                ExplanationLanguage = currentExplanation.ExplanationLanguage,
                MarkdownExplanation = newMarkdown,
                ProviderModelName = $"{successfullyUsedAgent.Provider}:{successfullyUsedAgent.ModelName}",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };

            var newExplanationId = await wordExplanationRepository.InsertReturnIdentityAsync(newExplanation);
            newExplanation.Id = newExplanationId;

            logger.LogInformation($"Successfully created new explanation - ID: {newExplanationId}, Word: '{newExplanation.WordText}', Provider: {newExplanation.ProviderModelName}");

            return newExplanation;
        }

        public async Task<IList<WordExplanation>> MemoriesAsync(int userId, string localTimezone)
        {
            var earliest = await userWordRepository.GetFirstOrDefaultAsync(uw => uw.UserId == userId, "CreatedAt");
            if (earliest == null) return new List<WordExplanation>();

            var memories = new List<WordExplanation>();
            var timestampList = _GetTimestamps(earliest.CreatedAt, localTimezone);

            foreach (var timestamp in timestampList)
            {
                var wordExplanation = await db.Queryable<WordExplanation>()
                    .RightJoin<UserWord>((we, uw) => we.Id == uw.WordExplanationId)
                    .Where((we, uw) => uw.UserId == userId && uw.CreatedAt >= timestamp && uw.CreatedAt < timestamp + 86400)
                    .OrderBy((we, uw) => uw.CreatedAt)
                    .Select((we, uw) => new WordExplanation()
                    {
                        CreatedAt = uw.CreatedAt,
                    }, true)
                    .FirstAsync();

                if (wordExplanation != null)
                {
                    memories.Add(wordExplanation);
                }
            }

            return memories;
        }

        public async Task<IList<WordExplanation>> MemoriesOnAsync(int userId, string localTimezone, string yyyyMMdd)
        {
            // Get the specified time zone
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(localTimezone);
            // Parse the date string to a DateTime object
            var dayStartTimestamp = DateTimeOffset.ParseExact(yyyyMMdd, "yyyyMMdd", CultureInfo.InvariantCulture)
                .GetDayStartTimestamp(timeZone);

            var words = await db.Queryable<WordExplanation>()
                .RightJoin<UserWord>((we, uw) => we.Id == uw.WordExplanationId)
                .Where((we, uw) => uw.UserId == userId && uw.CreatedAt >= dayStartTimestamp && uw.CreatedAt < dayStartTimestamp + 86400)
                .OrderBy((we, uw) => uw.CreatedAt)
                .Select((we, uw) => new WordExplanation()
                {
                    CreatedAt = uw.CreatedAt,
                }, true)
                .ToListAsync();

            return words;
        }

        private async Task<UserWord> _HandleUserWord(int userId, WordExplanation explanationToReturn)
        {
            var userWord = await userWordRepository.GetFirstOrDefaultAsync(uw =>
                uw.UserId == userId && uw.WordExplanationId == explanationToReturn.Id);

            var currentTime = DateTime.UtcNow.ToUnixTimeSeconds();
            if (userWord == null)
            {
                var newUserWord = new UserWord
                {
                    UserId = userId,
                    WordCollectionId = explanationToReturn.WordCollectionId,
                    WordExplanationId = explanationToReturn.Id,
                    Status = 0,
                    CreatedAt = currentTime,
                    UpdatedAt = currentTime
                };
                await userWordRepository.InsertAsync(newUserWord);
                return newUserWord;
            }

            // Word exists - update UpdatedAt to move it to front of timeline
            userWord.UpdatedAt = currentTime;
            await userWordRepository.UpdateAsync(userWord);

            return userWord;
        }


        private async Task<WordExplanation> _HandleExplanation(string wordText, string learningLanguageCode, string explanationLanguageCode,
            long wordCollectionId, ExplanationResult? aiResult)
        {
            var aiSuccess = aiResult != null && aiResult.IsSuccess && !string.IsNullOrWhiteSpace(aiResult.Markdown);

            var explanation = await wordExplanationRepository.GetFirstOrDefaultAsync(we =>
                we.WordCollectionId == wordCollectionId &&
                we.LearningLanguage == learningLanguageCode &&
                we.ExplanationLanguage == explanationLanguageCode);

            if (explanation is not null)
            {
                // A Ready row is authoritative; a Pending row with no fresh AI result stays pending.
                if (explanation.Status != ExplanationStatus.Pending || !aiSuccess)
                {
                    return explanation;
                }

                // Pending row + fresh AI success: fill it in place rather than returning it stale.
                explanation.MarkdownExplanation = aiResult!.Markdown!;
                explanation.ProviderModelName = aiResult.ModelName;
                explanation.Status = ExplanationStatus.Ready;
                await wordExplanationRepository.UpdateAsync(explanation);
                logger.LogInformation("Filled pre-existing pending explanation in place for word '{WordText}'", wordText);
                return explanation;
            }

            // No existing row: insert a Ready explanation from the AI result, or a Pending placeholder
            // when the AI is unavailable so the add-word transaction still commits the user's word.
            if (aiSuccess)
            {
                logger.LogInformation("Reusing AI result for word '{WordText}' from provider {Provider}",
                    wordText, aiResult!.ModelName);
            }
            else
            {
                logger.LogWarning("Persisting pending explanation for word '{WordText}' (AI unavailable: {ErrorMessage})",
                    wordText, aiResult?.ErrorMessage ?? "null result");
            }

            var newExplanation = new WordExplanation
            {
                WordCollectionId = wordCollectionId,
                WordText = wordText,
                LearningLanguage = learningLanguageCode,
                ExplanationLanguage = explanationLanguageCode,
                MarkdownExplanation = aiSuccess ? aiResult!.Markdown! : BuildPendingPlaceholder(wordText),
                ProviderModelName = aiSuccess ? aiResult!.ModelName : null,
                Status = aiSuccess ? ExplanationStatus.Ready : ExplanationStatus.Pending,
                CreatedAt = DateTime.UtcNow.ToUnixTimeSeconds(),
            };
            var newExplanationId = await wordExplanationRepository.InsertReturnIdentityAsync(newExplanation);
            newExplanation.Id = newExplanationId;
            return newExplanation;
        }

        /// <summary>
        /// Placeholder markdown stored on a Pending explanation. Valid markdown so existing clients render
        /// it safely; the frontend can switch on the numeric <see cref="WordExplanation.Status"/> field.
        /// </summary>
        private static string BuildPendingPlaceholder(string word)
            => $"**{word}**\n\n_The explanation is being generated and will appear shortly._";

        /// <summary>
        /// Fill a Pending explanation with a fresh, successful AI result. Runs the same canonical-word /
        /// typo-correction path as the online add flow (<see cref="EnsureCanonicalWordAsync"/>), then updates
        /// the row in place inside a transaction. Shared by inline re-add retry and the background worker.
        /// </summary>
        internal async Task FillPendingExplanationAsync(WordExplanation pending, ExplanationResult aiResult)
        {
            await TransactionHelper.ExecuteInTransactionAsync(db, async () =>
            {
                var canonical = ExtractCanonicalWordFromMarkdown(aiResult.Markdown ?? string.Empty);
                if (string.IsNullOrWhiteSpace(canonical)) canonical = pending.WordText;

                var originalCollectionId = pending.WordCollectionId;
                var canonicalCollectionId = await EnsureCanonicalWordAsync(pending.WordText, canonical);

                if (canonicalCollectionId != originalCollectionId)
                {
                    // Late typo correction merged this word into a different, already-existing collection.
                    // Guard the (WordCollectionId, LearningLanguage, ExplanationLanguage, ProviderModelName)
                    // unique index: if a Ready explanation already exists for the canonical tuple, relink the
                    // user words to it and drop the pending row rather than updating it into a duplicate.
                    var existingReady = await wordExplanationRepository.GetFirstOrDefaultAsync(we =>
                        we.WordCollectionId == canonicalCollectionId &&
                        we.LearningLanguage == pending.LearningLanguage &&
                        we.ExplanationLanguage == pending.ExplanationLanguage &&
                        we.Status == ExplanationStatus.Ready);

                    if (existingReady != null)
                    {
                        await userWordRepository.UpdateAsync(
                            uw => new UserWord { WordExplanationId = existingReady.Id, WordCollectionId = canonicalCollectionId },
                            uw => uw.WordExplanationId == pending.Id);
                        await wordExplanationRepository.DeleteAsync(pending);
                        logger.LogInformation("Pending explanation {PendingId} superseded by existing ready explanation {ReadyId} for word '{WordText}'",
                            pending.Id, existingReady.Id, canonical);

                        // Reflect the surviving row back to the caller.
                        pending.Id = existingReady.Id;
                        pending.WordCollectionId = existingReady.WordCollectionId;
                        pending.WordText = existingReady.WordText;
                        pending.MarkdownExplanation = existingReady.MarkdownExplanation;
                        pending.ProviderModelName = existingReady.ProviderModelName;
                        pending.Status = ExplanationStatus.Ready;
                        return;
                    }

                    // No conflicting row: relink user words to the canonical collection before updating.
                    await userWordRepository.UpdateAsync(
                        uw => new UserWord { WordCollectionId = canonicalCollectionId },
                        uw => uw.WordExplanationId == pending.Id);
                }

                pending.WordCollectionId = canonicalCollectionId;
                pending.WordText = canonical;
                pending.MarkdownExplanation = aiResult.Markdown ?? string.Empty;
                pending.ProviderModelName = aiResult.ModelName;
                pending.Status = ExplanationStatus.Ready;
                await wordExplanationRepository.UpdateAsync(pending);
                logger.LogInformation("Filled pending explanation {Id} for word '{WordText}' from provider {Provider}",
                    pending.Id, canonical, aiResult.ModelName);
            }, logger);
        }

        /// <summary>
        /// Background-worker entry point: fill up to <paramref name="batchSize"/> pending explanations
        /// (oldest first) whose <see cref="WordExplanation.RetryCount"/> is below <paramref name="maxRetryCount"/>.
        /// On a successful LLM call the row is filled; on failure its RetryCount is incremented so a
        /// permanently-unexplainable word eventually stops auto-retrying. Returns the number of rows filled.
        /// </summary>
        public async Task<int> RetryPendingExplanationsAsync(int batchSize = 20, int maxRetryCount = 20)
        {
            var pendingRows = await wordExplanationRepository.GetListAsync(
                batchSize,
                we => we.Status == ExplanationStatus.Pending && we.RetryCount < maxRetryCount,
                we => we.CreatedAt,
                true);

            if (pendingRows.Count == 0)
            {
                return 0;
            }

            logger.LogInformation("ExplanationRetry: processing {Count} pending explanation(s)", pendingRows.Count);

            var filled = 0;
            foreach (var row in pendingRows)
            {
                var learningLanguageName = configurationService.GetLanguageName(row.LearningLanguage);
                var explanationLanguageName = configurationService.GetLanguageName(row.ExplanationLanguage);
                if (learningLanguageName == null || explanationLanguageName == null)
                {
                    logger.LogWarning("ExplanationRetry: skipping row {Id}; unknown language code(s) learning={Learning} explanation={Explanation}",
                        row.Id, row.LearningLanguage, row.ExplanationLanguage);
                    continue;
                }

                var aiResult = await InvokeAiServiceAsync(row.WordText, explanationLanguageName, learningLanguageName);
                if (aiResult.IsSuccess && !string.IsNullOrWhiteSpace(aiResult.Markdown))
                {
                    await FillPendingExplanationAsync(row, aiResult);
                    filled++;
                }
                else
                {
                    row.RetryCount++;
                    await wordExplanationRepository.UpdateAsync(row);
                    logger.LogWarning("ExplanationRetry: fill failed for word '{WordText}', RetryCount now {RetryCount}",
                        row.WordText, row.RetryCount);
                }
            }

            logger.LogInformation("ExplanationRetry: filled {Filled}/{Total} pending explanation(s)", filled, pendingRows.Count);
            return filled;
        }
        // ...existing code...

        /// <summary>
        /// 处理单词规范化：如果AI纠正了用户输入，确保WordCollection只保留标准词。
        /// </summary>
        /// <param name="userInput">用户原始输入</param>
        /// <param name="canonicalWord">AI返回的标准词</param>
        /// <returns>标准词的WordCollection.Id</returns>
        internal async Task<long> EnsureCanonicalWordAsync(string userInput, string canonicalWord)
        {
            var currentTime = DateTime.UtcNow.ToUnixTimeSeconds();
            userInput = NormalizeWord(userInput);
            canonicalWord = NormalizeWord(canonicalWord);

            // 查找错词和标准词
            var wrongEntry = await wordCollectionRepository.GetFirstOrDefaultAsync(wc => wc.WordText == userInput && wc.DeletedAt == null);
            var correctEntry = await wordCollectionRepository.GetFirstOrDefaultAsync(wc => wc.WordText == canonicalWord && wc.DeletedAt == null);

            if (correctEntry != null)
            {
                // Canonical word exists, soft-delete the typo entry
                if (wrongEntry != null && wrongEntry.WordText != canonicalWord)
                {
                    wrongEntry.DeletedAt = currentTime;
                    await wordCollectionRepository.UpdateAsync(wrongEntry);
                }
                return correctEntry.Id;
            }
            else if (wrongEntry != null && wrongEntry.WordText != canonicalWord)
            {
                // Canonical word does not exist, update typo entry to canonical word
                wrongEntry.WordText = canonicalWord;
                wrongEntry.UpdatedAt = currentTime;
                await wordCollectionRepository.UpdateAsync(wrongEntry);
                return wrongEntry.Id;
            }
            else if (wrongEntry != null)
            {
                // User input is already the canonical word
                wrongEntry.QueryCount++;
                wrongEntry.UpdatedAt = currentTime;
                await wordCollectionRepository.UpdateAsync(wrongEntry);
                return wrongEntry.Id;
            }
            else
            {
                // 两者都不存在，插入标准词（并发时尝试插入失败则重查）
                return await _AddWordCollection(canonicalWord, currentTime);
            }
        }

        // Modified original _HandleWordCollection, added canonicalWord parameter
        private async Task<long> _HandleWordCollection(string userInput, string? canonicalWord = null)
        {
            // If canonicalWord is not specified, default to userInput
            canonicalWord ??= userInput;
            return await EnsureCanonicalWordAsync(userInput, canonicalWord);
        }

        /// <summary>
        /// Normalize word text for consistent comparisons and storage
        /// </summary>
        private static string NormalizeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;
            return word.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Call AI with limited retries and return ExplanationResult. This wraps the ILanguageService call.
        /// </summary>
        private async Task<ExplanationResult> InvokeAiServiceAsync(string inputText, string nativeLanguageName, string targetLanguageName)
        {
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Starting AI call (invoke+fallback) for word '{InputText}', native: {NativeLanguage}, target: {TargetLanguage}",
                inputText, nativeLanguageName, targetLanguageName);

            try
            {
                var result = await languageService.GetMarkdownExplanationWithFallbackAsync(inputText, nativeLanguageName, targetLanguageName);
                stopwatch.Stop();

                if (result.IsSuccess)
                {
                    logger.LogInformation("AI call succeeded in {ElapsedMs}ms for word '{InputText}', provider: {ProviderModel}",
                        stopwatch.ElapsedMilliseconds, inputText, result.ModelName);
                }
                else
                {
                    logger.LogWarning("AI call failed in {ElapsedMs}ms for word '{InputText}', error: {ErrorMessage}",
                        stopwatch.ElapsedMilliseconds, inputText, result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogWarning(ex, "AI call threw exception after {ElapsedMs}ms for word '{InputText}'",
                    stopwatch.ElapsedMilliseconds, inputText);
                return new ExplanationResult { IsSuccess = false, Markdown = string.Empty, ErrorMessage = ex.Message };
            }
        }

        private async Task<long> _AddWordCollection(string wordText, long currentTime)
        {
            // Ensure the word text is normalized before insert
            var normalized = NormalizeWord(wordText);
            var newCollectionWord = new WordCollection
            {
                WordText = normalized,
                QueryCount = 1,
                CreatedAt = currentTime,
                UpdatedAt = currentTime
            };

            try
            {
                return await wordCollectionRepository.InsertReturnIdentityAsync(newCollectionWord);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Insert WordCollection failed for '{WordText}'; attempting to re-query existing record", normalized);
                // Likely a duplicate-key caused by concurrent insert; try to find existing record
                var existing = await wordCollectionRepository.GetFirstOrDefaultAsync(wc => wc.WordText == normalized && wc.DeletedAt == null);
                if (existing != null)
                {
                    return existing.Id;
                }
                // If not found, rethrow original exception for visibility
                throw;
            }
        }

        private async Task _DeleteUserWordWithCleanup(UserWord userWord)
        {
            try
            {
                await db.AsTenant().BeginTranAsync();

                // Delete the user word
                await userWordRepository.DeleteAsync(userWord);

                // Perform cleanup check
                await _CleanupOrphanedRecords(userWord.WordExplanationId);

                await db.AsTenant().CommitTranAsync();
            }
            catch (Exception ex)
            {
                await db.AsTenant().RollbackTranAsync();
                logger.LogError(ex, $"Error in _DeleteUserWordWithCleanup for user {userWord.UserId}, wordExplanationId {userWord.WordExplanationId}: Rollback.");
                throw;
            }
        }

        private async Task _CleanupOrphanedRecords(long wordExplanationId)
        {
            const int cleanupThresholdMinutes = 5;

            // Get the word explanation
            var wordExplanation = await wordExplanationRepository.GetFirstOrDefaultAsync(we => we.Id == wordExplanationId);
            if (wordExplanation == null)
            {
                return;
            }

            // Check if any other users still reference this word explanation
            var otherUserWordExists = await userWordRepository.GetFirstOrDefaultAsync(uw => uw.WordExplanationId == wordExplanationId);
            if (otherUserWordExists != null)
            {
                // Other users still use this explanation, don't delete
                return;
            }

            // Get the word collection
            var wordCollection = await wordCollectionRepository.GetFirstOrDefaultAsync(wc => wc.Id == wordExplanation.WordCollectionId);
            if (wordCollection == null)
            {
                // Word collection doesn't exist, just delete the explanation
                await wordExplanationRepository.DeleteAsync(wordExplanation);
                logger.LogInformation($"Cleaned up orphaned word explanation: {wordExplanation.WordText} (ID: {wordExplanationId})");
                return;
            }

            // Check if both records were created within the threshold time
            var timeDifferenceSeconds = Math.Abs(wordExplanation.CreatedAt - wordCollection.CreatedAt);
            var timeDifferenceMinutes = timeDifferenceSeconds / 60.0;

            if (timeDifferenceMinutes <= cleanupThresholdMinutes)
            {
                // Check if any other word explanations reference this word collection
                var otherExplanationExists = await wordExplanationRepository.GetFirstOrDefaultAsync(we =>
                    we.WordCollectionId == wordCollection.Id && we.Id != wordExplanationId);

                if (otherExplanationExists == null)
                {
                    // No other explanations reference this collection, safe to delete both
                    await wordExplanationRepository.DeleteAsync(wordExplanation);
                    await _CleanupQueryHistory(wordCollection.Id);
                    await wordCollectionRepository.DeleteAsync(wordCollection);
                    logger.LogInformation($"Cleaned up orphaned word collection, explanation, and query history: {wordCollection.WordText} (Collection ID: {wordCollection.Id}, Explanation ID: {wordExplanationId})");
                }
                else
                {
                    // Other explanations exist for this collection, only delete the explanation
                    await wordExplanationRepository.DeleteAsync(wordExplanation);
                    logger.LogInformation($"Cleaned up orphaned word explanation: {wordExplanation.WordText} (ID: {wordExplanationId})");
                }
            }
        }

        private async Task _CleanupQueryHistory(long wordCollectionId)
        {
            var deletedCount = await queryHistoryRepository.DeleteReturnRowsAsync(qh => qh.WordCollectionId == wordCollectionId);

            if (deletedCount > 0)
            {
                logger.LogInformation($"Cleaned up {deletedCount} query history records for word collection ID: {wordCollectionId}");
            }
        }

        private static long[] _GetTimestamps(long initialUnixTimestamp, string timeZoneId)
        {
            var targetTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var startDate = initialUnixTimestamp.ToDateTimeOffset(targetTimeZone);
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, targetTimeZone);

            var timestamps = new List<long>();

            // Add spaced repetition intervals optimized for vocabulary learning
            var dayIntervals = new[] { 0, 1, 3, 7, 14, 30, 60, 90, 180, 365 }; // today, yesterday, 3 days, 1 week, 2 weeks, 1 month, 2 months, 3 months, 6 months, 1 year

            foreach (var daysAgo in dayIntervals)
            {
                var targetDate = today.AddDays(-daysAgo);

                // Only include if the target date is after the user started using the app
                if (targetDate >= startDate)
                {
                    timestamps.Add(targetDate.GetDayStartTimestamp(targetTimeZone));
                }
            }

            // Note: today and yesterday are now included in dayIntervals with proper filtering

            return timestamps.OrderBy(t => t).ToArray();
        }

        public async Task<Models.DTOs.Vocabulary.ExplanationsResponse> GetAllExplanationsForWordAsync(
            int userId,
            long wordCollectionId,
            string learningLanguage,
            string explanationLanguage)
        {
            // Get all explanations for this word
            var explanations = await wordExplanationRepository.GetListAsync(we =>
                we.WordCollectionId == wordCollectionId &&
                we.LearningLanguage == learningLanguage &&
                we.ExplanationLanguage == explanationLanguage);

            // Order by creation time (newest first)
            var orderedExplanations = explanations
                .OrderByDescending(e => e.CreatedAt)
                .ToList();

            logger.LogInformation($"Found {orderedExplanations.Count} explanations for word collection {wordCollectionId}");

            // Get user's default explanation ID
            var userWord = await userWordRepository.GetFirstOrDefaultAsync(uw =>
                uw.UserId == userId &&
                uw.WordCollectionId == wordCollectionId);

            return new Models.DTOs.Vocabulary.ExplanationsResponse
            {
                Explanations = orderedExplanations,
                UserDefaultExplanationId = userWord?.WordExplanationId
            };
        }

        public async Task SwitchUserDefaultExplanationAsync(
            int userId,
            long wordCollectionId,
            long newExplanationId)
        {
            // Validate: explanation exists and matches word collection
            var explanation = await wordExplanationRepository.GetFirstOrDefaultAsync(we => we.Id == newExplanationId);
            if (explanation == null)
            {
                logger.LogWarning($"Explanation not found - ExplanationId: {newExplanationId}");
                throw new BusinessException("Explanation not found");
            }

            if (explanation.WordCollectionId != wordCollectionId)
            {
                logger.LogWarning($"Explanation {newExplanationId} does not belong to word collection {wordCollectionId}");
                throw new InvalidOperationException("Explanation does not belong to this word");
            }

            // Update user's default
            var userWord = await userWordRepository.GetFirstOrDefaultAsync(uw =>
                uw.UserId == userId &&
                uw.WordCollectionId == wordCollectionId);

            if (userWord == null)
            {
                logger.LogWarning($"User word not found - UserId: {userId}, WordCollectionId: {wordCollectionId}");
                throw new BusinessException("User word not found");
            }

            userWord.WordExplanationId = newExplanationId;
            userWord.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await userWordRepository.UpdateAsync(userWord);

            logger.LogInformation($"Switched default explanation for user {userId}, word collection {wordCollectionId} to explanation {newExplanationId}");
        }
    }
}
