using System.ComponentModel.DataAnnotations;

namespace NewWords.Api.Models.DTOs.Entitlement
{
    /// <summary>
    /// Client request to verify an Apple App Store purchase and (if valid and active) grant
    /// premium. The client submits the StoreKit 2 transaction id (from the purchase result or,
    /// on restore, from <c>Transaction.currentEntitlements</c>); the server fetches the
    /// authoritative signed transaction from the App Store Server API and verifies it. Restore
    /// reuses this same request.
    /// </summary>
    public class VerifyAppleTransactionRequest
    {
        [Required]
        [StringLength(255)]
        public string TransactionId { get; set; } = string.Empty;
    }
}
