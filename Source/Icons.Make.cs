using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace AudionVoiceTools;

/// <summary>
/// How the signs of Icons.cs get onto the screen. A Lucide sign is strokes on a 24 grid, shown on
/// the 10 grid of the style "Path.icon" (the shell of Audion Disk Tools: centred by App.CenterIcon,
/// the line as thick as there).
/// </summary>
public static partial class Icons
{
    /// <summary>The geometry of a Lucide sign on the 10 grid; null when the set has no such name.</summary>
    public static Geometry? Glyph(string name)
    {
        if (Made.TryGetValue(name, out var made)) return made;
        if (!Lucide.TryGetValue(name, out var data)) return null;
        var geometry = Geometry.Parse(data);
        // Scaled in the geometry, not by a Viewbox: the pen keeps the thickness of the style.
        geometry.Transform = new ScaleTransform(10 / 24.0, 10 / 24.0);
        // A sign is drawn hundreds of times on a page of cards: its drawing is read once and shared.
        return Made[name] = geometry;
    }

    static readonly System.Collections.Generic.Dictionary<string, Geometry> Made = new();

    /// <summary>A Lucide sign ready for a button.</summary>
    public static Path Sign(string name) => new() { Classes = { "icon" }, Data = Glyph(name) };

    /// <summary>
    /// A brand mark in the colour of its brand (the owner, 15.10.2026: "Пиктограммы брендов акцентируем цветом бренда"),
    /// fitted into a square of <paramref name="size"/> by the bounds of its own drawing - as Audion Browsers Portable draws
    /// it. A colour that would vanish on the ground of the theme - the black of deno and 7-Zip, a dark purple on a dark
    /// ground - gives way to the ink of the text around it. Null when the set has no such mark.
    /// </summary>
    public static Path? Mark(string name, double size = 12)
    {
        if (!Brand.TryGetValue(name, out var brand)) return null;
        var geometry = Geometry.Parse(brand.Path);
        var bounds = geometry.Bounds;
        double side = System.Math.Max(bounds.Width, bounds.Height);
        if (side <= 0 || bounds.Height <= 0) return null;
        // A word for a mark (intel) fitted into the square is a line of dust: it takes the height, up to three squares wide.
        double width = bounds.Width / bounds.Height > 1.8 ? System.Math.Min(size * 3, size * bounds.Width / bounds.Height) : size;
        double scale = System.Math.Min(width / bounds.Width, size / bounds.Height);
        geometry.Transform = new MatrixTransform(
            Avalonia.Matrix.CreateTranslation(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2)
            * Avalonia.Matrix.CreateScale(scale, scale)
            * Avalonia.Matrix.CreateTranslation(width / 2, size / 2));
        var path = new Path { Classes = { "iconfill" }, Data = geometry, Width = width, Height = size };
        if (Readable(brand.Hex) is { } ink) path.Fill = ink;
        return path;
    }

    /// <summary>The colour of a brand when it stands out of the ground of the theme by 3 : 1 at least; null - the ink of the text.</summary>
    static IBrush? Readable(string hex)
    {
        if (hex.Length == 0 || !Color.TryParse(hex, out var colour)) return null;
        var ground = Avalonia.Application.Current is { } app && app.TryGetResource("PanelBg", app.ActualThemeVariant, out var found) && found is ISolidColorBrush brush
            ? brush.Color : Colors.Black;
        double Light(Color c)
        {
            double Part(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Part(c.R) + 0.7152 * Part(c.G) + 0.0722 * Part(c.B);
        }
        double a = Light(colour), b = Light(ground);
        double contrast = (System.Math.Max(a, b) + 0.05) / (System.Math.Min(a, b) + 0.05);
        return contrast >= 3 ? new SolidColorBrush(colour) : null;
    }
}
