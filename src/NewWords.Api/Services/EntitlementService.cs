using Api.Framework;
using Microsoft.Extensions.Configuration;
using NewWords.Api.Constants;
using NewWords.Api.Entities;
using NewWords.Api.Models.DTOs.Entitlement;
using NewWords.Api.Repositories;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services
{
    public class EntitlementService(
        IRepositoryBase<UserEntitlement> entitlementRepository,
        IUserWordRepository userWordRepository,
        IConfiguration configuration)
        : IEntitlementService
    {
        // Re-read on every access so a Redis/ConfigManager change is picked up without a
        // process restart. A missing or non-positive value falls back to the default.
        public int FreeWordCap
        {
            get
            {
                var configured = configuration.GetValue<int>(EntitlementConstants.FreeWordCapConfigKey);
                return configured > 0 ? configured : EntitlementConstants.DefaultFreeWordCap;
            }
        }

        public async Task<bool> IsPremiumAsync(int userId)
        {
            var entitlement = await entitlementRepository.GetFirstOrDefaultAsync(e => e.UserId == userId);
            return IsPremium(entitlement);
        }

        public async Task<EntitlementStatusDto> GetStatusAsync(int userId)
        {
            var entitlement = await entitlementRepository.GetFirstOrDefaultAsync(e => e.UserId == userId);
            var isPremium = IsPremium(entitlement);
            var savedWordCount = await userWordRepository.GetUserWordsCountAsync(userId, null);

            return new EntitlementStatusDto
            {
                Plan = isPremium ? "premium" : "free",
                PremiumExpiresAt = entitlement?.PremiumExpiresAt,
                SavedWordCount = savedWordCount,
                WordCap = FreeWordCap
            };
        }

        public async Task UpsertAsync(int userId, long? premiumExpiresAt, string? store, string? originalTransactionId)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var existing = await entitlementRepository.GetFirstOrDefaultAsync(e => e.UserId == userId);

            if (existing == null)
            {
                await entitlementRepository.InsertAsync(new UserEntitlement
                {
                    UserId = userId,
                    PremiumExpiresAt = premiumExpiresAt,
                    Store = store,
                    OriginalTransactionId = originalTransactionId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                return;
            }

            existing.PremiumExpiresAt = premiumExpiresAt;
            existing.Store = store;
            existing.OriginalTransactionId = originalTransactionId;
            existing.UpdatedAt = now;
            await entitlementRepository.UpdateAsync(existing);
        }

        private static bool IsPremium(UserEntitlement? entitlement)
        {
            if (entitlement?.PremiumExpiresAt == null) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < entitlement.PremiumExpiresAt.Value;
        }
    }
}
