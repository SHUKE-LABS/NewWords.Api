using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NewWords.Api.Constants;
using NewWords.Api.Exceptions;
using NewWords.Api.Models.DTOs.Entitlement;
using NewWords.Api.Services.AppleAppStore;
using NewWords.Api.Services.interfaces;
using Xunit;

namespace NewWords.Api.Tests.Services
{
    public class AppStoreServiceTests
    {
        private readonly Mock<IAppleTransactionVerifier> _verifierMock = new();
        private readonly Mock<IEntitlementService> _entitlementMock = new();

        private const int UserId = 42;
        private const string TransactionId = "txn-123";
        private const string OriginalTransactionId = "orig-999";

        private AppStoreService CreateService() => new(_verifierMock.Object, _entitlementMock.Object);

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private void SetupVerified(AppleVerifiedTransaction transaction)
            => _verifierMock.Setup(v => v.VerifyAsync(TransactionId)).ReturnsAsync(transaction);

        [Fact]
        public async Task VerifyAndGrant_ActiveSubscription_UpsertsEntitlementWithSecondsStoreAndOriginalTxn()
        {
            var expiresMs = NowMs() + 30L * 24 * 60 * 60 * 1000; // ~30 days out
            SetupVerified(new AppleVerifiedTransaction(OriginalTransactionId, "premium.monthly", expiresMs, 0));
            var expectedStatus = new EntitlementStatusDto { Plan = "premium" };
            _entitlementMock.Setup(e => e.GetStatusAsync(UserId)).ReturnsAsync(expectedStatus);

            var service = CreateService();
            var result = await service.VerifyAndGrantAsync(UserId, TransactionId);

            result.Should().BeSameAs(expectedStatus);
            _entitlementMock.Verify(e => e.UpsertAsync(
                UserId,
                expiresMs / 1000,                       // stored as UNIX seconds
                EntitlementConstants.AppleStore,        // "appstore"
                OriginalTransactionId), Times.Once);
        }

        [Fact]
        public async Task VerifyAndGrant_ExpiredSubscription_RejectsAndGrantsNothing()
        {
            var expiredMs = NowMs() - 1000;
            SetupVerified(new AppleVerifiedTransaction(OriginalTransactionId, "premium.monthly", expiredMs, 0));

            var service = CreateService();
            var act = () => service.VerifyAndGrantAsync(UserId, TransactionId);

            (await act.Should().ThrowAsync<BusinessException>())
                .Which.ErrorCode.Should().Be(EntitlementConstants.AppleVerificationFailedErrorCode);
            _entitlementMock.Verify(e => e.UpsertAsync(
                It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task VerifyAndGrant_RevokedTransaction_RejectsAndGrantsNothing()
        {
            var futureMs = NowMs() + 30L * 24 * 60 * 60 * 1000;
            // Not expired, but revoked (refunded) -> RevocationDate is set.
            SetupVerified(new AppleVerifiedTransaction(OriginalTransactionId, "premium.monthly", futureMs, NowMs()));

            var service = CreateService();
            var act = () => service.VerifyAndGrantAsync(UserId, TransactionId);

            (await act.Should().ThrowAsync<BusinessException>())
                .Which.ErrorCode.Should().Be(EntitlementConstants.AppleVerificationFailedErrorCode);
            _entitlementMock.Verify(e => e.UpsertAsync(
                It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task VerifyAndGrant_TransactionNotFound_RejectsAndGrantsNothing()
        {
            _verifierMock.Setup(v => v.VerifyAsync(TransactionId))
                .ThrowsAsync(new AppleTransactionNotFoundException("unknown"));

            var service = CreateService();
            var act = () => service.VerifyAndGrantAsync(UserId, TransactionId);

            (await act.Should().ThrowAsync<BusinessException>())
                .Which.ErrorCode.Should().Be(EntitlementConstants.AppleVerificationFailedErrorCode);
            _entitlementMock.Verify(e => e.UpsertAsync(
                It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task VerifyAndGrant_TamperedTransaction_RejectsAndGrantsNothing()
        {
            _verifierMock.Setup(v => v.VerifyAsync(TransactionId))
                .ThrowsAsync(new AppleReceiptVerificationException("bad signature"));

            var service = CreateService();
            var act = () => service.VerifyAndGrantAsync(UserId, TransactionId);

            (await act.Should().ThrowAsync<BusinessException>())
                .Which.ErrorCode.Should().Be(EntitlementConstants.AppleVerificationFailedErrorCode);
            _entitlementMock.Verify(e => e.UpsertAsync(
                It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task VerifyAndGrant_Restore_GrantsPremiumFromVerifiedRecord()
        {
            // Restore on a new device sends the current transaction id through the same path; the
            // grant must succeed and the fresh status returned.
            var expiresMs = NowMs() + 7L * 24 * 60 * 60 * 1000;
            SetupVerified(new AppleVerifiedTransaction(OriginalTransactionId, "premium.monthly", expiresMs, 0));
            var premiumStatus = new EntitlementStatusDto { Plan = "premium", PremiumExpiresAt = expiresMs / 1000 };
            _entitlementMock.Setup(e => e.GetStatusAsync(UserId)).ReturnsAsync(premiumStatus);

            var service = CreateService();
            var result = await service.VerifyAndGrantAsync(UserId, TransactionId);

            result.Plan.Should().Be("premium");
            _entitlementMock.Verify(e => e.UpsertAsync(
                UserId, expiresMs / 1000, EntitlementConstants.AppleStore, OriginalTransactionId), Times.Once);
            _entitlementMock.Verify(e => e.GetStatusAsync(UserId), Times.Once);
        }
    }
}
