using Api.Framework.Models;
using Api.Framework.Result;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Entities;
using NewWords.Api.Services;
using NewWords.Api.Models.DTOs.Vocabulary;
using NewWords.Api.Services.interfaces;
using System.Diagnostics;
using NewWords.Api.Exceptions;

namespace NewWords.Api.Controllers
{
    [Authorize]
    public class VocabularyController(
        IVocabularyService vocabularyService,
        IQueryHistoryService queryHistoryService,
        ICurrentUser currentUser,
        ILogger<VocabularyController> logger)
        : BaseController
    {
        /// <summary>
        /// Retrieves a paginated list of the current user's words.
        /// </summary>
        /// <param name="pageSize">Number of words per page.</param>
        /// <param name="pageNumber">Page number to retrieve.</param>
        /// <returns>Paginated list of words.</returns>
        [HttpGet]
        public async Task<ApiResult<PageData<WordExplanation>>> List(int pageSize = 10, int pageNumber = 1)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var words = await vocabularyService.GetUserWordsAsync(userId, pageSize, pageNumber);
            return new SuccessfulResult<PageData<WordExplanation>>(words);
        }

        /// <summary>
        /// Adds a new word to the current user's list.
        /// </summary>
        /// <param name="addWordRequest">The word details to add.</param>
        /// <returns>The added word explanation.</returns>
        [HttpPost]
        public async Task<ApiResult<WordExplanation>> Add(AddWordRequest addWordRequest)
        {
            var stopwatch = Stopwatch.StartNew();
            var userId = currentUser.Id;
            
            logger.LogInformation("Starting add word operation for user {UserId}, word '{WordText}', learning language: {LearningLanguage}, explanation language: {ExplanationLanguage}", 
                userId, addWordRequest.WordText, addWordRequest.LearningLanguage, addWordRequest.ExplanationLanguage);
            
            try
            {
                var addedWordExplanation = await vocabularyService.AddUserWordAsync(
                    userId,
                    addWordRequest.WordText,
                    addWordRequest.LearningLanguage,
                    addWordRequest.ExplanationLanguage
                );
                
                stopwatch.Stop();
                logger.LogInformation("Successfully added word '{WordText}' for user {UserId} in {ElapsedMs}ms", 
                    addWordRequest.WordText, userId, stopwatch.ElapsedMilliseconds);

                queryHistoryService.LogQueryAsync(addedWordExplanation.WordCollectionId, currentUser.Id);
                return new SuccessfulResult<WordExplanation>(addedWordExplanation);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Failed to add word '{WordText}' for user {UserId} after {ElapsedMs}ms", 
                    addWordRequest.WordText, userId, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// Deletes a word from the current user's list.
        /// </summary>
        /// <param name="wordExplanationId">The ID of the word explanation to delete.</param>
        [HttpDelete("{wordExplanationId}")]
        public async Task<ApiResult> Delete(long wordExplanationId)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            await vocabularyService.DelUserWordAsync(userId, wordExplanationId);
            return Success();
        }

        /// <summary>
        /// Refreshes the explanation for a word using the current first agent if different from the original provider.
        /// </summary>
        /// <param name="wordExplanationId">The ID of the word explanation to refresh.</param>
        /// <returns>The refreshed word explanation.</returns>
        [HttpPut("{wordExplanationId}")]
        public async Task<ApiResult<WordExplanation>> RefreshExplanation(long wordExplanationId)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var refreshedExplanation = await vocabularyService.RefreshUserWordExplanationAsync(userId, wordExplanationId);
            return new SuccessfulResult<WordExplanation>(refreshedExplanation);
        }

        /// <summary>
        /// Retrieves vocabulary memories from various days ago (3 days, 1 week, 2 weeks, etc.) for spaced repetition.
        /// </summary>
        /// <param name="localTimezone">The user's local timezone (e.g., "America/New_York").</param>
        /// <returns>List of word explanations from memory dates.</returns>
        [HttpGet]
        public async Task<ApiResult<List<WordExplanation>>> Memories(string localTimezone)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var memories = await vocabularyService.MemoriesAsync(userId, localTimezone);
            return new SuccessfulResult<List<WordExplanation>>(memories.ToList());
        }

        /// <summary>
        /// Retrieves all vocabulary words learned on a specific date.
        /// </summary>
        /// <param name="localTimezone">The user's local timezone (e.g., "America/New_York").</param>
        /// <param name="yyyyMMdd">The date in YYYYMMDD format (e.g., "20240115").</param>
        /// <returns>List of word explanations from the specified date.</returns>
        [HttpGet]
        public async Task<ApiResult<List<WordExplanation>>> MemoriesOn(string localTimezone, string yyyyMMdd)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var memories = await vocabularyService.MemoriesOnAsync(userId, localTimezone, yyyyMMdd);
            return new SuccessfulResult<List<WordExplanation>>(memories.ToList());
        }

        /// <summary>
        /// Get all available explanations for a word
        /// </summary>
        /// <param name="wordCollectionId">Word collection ID</param>
        /// <param name="learningLanguage">Learning language code (e.g., "en", "zh")</param>
        /// <param name="explanationLanguage">Explanation language code</param>
        /// <returns>All explanations and user's default</returns>
        [HttpGet("{wordCollectionId}/{learningLanguage}/{explanationLanguage}")]
        public async Task<ApiResult<ExplanationsResponse>> Explanations(
            long wordCollectionId,
            string learningLanguage,
            string explanationLanguage)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var result = await vocabularyService.GetAllExplanationsForWordAsync(
                userId,
                wordCollectionId,
                learningLanguage,
                explanationLanguage);

            return new SuccessfulResult<ExplanationsResponse>(result);
        }

        /// <summary>
        /// Switch user's default explanation for a word
        /// </summary>
        /// <param name="wordCollectionId">Word collection ID</param>
        /// <param name="wordExplanationId">New default explanation ID</param>
        [HttpPut("{wordCollectionId}/{wordExplanationId}")]
        public async Task<ApiResult> SwitchExplanation(
            long wordCollectionId,
            long wordExplanationId)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            await vocabularyService.SwitchUserDefaultExplanationAsync(
                userId,
                wordCollectionId,
                wordExplanationId);

            return Success("Default explanation updated successfully");
        }
    }
}
