using System.IO;
using System.Windows;
using Silo.Ui.Windows;

namespace Silo.Tests.Ui;

public class MainWindowStateTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    [Test]
    public void A_frame_on_the_screen_is_kept_as_it_is()
    {
        var frame = new MainWindowState.Frame(100, 50, 1200, 800, true);

        Assert.That(MainWindowState.Fit(frame, Screen), Is.EqualTo(frame));
    }

    [Test]
    public void A_frame_bigger_than_the_screen_is_made_to_fit()
    {
        var fitted = MainWindowState.Fit(new MainWindowState.Frame(0, 0, 5000, 3000, false), Screen)!;

        Assert.That(fitted.Width,  Is.EqualTo(1920));
        Assert.That(fitted.Height, Is.EqualTo(1080));
    }

    [Test]
    public void A_place_on_a_screen_that_is_gone_is_dropped_but_the_size_and_state_are_kept()
    {
        var fitted = MainWindowState.Fit(new MainWindowState.Frame(4000, 100, 1000, 700, true), Screen)!;

        Assert.That(fitted.Left,      Is.Null);
        Assert.That(fitted.Top,       Is.Null);
        Assert.That(fitted.Width,     Is.EqualTo(1000));
        Assert.That(fitted.Maximized, Is.True);
    }

    [Test]
    public void A_frame_that_is_too_small_or_missing_is_not_used()
    {
        Assert.That(MainWindowState.Fit(null,                                                    Screen), Is.Null);
        Assert.That(MainWindowState.Fit(new MainWindowState.Frame(0, 0, 50, 40, false), Screen), Is.Null);
    }

    [Test]
    public void A_frame_is_written_and_read_back()
    {
        string file = Path.Combine(Path.GetTempPath(), $"silo-mainwindow-{Guid.NewGuid():N}", "mainwindow.json");
        try
        {
            var frame = new MainWindowState.Frame(10, 20, 900, 600, true);

            MainWindowState.Write(frame, file);

            Assert.That(MainWindowState.Read(file), Is.EqualTo(frame));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Test]
    public void Nothing_kept_or_garbage_reads_as_no_frame()
    {
        string file = Path.Combine(Path.GetTempPath(), $"silo-mainwindow-{Guid.NewGuid():N}.json");
        Assert.That(MainWindowState.Read(file), Is.Null);

        File.WriteAllText(file, "not json");
        try
        {
            Assert.That(MainWindowState.Read(file), Is.Null);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
