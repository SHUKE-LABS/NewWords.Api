using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using FluentAssertions;
using NewWords.Api.Constants;
using NewWords.Api.Models.DTOs.Stories;
using Xunit;

namespace NewWords.Api.Tests.Models
{
    public class GenerateStoryRequestValidationTests
    {
        private static IList<ValidationResult> Validate(GenerateStoryRequest request)
        {
            var context = new ValidationContext(request);
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(request, context, results, validateAllProperties: true);
            return results;
        }

        private static GenerateStoryRequest WithWords(int count)
        {
            return new GenerateStoryRequest
            {
                Words = Enumerable.Range(0, count).Select(i => $"word{i}").ToList()
            };
        }

        [Fact]
        public void Words_AtCap_PassesValidation()
        {
            var results = Validate(WithWords(StoryConstants.MaxCustomWordsPerRequest));

            results.Should().BeEmpty();
        }

        [Fact]
        public void Words_OverCap_FailsValidation()
        {
            var results = Validate(WithWords(StoryConstants.MaxCustomWordsPerRequest + 1));

            results.Should().ContainSingle()
                .Which.MemberNames.Should().Contain(nameof(GenerateStoryRequest.Words));
        }

        [Fact]
        public void Words_Null_PassesValidation()
        {
            var results = Validate(new GenerateStoryRequest { Words = null });

            results.Should().BeEmpty();
        }
    }
}
