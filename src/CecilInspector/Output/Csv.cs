using System.Buffers;
using System.Text;

namespace CecilInspector.Output;

/// <summary>
/// RFC 4180 style fields: a field is quoted only when it contains a comma, a quote or a line
/// break, and embedded quotes are doubled. Every value goes through <see cref="TextSanitizer"/>
/// first, so control characters (line breaks included) are already spelled out as \n, \uXXXX
/// and so on and a record can never span more than one physical line.
/// </summary>
internal static class Csv
{
    public const string Header = "scope,kind,symbol,container,assembly_name,assembly_path,document,line,column,il_offset";

    /// <summary>
    /// Written once at the start of a csv report so Excel on Windows reads the file as UTF-8.
    /// Neither the console writer nor the --output file emits a preamble of its own, and a BOM
    /// inside a symbol is a format character that the sanitizer spells out as the six characters
    /// \uFEFF, so the report carries exactly one.
    /// </summary>
    public const char ByteOrderMark = '\uFEFF';

    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    public static string Field(string? value)
    {
        var row = new StringBuilder();
        AppendField(row, value);
        return row.ToString();
    }

    /// <summary>
    /// Assembles the whole record in <paramref name="row"/> and writes it with one call: the
    /// console writer flushes on every write, so field-by-field writes would cost twenty
    /// flushes per row instead of one.
    /// </summary>
    public static void WriteRow(TextWriter writer, StringBuilder row, params ReadOnlySpan<string?> fields)
    {
        row.Clear();
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                row.Append(',');
            }

            AppendField(row, fields[index]);
        }

        writer.WriteLine(row.ToString());
    }

    private static void AppendField(StringBuilder row, string? value)
    {
        var escaped = TextSanitizer.Escape(value);
        if (escaped.AsSpan().IndexOfAny(NeedsQuoting) < 0)
        {
            row.Append(escaped);
            return;
        }

        row.Append('"').Append(escaped.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}
