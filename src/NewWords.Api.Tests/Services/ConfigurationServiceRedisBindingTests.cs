using FluentAssertions;
using LLM.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace NewWords.Api.Tests.Services;

/// <summary>
/// Locks the Redis-backed key layout for issue #19. ConfigManager.Provider stores
/// each Redis key "newwords.api:&lt;path&gt;" as a flat IConfiguration key "&lt;path&gt;"
/// with a string value (it does no JSON parsing). These tests feed ConfigurationService
/// the exact flat keys the provider emits and assert the existing shapes still bind,
/// so the documented layout cannot drift without a failing test — no live Redis needed.
/// </summary>
public class ConfigurationServiceRedisBindingTests
{
    [Fact]
    public void Agents_BindFromFlatIndexedKeys_AsEmittedByRedisProvider()
    {
        // Keys are what RedisConfigurationProvider produces after stripping the
        // "newwords.api:" project prefix from "newwords.api:Agents:0:Provider" etc.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:0:Provider"] = "openrouter",
                ["Agents:0:BaseUrl"] = "https://openrouter.ai/api/v1",
                ["Agents:0:ApiKey"] = "redis-key",
                ["Agents:0:Models:0"] = "openai/gpt-5.6-luna",
                ["Agents:0:Models:1"] = "google/gemma-4-26b-a4b-it",
            })
            .Build();

        var service = new ConfigurationService(configuration);

        service.Agents.Select(a => $"{a.Provider}:{a.ModelName}:{a.BaseUrl}:{a.ApiKey}")
            .Should()
            .Equal(
                "openrouter:openai/gpt-5.6-luna:https://openrouter.ai/api/v1:redis-key",
                "openrouter:google/gemma-4-26b-a4b-it:https://openrouter.ai/api/v1:redis-key");
    }

    [Fact]
    public void PreferredExplanationModels_BindFromFlatIndexedKeys_AsEmittedByRedisProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Explanation:PreferredModels:0"] = "openai/gpt-5.6-luna",
                ["Explanation:PreferredModels:1"] = "google/gemma-4-26b-a4b-it",
            })
            .Build();

        var service = new ConfigurationService(configuration);

        service.PreferredExplanationModels
            .Should()
            .Equal("openai/gpt-5.6-luna", "google/gemma-4-26b-a4b-it");
    }

    [Fact]
    public void Agents_ReflectLiveConfigurationChange_WithoutReconstruction()
    {
        // Simulates a Redis-backed reload (#20): the IConfiguration underneath the
        // long-lived singleton changes, and the next read must reflect it — no restart,
        // no new ConfigurationService instance.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:0:Provider"] = "openrouter",
                ["Agents:0:BaseUrl"] = "https://openrouter.ai/api/v1",
                ["Agents:0:ApiKey"] = "old-key",
                ["Agents:0:Models:0"] = "openai/gpt-5.6-luna",
                ["Explanation:PreferredModels:0"] = "openai/gpt-5.6-luna",
            })
            .Build();

        var service = new ConfigurationService(configuration);

        service.Agents.Single().ApiKey.Should().Be("old-key");
        service.PreferredExplanationModels.Should().Equal("openai/gpt-5.6-luna");

        // Mutate the live configuration after construction.
        configuration["Agents:0:ApiKey"] = "new-key";
        configuration["Agents:0:Models:1"] = "google/gemma-4-26b-a4b-it";
        configuration["Explanation:PreferredModels:0"] = "google/gemma-4-26b-a4b-it";

        service.Agents.Select(a => $"{a.ModelName}:{a.ApiKey}")
            .Should()
            .Equal(
                "openai/gpt-5.6-luna:new-key",
                "google/gemma-4-26b-a4b-it:new-key");
        service.PreferredExplanationModels.Should().Equal("google/gemma-4-26b-a4b-it");
    }
}
