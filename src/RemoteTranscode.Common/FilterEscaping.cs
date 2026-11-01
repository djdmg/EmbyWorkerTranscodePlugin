using System.Text.RegularExpressions;

namespace RemoteTranscode.Common;

/// <summary>
/// Mirrors Emby's EncodingHelpers.EscapeSubtitleFilterPath so file references inside filter graphs can be
/// located, mapped and rewritten without breaking the graph syntax.
/// </summary>
public static partial class FilterEscaping
{
    // Emby escapes ' as '\\\'' and : as \: inside single-quoted filter option values.
    private const string EscapedQuote = @"'\\\''";

    /// <summary>
    /// A single-quoted filename=/fontsdir= option value, escaped quotes included. Unquoted or positional file
    /// arguments (e.g. subtitles=/path) are not matched.
    /// </summary>
    [GeneratedRegex(@"(?<key>filename|fontsdir)='(?<value>(?:'\\\\\\''|[^'])*)'")]
    public static partial Regex QuotedFileOption();

    /// <summary>Escapes a path for use inside '...' (Windows separators become '/' so ffmpeg does not read them as escapes).</summary>
    public static string Escape(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:").Replace("'", EscapedQuote);

    /// <summary>Inverse of <see cref="Escape"/> for a value captured by <see cref="QuotedFileOption"/>.</summary>
    public static string Unescape(string escaped) =>
        escaped.Replace(EscapedQuote, "'").Replace("\\:", ":");
}
