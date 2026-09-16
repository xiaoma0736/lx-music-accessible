using System.IO;
using System.Net.Http;
using LXMusicAccessible.App;
using LXMusicAccessible.Core.MusicApi;
using LXMusicAccessible.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LXMusicAccessible.Tests;

public sealed class LoginDiagnosticsTests
{
    private const string ShortCanary = "S7-canary-value";
    private const string LongCanary = "L24-canary-value-that-must-never-appear-in-any-output";
    private const string HttpOnlyCanary = "H24-http-only-canary-that-must-stay-secret";
    private const string AuthorizationCanary = "A24-authorization-canary-that-must-stay-secret";
    private const string TokenCanary = "T24-token-canary-that-must-stay-secret";

    private static readonly string Credentials =
        $"short={ShortCanary}; MUSIC_U={LongCanary}; httpOnlyCookie={HttpOnlyCanary}; " +
        $"Authorization: Bearer {AuthorizationCanary}; token={TokenCanary}";

    [Theory]
    [InlineData(LoginDiagnosticOutcome.Success, LoginDiagnosticError.None)]
    [InlineData(LoginDiagnosticOutcome.Failure, LoginDiagnosticError.LoginRejected)]
    [InlineData(LoginDiagnosticOutcome.Canceled, LoginDiagnosticError.None)]
    [InlineData(LoginDiagnosticOutcome.Exception, LoginDiagnosticError.Unexpected)]
    public void Report_log_and_user_message_never_contain_credentials(
        LoginDiagnosticOutcome outcome,
        LoginDiagnosticError error)
    {
        var correlationId = LoginDiagnostics.CreateCorrelationId();
        var report = LoginDiagnostics.CreateReport("netease", Credentials, outcome, error, correlationId);
        var capturedLog = LoginDiagnostics.CreateSafeSummary("netease", Credentials, outcome, error, correlationId);
        var uiMessage = LoginDiagnostics.CreateUserMessage(outcome, error, correlationId);
        var allOutput = string.Join(Environment.NewLine, report, capturedLog, uiMessage);

        AssertNoCredentialFragments(allOutput);
        Assert.Contains("name=MUSIC_U", report);
        Assert.Contains($"length={LongCanary.Length}", report);
        Assert.Contains("domain=music.163.com", capturedLog);
        Assert.Contains(correlationId, allOutput);
    }

    [Fact]
    public void Report_in_temporary_directory_contains_only_safe_metadata_and_can_be_deleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LXMusicAccessible.LoginDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = LoginDiagnosticReportStore.Write(directory, "qq", Credentials,
                LoginDiagnosticOutcome.Failure, LoginDiagnosticError.LoginRejected,
                LoginDiagnostics.CreateCorrelationId());

            Assert.NotNull(path);
            Assert.Equal(LoginDiagnostics.ReportFileName, Path.GetFileName(path));
            AssertNoCredentialFragments(string.Join(Environment.NewLine,
                Directory.EnumerateFiles(directory).Select(File.ReadAllText)));
            Assert.True(LoginDiagnosticReportStore.Delete(path!));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Account_credentials_remain_protected_by_dpapi()
    {
        var protectedValue = SecureStore.Protect(LongCanary);

        Assert.NotNull(protectedValue);
        Assert.NotEqual(LongCanary, protectedValue);
        Assert.Equal(LongCanary, SecureStore.Unprotect(protectedValue));
    }

    [Fact]
    public async Task Http_logs_omit_query_credentials_and_exception_text()
    {
        var logger = new CapturingLogger<MusicHttpClient>();
        using var client = new MusicHttpClient(logger, new ThrowingHandler());
        var url = $"https://fixture.invalid/login?token={TokenCanary}";

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        var capturedLogs = string.Join(Environment.NewLine, logger.Messages);
        AssertNoCredentialFragments(capturedLogs);
        Assert.Contains("https://fixture.invalid", capturedLogs);
        Assert.DoesNotContain("/login", capturedLogs);
        Assert.DoesNotContain("?", capturedLogs);
    }

    private static void AssertNoCredentialFragments(string output)
    {
        foreach (var value in new[]
                 {
                     ShortCanary,
                     LongCanary,
                     HttpOnlyCanary,
                     AuthorizationCanary,
                     TokenCanary
                 })
        {
            Assert.DoesNotContain(value, output, StringComparison.Ordinal);
            Assert.DoesNotContain(value[..Math.Min(24, value.Length)], output, StringComparison.Ordinal);
            Assert.DoesNotContain(value[..8], output, StringComparison.Ordinal);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Authorization: Bearer {AuthorizationCanary}; token={TokenCanary}");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
