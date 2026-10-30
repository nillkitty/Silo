namespace Silo.Design;

/// <summary>
///     The icon key (Resources/Paths.xaml) of a file type, by extension: the PNG icon for ".png", and so on. Used by commands that
///     read or write a file of one type (Export PNG, Save as JSON) so they do not all wear the same generic icon.
/// </summary>
public static class FileTypeIcons
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp4"] = "Icon001Mp4", ["pdf"] = "Icon002Pdf", ["txt"] = "Icon003Txt", ["psd"] = "Icon004Psd",
        ["jpg"] = "Icon005Jpg", ["jpeg"] = "Icon005Jpg", ["png"] = "Icon006Png", ["bmp"] = "Icon007Bmp", ["gif"] = "Icon008Gif",
        ["rtf"] = "Icon009Rtf", ["xls"] = "Icon010Xls", ["csv"] = "Icon011Csv", ["ai"] = "Icon012Ai", ["eps"] = "Icon013Eps",
        ["cdr"] = "Icon014Cdr", ["svg"] = "Icon015Svg", ["docx"] = "Icon016Docx", ["ppt"] = "Icon017Ppt", ["doc"] = "Icon018Doc",
        ["xlsx"] = "Icon019Xlsx", ["exe"] = "Icon021Exe", ["dwg"] = "Icon022Cad", ["sql"] = "Icon023Sql", ["swf"] = "Icon024Swf",
        ["db"] = "Icon025DB", ["sqlite"] = "Icon025DB", ["ini"] = "Icon026Ini", ["hlp"] = "Icon027Hlp", ["sys"] = "Icon028Sys",
        ["xml"] = "Icon029Xml", ["xaml"] = "Icon029Xml", ["drawio"] = "Icon029Xml", ["mpg"] = "Icon030Mpg", ["mpeg"] = "Icon030Mpg",
        ["zip"] = "Icon031Zip", ["iso"] = "Icon032Iso", ["rss"] = "Icon033Rss", ["html"] = "Icon034Html", ["rar"] = "Icon035Rar",
        ["bin"] = "Icon036Bin", ["php"] = "Icon037Php", ["css"] = "Icon038Css", ["flv"] = "Icon039Flv", ["htm"] = "Icon040Htm",
        ["wma"] = "Icon041Wma", ["aac"] = "Icon042Aac", ["avi"] = "Icon043Avi", ["mov"] = "Icon044Mov", ["tif"] = "Icon045Tif",
        ["tiff"] = "Icon045Tif", ["mp3"] = "Icon046Mp3", ["apk"] = "Icon047Apk", ["mkv"] = "Icon048Mkv", ["3gp"] = "Icon0493Gp",
        ["lnk"] = "Icon050Lnk", ["log"] = "Icon051Log", ["ods"] = "Icon052Ods"
    };

    /// <summary>The icon key for an extension (with or without the dot), or <paramref name="fallback" /> when it has none.</summary>
    public static string For(string? extension, string fallback = "TbExport")
    {
        var key = (extension ?? string.Empty).TrimStart('.');

        return ByExtension.TryGetValue(key, out var icon) ? icon : fallback;
    }
}
