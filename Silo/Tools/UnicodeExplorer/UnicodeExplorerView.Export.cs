using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Windows.Controls;
using System.Windows.Xps.Packaging;
using Microsoft.Win32;
using Newtonsoft.Json;
using Silo.Providers.Format;
using Silo.Providers.Reflection;
using Silo.Ui.Windows;
using Silo.Unicode;
using YamlDotNet.Core;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Tool &gt; Export Collection, Import Glyphs, Page Setup and Print. The files go through the application's format
///     catalog
///     (<see cref="Formats" />): a format that can be written is offered here, and one that can be read is opened by
///     Import, so the two stay in step.
/// </summary>
public sealed partial class UnicodeExplorerView
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    /// <summary>
    ///     The ids of the formats the Tool menu names itself; the others the catalog knows are listed under "Other
    ///     Formats".
    /// </summary>
    private static readonly string[] NamedFormats = ["text", "json", "xml", "yaml", "html"];

    // ---- export --------------------------------------------------------------------------------------------------

    /// <summary>The collection as a document: the selected characters, or all of them, with the colors and how they are shown.</summary>
    private CharacterCollection ExportDocument(IReadOnlyList<CollectedCharacter> items)
    {
        return new CharacterCollection(items, "Unicode collection", cp => _colors.ColorOf(cp)?.Hex, _settings.ColorMode);
    }

    /// <summary>The formats the catalog can write that the Tool menu does not name, as menu items.</summary>
    private IEnumerable<Control> OtherExportItems(bool any)
    {
        foreach (var format in Formats.CollectionWriters().Where(f => !NamedFormats.Contains(f.Id)))
        {
            var f = format;
            yield return UiKit.Item($"{f.DisplayName}…", () => ExportWith(f), any);
        }
    }

    private void ExportCollection(string id)
    {
        if (id == "pdf")
        {
            ExportPdf();
            return;
        }

        if (Formats.CollectionWriters().FirstOrDefault(f => f.Id == id) is not { } format)
        {
            Say($"This installation has no \"{id}\" format.");
            return;
        }

        ExportWith(format);
    }

    private void ExportWith(CharacterCollectionFormat format)
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        var dialog = new SaveFileDialog
                     {
                         Title        = $"Export the collection: {format.DisplayName}",
                         Filter       = format.Filter(true),
                         FileName     = "characters", DefaultExt = format.DefaultExtension(),
                         AddExtension = true, OverwritePrompt    = true
                     };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Formats.WriteCollection(dialog.FileName, format, ExportDocument(items));
            Say($"Exported {items.Count:N0} character{(items.Count == 1 ? "" : "s")} to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PromptDialog.Show("Export the collection", "The file could not be written: " + ex.Message, ["OK"]);
        }
    }

    // ---- import --------------------------------------------------------------------------------------------------

    /// <summary>Tool &gt; Import Glyphs &gt; From File: any file a collection format of the catalog can read.</summary>
    private void ImportFromFile()
    {
        var dialog = new OpenFileDialog
                     { Title = "Import glyphs from a file", Filter = Formats.OpenFilter<CharacterCollection>("All collection files") };
        if (dialog.ShowDialog() != true) return;

        IReadOnlyList<CollectedCharacter> items;
        try
        {
            items = Formats.ReadCollection(dialog.FileName).Items;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or YamlException)
        {
            PromptDialog.Show("Import glyphs", "The file could not be read: " + ex.Message, ["OK"]);
            return;
        }

        AddImported(items, Path.GetFileName(dialog.FileName));
    }

    /// <summary>
    ///     Tool &gt; Import Glyphs &gt; From Range: code points written as people write them (
    ///     <c>U+2190-21FF, 1F600..1F64F, a-z</c>).
    /// </summary>
    private void ImportFromRange()
    {
        string? text = PromptDialog.Ask("Import glyphs from a range",
                                        "Code points or ranges, separated by commas or spaces:\nU+2190-21FF, 1F600..1F64F, 0x2600, a-z\n\nOnly assigned characters are added.",
                                        "Add", "U+2190-21FF");
        if (text is null) return;

        var ranges = CodePointRanges.Parse(text);
        if (ranges.Problems.Count > 0)
        {
            PromptDialog.Show("Import glyphs from a range",
                              "These could not be read: " + string.Join(", ", ranges.Problems.Take(6)) +
                              (ranges.Problems.Count > 6 ? ", …" : string.Empty), ["OK"]);
            return;
        }

        if (ranges.Count == 0)
        {
            Say("There was nothing in the range.");
            return;
        }

        // only the assigned ones are characters; the count that is asked about is the one that would be added
        var assigned = ranges.Enumerate()
                             .TakeWhile((_, i) => i < 2_000_000)
                             .Where(IsCollectable)
                             .Select(cp => new CollectedCharacter(new CodePoint(cp)))
                             .ToList();
        if (assigned.Count == 0)
        {
            Say("None of those code points is an assigned character.");
            return;
        }

        AddImported(assigned, "the range");
    }

    /// <summary>Adds imported characters, asking whether they join the collection or replace it when it is not empty.</summary>
    private void AddImported(IReadOnlyList<CollectedCharacter> items, string from)
    {
        if (items.Count == 0)
        {
            Say($"There were no characters in {from}.");
            return;
        }

        bool replace = false;
        if (_tray.Count > 0)
        {
            var (button, _) = PromptDialog.Show("Import glyphs",
                                                $"{from} has {items.Count:N0} character{(items.Count == 1 ? "" : "s")}. Add them to the collection, or replace it?",
                                                ["Add", "Replace", "Cancel"]);
            if (button is < 0 or 2) return;
            replace = button == 1;
        }

        if (items.Count > TrayLimit - (replace ? 0 : _tray.Count))
        {
            Say($"The collection holds up to {TrayLimit:N0} characters; {items.Count:N0} would not fit.");
            return;
        }

        if (replace)
        {
            _tray.Clear();
            _traySel.Clear();
        }

        _tray.AddRange(items);
        RefreshTray();
        SaveTray();
        Say($"Imported {items.Count:N0} character{(items.Count == 1 ? "" : "s")} from {from}.");
    }

    // ---- page setup and printing ---------------------------------------------------------------------------------

    private void ShowPageSetup()
    {
        var page        = _settings.Page.Normalized();
        var form        = new FormDialog(DialogOwner, "Page Setup", "How the collection is laid out for Print and PDF.");
        var orientation = form.AddChoice("Orientation", ["Portrait", "Landscape"], page.Landscape ? 1 : 0);
        var margin      = form.AddNumber("Margin (inches)",     page.MarginHundredthsInch / 100.0, 0, 2, "The same on every side");
        var size        = form.AddNumber("Character size (pt)", page.GlyphSize,                    8, 144);
        var captions    = form.AddCheck("Code point and name under each character", page.Captions);
        var colors      = form.AddCheck("Show the color marks",                     page.Colors);
        if (!form.Ask()) return;

        _settings.Page = new PageSetup
                         {
                             Landscape = orientation.Index == 1, MarginHundredthsInch = (int)Math.Round(margin.Value * 100),
                             GlyphSize = size.Value, Captions                         = captions.Value, Colors = colors.Value
                         }.Normalized();
        SaveSettings();
        Say("Page setup saved.");
    }

    private CollectionPaginator MakePaginator(IReadOnlyList<CollectedCharacter> items, PageSetup page, Size paper)
    {
        return new CollectionPaginator(items, _db, page, "Unicode collection", cp => ColorOfCodePoint(cp), _settings.ColorMode, paper);
    }

    private void PrintCollection()
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        try
        {
            var dialog = new PrintDialog { PageRangeSelection = PageRangeSelection.AllPages };
            if (dialog.PrintTicket is { } ticket)
                ticket.PageOrientation = _settings.Page.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            if (dialog.ShowDialog() != true) return;

            // the person may have changed the orientation in the dialog: that is what is printed
            var page = _settings.Page.Clone();
            if (dialog.PrintTicket?.PageOrientation is { } o and (PageOrientation.Landscape or PageOrientation.Portrait))
                page.Landscape = o == PageOrientation.Landscape;

            var paginator = MakePaginator(items, page, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
            dialog.PrintDocument(paginator, "Unicode collection");
            Say($"Sent {items.Count:N0} character{(items.Count == 1 ? "" : "s")} ({paginator.PageCount} page{(paginator.PageCount == 1 ? "" : "s")}) to the printer.");
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException or Win32Exception)
        {
            PromptDialog.Show("Print", "The collection could not be printed: " + ex.Message, ["OK"]);
        }
    }

    /// <summary>
    ///     Tool &gt; Export Collection &gt; As PDF: printed to the PDF printer Windows comes with (it asks for the file name
    ///     itself). Without it, the same
    ///     pages can be kept as an XPS document, which Windows opens and prints to PDF.
    /// </summary>
    private void ExportPdf()
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        PrintQueue? queue = null;
        try
        {
            using var server = new LocalPrintServer();
            queue = server.GetPrintQueues().FirstOrDefault(q => q.Name.Equals(PdfPrinter, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            // no print system to ask: the XPS way below
        }

        try
        {
            if (queue is not null)
            {
                var dialog = new PrintDialog { PrintQueue = queue };
                if (dialog.PrintTicket is { } ticket)
                    ticket.PageOrientation = _settings.Page.Landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                var paginator = MakePaginator(items, _settings.Page, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
                Say("Choose where to save the PDF…");
                dialog.PrintDocument(paginator, "Unicode collection");
                Say($"Made a PDF of {items.Count:N0} character{(items.Count == 1 ? "" : "s")}.");
                return;
            }

            var (button, _) = PromptDialog.Show("Export as PDF",
                                                $"This computer has no \"{PdfPrinter}\" printer. Save the pages as an XPS document instead? Windows opens XPS files, and any PDF printer can turn them into a PDF.",
                                                ["Save as XPS", "Cancel"]);
            if (button != 0) return;

            var xpsFormat = Formats.Of<XpsFormat>();
            var save = new SaveFileDialog
                       {
                           Title      = "Save the collection as XPS", Filter = xpsFormat.Filter(), FileName = "characters",
                           DefaultExt = xpsFormat.DefaultExtension(), AddExtension = true, OverwritePrompt = true
                       };
            if (save.ShowDialog() != true) return;

            var page = MakePaginator(items, _settings.Page, new Size(8.5 * 96, 11 * 96));
            using (var xps = new XpsDocument(save.FileName, FileAccess.ReadWrite))
            {
                XpsDocument.CreateXpsDocumentWriter(xps).Write(page);
            }

            Say($"Saved {Path.GetFileName(save.FileName)}.");
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException or IOException
                                      or UnauthorizedAccessException or Win32Exception)
        {
            PromptDialog.Show("Export as PDF", "The PDF could not be made: " + ex.Message, ["OK"]);
        }
    }
}