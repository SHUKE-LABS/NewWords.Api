using System.Text;
using Microsoft.Extensions.Configuration;
using Mimo.AppStoreServerLibrary;
using Mimo.AppStoreServerLibrary.Exceptions;
using Mimo.AppStoreServerLibrary.Models;
using NewWords.Api.Constants;
using NewWords.Api.Exceptions;
using NewWords.Api.Models;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services.AppleAppStore
{
    /// <summary>
    /// Concrete App Store Server API boundary using <c>Mimo.AppStoreServerLibrary</c>: fetches the
    /// authoritative signed transaction for one environment and validates its JWS against Apple's
    /// certificate chain. This is the network + Apple-crypto layer, verified by a manual sandbox
    /// run rather than CI (no Apple credentials in the pipeline). The library brings its own
    /// <c>HttpClient</c>, so no HTTP-client convention is introduced in this project.
    /// </summary>
    public class AppStoreTransactionClient(IConfiguration configuration) : IAppStoreTransactionClient
    {
        // Apple's TransactionIdNotFoundError; also surfaces as HTTP 404. Looking up a sandbox
        // transaction in Production returns this, which is what drives the environment fallback.
        private const int TransactionIdNotFoundErrorCode = 4040010;

        // Apple Root CA - G3 (the ECC root the StoreKit JWS chain terminates at). Committed under
        // Certificates/ and copied to the output directory; loaded once.
        private static readonly Lazy<byte[]> AppleRootCertificate = new(() =>
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Certificates", "AppleRootCA-G3.cer")));

        public async Task<AppleVerifiedTransaction> GetVerifiedTransactionAsync(AppleEnv env, string transactionId)
        {
            var config = LoadConfig();
            var environment = env == AppleEnv.Sandbox
                ? AppStoreEnvironment.Sandbox
                : AppStoreEnvironment.Production;

            var apiClient = new AppStoreServerApiClient(
                NormalizePrivateKey(config.PrivateKey),
                config.KeyId,
                config.IssuerId,
                config.BundleId,
                environment);

            TransactionInfoResponse? response;
            try
            {
                response = await apiClient.GetTransactionInfo(transactionId);
            }
            catch (ApiException ex) when (ex.ApiErrorCode == TransactionIdNotFoundErrorCode
                                          || ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new AppleTransactionNotFoundException(
                    $"Transaction {transactionId} not found in {environment.Name}.");
            }
            catch (ApiException ex)
            {
                throw new AppleReceiptVerificationException(
                    $"App Store Server API error verifying transaction {transactionId}.", ex);
            }

            var signedTransaction = response?.SignedTransactionInfo;
            if (string.IsNullOrEmpty(signedTransaction))
            {
                throw new AppleReceiptVerificationException(
                    $"App Store Server API returned no signed transaction for {transactionId}.");
            }

            var verifier = new SignedDataVerifier(
                [AppleRootCertificate.Value],
                enableOnlineChecks: true,
                environment,
                config.BundleId);

            JwsTransactionDecodedPayload payload;
            try
            {
                payload = await verifier.VerifyAndDecodeTransaction(signedTransaction);
            }
            catch (VerificationException ex)
            {
                throw new AppleReceiptVerificationException(
                    $"Signature verification failed for transaction {transactionId}.", ex);
            }

            // Defence in depth: the JWS is Apple-signed, but confirm it is for this app.
            if (!string.Equals(payload.BundleId, config.BundleId, StringComparison.Ordinal))
            {
                throw new AppleReceiptVerificationException(
                    $"BundleId mismatch for transaction {transactionId}.");
            }

            return new AppleVerifiedTransaction(
                payload.OriginalTransactionId,
                payload.ProductId,
                payload.ExpiresDate,
                payload.RevocationDate);
        }

        private AppStoreConfig LoadConfig()
        {
            var config = configuration.GetSection("AppStore").Get<AppStoreConfig>() ?? new AppStoreConfig();

            // Not validated at boot (iOS is not live yet); fail here with a clear, client-safe
            // message instead of crash-looping the app. Placeholder tokens count as unconfigured.
            if (IsUnset(config.BundleId) || IsUnset(config.KeyId)
                || IsUnset(config.IssuerId) || IsUnset(config.PrivateKey))
            {
                throw new BusinessException(
                    "Apple App Store verification is not configured on the server.",
                    EntitlementConstants.AppleVerificationFailedErrorCode);
            }

            return config;
        }

        // Empty or still the committed ALL_CAPS placeholder (e.g. "APPLE_BUNDLE_ID").
        private static bool IsUnset(string value)
            => string.IsNullOrWhiteSpace(value) || value.StartsWith("APPLE_", StringComparison.Ordinal);

        // Accept either a raw PEM or base64-encoded PEM text (the deploy-secret-friendly form).
        private static string NormalizePrivateKey(string key)
        {
            if (key.Contains("BEGIN", StringComparison.Ordinal))
            {
                return key;
            }

            var pemBytes = Convert.FromBase64String(key.Trim());
            return Encoding.UTF8.GetString(pemBytes);
        }
    }
}
