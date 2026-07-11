using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NewWords.Api.Exceptions;
using NewWords.Api.Helpers;
using EventId = NewWords.Api.Enums.EventId;
using Xunit;

namespace NewWords.Api.Tests.Exceptions
{
    public class AppExceptionHandlerTests
    {
        private static async Task<(bool handled, JsonElement body)> InvokeAsync(Exception exception, HttpContext context)
        {
            var handler = new AppExceptionHandler(new LoggerFactory().CreateLogger<AppExceptionHandler>());
            var responseStream = new MemoryStream();
            context.Response.Body = responseStream;

            var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

            responseStream.Position = 0;
            var json = await new StreamReader(responseStream, Encoding.UTF8).ReadToEndAsync();
            using var doc = JsonDocument.Parse(json);
            return (handled, doc.RootElement.Clone());
        }

        private static Task<(bool handled, JsonElement body)> InvokeAsync(Exception exception)
            => InvokeAsync(exception, new DefaultHttpContext());

        [Fact]
        public async Task BusinessException_SurfacesMessageVerbatim()
        {
            var (handled, body) = await InvokeAsync(new BusinessException("Username or Password is incorrect"));

            handled.Should().BeTrue();
            body.GetProperty("successful").GetBoolean().Should().BeFalse();
            body.GetProperty("message").GetString().Should().Be("Username or Password is incorrect");
        }

        [Fact]
        public async Task UnexpectedException_ReturnsGenericMessage_WithTraceRef()
        {
            const string secretSql = "MySqlException: near 'SELECT * FROM Users'; Server=db.internal;Password=hunter2";
            var context = new DefaultHttpContext();
            context.TraceIdentifier = "trace-abc-123";

            var unexpected = new InvalidOperationException(
                "Connection failed",
                new Exception(secretSql));

            var (handled, body) = await InvokeAsync(unexpected, context);

            handled.Should().BeTrue();
            body.GetProperty("successful").GetBoolean().Should().BeFalse();

            var message = body.GetProperty("message").GetString();
            // Generic text and the correlation ref are present...
            message.Should().Contain("An unexpected error occurred.");
            message.Should().Contain("trace-abc-123");
            // ...and NONE of the injected outer/inner detail survives (both directions).
            message.Should().NotContain(secretSql);
            message.Should().NotContain("MySqlException");
            message.Should().NotContain("Server=");
            message.Should().NotContain("hunter2");
            message.Should().NotContain("Connection failed");
        }

        [Fact]
        public async Task CustomException_PassesThroughCustomDataVerbatim()
        {
            // SettingsController path: framework CustomException<T> carries a
            // FailedResult in CustomData that must reach the client unchanged.
            var settingName = "bogusSetting";
            var custom = ExceptionHelper.New(
                settingName,
                EventId._00106_UnknownSettingName,
                settingName);

            var (handled, body) = await InvokeAsync(custom);

            handled.Should().BeTrue();
            body.GetProperty("successful").GetBoolean().Should().BeFalse();
            body.GetProperty("data").GetString().Should().Be((string?)custom.CustomData!.Data);
            body.GetProperty("message").GetString().Should().Be(custom.CustomData!.Message);
        }
    }
}
