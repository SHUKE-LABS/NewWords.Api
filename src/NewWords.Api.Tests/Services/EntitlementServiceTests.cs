using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Api.Framework;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NewWords.Api.Constants;
using NewWords.Api.Entities;
using NewWords.Api.Repositories;
using NewWords.Api.Services;
using Xunit;

namespace NewWords.Api.Tests.Services
{
    public class EntitlementServiceTests
    {
        private readonly Mock<IRepositoryBase<UserEntitlement>> _entitlementRepoMock = new();
        private readonly Mock<IUserWordRepository> _userWordRepoMock = new();

        private static IConfiguration BuildConfig(string? freeWordCap = null)
        {
            var dict = new Dictionary<string, string?>();
            if (freeWordCap != null)
            {
                dict[EntitlementConstants.FreeWordCapConfigKey] = freeWordCap;
            }
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        private EntitlementService CreateService(IConfiguration? configuration = null)
            => new(_entitlementRepoMock.Object, _userWordRepoMock.Object, configuration ?? BuildConfig());

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private void SetupEntitlement(UserEntitlement? entitlement)
            => _entitlementRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<UserEntitlement, bool>>>(), It.IsAny<string?>()))
                .ReturnsAsync(entitlement);

        // ---- FreeWordCap ----

        [Fact]
        public void FreeWordCap_DefaultsWhenUnset()
        {
            var service = CreateService(BuildConfig(freeWordCap: null));
            service.FreeWordCap.Should().Be(EntitlementConstants.DefaultFreeWordCap);
        }

        [Fact]
        public void FreeWordCap_UsesConfiguredValue()
        {
            var service = CreateService(BuildConfig(freeWordCap: "1000"));
            service.FreeWordCap.Should().Be(1000);
        }

        [Fact]
        public void FreeWordCap_FallsBackWhenNonPositive()
        {
            var service = CreateService(BuildConfig(freeWordCap: "0"));
            service.FreeWordCap.Should().Be(EntitlementConstants.DefaultFreeWordCap);
        }

        // ---- IsPremiumAsync ----

        [Fact]
        public async Task IsPremiumAsync_NoRow_False()
        {
            SetupEntitlement(null);
            var service = CreateService();
            (await service.IsPremiumAsync(1)).Should().BeFalse();
        }

        [Fact]
        public async Task IsPremiumAsync_NullExpiry_False()
        {
            SetupEntitlement(new UserEntitlement { UserId = 1, PremiumExpiresAt = null });
            var service = CreateService();
            (await service.IsPremiumAsync(1)).Should().BeFalse();
        }

        [Fact]
        public async Task IsPremiumAsync_ExpiredInPast_False()
        {
            SetupEntitlement(new UserEntitlement { UserId = 1, PremiumExpiresAt = Now() - 60 });
            var service = CreateService();
            (await service.IsPremiumAsync(1)).Should().BeFalse();
        }

        [Fact]
        public async Task IsPremiumAsync_UnexpiredInFuture_True()
        {
            SetupEntitlement(new UserEntitlement { UserId = 1, PremiumExpiresAt = Now() + 3600 });
            var service = CreateService();
            (await service.IsPremiumAsync(1)).Should().BeTrue();
        }

        // ---- GetStatusAsync ----

        [Fact]
        public async Task GetStatusAsync_FreeUser_ReturnsFreePlanAndCounts()
        {
            SetupEntitlement(null);
            _userWordRepoMock.Setup(r => r.GetUserWordsCountAsync(1, null)).ReturnsAsync(42);
            var service = CreateService(BuildConfig(freeWordCap: "500"));

            var status = await service.GetStatusAsync(1);

            status.Plan.Should().Be("free");
            status.PremiumExpiresAt.Should().BeNull();
            status.SavedWordCount.Should().Be(42);
            status.WordCap.Should().Be(500);
        }

        [Fact]
        public async Task GetStatusAsync_PremiumUser_ReturnsPremiumPlanAndExpiry()
        {
            var expiry = Now() + 3600;
            SetupEntitlement(new UserEntitlement { UserId = 1, PremiumExpiresAt = expiry });
            _userWordRepoMock.Setup(r => r.GetUserWordsCountAsync(1, null)).ReturnsAsync(1000);
            var service = CreateService(BuildConfig(freeWordCap: "500"));

            var status = await service.GetStatusAsync(1);

            status.Plan.Should().Be("premium");
            status.PremiumExpiresAt.Should().Be(expiry);
            status.SavedWordCount.Should().Be(1000);
            status.WordCap.Should().Be(500);
        }

        // ---- UpsertAsync ----

        [Fact]
        public async Task UpsertAsync_NoExistingRow_Inserts()
        {
            SetupEntitlement(null);
            UserEntitlement? inserted = null;
            _entitlementRepoMock.Setup(r => r.InsertAsync(It.IsAny<UserEntitlement>()))
                .Callback<UserEntitlement>(e => inserted = e)
                .ReturnsAsync(true);
            var service = CreateService();

            var expiry = Now() + 3600;
            await service.UpsertAsync(1, expiry, "appstore", "txn-1");

            inserted.Should().NotBeNull();
            inserted!.UserId.Should().Be(1);
            inserted.PremiumExpiresAt.Should().Be(expiry);
            inserted.Store.Should().Be("appstore");
            inserted.OriginalTransactionId.Should().Be("txn-1");
            inserted.CreatedAt.Should().BeGreaterThan(0);
            inserted.UpdatedAt.Should().Be(inserted.CreatedAt);
            _entitlementRepoMock.Verify(r => r.UpdateAsync(It.IsAny<UserEntitlement>()), Times.Never);
        }

        [Fact]
        public async Task UpsertAsync_ExistingRow_Updates()
        {
            var existing = new UserEntitlement
            {
                Id = 9, UserId = 1, PremiumExpiresAt = Now() - 10,
                Store = "old", OriginalTransactionId = "old-txn",
                CreatedAt = 111, UpdatedAt = 111
            };
            SetupEntitlement(existing);
            _entitlementRepoMock.Setup(r => r.UpdateAsync(It.IsAny<UserEntitlement>())).ReturnsAsync(true);
            var service = CreateService();

            var expiry = Now() + 7200;
            await service.UpsertAsync(1, expiry, "playstore", "txn-2");

            existing.PremiumExpiresAt.Should().Be(expiry);
            existing.Store.Should().Be("playstore");
            existing.OriginalTransactionId.Should().Be("txn-2");
            existing.UpdatedAt.Should().BeGreaterThan(111);
            _entitlementRepoMock.Verify(r => r.UpdateAsync(existing), Times.Once);
            _entitlementRepoMock.Verify(r => r.InsertAsync(It.IsAny<UserEntitlement>()), Times.Never);
        }
    }
}
