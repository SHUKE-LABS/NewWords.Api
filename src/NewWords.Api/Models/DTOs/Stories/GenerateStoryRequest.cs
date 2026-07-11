using System.ComponentModel.DataAnnotations;
using NewWords.Api.Constants;

namespace NewWords.Api.Models.DTOs.Stories
{
    /// <summary>
    /// Request model for manual story generation.
    /// </summary>
    public class GenerateStoryRequest
    {
        /// <summary>
        /// Optional: Custom word list for story generation.
        /// If null or empty, will use user's recent vocabulary words.
        /// </summary>
        [MaxLength(StoryConstants.MaxCustomWordsPerRequest,
            ErrorMessage = "A story generation request accepts at most {1} custom words.")]
        public List<string>? Words { get; set; }

        /// <summary>
        /// Optional: Specific learning language for the story.
        /// If null, will use user's current learning language.
        /// </summary>
        [StringLength(20)]
        public string? LearningLanguage { get; set; }
    }
}