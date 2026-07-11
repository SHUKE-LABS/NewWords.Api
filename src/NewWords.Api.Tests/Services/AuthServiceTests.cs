using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Api.Framework.Helper;
using Api.Framework.Models;
using FluentAssertions;
using Moq;
using NewWords.Api.Entities;
using NewWords.Api.Models.DTOs.Auth;
using NewWords.Api.Repositories;
using NewWords.Api.Services;
using Xunit;

namespace NewWords.Api.Tests.Services
{
    public class AuthServiceTests
    {
        private readonly Mock<IUserRepository> _userRepoMock = new();

        private static readonly JwtConfig JwtConfig = new()
        {
            // HmacSha256 requires a key of at least 256 bits (32 bytes).
            SymmetricSecurityKey = "unit-test-symmetric-security-key-0123456789",
            Issuer = "unit-tests",
            TokenExpiresInDays = 1,
        };

        private AuthService CreateService() => new(_userRepoMock.Object);

        private static User LegacyUser(string email, string password, string salt)
        {
            return new User
            {
                Id = 42,
                Email = email,
                Salt = salt,
                PasswordHash = CommonHelper.CalculateSha256Hash(password + salt),
                NativeLanguage = "en-US",
                CurrentLearningLanguage = "zh-CN",
            };
        }

        private void SetupLookup(User? user)
        {
            _userRepoMock.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(user);
            _userRepoMock.Setup(r => r.GetFirstOrDefaultAsync(
                    It.IsAny<Expression<Func<User, bool>>>(), null))
                .ReturnsAsync(user);
        }

        [Fact]
        public async Task RegisterAsync_StoresBcryptHash()
        {
            User? inserted = null;
            _userRepoMock.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
            _userRepoMock.Setup(r => r.InsertReturnIdentityAsync(It.IsAny<User>()))
                .Callback<User>(u => inserted = u)
                .ReturnsAsync(1);

            var service = CreateService();
            var request = new RegisterRequest
            {
                Email = "New@Example.com",
                Password = "correct horse battery staple",
                NativeLanguage = "en-US",
                LearningLanguage = "zh-CN",
            };

            await service.RegisterAsync(request, JwtConfig);

            inserted.Should().NotBeNull();
            inserted!.PasswordHash.Should().StartWith("$2");
            BCrypt.Net.BCrypt.Verify(request.Password, inserted.PasswordHash).Should().BeTrue();
        }

        [Fact]
        public async Task LoginAsync_BcryptUser_CorrectPassword_Succeeds()
        {
            const string password = "s3cret-pass";
            var user = new User
            {
                Id = 7,
                Email = "bob@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11),
                NativeLanguage = "en-US",
                CurrentLearningLanguage = "zh-CN",
            };
            SetupLookup(user);

            var service = CreateService();
            var session = await service.LoginAsync(
                new LoginRequest { Email = user.Email, Password = password }, JwtConfig);

            session.Token.Should().NotBeNullOrWhiteSpace();
            _userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
        }

        [Fact]
        public async Task LoginAsync_BcryptUser_WrongPassword_Throws()
        {
            var user = new User
            {
                Id = 7,
                Email = "bob@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("right", workFactor: 11),
                NativeLanguage = "en-US",
                CurrentLearningLanguage = "zh-CN",
            };
            SetupLookup(user);

            var service = CreateService();
            var act = () => service.LoginAsync(
                new LoginRequest { Email = user.Email, Password = "wrong" }, JwtConfig);

            await act.Should().ThrowAsync<Exception>();
            _userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
        }

        [Fact]
        public async Task LoginAsync_LegacyUser_CorrectPassword_UpgradesToBcrypt()
        {
            const string password = "legacy-pass";
            var user = LegacyUser("alice@example.com", password, "some-salt");
            SetupLookup(user);
            User? updated = null;
            _userRepoMock.Setup(r => r.UpdateAsync(It.IsAny<User>()))
                .Callback<User>(u => updated = u)
                .ReturnsAsync(true);

            var service = CreateService();
            var session = await service.LoginAsync(
                new LoginRequest { Email = user.Email, Password = password }, JwtConfig);

            session.Token.Should().NotBeNullOrWhiteSpace();
            // Rehashed and persisted.
            _userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Once);
            updated.Should().NotBeNull();
            updated!.PasswordHash.Should().StartWith("$2");
            BCrypt.Net.BCrypt.Verify(password, updated.PasswordHash).Should().BeTrue();
            // The session-carrying instance is the upgraded one.
            user.PasswordHash.Should().StartWith("$2");
        }

        [Fact]
        public async Task LoginAsync_LegacyUser_WrongPassword_Throws_NoUpgrade()
        {
            var user = LegacyUser("alice@example.com", "legacy-pass", "some-salt");
            SetupLookup(user);

            var service = CreateService();
            var act = () => service.LoginAsync(
                new LoginRequest { Email = user.Email, Password = "wrong" }, JwtConfig);

            await act.Should().ThrowAsync<Exception>();
            _userRepoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
        }
    }
}
