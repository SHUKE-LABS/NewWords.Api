using Microsoft.Extensions.Configuration;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services.AppleAppStore
{
    /// <summary>
    /// Resolves the App Store environment and delegates the per-environment fetch+verify to
    /// <see cref="IAppStoreTransactionClient"/>. Owns only the environment-selection/fallback
    /// branch, which is unit-tested with a mocked client.
    /// </summary>
    public class AppleTransactionVerifier(
        IAppStoreTransactionClient client,
        IConfiguration configuration)
        : IAppleTransactionVerifier
    {
        public async Task<AppleVerifiedTransaction> VerifyAsync(string transactionId)
        {
            // Re-read on each call so a Redis/ConfigManager change to the pinned environment takes
            // effect without a restart, consistent with the free-word cap.
            var pinned = configuration["AppStore:Environment"];

            if (string.Equals(pinned, "Sandbox", StringComparison.OrdinalIgnoreCase))
            {
                return await client.GetVerifiedTransactionAsync(AppleEnv.Sandbox, transactionId);
            }

            if (string.Equals(pinned, "Production", StringComparison.OrdinalIgnoreCase))
            {
                return await client.GetVerifiedTransactionAsync(AppleEnv.Production, transactionId);
            }

            // Unset: query Production first, then fall back to Sandbox when Apple reports the
            // transaction is unknown there (a sandbox transaction looked up in Production).
            try
            {
                return await client.GetVerifiedTransactionAsync(AppleEnv.Production, transactionId);
            }
            catch (AppleTransactionNotFoundException)
            {
                return await client.GetVerifiedTransactionAsync(AppleEnv.Sandbox, transactionId);
            }
        }
    }
}
