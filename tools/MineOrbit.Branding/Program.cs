using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        using var input = File.OpenRead(Path.Combine(root, "src/MineOrbit.UI/Assets/Brand.xaml"));
        var resources = (ResourceDictionary)XamlReader.Load(input);
        var drawing = (DrawingImage)resources["MineOrbitMark"];
        byte[] Render(int size)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(drawing, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var images = sizes.Select(Render).ToArray();
        var iconPath = Path.Combine(root, "src/MineOrbit.UI/Assets/MineOrbit.ico");
        using (var stream = File.Create(iconPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var index = 0; index < sizes.Length; index++)
            {
                writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index])); writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
                writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[index].Length); writer.Write(offset); offset += images[index].Length;
            }
            foreach (var image in images) writer.Write(image);
        }
        File.WriteAllBytes(Path.Combine(root, "assets/mineorbit.png"), Render(1024));
        Console.WriteLine("Generated original MineOrbit PNG + seven-size Windows ICO from editable vector.");
    }
}
