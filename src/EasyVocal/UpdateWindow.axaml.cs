using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using EasyVocal.Core.Text;

namespace EasyVocal;

/// <summary>
/// "EasyVocal X is available": the release notes, <b>Update now</b> (downloads with a progress bar and Cancel, checks
/// the download, then closes with <see cref="PreparedExe"/> set — <see cref="UpdateCoordinator"/> hands over to the helper
/// and quits), <b>Later</b>, and <b>Skip this version</b> (saved in the settings).
/// </summary>
public partial class UpdateWindow : AppWindow
{
    private readonly AppController controller;
    private readonly UpdateService service;
    private readonly ReleaseInfo release;
    private readonly ReleaseAsset asset;
    private readonly bool canInstall;
    private CancellationTokenSource? download;
    private double progressValue;

    /// <summary>One update dialog at a time.</summary>
    protected override string? SingleKind => "update";
    private bool closed;

    /// <param name="canInstall">False in a build run from the source code: the window only tells.</param>
    public UpdateWindow(AppController controller, UpdateService service, ReleaseInfo release, ReleaseAsset asset, bool canInstall)
    {
        this.controller = controller;
        this.service = service;
        this.release = release;
        this.asset = asset;
        this.canInstall = canInstall;
        InitializeComponent();
        Place("update", controller);
        Headline.Text = L.F("EasyVocal {0} is available (you have {1})", release.Version, service.Current);
        FillNotes(ReleaseNotes.ToLines(release.Notes));
        if (!canInstall)
        {
            UpdateButton.IsEnabled = false;
            ShowStatus(L.T("This EasyVocal runs from its source code, so it doesn't update itself."), problem: false);
        }
        ProgressTrack.SizeChanged += (_, _) => SetProgress(progressValue);
        Closing += (_, _) => download?.Cancel(); // the service removes what it had downloaded
        Closed += (_, _) => closed = true;
    }

    /// <summary>The checked new EasyVocal, once Update now finished downloading; null otherwise.</summary>
    public string? PreparedExe { get; private set; }

    /// <summary>A sentence for the person about why an update stopped.</summary>
    internal static string ProblemText(UpdateProblem problem) => problem switch
    {
        UpdateProblem.NotAZip or UpdateProblem.UnsafeZip or UpdateProblem.MissingExe or UpdateProblem.NotAnExe =>
            L.T("The downloaded update doesn't look right, so it wasn't installed. Try again later."),
        UpdateProblem.FolderNotWritable =>
            L.T("EasyVocal can't replace itself in this folder. We opened the download page so you can get the new version."),
        UpdateProblem.Launch => L.T("EasyVocal couldn't start the update. Try again later."),
        _ => L.T("The update couldn't be downloaded. Check your internet connection and try again."),
    };

    private void FillNotes(IReadOnlyList<NoteLine> lines)
    {
        NotesPanel.Children.Clear();
        if (lines.Count == 0)
        {
            NotesPanel.Children.Add(new TextBlock { Text = L.T("No notes for this version."), Foreground = Res.Brush("TextDim") });
            return;
        }
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case NoteKind.Blank:
                    NotesPanel.Children.Add(new Border { Height = 6 });
                    break;
                case NoteKind.Heading:
                    NotesPanel.Children.Add(new TextBlock
                    {
                        Text = line.Text, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 0 : 4, 0, 2),
                    });
                    break;
                case NoteKind.Bullet:
                    var row = new DockPanel { Margin = new Thickness(4, 1, 0, 1) };
                    var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), Foreground = Res.Brush("Accent") };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap });
                    NotesPanel.Children.Add(row);
                    break;
                default:
                    NotesPanel.Children.Add(new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                    break;
            }
        }
    }

    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (download != null || !canInstall) return;
        if (!service.CanSelfUpdate())
        {
            ShowStatus(ProblemText(UpdateProblem.FolderNotWritable), problem: true);
            OpenPage(release.PageUrl);
            return;
        }
        var cancel = download = new CancellationTokenSource();
        SetBusy(true);
        SetProgress(0);
        ShowStatus(L.F("Downloading… {0}%", 0), problem: false);
        var progress = new Progress<double>(p =>
        {
            if (download != cancel) return;
            SetProgress(0.95 * p);
            StatusText.Text = p >= 1 ? L.T("Checking the download…") : L.F("Downloading… {0}%", (int)Math.Round(100 * p));
        });
        try
        {
            var exe = await service.DownloadAsync(asset, progress, cancel.Token);
            if (closed)
            {
                service.Discard(exe); // closed while it was being checked: nobody will install it
                return;
            }
            PreparedExe = exe;
            SetProgress(1);
            Close();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            AppLog.Info("Update download stopped");
            ShowStatus(L.T("The download was stopped."), problem: false);
        }
        catch (UpdateException ex)
        {
            AppLog.Error("Update download failed (" + ex.Problem + ")", ex);
            ShowStatus(ProblemText(ex.Problem), problem: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update download failed", ex);
            ShowStatus(ProblemText(UpdateProblem.Download), problem: true);
        }
        finally
        {
            download = null;
            cancel.Dispose();
            if (PreparedExe == null) SetBusy(false);
        }
    }

    private void LaterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (download != null) download.Cancel(); // while downloading this button is Cancel
        else Close();
    }

    private void SkipButton_Click(object? sender, RoutedEventArgs e)
    {
        controller.Settings.SkippedUpdateVersion = release.Version.ToString();
        controller.Save();
        AppLog.Info("Update " + release.Tag + " skipped");
        Close();
    }

    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy && canInstall;
        SkipButton.IsEnabled = !busy;
        LaterButton.Content = busy ? L.T("Cancel") : L.T("Later");
        ProgressTrack.IsVisible = busy;
    }

    private void ShowStatus(string text, bool problem)
    {
        StatusText.Text = text;
        StatusText.Foreground = Res.Brush(problem ? "Warn" : "TextDim");
        StatusText.IsVisible = true;
    }

    private void SetProgress(double value)
    {
        progressValue = Math.Clamp(value, 0, 1);
        ProgressFill.Width = ProgressTrack.Bounds.Width * progressValue;
    }

    private static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("Opening the release page failed", ex); }
    }
}
