using NewWords.Api.Models.DTOs.Auth;
using NewWords.Api.Entities;
// For password hashing
// For reading config
// For JWT generation
using Api.Framework.Extensions;
using Api.Framework.Helper;
using Api.Framework.Models;
using NewWords.Api.Models;
using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services
{
    public class AuthService(Repositories.IUserRepository userRepository)
        : IAuthService
    {
        public async Task<UserSession> RegisterAsync(RegisterRequest request, JwtConfig jwtConfig)
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                throw new ArgumentException("Email or Password cannot be empty");
            }

            if (string.IsNullOrWhiteSpace(request.LearningLanguage) || string.IsNullOrWhiteSpace(request.NativeLanguage))
            {
                throw new ArgumentException("Learning Language or native language cannot be empty");
            }

            request.Email = request.Email.Trim().ToLower();
            var existingUser = await userRepository.GetByEmailAsync(request.Email);
            if (existingUser != null)
            {
                throw new Exception($"This Email ({request.Email}) has already registered before");
            }

            var gravatar = GravatarHelper.GetGravatarUrl(request.Email);

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 11);
            var newUser = new User
            {
                Email = request.Email,
                Gravatar = gravatar,
                PasswordHash = passwordHash,
                NativeLanguage = request.NativeLanguage,
                CurrentLearningLanguage = request.LearningLanguage,
                CreatedAt = DateTime.Now.ToUnixTimeSeconds(),
            };

            var id = await userRepository.InsertReturnIdentityAsync(newUser);

            var claims = TokenHelper.ClaimsGenerator(id, id.ToString(), newUser.Email); // Use newUser.Email for consistency
            var token = TokenHelper.JwtTokenGenerator(claims, jwtConfig.Issuer, jwtConfig.SymmetricSecurityKey, jwtConfig.TokenExpiresInDays);

            // Populate UserId in newUser object after insertion if it's not automatically handled by the ORM
            // Assuming 'id' is the UserId. If newUser object is tracked by ORM and 'id' is assigned to its UserId property, this is fine.
            // For clarity, explicitly assign if needed, e.g., newUser.UserId = id; (if User entity has UserId property)

            return new UserSession
            {
                Token = token,
                UserId = id, // Assuming 'id' is the UserId
                Email = newUser.Email,
                NativeLanguage = newUser.NativeLanguage,
                CurrentLearningLanguage = newUser.CurrentLearningLanguage
            };
        }

        public async Task<UserSession> LoginAsync(LoginRequest loginRequest, JwtConfig jwtConfig)
        {
            // 1. Find user by email
            var user = await userRepository.GetByEmailAsync(loginRequest.Email);
            if (user == null)
            {
                throw new Exception("User not found");
            }

            var validateResult = await _IsValidLogin(loginRequest.Email, loginRequest.Password);
            if (!validateResult.isValidLogin)
            {
                throw new Exception("Username or Password is incorrect");
            }

            if (user.DeletedAt != null)
            {
                throw new Exception("Sorry, your account has been deleted");
            }

            var claims = TokenHelper.ClaimsGenerator(user.Id, user.Id.ToString(), user.Email);
            var token = TokenHelper.JwtTokenGenerator(claims, jwtConfig.Issuer, jwtConfig.SymmetricSecurityKey, jwtConfig.TokenExpiresInDays);
            return new UserSession()
            {
                Token = token,
            }.From(validateResult.user!);
        }
        private async Task<(bool isValidLogin, User? user)> _IsValidLogin(string email, string password)
        {
            var user = await userRepository.GetFirstOrDefaultAsync(x => x.Email == email);
            if (user == null)
            {
                return (false, null);
            }

            // bcrypt hashes are prefixed with "$2"; legacy hashes are plain SHA256 hex.
            if (user.PasswordHash.StartsWith("$2"))
            {
                return (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash), user);
            }

            // Legacy single-round SHA256. On successful login, transparently
            // upgrade the stored hash to bcrypt so users migrate over time.
            var isLegacyValid = user.PasswordHash.Equals(CommonHelper.CalculateSha256Hash(password + user.Salt));
            if (isLegacyValid)
            {
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
                await userRepository.UpdateAsync(user);
            }

            return (isLegacyValid, user);
        }
    }
}
