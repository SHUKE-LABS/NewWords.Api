namespace NewWords.Api.Constants
{
    /// <summary>
    /// Constants for story generation functionality.
    /// </summary>
    public static class StoryConstants
    {
        /// <summary>
        /// Maximum number of words allowed in a single story.
        /// </summary>
        public const int MaxWordsPerStory = 10;

        /// <summary>
        /// Maximum number of custom words accepted in a single story generation request.
        /// Caps the number of sequential LLM calls (batched by <see cref="MaxWordsPerStory"/>)
        /// a single request can trigger.
        /// </summary>
        public const int MaxCustomWordsPerRequest = 30;

        /// <summary>
        /// Maximum number of words to fetch from user's recent vocabulary for story generation.
        /// </summary>
        public const int MaxRecentWordsToFetch = 8;

        /// <summary>
        /// Minimum number of words required to generate a story.
        /// </summary>
        public const int MinWordsForStory = 1;

        /// <summary>
        /// Minimum number of recent words required for automatic story generation (cron job).
        /// </summary>
        public const int MinRecentWordsForAutomaticGeneration = 5;

        /// <summary>
        /// Time window in hours to check for duplicate stories.
        /// </summary>
        public const int DuplicateCheckHours = 24;

        /// <summary>
        /// Days to look back for recent vocabulary words.
        /// </summary>
        public const int RecentWordsDays = 7;
    }
}