namespace Delta.Netcode;

internal static class Guard
{
    internal static void ThrowIfNull<T>(T? value, string parameterName) where T : class
    {
#if NETSTANDARD2_1
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
#else
        ArgumentNullException.ThrowIfNull(value, parameterName);
#endif
    }

    internal static void ThrowIfLessThan(int value, int minimum, string parameterName)
    {
#if NETSTANDARD2_1
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
#else
        ArgumentOutOfRangeException.ThrowIfLessThan(value, minimum, parameterName);
#endif
    }
}
