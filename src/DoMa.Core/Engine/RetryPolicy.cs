namespace DoMa.Core.Engine;

public class RetryPolicy
{
    public int MaxAttemptsPerSegment { get; init; } = 5;

    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Max(0, attempt - 1))));
}
