using System.Globalization;
using System.Text;

namespace CecilInspector.Output;

internal static class TextSanitizer
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        StringBuilder? escaped = null;
        for (var index = 0; index < value.Length;)
        {
            var length = 1;
            int scalar = value[index];
            UnicodeCategory category;
            if (Rune.TryCreate(value[index], out var rune))
            {
                scalar = rune.Value;
                category = Rune.GetUnicodeCategory(rune);
            }
            else if (index + 1 < value.Length && Rune.TryCreate(value[index], value[index + 1], out rune))
            {
                scalar = rune.Value;
                category = Rune.GetUnicodeCategory(rune);
                length = 2;
            }
            else
            {
                category = UnicodeCategory.Surrogate;
            }

            if (category is not (UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or
                UnicodeCategory.Surrogate))
            {
                escaped?.Append(value, index, length);
                index += length;
                continue;
            }

            escaped ??= new StringBuilder(value.Length + 8).Append(value, 0, index);
            escaped.Append(scalar switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\u001b' => "\\e",
                <= 0xFFFF => $"\\u{scalar:X4}",
                _ => $"\\U{scalar:X8}",
            });
            index += length;
        }

        return escaped?.ToString() ?? value;
    }
}
