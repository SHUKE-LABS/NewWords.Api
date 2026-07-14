using Xunit;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using NewWords.Api.Services;
using Api.Framework;
using LLM;
using NewWords.Api.Repositories;
using SqlSugar;

namespace NewWords.Api.Tests.Services
{
    public class VocabularyServiceMarkdownTests
    {
        private VocabularyService CreateService()
        {
            // Provide minimal mocks for ctor
            var dbMock = new Mock<ISqlSugarClient>().Object;
            var languageServiceMock = new Mock<ILanguageService>().Object;
            var configMock = new Mock<IConfigurationService>().Object;
            var loggerMock = new Mock<ILogger<VocabularyService>>().Object;
            var wcRepo = new Mock<IRepositoryBase<NewWords.Api.Entities.WordCollection>>().Object;
            var weRepo = new Mock<IRepositoryBase<NewWords.Api.Entities.WordExplanation>>().Object;
            var qhRepo = new Mock<IRepositoryBase<NewWords.Api.Entities.QueryHistory>>().Object;
            var uwRepo = new Mock<NewWords.Api.Repositories.IUserWordRepository>().Object;
            var entitlementMock = new Mock<NewWords.Api.Services.interfaces.IEntitlementService>().Object;

            return new VocabularyService(dbMock, languageServiceMock, configMock, loggerMock, wcRepo, weRepo, qhRepo, uwRepo, entitlementMock);
        }

        [Theory]
        [InlineData("**apple**", "apple")]
        [InlineData("**example** /ɪgˈzæmpl/ - meaning...", "example")]
        [InlineData("# apple\nSome explanation", "apple")]
        [InlineData("**Multiple Words** explanation", "Multiple Words")]
        [InlineData("apple - a fruit", "apple")]
        [InlineData("`apple` is a word", "apple")]
        public void ExtractCanonicalWordFromMarkdown_VariousFormats_ReturnsExpected(string input, string expected)
        {
            var svc = CreateService();
            var result = svc.ExtractCanonicalWordFromMarkdown(input);
            result.Should().BeEquivalentTo(expected);
        }
    }
}
