using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using NewWords.Api.Entities;
using NewWords.Api.Exceptions;
using NewWords.Api.Services;
using Api.Framework;
using LLM;
using LLM.Models;
using Microsoft.Extensions.Logging;
using NewWords.Api.Repositories;
using SqlSugar;
using System.Collections.Generic;

namespace NewWords.Api.Tests.Services
{
    public class VocabularyServiceTests
    {
        private readonly Mock<IRepositoryBase<WordCollection>> _wordCollectionRepoMock = new();
        private readonly Mock<IRepositoryBase<WordExplanation>> _wordExplanationRepoMock = new();
        private readonly Mock<IRepositoryBase<QueryHistory>> _queryHistoryRepoMock = new();
        private readonly Mock<IUserWordRepository> _userWordRepoMock = new();
        private readonly Mock<ILanguageService> _languageServiceMock = new();
        private readonly Mock<IConfigurationService> _configServiceMock = new();
        private readonly Mock<ILogger<VocabularyService>> _loggerMock = new();
        private readonly Mock<ISqlSugarClient> _dbMock = new();
        private readonly Mock<ITenant> _tenantMock = new();

        public VocabularyServiceTests()
        {
            // Wire the transaction seam: TransactionHelper.ExecuteInTransactionAsync
            // calls db.AsTenant().BeginTranAsync()/CommitTranAsync()/RollbackTranAsync().
            _dbMock.Setup(d => d.AsTenant()).Returns(_tenantMock.Object);
            _tenantMock.Setup(t => t.BeginTranAsync()).Returns(Task.CompletedTask);
            _tenantMock.Setup(t => t.CommitTranAsync()).Returns(Task.CompletedTask);
            _tenantMock.Setup(t => t.RollbackTranAsync()).Returns(Task.CompletedTask);
        }

        private VocabularyService CreateService() => new(
            _dbMock.Object,
            _languageServiceMock.Object,
            _configServiceMock.Object,
            _loggerMock.Object,
            _wordCollectionRepoMock.Object,
            _wordExplanationRepoMock.Object,
            _queryHistoryRepoMock.Object,
            _userWordRepoMock.Object
        );

        // ---- EnsureCanonicalWordAsync (direct calls; formerly reflection) ----

        [Fact]
        public async Task EnsureCanonicalWordAsync_UpdatesWrongWordToCanonical_WhenCanonicalNotExists()
        {
            // Arrange
            var wrongEntry = new WordCollection { Id = 1, WordText = "applw", DeletedAt = null };
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync((Expression<Func<WordCollection, bool>> expr, string? orderBy) =>
                {
                    var compiled = expr.Compile();
                    if (compiled(wrongEntry)) return wrongEntry;
                    return null;
                });
            _wordCollectionRepoMock.Setup(r => r.UpdateAsync(wrongEntry)).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.EnsureCanonicalWordAsync("applw", "apple");

            // Assert
            result.Should().Be(wrongEntry.Id);
            wrongEntry.WordText.Should().Be("apple");
            _wordCollectionRepoMock.Verify(r => r.UpdateAsync(wrongEntry), Times.Once);
        }

