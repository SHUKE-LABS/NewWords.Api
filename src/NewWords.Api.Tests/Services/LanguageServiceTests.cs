using FluentAssertions;
using LLM.Models;
using LLM.Services;
using Xunit;

namespace NewWords.Api.Tests.Services;

public class LanguageServiceTests
{
    [Fact]
    public void SelectExplanationAgents_KeepsPreferredOpenRouterModelsInOrder()
    {
        var agents = new[]
        {
            new Agent { Provider = "OpenRouter", ModelName = "openai/gpt-5.6-luna" },
            new Agent { Provider = "OpenAI", ModelName = "gpt-4.1-mini" },
            new Agent { Provider = "OpenRouter", ModelName = "google/gemma-4-26b-a4b-it" },
            new Agent { Provider = "OpenRouter", ModelName = "mistralai/devstral-2512:free" }
        };
        var preferredModels = new[]
        {
            "openai/gpt-5.6-luna",
            "google/gemma-4-26b-a4b-it"
        };

        var result = LanguageService.SelectExplanationAgents(agents, preferredModels);

        result.Select(agent => $"{agent.Provider}:{agent.ModelName}")
            .Should()
            .Equal(
                "OpenRouter:openai/gpt-5.6-luna",
                "OpenRouter:google/gemma-4-26b-a4b-it",
                "OpenAI:gpt-4.1-mini",
                "OpenRouter:mistralai/devstral-2512:free");
    }

    [Fact]
    public void SelectExplanationAgents_FallsBackToAllConfiguredAgents_WhenPreferredModelsAreMissing()
    {
        var agents = new[]
        {
            new Agent { Provider = "OpenRouter", ModelName = "mistralai/devstral-2512:free" },
            new Agent { Provider = "OpenAI", ModelName = "gpt-4.1-mini" }
        };
        var preferredModels = new[]
        {
            "openai/gpt-5.6-luna",
            "google/gemma-4-26b-a4b-it"
        };

        var result = LanguageService.SelectExplanationAgents(agents, preferredModels);

        result.Select(agent => $"{agent.Provider}:{agent.ModelName}")
            .Should()
            .Equal(
                "OpenRouter:mistralai/devstral-2512:free",
                "OpenAI:gpt-4.1-mini");
    }

    [Fact]
    public void BuildExplanationSystemPrompt_PreservesCriticalRulesForExamplesAndRelatedWords()
    {
        var prompt = LanguageService.BuildExplanationSystemPrompt("Chinese (Simplified)", "English");

        prompt.Should().Contain("Write the whole response in Chinese (Simplified) except for English words, phrases, IPA, and example sentences.");
        prompt.Should().Contain("The first non-empty line must be only the canonical word or phrase in bold");
        prompt.Should().Contain("Only silently correct the input when it is clearly a typo");
        prompt.Should().Contain("If the input is a phrase, idiom, or comparison like \"A vs B\"");
        prompt.Should().Contain("Provide 2 to 4 natural English examples");
        prompt.Should().Contain("Include a related words or phrases section with 3 to 5 relevant English items.");
        prompt.Should().Contain("Do not omit the example sentences section or the related words section unless the input makes that impossible.");
        prompt.Should().Contain("Input: ptofess -> first line: **profess**");
        prompt.Length.Should().BeLessThan(2200);
    }
}
