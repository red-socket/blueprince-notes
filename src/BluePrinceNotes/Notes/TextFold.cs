using System.Text;

namespace BluePrinceNotes.Notes;

/// <summary>
/// Search folding: lower case and no accents, so "cafe" finds "Café". Done by hand because the game runs .NET
/// with invariant globalization, where case-insensitive comparison only folds ASCII.
/// </summary>
internal static class TextFold
{
    private const string From = "ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÐðÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿŸ";
    private const string To   = "aaaaaaaaaaaacceeeeeeeeiiiiiiiiddnnoooooooooooouuuuuuuuyyyy";

    public static string Fold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 0x80) sb.Append(c >= 'A' && c <= 'Z' ? (char)(c + 32) : c);
            else if (c == 'ß' || c == 'ẞ') sb.Append("ss");
            else if (c == 'Æ' || c == 'æ') sb.Append("ae");
            else if (c == 'Œ' || c == 'œ') sb.Append("oe");
            else
            {
                int i = From.IndexOf(c);
                sb.Append(i >= 0 ? To[i] : char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}
