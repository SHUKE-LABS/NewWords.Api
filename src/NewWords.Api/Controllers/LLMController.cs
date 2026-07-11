using Microsoft.AspNetCore.Mvc;
using Api.Framework.Result;
using LLM.Models;
using Microsoft.AspNetCore.Authorization;
using LLM;

namespace NewWords.Api.Controllers;

/// <summary>
/// Controller for testing LLM services including language recognition and word explanations.
/// </summary>
[Authorize]
public class LlmController(
    ILanguageService languageService,
    IConfigurationService configurationService)
    : BaseController
{
    /// <summary>
    /// Endpoint to recognize the language of a given text. We should use a speedy language recognition model for it
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    [HttpGet]
    public async Task<ApiResult> RecognizeLanguage([FromQuery] string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Fail("Text parameter is required.");
        }
        var result = await languageService.GetDetectedLanguageWithFallbackAsync(text);
        return new SuccessfulResult<LanguageDetectionResult>(result);
    }

    [HttpGet]
    public async Task<ApiResult> ExplainWordMarkdown([FromQuery] string text, [FromQuery] string targetLanguage)
    {
        if (string.IsNullOrEmpty(text)) return Fail("Text parameter is required.");
        if (string.IsNullOrEmpty(targetLanguage)) return Fail("Target language parameter is required.");

        var nativeLanguageName = configurationService.GetLanguageName("zh-CN") ?? "Chinese (Simplified)";
        var targetLanguageName = configurationService.GetLanguageName(targetLanguage) ?? targetLanguage;
        var explanationResult = await languageService.GetMarkdownExplanationWithFallbackAsync(text, nativeLanguageName, targetLanguageName);
        if (explanationResult.IsSuccess && explanationResult.Markdown != null)
        {
            return new SuccessfulResult<string>(explanationResult.Markdown);
        }

        string errorMsg = explanationResult.ErrorMessage ?? "Unknown error";
        if (explanationResult.HttpStatusCode.HasValue)
        {
            errorMsg += $" (HTTP Status: {explanationResult.HttpStatusCode.Value})";
        }
        return Fail($"Could not retrieve explanation for '{text}': {errorMsg}");
    }
}
