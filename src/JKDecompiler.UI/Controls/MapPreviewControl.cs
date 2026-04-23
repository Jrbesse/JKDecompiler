using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JKDecompiler.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JKDecompiler.UI.Controls;

public class MapPreviewControl : Control
{
    private List<List<Point>>? _cachedLines;

    public static readonly StyledProperty<BspData?> BspDataProperty =
        AvaloniaProperty.Register<MapPreviewControl, BspData?>(nameof(BspData));

    public BspData? BspData
    {
        get => GetValue(BspDataProperty);
        set => SetValue(BspDataProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        PrepareGeometry();
        InvalidateVisual();
        return result;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BspDataProperty)
        {
            PrepareGeometry();
            InvalidateVisual();
        }
    }

    private void PrepareGeometry()
    {
        if (BspData == null || BspData.Vertices.Count == 0 || Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            _cachedLines = null;
            return;
        }

        var validVertices = BspData.Vertices
            .Where(v => !float.IsNaN(v.Position.X) && !float.IsInfinity(v.Position.X) && 
                        !float.IsNaN(v.Position.Z) && !float.IsInfinity(v.Position.Z) &&
                        Math.Abs(v.Position.X) < 50000 && Math.Abs(v.Position.Z) < 50000)
            .ToList();

        if (validVertices.Count == 0) return;

        float minX = validVertices.Min(v => v.Position.X);
        float maxX = validVertices.Max(v => v.Position.X);
        float minZ = validVertices.Min(v => v.Position.Z);
        float maxZ = validVertices.Max(v => v.Position.Z);
        
        float spanX = Math.Max(1, maxX - minX);
        float spanZ = Math.Max(1, maxZ - minZ);
        float scale = Math.Min((float)Bounds.Width / spanX, (float)Bounds.Height / spanZ) * 0.8f;
        var centerX = Bounds.Width / 2;
        var centerY = Bounds.Height / 2;

        _cachedLines = new List<List<Point>>();

        foreach (var face in BspData.Faces.Take(1000))
        {
            if (face.NumVertices < 3) continue;
            var points = new List<Point>();
            int vertexLimit = Math.Min(face.NumVertices, 50);
            for (int i = 0; i < vertexLimit; i++)
            {
                int vIdx = face.FirstVertexIndex + i;
                if (vIdx < 0 || vIdx >= BspData.Vertices.Count) continue;
                var v = BspData.Vertices[vIdx];
                if (float.IsNaN(v.Position.X) || float.IsNaN(v.Position.Z)) continue;

                // Project X/Z to 2D
                points.Add(new Point(centerX + (v.Position.X - (minX + maxX) / 2) * scale, 
                                     centerY - (v.Position.Z - (minZ + maxZ) / 2) * scale));
            }
            _cachedLines.Add(points);
        }
    }

    public override void Render(DrawingContext context)
    {
        // Debug: Draw a red cross
        context.DrawLine(new Pen(Brushes.Red, 2), new Point(0, 0), new Point(Bounds.Width, Bounds.Height));
        context.DrawLine(new Pen(Brushes.Red, 2), new Point(Bounds.Width, 0), new Point(0, Bounds.Height));

        if (_cachedLines == null) return;
        
        var pen = new Pen(Brushes.LimeGreen, 3);
        foreach (var points in _cachedLines)
        {
            if (points.Count < 2) continue;
            for (int i = 0; i < points.Count; i++)
            {
                context.DrawLine(pen, points[i], points[(i + 1) % points.Count]);
            }
        }
    }
}
