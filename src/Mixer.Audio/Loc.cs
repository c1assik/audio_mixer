using System.Globalization;

namespace Mixer.Audio;

/// <summary>Строки для пользователя (Resources\AudioStrings): числа и даты — по текущей культуре.</summary>
internal static class Loc
{
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
