using HinoDocumentAI.Service.Models;
using OpenCvSharp;
using PDFtoImage;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using SkiaSharp;

namespace HinoDocumentAI.Service.Services;

public interface IOcrService
{
    Task<List<OcrPageLayout>> ExtractPagesAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

public sealed class OcrService : IOcrService, IDisposable
{
    private readonly PaddleOcrAll _ocr;
    private readonly SemaphoreSlim _ocrLock = new(1, 1);
    private readonly bool _enableColorContrastPass;
    private readonly double _colorPassConfidenceThreshold;
    private readonly int _pdfDpi;
    private readonly ILogger<OcrService> _logger;

    public OcrService(IConfiguration configuration, ILogger<OcrService> logger)
    {
        _logger = logger;
        _enableColorContrastPass = configuration.GetValue("Ocr:EnableColorContrastPass", true);
        _colorPassConfidenceThreshold = configuration.GetValue(
            "Ocr:ColorPassConfidenceThreshold",
            0.92);
        _pdfDpi = Math.Clamp(configuration.GetValue("Ocr:PdfDpi", 300), 150, 450);

        FullOcrModel model = LocalFullModels.LatinV5;
        _ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };
    }

    public async Task<List<OcrPageLayout>> ExtractPagesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        bool isPdf = string.Equals(
            Path.GetExtension(filePath),
            ".pdf",
            StringComparison.OrdinalIgnoreCase);

        var pages = new List<OcrPageLayout>();

        if (isPdf)
        {
            using var pdfStream = File.OpenRead(filePath);
            var renderOptions = new RenderOptions(Dpi: _pdfDpi);
            await foreach (SKBitmap page in Conversion.ToImagesAsync(
                                   pdfStream,
                                   options: renderOptions,
                                   cancellationToken: cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                using (page)
                using (Mat mat = SkBitmapToMat(page))
                {
                    pages.Add(await RunOcrAsync(mat, cancellationToken));
                }
            }
        }
        else
        {
            using Mat mat = Cv2.ImRead(filePath, ImreadModes.Color);
            if (mat.Empty())
            {
                throw new InvalidDataException("File gambar tidak dapat dibaca oleh OpenCV.");
            }

            pages.Add(await RunOcrAsync(mat, cancellationToken));
        }

        return pages;
    }

    private async Task<OcrPageLayout> RunOcrAsync(Mat source, CancellationToken cancellationToken)
    {
        await _ocrLock.WaitAsync(cancellationToken);
        try
        {
            // Keep the original pixels authoritative. The former pipeline ran
            // aggressive colour enhancement first, which could turn already
            // clear black text into confident but incorrect characters.
            PaddleOcrResult originalResult = _ocr.Run(source);
            var candidates = ToCandidates(originalResult, source.Width, source.Height);
            double averageConfidence = candidates.Count == 0
                ? 0
                : candidates.Average(candidate => candidate.Confidence);

            bool hasColourInk = _enableColorContrastPass && HasChromaticInk(source);
            if (_enableColorContrastPass &&
                (hasColourInk || candidates.Count < 5 ||
                 averageConfidence < _colorPassConfidenceThreshold))
            {
                using Mat enhanced = CreateColorRobustImage(source);
                PaddleOcrResult colourResult = _ocr.Run(enhanced);
                candidates = MergeCandidates(
                    candidates,
                    ToCandidates(colourResult, source.Width, source.Height));

                _logger.LogDebug(
                    "OCR colour recovery dipakai. Region={RegionCount}, confidence asli={Confidence:F3}.",
                    candidates.Count,
                    averageConfidence);
            }

            var regions = candidates
                .Select(candidate => new OcrTextRegion(
                    candidate.Text,
                    candidate.Confidence,
                    candidate.X,
                    candidate.Y,
                    candidate.Width,
                    candidate.Height))
                .ToList();

            return OcrLayoutBuilder.Build(regions);
        }
        finally
        {
            _ocrLock.Release();
        }
    }

