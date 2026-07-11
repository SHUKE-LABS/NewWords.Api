using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Models.DTOs.User;
using NewWords.Api.Services.interfaces;
using Api.Framework.Result;
using NewWords.Api.Exceptions;

namespace NewWords.Api.Controllers
{
    [Authorize]
    public class AccountController(IAccountService accountService) : BaseController
    {
        /// <summary>
        /// Updates the current user's language preferences.
        /// </summary>
        /// <param name="updateDto">The language preferences to update.</param>
        /// <returns>Confirmation of update.</returns>
        [HttpPut]
        public async Task<ApiResult> UpdateLanguages([FromBody] UpdateLanguagesRequestDto updateDto)
        {
            var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!long.TryParse(userIdString, out var userId))
            {
                throw new BusinessException("Invalid user ID.");
            }

            var success = await accountService.UpdateUserLanguagesAsync(userId, updateDto);
            if (!success)
            {
                return Fail("User not found or language update failed.");
            }

            return Success("Languages updated successfully.");
        }
    }
}