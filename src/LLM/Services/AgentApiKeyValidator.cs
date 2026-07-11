using System.Text.RegularExpressions;
using LLM.Models;

namespace LLM.Services;

/// <summary>
/// Detects LLM agent <see cref="AgentConfig.ApiKey"/> values that are empty or an
/// unsubstituted deploy placeholder (e.g. the committed <c>"XAI_API_KEY"</c> token that
/// <c>deploy_to_production.yml</c> replaces via <c>sed</c>, or a value containing
/// <c>PLACEHOLDER</c>). The runtime guard in <c>LanguageService</c> only rejects
/// null/empty keys, so without this a placeholder would be sent verbatim as a bearer
/// token and fail with a provider 401 that is invisible until a request runs. Startup
/// calls this to warn everywhere and fail fast in Production. See issue #6.
/// </summary>
public static class AgentApiKeyValidator
{
    // Real provider keys carry lowercase letters and/or hyphens (e.g. "sk-or-v1-...").
    // An all-caps token of only [A-Z0-9_] is a screaming-snake-case placeholder such as
    // "XAI_API_KEY" / "OPENAI_API_KEY" that the deploy sed step never substituted.
    private static readonly Regex UnsubstitutedPlaceholder = new("^[A-Z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns one human-readable issue per agent whose ApiKey is empty or a placeholder.
    /// Operates on the raw <see cref="AgentConfig"/> list (one entry per agent) so a single
    /// misconfigured agent with N models yields one issue, not N.
    /// </summary>
    public static List<string> FindPlaceholderApiKeyIssues(IEnumerable<AgentConfig> agents)
    {
        var issues = new List<string>();
        var index = 0;
        foreach (var agent in agents)
        {
            var apiKey = agent.ApiKey;
            string? reason = null;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                reason = "is empty";
            }
            else if (apiKey.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"contains an unsubstituted placeholder ('{apiKey}')";
            }
            else if (UnsubstitutedPlaceholder.IsMatch(apiKey))
            {
                reason = $"looks like an unsubstituted deploy placeholder ('{apiKey}')";
            }

            if (reason != null)
            {
                issues.Add(
                    $"Agents:{index} (Provider '{agent.Provider}') ApiKey {reason}. "
                    + "It must be supplied via the deploy secret substitution or Redis config.");
            }

            index++;
        }

        return issues;
    }
}
