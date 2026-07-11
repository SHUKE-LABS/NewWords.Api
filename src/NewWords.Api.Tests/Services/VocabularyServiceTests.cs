using System;
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

        private VocabularyService CreateService() => new(
            null!,
            _languageServiceMock.Object,
            _configServiceMock.Object,
            _loggerMock.Object,
            _wordCollectionRepoMock.Object,
            _wordExplanationRepoMock.Object,
            _queryHistoryRepoMock.Object,
            _userWordRepoMock.Object
        );

        [Fact]
        public async Task EnsureCanonicalWordAsync_UpdatesWrongWordToCanonical_WhenCanonicalNotExists()
        {
            // Arrange
            var wrongEntry = new WordCollection { Id = 1, WordText = "applw", DeletedAt = null };
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync((System.Linq.Expressions.Expression<Func<WordCollection, bool>> expr, string? orderBy) =>
                {
                    var compiled = expr.Compile();
                    if (compiled(wrongEntry)) return wrongEntry;
                    return null;
                });
            _wordCollectionRepoMock.Setup(r => r.UpdateAsync(wrongEntry)).ReturnsAsync(true);

            var service = CreateService();
            var method = service.GetType().GetMethod("EnsureCanonicalWordAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            var result = await (Task<long>)method.Invoke(service, new object[] { "applw", "apple" });

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
            _wordCollectionRepoMock.SetupSequence(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync(wrongEntry)
                .ReturnsAsync(correctEntry);
            _wordCollectionRepoMock.Setup(r => r.UpdateAsync(wrongEntry)).ReturnsAsync(true);

            var service = CreateService();
            var method = service.GetType().GetMethod("EnsureCanonicalWordAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            var result = await (Task<long>)method.Invoke(service, new object[] { "applw", "apple" });

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
            _wordCollectionRepoMock.Setup(r => r.GetFirstOrDefaultAsync(It.IsAny<System.Linq.Expressions.Expression<Func<WordCollection, bool>>>(), null))
                .ReturnsAsync(correctEntry);

            var service = CreateService();
            var method = service.GetType().GetMethod("EnsureCanonicalWordAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act
            var result = await (Task<long>)method.Invoke(service, new object[] { "apple", "apple" });

            // Assert
            result.Should().Be(correctEntry.Id);
            _wordCollectionRepoMock.Verify(r => r.UpdateAsync(It.IsAny<WordCollection>()), Times.Never);
        }
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
                    It.IsAny<System.Linq.Expressions.Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<UserWord, bool>>>(), null))
                .ReturnsAsync(new UserWord { UserId = userId, WordCollectionId = 5, WordExplanationId = 100 });
            _wordExplanationRepoMock.Setup(r => r.GetListAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<WordExplanation, bool>>>(), null, true))
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
                    It.IsAny<System.Linq.Expressions.Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            // No matching UserWord for this caller.
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<UserWord, bool>>>(), null))
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
                    It.IsAny<System.Linq.Expressions.Expression<Func<WordExplanation, bool>>>(), null))
                .ReturnsAsync(current);
            _userWordRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<UserWord, bool>>>(), null))
                .ReturnsAsync(new UserWord { UserId = userId, WordCollectionId = 5, WordExplanationId = 100 });
            // The only configured model has already produced an explanation for this word.
            _wordExplanationRepoMock.Setup(r => r.GetListAsync(
                    It.IsAny<System.Linq.Expressions.Expression<Func<WordExplanation, bool>>>(), null, true))
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
            var service = new VocabularyService(null, null, null, null, null, null, null, null);
            var method = service.GetType().GetMethod("ExtractCanonicalWordFromMarkdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var result = (string)method.Invoke(service, new object[] { markdown });
            result.Should().Be(expected);
        }
    }
}
