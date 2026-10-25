using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using EasyVocal.Core.Dsp;
using EasyVocal.Core.Sounds;

namespace EasyVocal;

/// <summary>
/// The sound editor's picture of the sound: a time ruler on top, the wave (the quietest and loudest sample of every
/// pixel column, read from the sound's <see cref="WavePeaks"/> and cached until the sound, the zoom, the scroll or the
/// size changes), the selection, the cursor and the playhead. Drag to select, click to place the cursor (Shift+click
/// extends the selection), Ctrl+wheel zooms around the mouse, the wheel scrolls.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const int Sr = AudioFormat.SampleRate;
    public const double RulerHeight = 20;
    /// <summary>Most zoomed in: this many samples per pixel.</summary>
    public const double MinSamplesPerPixel = 2;

    private static readonly double[] TickSteps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };

    /// <summary>A sound longer than this gets its peaks built off the UI thread (the wave shows once they are ready).</summary>
    private const int BackgroundPeaksAbove = Sr * 20;

    private float[] samples = Array.Empty<float>();
    private WavePeaks? peaks;          // null while a long sound's peaks are being built
    private readonly ConditionalWeakTable<float[], WavePeaks> peaksOf = new(); // undo / redo reuse them
    private double viewStart;          // first sample at the left edge
    private double samplesPerPixel = 1;
    private bool fitted = true;        // zoomed all the way out (follows the width)

    private float[] colMin = Array.Empty<float>(), colMax = Array.Empty<float>();
    private (float[]? Samples, double Start, double Spp, int Columns) cacheKey;

    private int dragAnchor = -1;
    private bool dragging;
    private Point dragFrom;

    private readonly Brush waveBrush, selectionBrush, rulerBrush, textBrush, cursorBrush, playheadBrush, backBrush, borderBrush, centreBrush;
    private readonly Pen wavePen, cursorPen, playheadPen, tickPen, borderPen, centrePen, selEdgePen;
    private readonly Typeface typeface = new("Segoe UI");

    public WaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        var res = Application.Current.Resources;
        waveBrush = (Brush)res["Accent"];
        textBrush = (Brush)res["TextDim"];
        cursorBrush = (Brush)res["Text"];
        playheadBrush = (Brush)res["Warn"];
        backBrush = (Brush)res["Bg1"];
        rulerBrush = (Brush)res["Bg2"];
        borderBrush = (Brush)res["Border"];
        centreBrush = (Brush)res["Bg3"];
        selectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x4A, 0x4A, 0xDE, 0x80)));
        wavePen = Frozen(new Pen(waveBrush, 1));
        cursorPen = Frozen(new Pen(cursorBrush, 1.5));
        playheadPen = Frozen(new Pen(playheadBrush, 2));
        tickPen = Frozen(new Pen(textBrush, 1));
        borderPen = Frozen(new Pen(borderBrush, 1));
        centrePen = Frozen(new Pen(centreBrush, 1));
        selEdgePen = Frozen(new Pen(waveBrush, 1));
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>The selection or the cursor moved (by the mouse).</summary>
    public event Action? SelectionChanged;

    /// <summary>The zoom or the scroll changed.</summary>
    public event Action? ViewChanged;

    public float[] Samples
    {
        get => samples;
        set
        {
            samples = value ?? Array.Empty<float>();
            UsePeaks();
            SelectionStart = Math.Clamp(SelectionStart, 0, samples.Length);
            SelectionLength = Math.Clamp(SelectionLength, 0, samples.Length - SelectionStart);
            CursorPosition = Math.Clamp(CursorPosition, 0, samples.Length);
            if (fitted || samplesPerPixel >= FitSamplesPerPixel) Fit();
            else SetView(viewStart, samplesPerPixel);
            InvalidateVisual();
        }
    }

    /// <summary>The peaks of <see cref="Samples"/>: remembered per sound, built at once for a short one, in the background for a long one.</summary>
    private void UsePeaks()
    {
        cacheKey = default;
        if (peaksOf.TryGetValue(samples, out var known)) peaks = known;
        else if (samples.Length <= BackgroundPeaksAbove) peaksOf.Add(samples, peaks = new WavePeaks(samples));
        else
        {
            peaks = null;
            BuildPeaks(samples);
        }
    }

    private async void BuildPeaks(float[] sound)
    {
        var built = await Task.Run(() => new WavePeaks(sound));
        peaksOf.AddOrUpdate(sound, built);
        if (!ReferenceEquals(samples, sound)) return; // another version is shown by now
        peaks = built;
        cacheKey = default;
        InvalidateVisual();
    }

    public int SelectionStart { get; private set; }
    public int SelectionLength { get; private set; }
    public bool HasSelection => SelectionLength > 0;
    public int CursorPosition { get; private set; }

    /// <summary>Where the playhead is drawn (a sample), or -1 for none.</summary>
    public int Playhead
    {
        get => playhead;
        set
        {
            if (playhead == value) return;
            playhead = value;
            InvalidateVisual();
        }
    }
    private int playhead = -1;

    public double ViewStart => viewStart;
    public double SamplesPerPixel => samplesPerPixel;
    private double Columns => Math.Max(1, ActualWidth);
    /// <summary>How many samples the view shows.</summary>
    public double VisibleSamples => Columns * samplesPerPixel;
    public double FitSamplesPerPixel => Math.Max(MinSamplesPerPixel, samples.Length / Columns);
    public bool CanZoomIn => samplesPerPixel > MinSamplesPerPixel + 1e-9;
    public bool CanZoomOut => !fitted;

    public void Select(int start, int length)
    {
        int s = Math.Clamp(start, 0, samples.Length);
        int l = Math.Clamp(length, 0, samples.Length - s);
        SelectionStart = s;
        SelectionLength = l;
        CursorPosition = s;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void PlaceCursor(int position)
    {
        SelectionStart = Math.Clamp(position, 0, samples.Length);
        SelectionLength = 0;
        CursorPosition = SelectionStart;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    // ---------- zoom and scroll ----------

    public void Fit()
    {
        fitted = true;
        SetView(0, FitSamplesPerPixel);
    }

    /// <summary>Zooms by <paramref name="factor"/> (&gt;1 = in) keeping the sample under <paramref name="anchorX"/> in place (null = the centre).</summary>
    public void Zoom(double factor, double? anchorX = null)
    {
        double x = anchorX ?? Columns / 2;
        double anchor = viewStart + x * samplesPerPixel;
        double spp = Math.Clamp(samplesPerPixel / factor, MinSamplesPerPixel, FitSamplesPerPixel);
        if (spp >= FitSamplesPerPixel - 1e-9)
        {
            Fit();
            return;
        }
        fitted = false;
        SetView(anchor - x * spp, spp);
    }

    public void ScrollTo(double start) => SetView(start, samplesPerPixel);

    private void SetView(double start, double spp)
    {
        samplesPerPixel = Math.Max(MinSamplesPerPixel, spp);
        double maxStart = Math.Max(0, samples.Length - VisibleSamples);
        viewStart = Math.Clamp(start, 0, maxStart);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (fitted) Fit();
        else SetView(viewStart, Math.Min(samplesPerPixel, FitSamplesPerPixel));
    }

    // ---------- mouse ----------

    public int SampleAt(double x) => (int)Math.Clamp(Math.Round(viewStart + x * samplesPerPixel), 0, samples.Length);

    private double XOf(double sample) => (sample - viewStart) / samplesPerPixel;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (samples.Length == 0) return;
        Focus();
        dragFrom = e.GetPosition(this);
        int at = SampleAt(dragFrom.X);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int from = HasSelection ? (at < SelectionStart + SelectionLength / 2 ? SelectionStart + SelectionLength : SelectionStart) : CursorPosition;
            dragAnchor = from;
            dragging = true;
            ExtendTo(at);
        }
        else
        {
            dragAnchor = at;
            dragging = false;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragAnchor < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!dragging && Math.Abs(p.X - dragFrom.X) < 3) return;
        dragging = true;
        // Dragging past an edge scrolls the view along.
        if (p.X < 0) ScrollTo(viewStart + p.X * samplesPerPixel);
        else if (p.X > ActualWidth) ScrollTo(viewStart + (p.X - ActualWidth) * samplesPerPixel);
        ExtendTo(SampleAt(Math.Clamp(p.X, 0, ActualWidth)));
    }

    private void ExtendTo(int at)
    {
        int a = Math.Min(dragAnchor, at), b = Math.Max(dragAnchor, at);
        SelectionStart = a;
        SelectionLength = b - a;
        CursorPosition = a;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragAnchor < 0) return;
        ReleaseMouseCapture();
        if (!dragging) PlaceCursor(dragAnchor);
        dragAnchor = -1;
        dragging = false;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragAnchor = -1;
        dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (samples.Length == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Zoom(e.Delta > 0 ? 1.5 : 1 / 1.5, e.GetPosition(this).X);
        else if (!fitted) ScrollTo(viewStart - Math.Sign(e.Delta) * VisibleSamples * 0.1);
        e.Handled = true;
    }

    // ---------- drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < RulerHeight + 10) return;
        var waveRect = new Rect(0, RulerHeight, w, h - RulerHeight);
        dc.DrawRectangle(rulerBrush, null, new Rect(0, 0, w, RulerHeight));
        dc.DrawRectangle(backBrush, null, waveRect);

        if (HasSelection)
        {
            double x0 = Math.Max(0, XOf(SelectionStart)), x1 = Math.Min(w, XOf(SelectionStart + SelectionLength));
            if (x1 > x0)
            {
                dc.DrawRectangle(selectionBrush, null, new Rect(x0, RulerHeight, Math.Max(1, x1 - x0), waveRect.Height));
                dc.DrawLine(selEdgePen, new Point(x0 + 0.5, RulerHeight), new Point(x0 + 0.5, h));
                dc.DrawLine(selEdgePen, new Point(x1 - 0.5, RulerHeight), new Point(x1 - 0.5, h));
            }
        }

        double mid = RulerHeight + waveRect.Height / 2;
        double half = waveRect.Height / 2 - 4;
        dc.DrawLine(centrePen, new Point(0, mid), new Point(w, mid));
        DrawWave(dc, (int)Math.Ceiling(w), mid, half);
        DrawRuler(dc, w);

        if (!HasSelection && samples.Length > 0)
        {
            double cx = Math.Clamp(XOf(CursorPosition), 1, w - 1);
            if (XOf(CursorPosition) >= -1 && XOf(CursorPosition) <= w + 1) dc.DrawLine(cursorPen, new Point(cx, RulerHeight), new Point(cx, h));
        }
        if (playhead >= 0)
        {
            double px = XOf(playhead);
            if (px >= -2 && px <= w + 2) dc.DrawLine(playheadPen, new Point(px, 0), new Point(px, h));
        }
        dc.DrawRectangle(null, borderPen, new Rect(0.5, 0.5, w - 1, h - 1));
    }

    private void DrawWave(DrawingContext dc, int columns, double mid, double half)
    {
        if (samples.Length == 0 || peaks == null) return;
        Compute(peaks, columns);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int c = 0; c < columns; c++)
            {
                float lo = colMin[c], hi = colMax[c];
                if (lo > hi) continue; // past the end
                double y0 = mid - Math.Clamp(hi, -1f, 1f) * half;
                double y1 = mid - Math.Clamp(lo, -1f, 1f) * half;
                if (y1 - y0 < 1) { y0 -= 0.5; y1 += 0.5; }
                g.BeginFigure(new Point(c + 0.5, y0), false, false);
                g.LineTo(new Point(c + 0.5, y1), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, wavePen, geo);
    }

    /// <summary>The lowest and highest sample under every pixel column (only redone when something changed).</summary>
    private void Compute(WavePeaks peaks, int columns)
    {
        if (ReferenceEquals(cacheKey.Samples, samples) && cacheKey.Start == viewStart && cacheKey.Spp == samplesPerPixel && cacheKey.Columns == columns)
            return;
        if (colMin.Length != columns)
        {
            colMin = new float[columns];
            colMax = new float[columns];
        }
        for (int c = 0; c < columns; c++)
        {
            long a = (long)Math.Floor(viewStart + c * samplesPerPixel);
            long b = (long)Math.Floor(viewStart + (c + 1) * samplesPerPixel);
            if (b <= a) b = a + 1;
            if (a >= samples.Length) { colMin[c] = 1; colMax[c] = -1; continue; }
            b = Math.Min(b, samples.Length);
            if (a > 0) a--; // from the previous column's last sample: neighbouring columns join up, no gaps between them
            (colMin[c], colMax[c]) = peaks.MinMax(a, b);
        }
        // Zoomed in to less than a few periods per column, one column holds only part of a wave and the picture
        // turns into stripes: every column also takes in its neighbours, so the wave reads as one shape.
        if (samplesPerPixel < 2000)
        {
            float prevLo = colMin[0], prevHi = colMax[0];
            for (int c = 0; c < columns; c++)
            {
                float lo = colMin[c], hi = colMax[c];
                if (lo > hi) break; // past the end
                float nLo = lo, nHi = hi;
                if (c + 1 < columns && colMin[c + 1] <= colMax[c + 1]) { nLo = Math.Min(nLo, colMin[c + 1]); nHi = Math.Max(nHi, colMax[c + 1]); }
                colMin[c] = Math.Min(nLo, prevLo);
                colMax[c] = Math.Max(nHi, prevHi);
                prevLo = lo;
                prevHi = hi;
            }
        }
        cacheKey = (samples, viewStart, samplesPerPixel, columns);
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        double pxPerSecond = Sr / samplesPerPixel;
        double step = TickSteps[^1];
        foreach (var s in TickSteps)
            if (s * pxPerSecond >= 80) { step = s; break; }
        double first = Math.Floor(viewStart / Sr / step) * step;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = first; ; t += step)
        {
            double x = XOf(t * Sr);
            if (x > w) break;
            if (x < -60) continue;
            dc.DrawLine(tickPen, new Point(x + 0.5, RulerHeight - 6), new Point(x + 0.5, RulerHeight));
            var text = new FormattedText(Time(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, textBrush, dpi);
            dc.DrawText(text, new Point(x + 3, 2));
        }
        // Small ticks between the labels.
        double minor = step / 5;
        for (double t = first; ; t += minor)
        {
            double x = XOf(t * Sr);
            if (x > w) break;
            if (x < 0) continue;
            dc.DrawLine(tickPen, new Point(x + 0.5, RulerHeight - 3), new Point(x + 0.5, RulerHeight));
        }
    }

    /// <summary>m:ss.s</summary>
    public static string Time(double seconds)
    {
        if (!(seconds > 0)) seconds = 0;
        int tenths = (int)Math.Round(seconds * 10);
        int m = tenths / 600;
        double s = (tenths % 600) / 10.0;
        return m.ToString(CultureInfo.InvariantCulture) + ":" + s.ToString("00.0", CultureInfo.InvariantCulture);
    }

    public static string Time(int samples) => Time(samples / (double)Sr);
}
