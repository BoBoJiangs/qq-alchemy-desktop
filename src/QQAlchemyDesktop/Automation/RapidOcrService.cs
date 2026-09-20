using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using QQAlchemyDesktop.Domain;
using RapidOCRLib;

namespace QQAlchemyDesktop.Automation;

public sealed class RapidOcrService
{
    private const int ScaleFactor = 2;
    private readonly OcrLite _engine;
    private readonly Task _initialization;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RapidOcrService(string? modelDirectory = null)
    {
        var models = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "models");
        _engine = new OcrLite
        {
            DetPath = Path.Combine(models, "ch_PP-OCRv5_mobile_det.onnx"),
            ClsPath = Path.Combine(models, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            RecPath = Path.Combine(models, "ch_PP-OCRv5_rec_mobile_infer.onnx"),
            KeyDicPath = Path.Combine(models, "ppocrv5_dict.txt"),
            ThreadNum = Math.Clamp(Environment.ProcessorCount / 2, 1, 6)
        };
        _initialization = _engine.InitModels();
    }

    public async Task<OcrObservation> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var png = ToPng(bitmap);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _initialization;
            cancellationToken.ThrowIfCancellationRequested();
            using var scaled = Scale(bitmap, ScaleFactor);
            var result = await _engine.DetectAsync(scaled, padding: 0,
                maxSideLen: Math.Max(scaled.Width, scaled.Height), boxScoreThresh: 0.55f,
                boxThresh: 0.3f, unClipRatio: 1.6f, doAngle: false, mostAngle: false);
            try
            {
                var words = result.TextBlocks
                    .Where(block => !string.IsNullOrWhiteSpace(block.Text) && block.BoxPoints.Count >= 4)
                    .Select(block => ToWord(bitmap, block))
                    .OrderBy(word => word.Bounds.Y)
                    .ThenBy(word => word.Bounds.X)
                    .ToArray();
                return new OcrObservation(BuildText(words), words, hash, DateTimeOffset.Now);
            }
            finally
            {
                result.BoxImg?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static OcrWordData ToWord(Bitmap original, RapidOCRLib.Models.TextBlock block)
    {
        var minX = (int)Math.Floor(block.BoxPoints.Min(point => point.X) / (double)ScaleFactor);
        var minY = (int)Math.Floor(block.BoxPoints.Min(point => point.Y) / (double)ScaleFactor);
        var maxX = (int)Math.Ceiling(block.BoxPoints.Max(point => point.X) / (double)ScaleFactor);
        var maxY = (int)Math.Ceiling(block.BoxPoints.Max(point => point.Y) / (double)ScaleFactor);
        var detected = new PixelRect(
            Math.Clamp(minX, 0, original.Width - 1),
            Math.Clamp(minY, 0, original.Height - 1),
            Math.Max(1, Math.Min(original.Width, maxX) - Math.Clamp(minX, 0, original.Width - 1)),
            Math.Max(1, Math.Min(original.Height, maxY) - Math.Clamp(minY, 0, original.Height - 1)));
        var bounds = BlueLinkLocator.TryFindBounds(original, detected, out var blueBounds)
            ? blueBounds
            : detected;
        var recognitionScore = block.CharScores.Count == 0 ? 0d : block.CharScores.Average();
        return new OcrWordData(block.Text.Trim(), bounds, Math.Min(block.BoxScore, recognitionScore));
    }

    private static string BuildText(IReadOnlyList<OcrWordData> words)
    {
        var lines = new List<List<OcrWordData>>();
        foreach (var word in words)
        {
            var center = word.Bounds.Y + word.Bounds.Height / 2d;
            var line = lines.FirstOrDefault(candidate =>
                Math.Abs(candidate.Average(item => item.Bounds.Y + item.Bounds.Height / 2d) - center) <=
                Math.Max(8, word.Bounds.Height * 0.7));
            if (line is null) lines.Add([word]); else line.Add(word);
        }
        return string.Join('\n', lines.Select(line =>
            string.Concat(line.OrderBy(word => word.Bounds.X).Select(word => word.Text))));
    }

    private static Bitmap Scale(Bitmap source, int factor)
    {
        var result = new Bitmap(source.Width * factor, source.Height * factor, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
