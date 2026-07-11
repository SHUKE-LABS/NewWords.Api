using Api.Framework;
using Api.Framework.Models;
using NewWords.Api.Constants;
using NewWords.Api.Entities;
using NewWords.Api.Models.DTOs.Stories;
using NewWords.Api.Services.interfaces;
using SqlSugar;
using LLM;

namespace NewWords.Api.Services
{
    public class StoryService(
        ISqlSugarClient db,
        ILanguageService languageService,
        IConfigurationService configurationService,
        ILogger<StoryService> logger,
        IRepositoryBase<Story> storyRepository,
        IRepositoryBase<StoryWord> storyWordRepository,
        IRepositoryBase<UserFavoriteStory> userFavoriteStoryRepository,
        IRepositoryBase<UserStoryRead> userStoryReadRepository)
        : IStoryService
    {
        public async Task<PageData<StoryDto>> GetUserStoriesAsync(int userId, int pageSize, int pageNumber)
        {
            RefAsync<int> totalCount = 0;
            var storyDtos = await db.Queryable<Story>()
                .LeftJoin<UserFavoriteStory>((s, ufs) => s.Id == ufs.StoryId && ufs.UserId == userId)
                .LeftJoin<UserStoryRead>((s, ufs, usr) => s.Id == usr.StoryId && usr.UserId == userId)
                .Where(s => s.UserId == userId)
                .OrderBy(s => s.CreatedAt, OrderByType.Desc)
                .Select((s, ufs, usr) => new StoryDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    Content = s.Content,
                    StoryWords = s.StoryWords,
                    LearningLanguage = s.LearningLanguage,
                    FirstReadAt = usr.Id != 0 ? usr.FirstReadAt : (long?)null,
                    FavoriteCount = s.FavoriteCount,
                    IsFavorited = ufs.Id != 0,
                    ProviderModelName = s.ProviderModelName,
                    CreatedAt = s.CreatedAt
                })
                .ToPageListAsync(pageNumber, pageSize, totalCount);

            return new PageData<StoryDto>
            {
                DataList = storyDtos,
                TotalCount = totalCount,
                PageIndex = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<PageData<StoryDto>> GetStorySquareAsync(int userId, int pageSize, int pageNumber)
        {
            RefAsync<int> totalCount = 0;
            var storyDtos = await db.Queryable<Story>()
                .LeftJoin<UserFavoriteStory>((s, ufs) => s.Id == ufs.StoryId && ufs.UserId == userId)
                .LeftJoin<UserStoryRead>((s, ufs, usr) => s.Id == usr.StoryId && usr.UserId == userId)
                .Where(s => s.UserId != userId)
                .OrderBy(s => s.FavoriteCount, OrderByType.Desc)
                .OrderBy(s => s.CreatedAt, OrderByType.Desc)
                .Select((s, ufs, usr) => new StoryDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    Content = s.Content,
                    StoryWords = s.StoryWords,
                    LearningLanguage = s.LearningLanguage,
                    FirstReadAt = usr.Id != 0 ? usr.FirstReadAt : (long?)null,
                    FavoriteCount = s.FavoriteCount,
                    IsFavorited = ufs.Id != 0,
                    ProviderModelName = s.ProviderModelName,
                    CreatedAt = s.CreatedAt
                })
                .ToPageListAsync(pageNumber, pageSize, totalCount);

            return new PageData<StoryDto>
            {
                DataList = storyDtos,
                TotalCount = totalCount,
                PageIndex = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task<PageData<StoryDto>> GetUserFavoriteStoriesAsync(int userId, int pageSize, int pageNumber)
        {
            RefAsync<int> totalCount = 0;
            var storyDtos = await db.Queryable<Story>()
                .InnerJoin<UserFavoriteStory>((s, ufs) => s.Id == ufs.StoryId)
                .LeftJoin<UserStoryRead>((s, ufs, usr) => s.Id == usr.StoryId && usr.UserId == userId)
                .Where((s, ufs) => ufs.UserId == userId)
                .OrderBy((s, ufs) => ufs.CreatedAt, OrderByType.Desc)
                .Select((s, ufs, usr) => new StoryDto
                {
                    Id = s.Id,
                    UserId = s.UserId,
                    Content = s.Content,
                    StoryWords = s.StoryWords,
                    LearningLanguage = s.LearningLanguage,
                    FirstReadAt = usr.Id != 0 ? usr.FirstReadAt : (long?)null,
                    FavoriteCount = s.FavoriteCount,
                    IsFavorited = true, // Always true since we're querying user's favorites
                    ProviderModelName = s.ProviderModelName,
                    CreatedAt = s.CreatedAt
                })
                .ToPageListAsync(pageNumber, pageSize, totalCount);

            return new PageData<StoryDto>
            {
                DataList = storyDtos,
                TotalCount = totalCount,
                PageIndex = pageNumber,
                PageSize = pageSize
            };
        }

        public async Task MarkStoryAsReadAsync(int userId, long storyId)
        {
            // Check if story exists (remove ownership restriction)
            var story = await storyRepository.GetFirstOrDefaultAsync(s => s.Id == storyId);
            if (story == null)
            {
                logger.LogWarning($"Story not found - StoryId: {storyId}");
                return;
            }

            // Check if user has already read this story
            var existingRead = await userStoryReadRepository.GetFirstOrDefaultAsync(
                usr => usr.UserId == userId && usr.StoryId == storyId);

            if (existingRead == null)
            {
                // Create new read record
                var userStoryRead = new UserStoryRead
                {
                    UserId = userId,
                    StoryId = storyId,
                    FirstReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                await userStoryReadRepository.InsertAsync(userStoryRead);
                logger.LogInformation($"Story marked as read - StoryId: {storyId}, UserId: {userId}");
            }
        }

        public async Task<bool> ToggleFavoriteAsync(int userId, long storyId)
        {
            try
            {
                await db.AsTenant().BeginTranAsync();

                var story = await storyRepository.GetFirstOrDefaultAsync(s => s.Id == storyId);
                if (story == null)
                {
                    logger.LogWarning($"Story not found for favorite toggle - StoryId: {storyId}");
                    await db.AsTenant().RollbackTranAsync();
                    return false;
                }

                // Check if user has already favorited this story
                var existingFavorite = await userFavoriteStoryRepository.GetFirstOrDefaultAsync(
                    ufs => ufs.UserId == userId && ufs.StoryId == storyId);

                bool isFavorited;
                if (existingFavorite != null)
                {
                    // Remove favorite
                    await userFavoriteStoryRepository.DeleteAsync(existingFavorite);
                    story.FavoriteCount = Math.Max(0, story.FavoriteCount - 1);
                    isFavorited = false;
                    logger.LogInformation($"Story unfavorited - StoryId: {storyId}, UserId: {userId}");
                }
                else
                {
                    // Add favorite
                    var newFavorite = new UserFavoriteStory
                    {
                        UserId = userId,
                        StoryId = storyId,
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    await userFavoriteStoryRepository.InsertAsync(newFavorite);
                    story.FavoriteCount++;
                    isFavorited = true;
                    logger.LogInformation($"Story favorited - StoryId: {storyId}, UserId: {userId}");
                }

                await storyRepository.UpdateAsync(story);
                await db.AsTenant().CommitTranAsync();

                return isFavorited;
            }
            catch (Exception ex)
            {
                await db.AsTenant().RollbackTranAsync();
                logger.LogError(ex, $"Error toggling favorite for StoryId: {storyId}, UserId: {userId}");
                throw;
            }
        }

        public async Task<Story?> GenerateStoryForUserAsync(int userId)
        {
            try
            {
                // Get user's recent vocabulary words
                var recentWords = await GetUserRecentWordsAsync(userId);
                if (recentWords.Count < StoryConstants.MinWordsForStory)
                {
                    logger.LogInformation($"User {userId} doesn't have enough recent words for story generation. Found: {recentWords.Count}");
                    return null;
                }

                // Get user's learning language
                var user = await db.Queryable<User>().FirstAsync(u => u.Id == userId);
                if (user == null)
                {
                    logger.LogWarning($"User not found - UserId: {userId}");
                    return null;
                }

                // Generate story using AI
                var (storyContent, modelName) = await GenerateStoryContentAsync(recentWords, user.CurrentLearningLanguage, user.NativeLanguage);
                if (string.IsNullOrWhiteSpace(storyContent))
                {
                    logger.LogError($"Failed to generate story content for user {userId}");
                    return null;
                }

                // Create story entity
                var story = new Story
                {
                    UserId = userId,
                    Content = storyContent,
                    StoryWords = string.Join(", ", recentWords.Select(w => w.WordText)),
                    LearningLanguage = user.CurrentLearningLanguage,
                    ProviderModelName = modelName,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                // Save story
                var storyId = await storyRepository.InsertReturnIdentityAsync(story);
                story.Id = storyId;

                // Save story-word relationships
                await SaveStoryWordsAsync(storyId, recentWords);

                logger.LogInformation($"Story generated successfully for user {userId}, StoryId: {storyId}");
                return story;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error generating story for user {userId}");
                return null;
            }
        }

        public async Task<List<Story>> GenerateStoryWithWordsAsync(int userId, List<string>? customWords = null, string? learningLanguage = null)
        {
            try
            {
                // Get user information
                var user = await db.Queryable<User>().FirstAsync(u => u.Id == userId);
                if (user == null)
                {
                    logger.LogWarning($"User not found - UserId: {userId}");
                    return new List<Story>();
                }

                // Determine the target language
                var targetLanguage = learningLanguage ?? user.CurrentLearningLanguage;

                // Prepare word list for story generation
                List<string> wordsForStory;
                List<WordCollection>? wordCollections = null;
                bool usingRecentWords;

                if (customWords != null && customWords.Any())
                {
                    // Use custom words provided by user (don't limit to MaxWordsPerStory here - let batch generation handle it)
                    wordsForStory = customWords.Where(w => !string.IsNullOrWhiteSpace(w))
                        .Select(w => w.Trim())
                        .ToList();

                    // Defense in depth: cap the number of custom words so a single request
                    // cannot trigger an unbounded number of sequential LLM calls. The primary
                    // guard is the [MaxLength] validation on GenerateStoryRequest.Words (400);
                    // this backstops callers that bypass model validation.
                    if (wordsForStory.Count > StoryConstants.MaxCustomWordsPerRequest)
                    {
                        logger.LogWarning(
                            "Rejecting story generation for user {UserId}: {WordCount} custom words exceeds the cap of {Cap}.",
                            userId, wordsForStory.Count, StoryConstants.MaxCustomWordsPerRequest);
                        return new List<Story>();
                    }

                    usingRecentWords = false;
                }
                else
                {
                    // Use recent vocabulary words
                    var recentWordCollections = await GetUserRecentWordsAsync(userId);
                    if (recentWordCollections.Count < StoryConstants.MinWordsForStory)
                    {
                        logger.LogInformation($"User {userId} doesn't have enough recent words for story generation. Found: {recentWordCollections.Count}");
                        return new List<Story>();
                    }
                    wordsForStory = recentWordCollections.Select(w => w.WordText).ToList();
                    wordCollections = recentWordCollections; // Save for StoryWords relationships
                    usingRecentWords = true;
                }

                if (wordsForStory.Count == 0)
                {
                    logger.LogInformation($"No valid words found for story generation for user {userId}");
                    return new List<Story>();
                }

                // Update LastStoryGenerationAt BEFORE generation if using recent words to prevent race conditions
                if (usingRecentWords)
                {
                    var generationStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    user.LastStoryGenerationAt = generationStartTime;
                    await db.Updateable(user).ExecuteCommandAsync();

                    logger.LogInformation($"Starting manual generation with recent words for user {userId} at timestamp {generationStartTime}");
                }

                // Use core batch generation method
                // Skip duplicate check for custom words, but keep it for recent words
                var skipDuplicateCheck = !usingRecentWords; // true for custom words, false for recent words
                var generatedStories = await GenerateStoriesFromWordBatchesAsync(userId, wordsForStory, targetLanguage, wordCollections, skipDuplicateCheck);

                logger.LogInformation($"Manual story generation completed for user {userId}: {generatedStories.Count} stories generated (using {(usingRecentWords ? "recent words" : "custom words")})");
                return generatedStories;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error generating stories for user {userId}");
                return new List<Story>();
            }
        }

        public async Task<List<Story>> GenerateMultipleStoriesForUserAsync(int userId)
        {
            try
            {
                // Get user information
                var user = await db.Queryable<User>().FirstAsync(u => u.Id == userId);
                if (user == null)
                {
                    logger.LogWarning($"User not found - UserId: {userId}");
                    return new List<Story>();
                }

                // Get words added since last story generation
                var lastGenerationTime = user.LastStoryGenerationAt ?? 0;
                var newWords = await GetUserWordsAddedSinceAsync(userId, lastGenerationTime);

                if (newWords.Count < StoryConstants.MinRecentWordsForAutomaticGeneration)
                {
                    logger.LogInformation($"User {userId} doesn't have enough new words for automatic story generation. Found: {newWords.Count}");
                    return new List<Story>();
                }

                // Set generation timestamp BEFORE starting generation to prevent race conditions
                var generationStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                user.LastStoryGenerationAt = generationStartTime;
                await db.Updateable(user).ExecuteCommandAsync();

                logger.LogInformation($"Starting automatic generation of {newWords.Count} words for user {userId} at timestamp {generationStartTime}");

                // Use core batch generation method
                // For automatic generation, always enable duplicate checking (default skipDuplicateCheck = false)
                var wordsForStory = newWords.Select(w => w.WordText).ToList();
                var generatedStories = await GenerateStoriesFromWordBatchesAsync(userId, wordsForStory, user.CurrentLearningLanguage, newWords);

                logger.LogInformation($"Automatic story generation completed for user {userId}: {generatedStories.Count} stories generated");
                return generatedStories;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error generating multiple stories for user {userId}");
                return new List<Story>();
            }
        }

        public async Task GenerateStoriesForEligibleUsersAsync()
        {
            try
            {
                // Get users who have new words since their last story generation
                var eligibleUsers = await db.Queryable<User>()
                    .Where(u => SqlFunc.Subqueryable<UserWord>()
                        .InnerJoin<WordExplanation>((uw, we) => uw.WordExplanationId == we.Id)
                        .Where((uw, we) => uw.UserId == u.Id &&
                                           uw.CreatedAt > (u.LastStoryGenerationAt ?? 0) &&
                                           we.LearningLanguage == u.CurrentLearningLanguage)
                        .Count() >= StoryConstants.MinRecentWordsForAutomaticGeneration)
                    .ToListAsync();

                logger.LogInformation($"Found {eligibleUsers.Count} eligible users for story generation");

                foreach (var user in eligibleUsers)
                {
                    try
                    {
                        await GenerateMultipleStoriesForUserAsync(user.Id);
                        // Add delay between users to respect API rate limits
                        await Task.Delay(2000);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"Failed to generate stories for user {user.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in batch story generation");
            }
        }

        private async Task<List<WordCollection>> GetUserRecentWordsAsync(int userId)
        {
            return await db.Queryable<WordCollection>()
                .InnerJoin<WordExplanation>((wc, we) => wc.Id == we.WordCollectionId)
                .InnerJoin<UserWord>((wc, we, uw) => we.Id == uw.WordExplanationId)
                .InnerJoin<User>((wc, we, uw, u) => uw.UserId == u.Id)
                .Where((wc, we, uw, u) => uw.UserId == userId &&
                                          uw.CreatedAt > (u.LastStoryGenerationAt ?? 0) &&
                                          we.LearningLanguage == u.CurrentLearningLanguage)
                .Select((wc, we, uw, u) => wc)
                .Distinct()
                .ToListAsync();
        }

        private async Task<List<WordCollection>> GetUserWordsAddedSinceAsync(int userId, long sinceTimestamp)
        {
            return await db.Queryable<WordCollection>()
                .InnerJoin<WordExplanation>((wc, we) => wc.Id == we.WordCollectionId)
                .InnerJoin<UserWord>((wc, we, uw) => we.Id == uw.WordExplanationId)
                .InnerJoin<User>((wc, we, uw, u) => uw.UserId == u.Id)
                .Where((wc, we, uw, u) => uw.UserId == userId &&
                                          uw.CreatedAt > sinceTimestamp &&
                                          we.LearningLanguage == u.CurrentLearningLanguage)
                .Select((wc, we, uw, u) => wc)
                .Distinct()
                .ToListAsync();
        }

        /// <summary>
        /// Core batch generation method shared by both automatic and manual story generation.
        /// Generates multiple stories from a list of words, handling batching and story creation.
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="words">List of words to generate stories from</param>
        /// <param name="learningLanguage">Target language for the stories</param>
        /// <param name="wordCollections">Optional word collections for StoryWords relationships</param>
        /// <param name="skipDuplicateCheck">Whether to skip duplicate checking (true for custom words, false for recent words)</param>
        /// <returns>List of generated stories</returns>
        private async Task<List<Story>> GenerateStoriesFromWordBatchesAsync(
            int userId,
            List<string> words,
            string learningLanguage,
            List<WordCollection>? wordCollections = null,
            bool skipDuplicateCheck = false)
        {
            var generatedStories = new List<Story>();

            if (words.Count == 0)
            {
                return generatedStories;
            }

            // Group words into batches for multiple stories
            var wordBatches = CreateWordBatches(words, StoryConstants.MaxWordsPerStory);

            logger.LogInformation($"Generating {wordBatches.Count} stories for user {userId} with {words.Count} words");

            foreach (var wordBatch in wordBatches)
            {
                try
                {
                    // Check for duplicate stories with same word list (skip for custom words)
                    var wordListString = string.Join(", ", wordBatch.OrderBy(w => w));
                    if (!skipDuplicateCheck)
                    {
                        var isDuplicate = await CheckForDuplicateStoryAsync(userId, wordListString);
                        if (isDuplicate)
                        {
                            logger.LogInformation($"Skipping duplicate story for user {userId} with words: {wordListString}");
                            continue;
                        }
                    }

                    // Generate story for this batch
                    var (storyContent, modelName) = await GenerateStoryContentWithWordsAsync(userId, wordBatch, learningLanguage);
                    if (string.IsNullOrWhiteSpace(storyContent))
                    {
                        logger.LogWarning($"Failed to generate story content for user {userId} with words: {string.Join(", ", wordBatch)}");
                        continue;
                    }

                    // Create story entity
                    var story = new Story
                    {
                        UserId = userId,
                        Content = storyContent,
                        StoryWords = wordListString,
                        LearningLanguage = learningLanguage,
                        ProviderModelName = modelName,
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    // Save story
                    var storyId = await storyRepository.InsertReturnIdentityAsync(story);
                    story.Id = storyId;

                    // Save story-word relationships if word collections are provided
                    if (wordCollections != null)
                    {
                        var relatedWordCollections = wordCollections.Where(w => wordBatch.Contains(w.WordText)).ToList();
                        await SaveStoryWordsAsync(storyId, relatedWordCollections);
                    }

                    generatedStories.Add(story);

                    // Add delay to respect API rate limits
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to generate story for user {userId} with batch: {string.Join(", ", wordBatch)}");
                }
            }

            logger.LogInformation($"Generated {generatedStories.Count} stories for user {userId}");
            return generatedStories;
        }

        private static List<List<string>> CreateWordBatches(List<string> words, int maxWordsPerBatch)
        {
            var batches = new List<List<string>>();

            for (int i = 0; i < words.Count; i += maxWordsPerBatch)
            {
                var batch = words.Skip(i).Take(maxWordsPerBatch).ToList();
                batches.Add(batch);
            }

            return batches;
        }

        private async Task<(string content, string? modelName)> GenerateStoryContentAsync(List<WordCollection> words, string learningLanguage, string nativeLanguage)
        {
            try
            {
                var wordList = string.Join(", ", words.Select(w => w.WordText));
                var languageName = configurationService.GetLanguageName(learningLanguage) ?? "English";
                var nativeLanguageName = configurationService.GetLanguageName(nativeLanguage) ?? "Chinese";

                // Use the dedicated story generation method with native language
                var result = await languageService.GetStoryWithFallbackAsync(wordList, languageName, nativeLanguageName);

                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Content))
                {
                    return (result.Content, result.ModelName);
                }

                logger.LogError($"AI story generation failed: {result.ErrorMessage}");
                return (string.Empty, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in AI story generation");
                return (string.Empty, null);
            }
        }

        private async Task<bool> CheckForDuplicateStoryAsync(int userId, string wordListString)
        {
            // Check if user has a recent story with the exact same word list
            var duplicateCheckTime = DateTimeOffset.UtcNow.AddHours(-StoryConstants.DuplicateCheckHours).ToUnixTimeSeconds();

            var existingStory = await storyRepository.GetFirstOrDefaultAsync(s =>
                s.UserId == userId &&
                s.StoryWords == wordListString &&
                s.CreatedAt > duplicateCheckTime);

            return existingStory != null;
        }

        private async Task<(string content, string? modelName)> GenerateStoryContentWithWordsAsync(int userId, List<string> words, string learningLanguage)
        {
            try
            {
                // Get user's native language
                var user = await db.Queryable<User>().FirstAsync(u => u.Id == userId);
                if (user == null)
                {
                    logger.LogWarning($"User not found for story generation - UserId: {userId}");
                    return (string.Empty, null);
                }

                var wordList = string.Join(", ", words);
                var languageName = configurationService.GetLanguageName(learningLanguage) ?? "English";
                var nativeLanguageName = configurationService.GetLanguageName(user.NativeLanguage) ?? "Chinese";

                // Use the dedicated story generation method with native language
                var result = await languageService.GetStoryWithFallbackAsync(wordList, languageName, nativeLanguageName);

                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Content))
                {
                    return (result.Content, result.ModelName);
                }

                logger.LogError($"AI story generation failed: {result.ErrorMessage}");
                return (string.Empty, null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in AI story generation");
                return (string.Empty, null);
            }
        }


        private async Task SaveStoryWordsAsync(long storyId, List<WordCollection> words)
        {
            var storyWords = words.Select(w => new StoryWord
            {
                StoryId = storyId,
                WordCollectionId = w.Id
            }).ToList();

            foreach (var storyWord in storyWords)
            {
                await storyWordRepository.InsertAsync(storyWord);
            }
        }
    }
}