    /// <summary>
    /// Converts every colour channel to a dark-on-light representation. Taking
    /// the minimum B/G/R value keeps black text dark and also turns red, blue,
    /// or green text dark instead of washing it out during normal grayscale
    /// conversion. CLAHE then improves low-contrast scans locally.
    /// </summary>
    private static Mat CreateColorRobustImage(Mat source)
    {
        using Mat bgr = EnsureBgr(source);
        Mat[] channels = Cv2.Split(bgr);
        try
        {
            using var firstMin = new Mat();
            using var darkest = new Mat();
            Cv2.Min(channels[0], channels[1], firstMin);
            Cv2.Min(firstMin, channels[2], darkest);

            using var enhanced = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.5, tileGridSize: new Size(8, 8));
            clahe.Apply(darkest, enhanced);

            var result = new Mat();
            Cv2.CvtColor(enhanced, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private static bool HasChromaticInk(Mat source)
    {
        using Mat bgr = EnsureBgr(source);
        Mat[] channels = Cv2.Split(bgr);
        try
        {
            using var minimum = new Mat();
            using var maximum = new Mat();
            using var firstMinimum = new Mat();
            using var firstMaximum = new Mat();
            using var spread = new Mat();
            using var colourMask = new Mat();
            using var inkMask = new Mat();
            using var chromaticInk = new Mat();

            Cv2.Min(channels[0], channels[1], firstMinimum);
            Cv2.Min(firstMinimum, channels[2], minimum);
            Cv2.Max(channels[0], channels[1], firstMaximum);
            Cv2.Max(firstMaximum, channels[2], maximum);
            Cv2.Subtract(maximum, minimum, spread);
            Cv2.Threshold(spread, colourMask, 30, 255, ThresholdTypes.Binary);
            Cv2.Threshold(minimum, inkMask, 225, 255, ThresholdTypes.BinaryInv);
            Cv2.BitwiseAnd(colourMask, inkMask, chromaticInk);

            double ratio = Cv2.CountNonZero(chromaticInk) /
                           (double)Math.Max(1L, source.Rows * (long)source.Cols);
            return ratio >= 0.0002;
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private static Mat EnsureBgr(Mat source)
    {
        if (source.Channels() == 3)
        {
            return source.Clone();
        }

        var result = new Mat();
        Cv2.CvtColor(
            source,
            result,
            source.Channels() == 4
                ? ColorConversionCodes.BGRA2BGR
                : ColorConversionCodes.GRAY2BGR);
        return result;
    }

    private static List<RegionCandidate> ToCandidates(
        PaddleOcrResult result,
        int imageWidth,
        int imageHeight)
    {
        double safeWidth = Math.Max(1, imageWidth);
        double safeHeight = Math.Max(1, imageHeight);

        return result.Regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .Select(region =>
            {
                Point2f[] corners = region.Rect.Points();
                double left = corners.Min(point => point.X);
                double right = corners.Max(point => point.X);
                double top = corners.Min(point => point.Y);
                double bottom = corners.Max(point => point.Y);

                return new RegionCandidate(
                    region.Text.Trim(),
                    double.IsFinite(region.Score) ? Math.Clamp(region.Score, 0, 1) : 0,
                    Math.Clamp(((left + right) / 2.0) / safeWidth, 0, 1),
                    Math.Clamp(((top + bottom) / 2.0) / safeHeight, 0, 1),
                    Math.Clamp((right - left) / safeWidth, 0, 1),
                    Math.Clamp((bottom - top) / safeHeight, 0, 1));
            })
            .ToList();
    }

    private static List<RegionCandidate> MergeCandidates(
        List<RegionCandidate> primary,
        List<RegionCandidate> fallback)
    {
        var merged = new List<RegionCandidate>(primary);

        foreach (RegionCandidate candidate in fallback)
        {
            int duplicateIndex = merged.FindIndex(existing => SameVisualRegion(existing, candidate));
            if (duplicateIndex < 0)
            {
                merged.Add(candidate);
                continue;
            }

            RegionCandidate existing = merged[duplicateIndex];
            if (candidate.Confidence >= existing.Confidence + 0.08 ||
                (existing.Confidence < 0.7 && candidate.Confidence >= 0.85))
            {
                merged[duplicateIndex] = candidate;
            }
        }

        return merged;
    }

    private static bool SameVisualRegion(RegionCandidate first, RegionCandidate second)
    {
        double xTolerance = Math.Max(0.008, Math.Max(first.Width, second.Width) * 0.35);
        double yTolerance = Math.Max(0.005, Math.Max(first.Height, second.Height) * 0.55);
        return Math.Abs(first.X - second.X) <= xTolerance &&
               Math.Abs(first.Y - second.Y) <= yTolerance;
    }

    private static Mat SkBitmapToMat(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Color);
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _ocrLock.Dispose();
    }

    private sealed record RegionCandidate(
        string Text,
        double Confidence,
        double X,
        double Y,
        double Width,
        double Height);
}
