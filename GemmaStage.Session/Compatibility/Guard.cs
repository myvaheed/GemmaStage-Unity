using System.Runtime.CompilerServices;

namespace GemmaStage.Session;

internal static class Guard
{
    public static void NotNull<T>(
        T? value,
        [CallerArgumentExpression("value")] string? paramName = null)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void NotNullOrWhiteSpace(
        string? value,
        [CallerArgumentExpression("value")] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
        }
    }

    public static void NotDisposed(bool disposed, object instance)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().Name);
        }
    }
}
