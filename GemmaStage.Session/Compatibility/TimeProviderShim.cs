#if !NET8_0_OR_GREATER
namespace System;

public abstract class TimeProvider
{
    private sealed class SystemTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    }

    private static readonly TimeProvider SystemInstance = new SystemTimeProvider();

    public static TimeProvider System => SystemInstance;

    public abstract DateTimeOffset GetUtcNow();
}
#endif
