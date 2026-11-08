using System.Net;
using DoMa.Core.Updates;
using Xunit;

namespace DoMa.Core.Tests.Updates;

public class UpdateErrorsTests
{
    static HttpRequestException Status(HttpStatusCode code) => new("x", null, code);

    [Fact]
    public async Task retries_gateway_timeout_then_succeeds()
    {
        var calls = 0;
        var result = await UpdateErrors.RetryAsync(_ => ++calls < 3 ? throw Status(HttpStatusCode.GatewayTimeout) : Task.FromResult(42),
            CancellationToken.None, delay: _ => TimeSpan.Zero);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task does_not_retry_not_found()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateErrors.RetryAsync<int>(_ => { calls++; throw Status(HttpStatusCode.NotFound); },
            CancellationToken.None, delay: _ => TimeSpan.Zero));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task gives_up_after_attempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateErrors.RetryAsync<int>(_ => { calls++; throw Status(HttpStatusCode.BadGateway); },
            CancellationToken.None, attempts: 3, delay: _ => TimeSpan.Zero));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void user_cancel_is_not_transient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(UpdateErrors.IsTransient(new TaskCanceledException(), cts.Token));
        Assert.True(UpdateErrors.IsTransient(new TaskCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void describes_reason()
    {
        Assert.Contains("504", UpdateErrors.Describe(Status(HttpStatusCode.GatewayTimeout)));
        Assert.Contains("kiểm tra mạng", UpdateErrors.Describe(new HttpRequestException("no route")));
        Assert.Contains("SHA-256", UpdateErrors.Describe(new InvalidDataException("Bộ cài tải về không khớp mã SHA-256")));
    }
}
