using Microsoft.Maui.Graphics;

namespace Examifo_Desktop.Pages;

public sealed class DrawingCanvasDrawable : IDrawable
{
    public List<DrawingStroke> Strokes { get; } = [];

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        foreach (DrawingStroke stroke in Strokes)
        {
            canvas.StrokeColor = Color.FromArgb(stroke.ColorHex);
            canvas.StrokeSize = stroke.Thickness;
            PointF Map(PointF point) => new(point.X * dirtyRect.Width, point.Y * dirtyRect.Height);
            if (stroke.Points.Count == 1)
            {
                canvas.FillColor = Color.FromArgb(stroke.ColorHex);
                canvas.FillCircle(Map(stroke.Points[0]), stroke.Thickness / 2);
                continue;
            }
            if (stroke.Points.Count < 2) continue;
            var path = new PathF();
            path.MoveTo(Map(stroke.Points[0]));
            foreach (PointF point in stroke.Points.Skip(1)) path.LineTo(Map(point));
            canvas.DrawPath(path);
        }
    }

    public void Clear() => Strokes.Clear();
}

public sealed record DrawingStroke(List<PointF> Points, string ColorHex, float Thickness);
