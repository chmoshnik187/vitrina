using System.Text;
using Vitrines.Core.Models;

namespace Vitrines.Core.Rules;

/// <summary>
/// Безопасный рендер шаблона имени файла. Поддерживаются токены:
/// {name} — имя без расширения, {filename} — полное имя, {ext} — расширение с точкой,
/// {kind} — тип элемента, {date}/{time}/{yyyy}/{MM}/{dd}/{HH}/{mm}/{ss}, {counter} — номер (01…).
/// Перевод строки, запрещённые символы и хвостовые точки/пробелы вычищаются.
/// </summary>
public static class NameTemplate
{
    public static bool HasTemplate(string? template) => !string.IsNullOrWhiteSpace(template);

    public static string Render(string template, string currentPath, ItemKind kind, DateTimeOffset? now = null, int counter = 1)
    {
        var stamp = now ?? DateTimeOffset.Now;
        var currentName = Path.GetFileName(currentPath);
        var stem = Path.GetFileNameWithoutExtension(currentName);
        if (string.IsNullOrEmpty(stem)) stem = currentName;
        var ext = Path.GetExtension(currentName);

        var result = template;
        Replace(ref result, "filename", currentName);
        Replace(ref result, "name", stem);
        Replace(ref result, "ext", ext);
        Replace(ref result, "kind", KindName(kind));
        Replace(ref result, "date", stamp.ToString("yyyy-MM-dd"));
        Replace(ref result, "time", stamp.ToString("HH-mm-ss"));
        Replace(ref result, "yyyy", stamp.ToString("yyyy"));
        Replace(ref result, "MM", stamp.ToString("MM"));
        Replace(ref result, "dd", stamp.ToString("dd"));
        Replace(ref result, "HH", stamp.ToString("HH"));
        Replace(ref result, "mm", stamp.ToString("mm"));
        Replace(ref result, "ss", stamp.ToString("ss"));
        Replace(ref result, "counter", Math.Max(1, counter).ToString("D2"));

        result = Sanitize(result);
        if (string.IsNullOrWhiteSpace(result)) result = stem;

        // Если пользователь не указал расширение в шаблоне — сохраняем исходное.
        if (string.IsNullOrEmpty(Path.GetExtension(result)) && !string.IsNullOrEmpty(ext))
        {
            var maxStemLength = Math.Max(1, 200 - ext.Length);
            if (result.Length > maxStemLength) result = result[..maxStemLength];
            result += ext;
        }

        return result;
    }

    private static string KindName(ItemKind kind) => kind switch
    {
        ItemKind.Folder => "папка",
        ItemKind.Shortcut => "ярлык",
        ItemKind.Application => "программа",
        _ => "файл"
    };

    private static void Replace(ref string value, string token, string replacement)
    {
        if (value.IndexOf('{') < 0) return;
        var sb = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '{')
            {
                var end = value.IndexOf('}', i + 1);
                if (end > i)
                {
                    var name = value.Substring(i + 1, end - i - 1).Trim();
                    if (string.Equals(name, token, StringComparison.Ordinal))
                    {
                        sb.Append(replacement);
                        i = end + 1;
                        continue;
                    }
                }
            }
            sb.Append(value[i]);
            i++;
        }
        value = sb.ToString();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch is '\r' or '\n' or '\t') { sb.Append(' '); continue; }
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        // Windows: имена устройств зарезервированы; добавляем подчёркивание.
        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        if (reserved.Contains(cleaned, StringComparer.OrdinalIgnoreCase)) cleaned += "_";
        if (cleaned.Length > 200) cleaned = cleaned[..200].TrimEnd('.', ' ');
        return cleaned;
    }
}
