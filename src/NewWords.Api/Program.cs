using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Api.Framework;
using ConfigManager.Provider;
using Api.Framework.Database;
using Api.Framework.Extensions;
using Api.Framework.Models;
using LLM.Models;
using LLM.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using NewWords.Api.Extensions;
using NewWords.Api.Models.DTOs;
using NLog.Web;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);

// Redis-backed dynamic configuration (ConfigManager.Provider). Registered last so
// operator-set values in Redis take authority over appsettings. Added only when
// really configured: skip empty settings and the unsubstituted deploy placeholder
// so dev/CI boot instantly instead of dialing a bogus host. Optional = true means an
// unreachable Redis degrades gracefully back to appsettings. Keys follow
// "newwords.api:<path>" (e.g. "newwords.api:Agents:0:Provider").
var redisSection = builder.Configuration.GetSection("Redis");
var redisConn = redisSection["ConnectionString"];
var redisPrefix = redisSection["ProjectPrefix"];
if (!string.IsNullOrWhiteSpace(redisConn)
    && !string.IsNullOrWhiteSpace(redisPrefix)
    && redisConn != "PRODUCTION_REDIS_CONNECTION")
{
    builder.Configuration.AddRedis(source =>
    {
        source.ProjectName = redisPrefix;
        source.ConnectionString = redisConn;
        source.Database = redisSection.GetValue<int>("Database");
        source.Optional = true;
    });
}

var logger = LoggerFactory.Create(config =>
{
    config.AddConsole();
    config.AddConfiguration(builder.Configuration.GetSection("Logging"));
}).CreateLogger("Program");

var envName = builder.Environment.EnvironmentName;
builder.Host.UseNLog();

// Fail fast on an unsubstituted / empty agent ApiKey (issue #6). Config is final here
// (Redis registered above), so we see the effective value. The runtime guard in
// LanguageService only rejects null/empty, so a leftover deploy placeholder (e.g. the
// committed "XAI_API_KEY" token) would otherwise reach the provider as a bearer token and
// 401 silently. Warn in every environment; throw in Production so a bad deploy stops here.
var agentConfigs = builder.Configuration.GetSection("Agents").Get<List<AgentConfig>>() ?? [];
var apiKeyIssues = AgentApiKeyValidator.FindPlaceholderApiKeyIssues(agentConfigs);
foreach (var issue in apiKeyIssues)
{
    logger.LogWarning("LLM agent config: {Issue}", issue);
}
if (apiKeyIssues.Count > 0 && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "LLM agent ApiKey misconfiguration in Production: " + string.Join(" ", apiKeyIssues));
}

builder.Services.AddControllers().AddJsonOptions(options =>
{
    // options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(SetupSwaggerGen());

// Configure SQLSugar
builder.Services.AddSqlSugarSetup(builder.Configuration.GetSection("DatabaseConnectionOptions").Get<DatabaseConnectionOptions>()!, logger);

builder.Services.AddExceptionHandler<NewWords.Api.Exceptions.AppExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(SetupCors(builder));
ConfigAuthentication(builder);
builder.Services.AddHttpContextAccessor();
builder.Services.AddAutoMapper(
    NewWords.Api.MappingProfiles.AutoMapperConfiguration.ApplyRecursionGuard,
    typeof(NewWords.Api.MappingProfiles.SettingsMappingProfile));
builder.Services.RegisterServices();

var app = builder.Build();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Local"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowOrigins");
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
logger.LogInformation(envName);
app.Run();
return;

// Main program ends here, following are local methods

void ConfigAuthentication(WebApplicationBuilder b)
{
    var services = b.Services;
    var configuration = b.Configuration;
    services.Configure<JwtConfig>(configuration.GetSection("Jwt"));
    var jwtConfig = configuration.GetSection("Jwt").Get<JwtConfig>();
    services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtConfig!.Issuer,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.SymmetricSecurityKey)),
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
}

Action<SwaggerGenOptions> SetupSwaggerGen()
{
    return c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "NewWords API",
            Version = "v1"
        });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme()
        {
            Name = "Authorization",
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token in the text input below.\nExample: \"Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwO\"",
        });
        c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference("Bearer"),
                new List<string>()
            }
        });
    };
}

Action<CorsOptions> SetupCors(WebApplicationBuilder webApplicationBuilder)
{
    return opts =>
    {
        string[] originList = webApplicationBuilder.Configuration.GetSection("AllowedCorsOrigins").Get<List<string>>()?.ToArray() ?? [];
        opts.AddPolicy("AllowOrigins", policy => policy.WithOrigins(originList)
            .AllowCredentials()
            .AllowAnyMethod()
            .AllowAnyHeader()
        );
    };
}
