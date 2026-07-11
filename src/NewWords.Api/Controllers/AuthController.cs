using Api.Framework.Models;
using Microsoft.AspNetCore.Mvc;
using NewWords.Api.Models.DTOs.Auth;
using NewWords.Api.Services;
using Api.Framework.Result;
using Microsoft.Extensions.Options;
using NewWords.Api.Models;
using NewWords.Api.Services.interfaces;
using NewWords.Api.Exceptions;

namespace NewWords.Api.Controllers;

public class AuthController(IAuthService authService, IOptions<JwtConfig> jwtConfig) : BaseController
{
    private readonly JwtConfig _jwtConfig = jwtConfig.Value;

    /// <summary>
    /// Registers a new user.
    /// </summary>
    /// <param name="register">The registration details.</param>
    /// <returns>Jwt token if successful</returns>
    [HttpPost]
    public async Task<ApiResult<UserSession>> Register(RegisterRequest register)
    {
        var userSession = await authService.RegisterAsync(register, _jwtConfig);
        return new SuccessfulResult<UserSession>(userSession);
    }

    /// <summary>
    /// Logs in a user and returns a JWT token.
    /// </summary>
    /// <param name="loginRequest">The login credentials.</param>
    /// <returns>Jwt token if successful</returns>
    [HttpPost]
    public async Task<ApiResult<UserSession>> Login(LoginRequest loginRequest)
    {
        if (string.IsNullOrWhiteSpace(loginRequest.Email) || string.IsNullOrWhiteSpace(loginRequest.Password))
        {
            throw new BusinessException("Email or Password cannot be empty");
        }
        var userSession = await authService.LoginAsync(loginRequest, _jwtConfig);
        return new SuccessfulResult<UserSession>(userSession);
    }
}
