namespace TypeWhisper.Windows.Services;

internal static class NonFatalExceptionFilter
{
    public static bool IsNonFatal(Exception ex) =>
        ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException
            and not CannotUnloadAppDomainException;
}