        [Fact]
        public async Task EnsureCanonicalWordAsync_DeletesWrongWord_WhenCanonicalExists()
        {
            // Arrange
            var wrongEntry = new WordCollection { Id = 1, WordText = "applw", DeletedAt = null };
            var correctEntry = new WordCollection { Id = 2, WordText = "apple", DeletedAt = null };
            _wordCollectionRepoMock.SetupSequence(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync(wrongEntry)
                .ReturnsAsync(correctEntry);
            _wordCollectionRepoMock.Setup(r => r.UpdateAsync(wrongEntry)).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.EnsureCanonicalWordAsync("applw", "apple");

            // Assert
            result.Should().Be(correctEntry.Id);
            wrongEntry.DeletedAt.Should().NotBeNull();
            _wordCollectionRepoMock.Verify(r => r.UpdateAsync(wrongEntry), Times.Once);
        }

        [Fact]
        public async Task EnsureCanonicalWordAsync_NoAction_WhenInputIsCanonical()
        {
            // Arrange
            var correctEntry = new WordCollection { Id = 2, WordText = "apple", DeletedAt = null };
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync(correctEntry);

            var service = CreateService();

            // Act
            var result = await service.EnsureCanonicalWordAsync("apple", "apple");

            // Assert
            result.Should().Be(correctEntry.Id);
            _wordCollectionRepoMock.Verify(r => r.UpdateAsync(It.IsAny<WordCollection>()), Times.Never);
        }

        // ---- AddUserWordAsync (service-level coverage of the core add-word flow) ----

        private void SetupLanguageNames()
        {
            _configServiceMock.Setup(c => c.GetLanguageName("en")).Returns("English");
            _configServiceMock.Setup(c => c.GetLanguageName("zh")).Returns("Chinese");
        }

        [Fact]
        public async Task AddUserWordAsync_LocalExplanationHit_SkipsLlm_AndCreatesUserWord()
        {
            // Arrange: word + explanation already in the local store, no user word yet.
            SetupLanguageNames();
            var localWord = new WordCollection { Id = 5, WordText = "apple", DeletedAt = null };
            var explanation = new WordExplanation
            {
                Id = 100, WordCollectionId = 5, WordText = "apple",
                LearningLanguage = "en", ExplanationLanguage = "zh",
                MarkdownExplanation = "**apple** cached",
            };
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(localWord);
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(explanation);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((UserWord?)null);
            _userWordRepoMock.Setup(r => r.InsertAsync(It.IsAny<UserWord>())).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.AddUserWordAsync(1, "apple", "en", "zh");

            // Assert
            result.Id.Should().Be(100);
            result.CreatedAt.Should().BeGreaterThan(0);
            result.UpdatedAt.Should().Be(result.CreatedAt); // new user word: created == updated
            _userWordRepoMock.Verify(r => r.InsertAsync(It.IsAny<UserWord>()), Times.Once);
            _languageServiceMock.Verify(l => l.GetMarkdownExplanationWithFallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task AddUserWordAsync_NewWord_AiSuccess_CreatesCollectionExplanationAndUserWord()
        {
            // Arrange: word not local -> AI succeeds -> everything created.
            SetupLanguageNames();
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordCollection?)null); // local lookup + both EnsureCanonical lookups miss
            _wordCollectionRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()))
                .ReturnsAsync(5L);
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordExplanation?)null);
            _wordExplanationRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()))
                .ReturnsAsync(100L);
            _languageServiceMock.Setup(l => l.GetMarkdownExplanationWithFallbackAsync("apple", "Chinese", "English"))
                .ReturnsAsync(new ExplanationResult { IsSuccess = true, Markdown = "**apple**\nA fruit.", ModelName = "prov:model" });
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((UserWord?)null);
            _userWordRepoMock.Setup(r => r.InsertAsync(It.IsAny<UserWord>())).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.AddUserWordAsync(1, "apple", "en", "zh");

            // Assert
            result.Id.Should().Be(100);
            result.WordCollectionId.Should().Be(5);
            result.WordText.Should().Be("apple");
            result.MarkdownExplanation.Should().Be("**apple**\nA fruit.");
            result.ProviderModelName.Should().Be("prov:model");
            result.CreatedAt.Should().BeGreaterThan(0);
            result.UpdatedAt.Should().Be(result.CreatedAt);
            _wordCollectionRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()), Times.Once);
            _wordExplanationRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()), Times.Once);
            _userWordRepoMock.Verify(r => r.InsertAsync(It.IsAny<UserWord>()), Times.Once);
        }

        [Fact]
        public async Task AddUserWordAsync_AiTypoCorrection_MergesToCanonicalWord()
        {
            // Arrange: user types "applw", AI's canonical word is "apple".
            SetupLanguageNames();
            var wrongEntry = new WordCollection { Id = 1, WordText = "applw", DeletedAt = null };
            // 1) local lookup ("applw") miss -> AI; 2) EnsureCanonical wrongEntry ("applw") hit;
            // 3) EnsureCanonical correctEntry ("apple") miss -> update-typo branch.
            _wordCollectionRepoMock.SetupSequence(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordCollection?)null)
                .ReturnsAsync(wrongEntry)
                .ReturnsAsync((WordCollection?)null);
            _wordCollectionRepoMock.Setup(r => r.UpdateAsync(It.IsAny<WordCollection>())).ReturnsAsync(true);
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordExplanation?)null);
            _wordExplanationRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()))
                .ReturnsAsync(100L);
            _languageServiceMock.Setup(l => l.GetMarkdownExplanationWithFallbackAsync("applw", "Chinese", "English"))
                .ReturnsAsync(new ExplanationResult { IsSuccess = true, Markdown = "**apple**\nCorrected.", ModelName = "prov:model" });
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((UserWord?)null);
            _userWordRepoMock.Setup(r => r.InsertAsync(It.IsAny<UserWord>())).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.AddUserWordAsync(1, "applw", "en", "zh");

            // Assert: typo entry merged to canonical, explanation persisted under canonical word.
            wrongEntry.WordText.Should().Be("apple");
            _wordCollectionRepoMock.Verify(r => r.UpdateAsync(wrongEntry), Times.Once);
            _wordCollectionRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()), Times.Never);
            result.WordCollectionId.Should().Be(1);
            result.WordText.Should().Be("apple");
        }

        [Fact]
        public async Task AddUserWordAsync_AiFailure_Throws_AndPersistsNoUserWord()
        {
            // Arrange: not local, AI returns a failed result. Documents today's contract:
            // _HandleExplanation rejects the failed result -> transaction throws -> no user word.
            SetupLanguageNames();
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordCollection?)null);
            _wordCollectionRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()))
                .ReturnsAsync(7L);
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordExplanation?)null);
            _languageServiceMock.Setup(l => l.GetMarkdownExplanationWithFallbackAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ExplanationResult { IsSuccess = false, ErrorMessage = "boom" });

            var service = CreateService();

            // Act
            var act = () => service.AddUserWordAsync(1, "xyzzy", "en", "zh");

            // Assert
            await act.Should().ThrowAsync<Exception>();
            _userWordRepoMock.Verify(r => r.InsertAsync(It.IsAny<UserWord>()), Times.Never);
            _languageServiceMock.Verify(l => l.GetMarkdownExplanationWithFallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task AddUserWordAsync_DuplicateKeyOnCollectionInsert_RequeriesExistingId()
        {
            // Arrange: new word, AI succeeds, but the WordCollection insert loses a race
            // (duplicate key). _AddWordCollection must re-query and return the existing id.
            SetupLanguageNames();
            var existing = new WordCollection { Id = 99, WordText = "apple", DeletedAt = null };
            // 1) local lookup miss; 2) wrongEntry miss; 3) correctEntry miss; 4) requery hit.
            _wordCollectionRepoMock.SetupSequence(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordCollection?)null)
                .ReturnsAsync((WordCollection?)null)
                .ReturnsAsync((WordCollection?)null)
                .ReturnsAsync(existing);
            _wordCollectionRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()))
                .ThrowsAsync(new Exception("duplicate key"));
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((WordExplanation?)null);
            _wordExplanationRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()))
                .ReturnsAsync(100L);
            _languageServiceMock.Setup(l => l.GetMarkdownExplanationWithFallbackAsync("apple", "Chinese", "English"))
                .ReturnsAsync(new ExplanationResult { IsSuccess = true, Markdown = "**apple**", ModelName = "prov:model" });
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync((UserWord?)null);
            _userWordRepoMock.Setup(r => r.InsertAsync(It.IsAny<UserWord>())).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.AddUserWordAsync(1, "apple", "en", "zh");

            // Assert: explanation is attached to the pre-existing collection id from the re-query.
            result.WordCollectionId.Should().Be(99);
            _wordCollectionRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()), Times.Once);
        }

        [Fact]
        public async Task AddUserWordAsync_ReAddingExistingWord_BumpsUpdatedAt_NoNewRows()
        {
            // Arrange: word + explanation + user word all already exist.
            SetupLanguageNames();
            var localWord = new WordCollection { Id = 5, WordText = "apple", DeletedAt = null };
            var explanation = new WordExplanation
            {
                Id = 100, WordCollectionId = 5, WordText = "apple",
                LearningLanguage = "en", ExplanationLanguage = "zh",
            };
            var existingUserWord = new UserWord
            {
                Id = 50, UserId = 1, WordCollectionId = 5, WordExplanationId = 100,
                CreatedAt = 12345, UpdatedAt = 12345,
            };
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordCollection, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(localWord);
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(explanation);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(existingUserWord);
            _userWordRepoMock.Setup(r => r.UpdateAsync(It.IsAny<UserWord>())).ReturnsAsync(true);

            var service = CreateService();

            // Act
            var result = await service.AddUserWordAsync(1, "apple", "en", "zh");

            // Assert
            result.CreatedAt.Should().Be(12345);            // unchanged
            result.UpdatedAt.Should().BeGreaterThan(12345); // bumped to now
            _userWordRepoMock.Verify(r => r.UpdateAsync(existingUserWord), Times.Once);
            _userWordRepoMock.Verify(r => r.InsertAsync(It.IsAny<UserWord>()), Times.Never);
            _wordCollectionRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordCollection>()), Times.Never);
            _wordExplanationRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()), Times.Never);
            _languageServiceMock.Verify(l => l.GetMarkdownExplanationWithFallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ---- RefreshUserWordExplanationAsync (unchanged) ----

        [Fact]
        public async Task RefreshUserWordExplanationAsync_Succeeds_WhenCallerOwnsWord()
        {
            // Arrange
            const int userId = 42;
            var current = new WordExplanation
            {
                Id = 100, WordCollectionId = 5, WordText = "apple",
                LearningLanguage = "en", ExplanationLanguage = "zh",
                ProviderModelName = "prov:model-a",
            };
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), null))
                .ReturnsAsync(new UserWord { UserId = userId, WordCollectionId = 5, WordExplanationId = 100 });
            _wordExplanationRepoMock.Setup(r => r.GetListAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), null, true))
                .ReturnsAsync(new List<WordExplanation> { current });
            _configServiceMock.Setup(c => c.Agents)
                .Returns(new List<Agent> { new() { Provider = "prov", ModelName = "model-b" } });
            _configServiceMock.Setup(c => c.GetLanguageName("en")).Returns("English");
            _configServiceMock.Setup(c => c.GetLanguageName("zh")).Returns("Chinese");
            _languageServiceMock.Setup(l => l.GetMarkdownExplanationAsync(
                    It.IsAny<Agent>(), "apple", "English", "Chinese"))
                .ReturnsAsync("new markdown");
            _wordExplanationRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()))
                .ReturnsAsync(200L);

            var service = CreateService();

            // Act
            var result = await service.RefreshUserWordExplanationAsync(userId, 100);

            // Assert
            result.MarkdownExplanation.Should().Be("new markdown");
            result.ProviderModelName.Should().Be("prov:model-b");
            result.Id.Should().Be(200L);
            _wordExplanationRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()), Times.Once);
        }

        [Fact]
        public async Task RefreshUserWordExplanationAsync_Throws_AndDoesNoAiCallOrInsert_WhenCallerDoesNotOwnWord()
        {
            // Arrange
            var current = new WordExplanation
            {
                Id = 100, WordCollectionId = 5, WordText = "apple",
                LearningLanguage = "en", ExplanationLanguage = "zh",
                ProviderModelName = "prov:model-a",
            };
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            // No matching UserWord for this caller.
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), null))
                .ReturnsAsync((UserWord?)null);

            var service = CreateService();

            // Act
            var act = () => service.RefreshUserWordExplanationAsync(999, 100);

            // Assert
            await act.Should().ThrowAsync<BusinessException>().WithMessage("User word not found");
            _languageServiceMock.Verify(l => l.GetMarkdownExplanationAsync(
                It.IsAny<Agent>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _wordExplanationRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()), Times.Never);
        }

        [Fact]
        public async Task RefreshUserWordExplanationAsync_Throws_WhenAllModelsUsed_ForOwningCaller()
        {
            // Arrange
            const int userId = 42;
            var current = new WordExplanation
            {
                Id = 100, WordCollectionId = 5, WordText = "apple",
                LearningLanguage = "en", ExplanationLanguage = "zh",
                ProviderModelName = "prov:model-a",
            };
            _wordExplanationRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserWord, bool>>>(), null))
                .ReturnsAsync(new UserWord { UserId = userId, WordCollectionId = 5, WordExplanationId = 100 });
            // The only configured model has already produced an explanation for this word.
            _wordExplanationRepoMock.Setup(r => r.GetListAsync(
                    It.IsAny<Expression<Func<WordExplanation, bool>>>(), null, true))
                .ReturnsAsync(new List<WordExplanation> { current });
            _configServiceMock.Setup(c => c.Agents)
                .Returns(new List<Agent> { new() { Provider = "prov", ModelName = "model-a" } });

            var service = CreateService();

            // Act
            var act = () => service.RefreshUserWordExplanationAsync(userId, 100);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("All available models have been used for this word");
            _languageServiceMock.Verify(l => l.GetMarkdownExplanationAsync(
                It.IsAny<Agent>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _wordExplanationRepoMock.Verify(r => r.InsertReturnIdentityAsync(It.IsAny<WordExplanation>()), Times.Never);
        }

        // ---- ExtractCanonicalWordFromMarkdown (direct call; formerly reflection) ----

        [Theory]
        [InlineData("**apple**", "apple")]
        [InlineData("**take off** (phrasal verb)", "take off")]
        [InlineData("apple", "apple")]
        [InlineData("Some text **apple**", "apple")]
        [InlineData("", "")]
        [InlineData("**run**\n**walk**", "run")]
        [InlineData("**multi word phrase** - explanation", "multi word phrase")]
        [InlineData("** spaced  phrase  **", "spaced  phrase")]
        public void ExtractCanonicalWordFromMarkdown_HandlesVariousCases(string markdown, string expected)
        {
            var service = CreateService();
            var result = service.ExtractCanonicalWordFromMarkdown(markdown);
            result.Should().Be(expected);
        }
    }
}
