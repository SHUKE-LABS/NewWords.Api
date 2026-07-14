namespace NewWords.Api.Models
{
    /// <summary>
    /// App Store Server API credentials, bound from the <c>AppStore</c> configuration section
    /// (appsettings / env / Redis). All values are secrets in production and ship as placeholders
    /// in <c>appsettings.json</c>; see CLAUDE.md for the rollout note. Apple verification is not
    /// validated at boot (iOS is not live yet), so a placeholder/empty config must not crash the
    /// app — the verification endpoint surfaces a clear error instead.
    /// </summary>
    public class AppStoreConfig
    {
        /// <summary>The app's bundle identifier (must match the signed transaction's bundleId).</summary>
        public string BundleId { get; set; } = string.Empty;

        /// <summary>The private key ID from the App Store Connect Keys page.</summary>
        public string KeyId { get; set; } = string.Empty;

        /// <summary>The issuer ID from the App Store Connect Keys page.</summary>
        public string IssuerId { get; set; } = string.Empty;

        /// <summary>
        /// The ES256 <c>.p8</c> private key, either as a raw PEM (<c>-----BEGIN PRIVATE KEY-----</c>,
        /// convenient for Redis/Local) or base64-encoded PEM text (single-line, convenient for the
        /// deploy secret since a multiline PEM does not <c>sed</c>-substitute into JSON cleanly).
        /// </summary>
        public string PrivateKey { get; set; } = string.Empty;

        /// <summary>
        /// Pins the verification environment: "Production" or "Sandbox". When empty, verification
        /// tries Production first and falls back to Sandbox on a TransactionIdNotFound error, which
        /// is the correct behaviour for a mix of live and sandbox transactions.
        /// </summary>
        public string Environment { get; set; } = string.Empty;
    }
}
