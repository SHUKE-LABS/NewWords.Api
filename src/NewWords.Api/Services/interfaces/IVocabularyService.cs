using Api.Framework.Models;
using NewWords.Api.Entities;
using NewWords.Api.Models.DTOs.Vocabulary;

namespace NewWords.Api.Services.interfaces
{
    public interface IVocabularyService
    {
        Task<PageData<WordExplanation>> GetUserWordsAsync(int userId, int pageSize, int pageNumber);
        Task<WordExplanation> AddUserWordAsync(int userId, string wordText, string learningLanguageCode, string explanationLanguageCode);
        Task DelUserWordAsync(int userId, long wordExplanationId);
        Task<WordExplanation> RefreshUserWordExplanationAsync(int userId, long wordExplanationId);
        Task<IList<WordExplanation>> MemoriesAsync(int userId, string localTimezone);
        Task<IList<WordExplanation>> MemoriesOnAsync(int userId, string localTimezone, string yyyyMMdd);
        Task<ExplanationsResponse> GetAllExplanationsForWordAsync(int userId, long wordCollectionId, string learningLanguage, string explanationLanguage);
        Task SwitchUserDefaultExplanationAsync(int userId, long wordCollectionId, long newExplanationId);
    }
}
