using System.Text.RegularExpressions;

namespace MapCore;

/// <summary>
/// Saves the orientation into config.json by editing only the three values in place, so the
/// comments and every other setting in the hand-written file survive.
/// </summary>
public static class ConfigFile
{
    public static void SaveOrientation(string configPath, Orientation orientation)
    {
        string text = File.Exists(configPath) ? File.ReadAllText(configPath) : "{\n}\n";
        text = SetOrientation(text, orientation);

        // Refuse to write something the loader could no longer read.
        string tmp = configPath + ".tmp";
        File.WriteAllText(tmp, text);
        try
        {
            var check = Config.Load(tmp);
            if (check.Orientation != orientation.Normalized())
                throw new InvalidDataException("Orientation did not round-trip through config.json.");
            File.Move(tmp, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public static string SetOrientation(string json, Orientation orientation)
    {
        var o = orientation.Normalized();
        json = SetValue(json, "flipX", o.FlipX ? "true" : "false");
        json = SetValue(json, "flipZ", o.FlipZ ? "true" : "false");
        json = SetValue(json, "rotation", o.Rotation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return json;
    }

    private static string SetValue(string json, string key, string value)
    {
        // An existing "key": <literal> that is not inside a // comment.
        var existing = new Regex(@"""" + Regex.Escape(key) + @"""\s*:\s*(?<val>true|false|-?\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in existing.Matches(json))
        {
            int lineStart = json.LastIndexOf('\n', m.Index) + 1;
            if (json[lineStart..m.Index].Contains("//"))
                continue;
            var val = m.Groups["val"];
            return json[..val.Index] + value + json[(val.Index + val.Length)..];
        }

        // Not present: insert as the first property, right after the opening brace.
        int brace = json.IndexOf('{');
        if (brace < 0)
            throw new InvalidDataException("config.json has no opening brace.");
        string newline = json.Contains("\r\n") ? "\r\n" : "\n";
        bool hasOtherProperties = Regex.IsMatch(json[(brace + 1)..], @"""\s*:");
        string line = $"{newline}  \"{key}\": {value}{(hasOtherProperties ? "," : "")}";
        return json[..(brace + 1)] + line + json[(brace + 1)..];
    }
}
