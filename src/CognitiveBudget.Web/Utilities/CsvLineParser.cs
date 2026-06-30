using System.Collections.Generic;
using System.Text;

namespace CognitiveBudget.Web.Utilities;

/// <summary>
/// Minimal RFC 4180 single-line CSV field parser. Handles quoted fields,
/// embedded commas, and escaped quotes ("") so a value like "Coffee, large"
/// is parsed as one field rather than split on the comma.
/// </summary>
public static class CsvLineParser
{
    public static List<string> Parse(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }

        fields.Add(sb.ToString().Trim());
        return fields;
    }
}
