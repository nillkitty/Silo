using Microsoft.Win32;

namespace Silo.Ui;

public static class Dialogs
{
    public static string AllFilesFilter { get; } = "All files (*.*)|*.*";

    public static List<string>? OpenFiles(string? title, string? filter)
    {
        var ofd = new OpenFileDialog()
                  {
                      Title =
                          title ?? "Open file(s)...",
                      Filter      = filter ?? AllFilesFilter,
                      Multiselect = true
                  };
        if (ofd.ShowDialog() is true)
        {
            return ofd.FileNames?.ToList() ?? [];
        }

        return null;
    }

    public static string? OpenFile(string? title, string? filter)
    {
        var ofd = new OpenFileDialog()
                  {
                      Title =
                          title ?? "Open file(s)...",
                      Filter      = filter ?? AllFilesFilter,
                      Multiselect = false
                  };
        if (ofd.ShowDialog() is true)
        {
            return ofd.FileName;
        }

        return null;
    }

    // `filter` and `restore` are kept as parameters (unused - OpenFolderDialog has no
    // file-name-filter or restore-last-directory concept) so existing callers don't need
    // to change; Telerik's RadOpenFolderDialog supported both but Microsoft.Win32's
    // built-in OpenFolderDialog (net8.0-windows+, no extra package needed) doesn't.
    public static string? ChooseFolder(string? title, string? filter,
                                       string? initial = null,
                                       bool    restore = true)
    {
        var ofd = new OpenFolderDialog()
                  {
                      Title =
                          title ?? "Select directory",
                      Multiselect      = false,
                      InitialDirectory = initial
                  };
        if (ofd.ShowDialog() is true)
        {
            return ofd.FolderName;
        }

        return null;
    }

    public static List<string>? ChooseFolders(string? title, string? filter,
                                              string? initial = null,
                                              bool    restore = true)
    {
        var ofd = new OpenFolderDialog()
                  {
                      Title =
                          title ?? "Select directory",
                      Multiselect      = true,
                      InitialDirectory = initial
                  };
        if (ofd.ShowDialog() is true)
        {
            return ofd.FolderNames?.ToList() ?? [];
        }

        return null;
    }

    public static string? SaveFile(string? title, string? filter,
                                   string? name = null)
    {
        var ofd = new SaveFileDialog()
                  {
                      Title    = title  ?? "Save file...",
                      Filter   = filter ?? AllFilesFilter,
                      FileName = name
                  };
        if (ofd.ShowDialog() is true)
        {
            return ofd.FileName;
        }

        return null;
    }
}
