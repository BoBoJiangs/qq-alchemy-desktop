using System.Drawing.Imaging;
using System.Security.Cryptography;
using QQAlchemyDesktop.Domain;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace QQAlchemyDesktop.Automation;

public sealed class WindowsOcrService
{
    private readonly OcrEngine _engine;

    public WindowsOcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("zh-CN"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException("未安装可用的 Windows OCR 中文语言包");
    }

    public async Task<OcrObservation> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var png = ToPng(bitmap);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(softwareBitmap);
        var words = result.Lines.SelectMany(line => line.Words).Select(word =>
            new OcrWordData(word.Text, new PixelRect(
                (int)Math.Round(word.BoundingRect.X),
                (int)Math.Round(word.BoundingRect.Y),
                (int)Math.Round(word.BoundingRect.Width),
                (int)Math.Round(word.BoundingRect.Height)))).ToArray();
        var text = string.Join('\n', result.Lines.Select(line => line.Text));
        return new OcrObservation(text, words, hash, DateTimeOffset.Now);
    }

    public static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

