using FluentAssertions;
using LLM.Models;
using LLM.Services;
using Xunit;

namespace NewWords.Api.Tests.Services;

/// <summary>
/// Covers the placeholder/empty ApiKey detection introduced for issue #6 so a leftover
/// deploy placeholder cannot silently reach a provider. Validates the raw AgentConfig list.
/// </summary>
public class AgentApiKeyValidatorTests
{
    private static AgentConfig Agent(string provider, string apiKey) => new()
    {
        Provider = provider,
        BaseUrl = "https://openrouter.ai/api/v1",
        ApiKey = apiKey,
        Models = ["google/gemma-4-26b-a4b-it"]
    };

    [Theory]
    [InlineData("XAI_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("OPENROUTER_API_KEY")]
    public void FlagsUnsubstitutedAllCapsPlaceholder(string placeholder)
    {
        var issues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues([Agent("openrouter", placeholder)]);

        issues.Should().ContainSingle().Which.Should().Contain(placeholder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FlagsEmptyOrWhitespaceKey(string apiKey)
    {
        var issues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues([Agent("openrouter", apiKey)]);

        issues.Should().ContainSingle().Which.Should().Contain("is empty");
    }

    [Fact]
    public void FlagsKeyContainingPlaceholderSubstring()
    {
        var issues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues([Agent("openrouter", "MY_PLACEHOLDER_key")]);

        issues.Should().ContainSingle();
    }

    [Theory]
    [InlineData("sk-or-v1-0a1b2c3d4e5f")]
    [InlineData("sk-proj-abc123def456")]
    public void DoesNotFlagRealisticKey(string apiKey)
    {
        var issues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues([Agent("openrouter", apiKey)]);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void EmitsOneIssuePerAgentNotPerModel()
    {
        var bad = new AgentConfig
        {
            Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = "XAI_API_KEY",
            Models = ["google/gemma-4-26b-a4b-it", "anthropic/claude-3.5-haiku"]
        };

        var issues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues([bad, Agent("openai", "sk-real-key-lower")]);

        issues.Should().ContainSingle().Which.Should().Contain("Agents:0");
    }
}
