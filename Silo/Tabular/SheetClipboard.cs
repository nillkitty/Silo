using System.Text;

namespace Silo.Tabular;

/// <summary>
///     Builds the pieces of a multi-format clipboard copy. Kept free of WPF so it can be tested: the editor puts these on
///     a <c>DataObject</c> together, so one Ctrl+C pastes as a table into Excel / Slack / Word and as plain TSV into a
///     text box.
/// </summary>
public static class SheetClipboard
{
    /// <summary>
    ///     Wraps an HTML fragment in the "HTML Format" (CF_HTML) clipboard envelope, whose header carries byte offsets into
    ///     the UTF-8 text. Word, Outlook, Slack and browsers read this to paste a table with its formatting.
    /// </summary>
    public static string ToCfHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre    = "<html>\r\n<body>\r\n<!--StartFragment-->";
        const string post   = "<!--EndFragment-->\r\n</body>\r\n</html>";

        int headerLen = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        int startHtml = headerLen;
        int startFrag = startHtml + Encoding.UTF8.GetByteCount(pre);
        int endFrag   = startFrag + Encoding.UTF8.GetByteCount(fragment);
        int endHtml   = endFrag + Encoding.UTF8.GetByteCount(post);

        return string.Format(header, startHtml, endHtml, startFrag, endFrag) + pre + fragment + post;
    }

    /// <summary>The formats copied together by the default copy, keyed by the clipboard format name.</summary>
    public static IReadOnlyDictionary<string, string> BuildAll(SheetData sheet)
    {
        return new Dictionary<string, string>
               {
                   ["UnicodeText"]       = SheetExport.ToTsv(sheet),
                   ["HTML Format"]       = ToCfHtml(SheetExport.ToHtml(sheet, true)),
                   ["Csv"]               = SheetExport.ToCsv(sheet),
                   ["Silo.Table"]        = sheet.ToJson()
               };
    }
}
