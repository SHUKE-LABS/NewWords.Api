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
                ["Agents:0:Models:0"] = "google/gemma-4-26b-a4b-it",
                ["Agents:0:Models:1"] = "anthropic/claude-3.5-haiku",
            })
            .Build();

        var service = new ConfigurationService(configuration);

        service.Agents.Select(a => $"{a.Provider}:{a.ModelName}:{a.BaseUrl}:{a.ApiKey}")
            .Should()
            .Equal(
                "openrouter:google/gemma-4-26b-a4b-it:https://openrouter.ai/api/v1:redis-key",
                "openrouter:anthropic/claude-3.5-haiku:https://openrouter.ai/api/v1:redis-key");
    }

    [Fact]
    public void PreferredExplanationModels_BindFromFlatIndexedKeys_AsEmittedByRedisProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Explanation:PreferredModels:0"] = "google/gemma-4-26b-a4b-it",
                ["Explanation:PreferredModels:1"] = "anthropic/claude-3.5-haiku",
            })
            .Build();

        var service = new ConfigurationService(configuration);

        service.PreferredExplanationModels
            .Should()
            .Equal("google/gemma-4-26b-a4b-it", "anthropic/claude-3.5-haiku");
    }
}
