using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// <c>--smoketest</c>: starts like a normal launch, saves a picture of each tab (<c>smoke_voices.png</c>,
/// <c>smoke_sounds.png</c> in the data folder), then quits the normal way and logs "Smoke test passed".
/// Proves the app starts and closes cleanly without a person (use it with <c>--data</c> and a scratch folder).
/// </summary>
public partial class App
{
    private void RunSmokeTest(string dataFolder)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window == null || controller == null) return;
            try
            {
                Snapshot(window, Path.Combine(dataFolder, "smoke_voices.png"));
                window.Tabs.SelectedIndex = 1;
                window.UpdateLayout();
                Snapshot(window, Path.Combine(dataFolder, "smoke_sounds.png"));
                window.SetShowHidden(true);
                window.UpdateLayout();
                Snapshot(window, Path.Combine(dataFolder, "smoke_hidden.png"));
            }
            catch (Exception ex)
            {
                AppLog.Error("Smoke test snapshot failed", ex);
            }
            AppLog.Info($"Smoke test: language {L.Language}, audio {controller.State}, {window.VoiceCount} voices and "
                        + $"{window.SoundCount} sounds on screen");
            Quit(ask: false);
            AppLog.Info("Smoke test passed");
        };
        timer.Start();
    }

    private static void Snapshot(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + 32), (int)Math.Ceiling(content.ActualHeight + 26),
            96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, bitmap.Width, bitmap.Height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(16, 14, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
