using Api.Framework.Result;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Exceptions;
using NewWords.Api.Models.DTOs.Entitlement;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Controllers
{
    [Authorize]
    public class EntitlementController(
        IEntitlementService entitlementService,
        ICurrentUser currentUser)
        : BaseController
    {
        /// <summary>
        /// Returns the current user's entitlement status: plan (free/premium), premium expiry,
        /// current saved-word count, and the configured free-word cap.
        /// </summary>
        [HttpGet]
        public async Task<ApiResult<EntitlementStatusDto>> Status()
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var status = await entitlementService.GetStatusAsync(userId);
            return new SuccessfulResult<EntitlementStatusDto>(status);
        }
    }
}
