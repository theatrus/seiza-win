using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace Seiza.App.Rendering;

internal static class TiledOverlayRasterizer
{
    public static void Draw(ICanvasResourceCreator creator, int width, int height,
        Action<CanvasDrawingSession> draw, Action<ImagePixelRegion, byte[]> composite)
    {
        // Record global geometry once. Each bounded target sees the same scene
        // translated to its tile, including labels/lines crossing a tile edge.
        using var commands = new CanvasCommandList(creator);
        using (var drawing = commands.CreateDrawingSession()) draw(drawing);
        int maximum = Math.Min(4096, creator.Device.MaximumBitmapSizeInPixels);
        foreach (ImagePixelRegion region in ImageTileLayout.Create(width, height, maximum))
        {
            using var target = new CanvasRenderTarget(creator, region.Width, region.Height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                drawing.DrawImage(commands, new Vector2(-region.X, -region.Y));
            }
            composite(region, target.GetPixelBytes());
        }
    }
}
