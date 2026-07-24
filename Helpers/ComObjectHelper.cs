using System.Runtime.InteropServices;

namespace ECS.CommissionsMailer.Helpers;

internal static class ComObjectHelper
{
    public static void FinalRelease(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // La limpieza nunca debe sustituir la excepción original.
        }
    }
}
