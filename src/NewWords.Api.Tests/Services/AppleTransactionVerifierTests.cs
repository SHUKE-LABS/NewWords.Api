using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NewWords.Api.Services.AppleAppStore;
using NewWords.Api.Services.interfaces;
using Xunit;

namespace NewWords.Api.Tests.Services
{
    public class AppleTransactionVerifierTests
    {
        private readonly Mock<IAppStoreTransactionClient> _clientMock = new();

        private const string TransactionId = "txn-abc";

        private static readonly AppleVerifiedTransaction ProdResult =
            new("orig-prod", "premium.monthly", 111, 0);
        private static readonly AppleVerifiedTransaction SandboxResult =
            new("orig-sandbox", "premium.monthly", 222, 0);

        private static IConfiguration Config(string? environment)
        {
            var dict = new Dictionary<string, string?>();
            if (environment != null)
            {
                dict["AppStore:Environment"] = environment;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private AppleTransactionVerifier CreateVerifier(string? environment)
            => new(_clientMock.Object, Config(environment));

        [Fact]
        public async Task Unset_ProductionSucceeds_UsesProductionAndSkipsSandbox()
        {
            _clientMock.Setup(c => c.GetVerifiedTransactionAsync(AppleEnv.Production, TransactionId))
                .ReturnsAsync(ProdResult);

            var result = await CreateVerifier(environment: "").VerifyAsync(TransactionId);

            result.Should().BeSameAs(ProdResult);
            _clientMock.Verify(c => c.GetVerifiedTransactionAsync(AppleEnv.Sandbox, It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Unset_ProductionNotFound_FallsBackToSandbox()
        {
            _clientMock.Setup(c => c.GetVerifiedTransactionAsync(AppleEnv.Production, TransactionId))
                .ThrowsAsync(new AppleTransactionNotFoundException("not in prod"));
            _clientMock.Setup(c => c.GetVerifiedTransactionAsync(AppleEnv.Sandbox, TransactionId))
                .ReturnsAsync(SandboxResult);

            var result = await CreateVerifier(environment: "").VerifyAsync(TransactionId);

            result.Should().BeSameAs(SandboxResult);
            _clientMock.Verify(c => c.GetVerifiedTransactionAsync(AppleEnv.Production, TransactionId), Times.Once);
            _clientMock.Verify(c => c.GetVerifiedTransactionAsync(AppleEnv.Sandbox, TransactionId), Times.Once);
        }

        [Fact]
        public async Task PinnedSandbox_UsesSandboxOnly()
        {
            _clientMock.Setup(c => c.GetVerifiedTransactionAsync(AppleEnv.Sandbox, TransactionId))
                .ReturnsAsync(SandboxResult);

            var result = await CreateVerifier(environment: "Sandbox").VerifyAsync(TransactionId);

            result.Should().BeSameAs(SandboxResult);
            _clientMock.Verify(c => c.GetVerifiedTransactionAsync(AppleEnv.Production, It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task PinnedProduction_NotFound_DoesNotFallBack()
        {
            _clientMock.Setup(c => c.GetVerifiedTransactionAsync(AppleEnv.Production, TransactionId))
                .ThrowsAsync(new AppleTransactionNotFoundException("not in prod"));

            var act = () => CreateVerifier(environment: "Production").VerifyAsync(TransactionId);

            await act.Should().ThrowAsync<AppleTransactionNotFoundException>();
            _clientMock.Verify(c => c.GetVerifiedTransactionAsync(AppleEnv.Sandbox, It.IsAny<string>()), Times.Never);
        }
    }
}
