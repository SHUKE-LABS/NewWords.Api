using LLM.Models;
using Microsoft.Extensions.Configuration;

namespace LLM.Services;

public class ConfigurationService(IConfiguration configuration) : IConfigurationService
{
    private Dictionary<string, string>? _languageLookup;

    public List<Language> SupportedLanguages { get; } = configuration
        .GetSection("SupportedLanguages")
        .Get<List<Language>>() ?? [];

    // Agents and PreferredExplanationModels re-bind the live IConfiguration on every
    // read so a Redis-backed reload (#20) is picked up by the next explanation request
    // without a process restart. Each read builds its own list, so concurrent reads
    // never share mutable state — no lock or torn list is possible.
    public IReadOnlyList<string> PreferredExplanationModels => configuration
        .GetSection("Explanation:PreferredModels")
        .Get<List<string>>() ?? [];

    public List<Agent> Agents => (configuration.GetSection("Agents").Get<List<AgentConfig>>() ?? [])
        .SelectMany(a => a.Models.Select(m => new Agent
        {
            Provider = a.Provider,
            ModelName = m,
            BaseUrl = a.BaseUrl,
            ApiKey = a.ApiKey
        }))
        .ToList();

    /// <summary>
    /// Gets the language name based on the provided language code.
    /// Optimized with dictionary lookup for better performance.
    /// </summary>
    /// <param name="code">The language code to look up.</param>
    /// <returns>The name of the language if found; otherwise, null.</returns>
    public string? GetLanguageName(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        _languageLookup ??= SupportedLanguages.ToDictionary(l => l.Code, l => l.Name);

        return _languageLookup.GetValueOrDefault(code);
    }


}
