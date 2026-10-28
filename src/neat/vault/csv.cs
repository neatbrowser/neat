using System.Text;

namespace neat;

/// <summary>
/// Reading and writing CSV (RFC 4180), which is what Chrome, Edge and Firefox use to export and
/// import passwords. A field with a comma, a quote or a line break is wrapped in quotes and a quote
/// inside it is written twice; the reader undoes exactly that, and is forgiving about the rest
/// (a leading byte order mark, CR, LF or CRLF line ends, blank lines, a missing final line break).
/// </summary>
public static class Csv
{
    /// <summary>The rows of a CSV text. A blank line is not a row.</summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var atStart = true;
        var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            atStart = true;
        }

        void EndRow()
        {
            EndField();

            // "\n\n" is a blank line, which is not a row with one empty field.
            if (!(row.Count == 1 && row[0].Length == 0))
                rows.Add(row.ToArray());

            row.Clear();
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                    quoted = false;
            }
            else if (c == '"' && atStart)
            {
                quoted = true;
                atStart = false;
            }
            else if (c == ',')
            {
                EndField();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                EndRow();
            }
            else
            {
                field.Append(c);
                atStart = false;
            }
        }

        if (field.Length > 0 || row.Count > 0)
            EndRow();

        return rows;
    }

    /// <summary>One value, quoted when it has to be.</summary>
    public static string Field(string value)
    {
        var plain = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            && (value.Length == 0 || (value[0] != ' ' && value[^1] != ' '));

        return plain ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string Row(params string[] fields)
    {
        return string.Join(",", fields.Select(Field));
    }
}
