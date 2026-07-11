using Api.Framework.Models;
using Api.Framework.Result;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Entities;
using NewWords.Api.Models.DTOs.Stories;
using NewWords.Api.Services.interfaces;
using NewWords.Api.Exceptions;

namespace NewWords.Api.Controllers
{
    [Authorize]
    public class StoriesController(
        IStoryService storyService,
        ICurrentUser currentUser)
        : BaseController
    {
        /// <summary>
        /// Gets the current user's stories.
        /// </summary>
        /// <param name="pageSize">Number of stories per page.</param>
        /// <param name="pageNumber">Page number to retrieve.</param>
        /// <returns>Paginated list of user's stories.</returns>
        [HttpGet]
        [EnforcePageSizeLimit(100)]
        public async Task<ApiResult<PageData<StoryDto>>> MyStories(int pageSize = 10, int pageNumber = 1)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var stories = await storyService.GetUserStoriesAsync(userId, pageSize, pageNumber);
            return new SuccessfulResult<PageData<StoryDto>>(stories);
        }

        /// <summary>
        /// Gets stories from other users for discovery (Story Square).
        /// </summary>
        /// <param name="pageSize">Number of stories per page.</param>
        /// <param name="pageNumber">Page number to retrieve.</param>
        /// <returns>Paginated list of other users' stories sorted by popularity.</returns>
        [HttpGet]
        [EnforcePageSizeLimit(100)]
        public async Task<ApiResult<PageData<StoryDto>>> StorySquare(int pageSize = 10, int pageNumber = 1)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var stories = await storyService.GetStorySquareAsync(userId, pageSize, pageNumber);
            return new SuccessfulResult<PageData<StoryDto>>(stories);
        }

        /// <summary>
        /// Gets the current user's favorite stories.
        /// </summary>
        /// <param name="pageSize">Number of stories per page.</param>
        /// <param name="pageNumber">Page number to retrieve.</param>
        /// <returns>Paginated list of user's favorite stories.</returns>
        [HttpGet]
        [EnforcePageSizeLimit(100)]
        public async Task<ApiResult<PageData<StoryDto>>> MyFavorite(int pageSize = 10, int pageNumber = 1)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var stories = await storyService.GetUserFavoriteStoriesAsync(userId, pageSize, pageNumber);
            return new SuccessfulResult<PageData<StoryDto>>(stories);
        }

        /// <summary>
        /// Marks a story as read by the current user.
        /// </summary>
        /// <param name="storyId">The ID of the story to mark as read.</param>
        [HttpPost("{storyId}")]
        public async Task<ApiResult> MarkRead(long storyId)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            await storyService.MarkStoryAsReadAsync(userId, storyId);
            return Success("Story marked as read");
        }

        /// <summary>
        /// Toggles favorite status for a story.
        /// </summary>
        /// <param name="storyId">The ID of the story to favorite/unfavorite.</param>
        [HttpPost("{storyId}")]
        public async Task<ApiResult> ToggleFavorite(long storyId)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var isFavorited = await storyService.ToggleFavoriteAsync(userId, storyId);
            var message = isFavorited ? "Story favorited" : "Story unfavorited";
            return Success(message);
        }

        /// <summary>
        /// Manually generates one or more stories for the current user.
        /// Can use custom word list or recent vocabulary words.
        /// Uses batch generation to create multiple stories if there are many words.
        /// </summary>
        /// <param name="request">Optional request with custom words and language</param>
        [HttpPost]
        public async Task<ApiResult<List<Story>>> Generate([FromBody] GenerateStoryRequest? request = null)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var stories = await storyService.GenerateStoryWithWordsAsync(
                userId,
                request?.Words,
                request?.LearningLanguage);

            if (stories.Count == 0)
            {
                throw new BusinessException("Unable to generate stories. Please ensure you have recent vocabulary words or provide custom words.");
            }

            var message = stories.Count == 1 ? "Story generated successfully" : $"{stories.Count} stories generated successfully";
            return new SuccessfulResult<List<Story>>(stories, message);
        }
    }
}