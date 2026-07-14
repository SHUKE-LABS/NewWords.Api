using Api.Framework.Result;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Constants;
using NewWords.Api.Exceptions;
using NewWords.Api.Models.DTOs.Entitlement;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Controllers
{
    [Authorize]
    public class EntitlementController(
        IEntitlementService entitlementService,
        IAppStoreService appStoreService,
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

        /// <summary>
        /// Verifies an Apple App Store transaction via the App Store Server API and, when it is a
        /// valid active subscription, grants/renews premium. Restore reuses this endpoint. Returns
        /// the user's fresh entitlement status; invalid/expired/tampered/revoked transactions grant
        /// nothing and fail with <see cref="EntitlementConstants.AppleVerificationFailedErrorCode"/>.
        /// </summary>
        [HttpPost]
        public async Task<ApiResult<EntitlementStatusDto>> VerifyApple([FromBody] VerifyAppleTransactionRequest request)
        {
            var userId = currentUser.Id;
            if (userId == 0)
            {
                throw new BusinessException("User not authenticated or ID not found.");
            }

            var status = await appStoreService.VerifyAndGrantAsync(userId, request.TransactionId);
            return new SuccessfulResult<EntitlementStatusDto>(status);
        }
    }
}
