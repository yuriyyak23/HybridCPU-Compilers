using System;

namespace Phase09;

public static class EhCorpus
{
    public static int Marker;

    public static int NestedCatchFinally(Exception exception)
    {
        try
        {
            try
            {
                throw exception;
            }
            finally
            {
                Marker = 1;
            }
        }
        catch (ArgumentException)
        {
            return 7;
        }
    }

    public static void Rethrow(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch
        {
            throw;
        }
    }

    public static void ThrowInFinally(Exception first, Exception replacement)
    {
        try
        {
            throw first;
        }
        finally
        {
            throw replacement;
        }
    }

    public static int FilterIsUnsupported(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception) when (Marker == 0)
        {
            return 1;
        }
    }
}
