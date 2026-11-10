using System.Globalization;
using Mixer.Core.Resources;

namespace Mixer.Core;

/// <summary>
/// Строки для пользователя (Resources\CoreStrings): числа и даты — по текущей культуре. Журнал пишется по-английски
/// литералами в коде, а не отсюда.
/// </summary>
public static class Loc
{
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>Сообщение об узле: «Имя»: текст. Карточка узла срезает эту приставку (<see cref="SubjectPrefix"/>).</summary>
    public static string Subject(string name, string message) => F(CoreStrings.Issue_SubjectFormat, name, message);

    public static string SubjectPrefix(string name) => Subject(name, "");
}
