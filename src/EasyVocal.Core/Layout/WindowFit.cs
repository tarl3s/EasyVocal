using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EasyVocal.Core.Model;

/// <summary>
/// A window's place on the screen in device-independent pixels. <see cref="Width"/> and <see cref="Height"/> of 0
/// mean "only the position" (a window that sizes itself to its content).
/// </summary>
public record struct WindowBox(double Left, double Top, double Width, double Height)
{
    [JsonIgnore] public readonly double Right => Left + Width;
    [JsonIgnore] public readonly double Bottom => Top + Height;
    [JsonIgnore] public readonly bool HasSize => Width > 0 && Height > 0;

    [JsonIgnore] public readonly bool IsFinite =>
        double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) &&
        Width >= 0 && Height >= 0;
}

/// <summary>
/// Keeps windows on the screen: never bigger than the monitor's working area (the screen minus the taskbar), never
/// off it, and a remembered place only when it still fits one of today's screens.
/// </summary>
public static class WindowFit
{
    /// <summary>A remembered window may hang this far over a screen's edge (the invisible resize borders).</summary>
    public const double Slack = 12;

    /// <summary>
    /// The window moved (and shrunk, if it must) so it lies inside <paramref name="area"/>. The size is never
    /// below <paramref name="minWidth"/> × <paramref name="minHeight"/> unless the area itself is smaller.
    /// </summary>
    public static WindowBox Fit(WindowBox window, WindowBox area, double minWidth = 0, double minHeight = 0)
    {
        double w = Math.Min(Math.Max(window.Width, Math.Min(minWidth, area.Width)), area.Width);
        double h = Math.Min(Math.Max(window.Height, Math.Min(minHeight, area.Height)), area.Height);
        double left = Math.Clamp(window.Left, area.Left, area.Right - w);
        double top = Math.Clamp(window.Top, area.Top, area.Bottom - h);
        return new WindowBox(left, top, w, h);
    }

    /// <summary>
    /// The working area a window belongs to: the one holding its centre, else the one it overlaps most, else the
    /// first (the primary screen). Throws when there is none.
    /// </summary>
    public static WindowBox AreaFor(WindowBox window, IReadOnlyList<WindowBox> areas)
    {
        if (areas.Count == 0) throw new ArgumentException("No screens.", nameof(areas));
        double cx = window.Left + window.Width / 2, cy = window.Top + window.Height / 2;
        foreach (var a in areas)
            if (cx >= a.Left && cx < a.Right && cy >= a.Top && cy < a.Bottom) return a;
        WindowBox best = areas[0];
        double bestOverlap = 0;
        foreach (var a in areas)
        {
            double ow = Math.Min(window.Right, a.Right) - Math.Max(window.Left, a.Left);
            double oh = Math.Min(window.Bottom, a.Bottom) - Math.Max(window.Top, a.Top);
            double overlap = ow > 0 && oh > 0 ? ow * oh : 0;
            if (overlap > bestOverlap) { bestOverlap = overlap; best = a; }
        }
        return best;
    }

    /// <summary>
    /// True when a remembered place can be used as it is: a sane box lying inside one screen's working area
    /// (give or take <see cref="Slack"/>), at least <paramref name="minWidth"/> × <paramref name="minHeight"/> when
    /// it has a size. A position-only box needs its top-left corner region (where the title bar is) on a screen.
    /// </summary>
    public static bool Fits(WindowBox saved, IReadOnlyList<WindowBox> areas, double minWidth = 0, double minHeight = 0)
    {
        if (!saved.IsFinite || areas.Count == 0) return false;
        if (saved.HasSize && (saved.Width < minWidth || saved.Height < minHeight)) return false;
        if (!saved.HasSize && (saved.Width != 0 || saved.Height != 0)) return false;
        double w = saved.HasSize ? saved.Width : 120, h = saved.HasSize ? saved.Height : 40;
        foreach (var a in areas)
        {
            if (saved.Left >= a.Left - Slack && saved.Top >= a.Top - Slack &&
                saved.Left + w <= a.Right + Slack && saved.Top + h <= a.Bottom + Slack)
                return true;
        }
        return false;
    }
}
