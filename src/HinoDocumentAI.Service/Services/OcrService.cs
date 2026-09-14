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
    /// <summary>Ekstrak teks mentah per halaman. Tidak lagi mencoba
    /// memasangkan label/value di sini — itu sekarang tugas LLM di
    /// CleaningService, karena posisi/heuristik terbukti tidak generalize
    /// ke variasi layout supplier yang sangat beragam.</summary>
    Task<List<(int PageNumber, string RawText)>> ExtractPagesAsync(string filePath);
}

public class OcrService : IOcrService, IDisposable
{
    private readonly PaddleOcrAll _ocr;

    public OcrService(ILogger<OcrService> logger)
    {
        FullOcrModel model = LocalFullModels.LatinV5;
        _ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };
    }

    public async Task<List<(int PageNumber, string RawText)>> ExtractPagesAsync(string filePath)
    {
        bool isPdf = string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        var pages = new List<(int, string)>();

        if (isPdf)
        {
            using var pdfStream = File.OpenRead(filePath);
            int pageNumber = 0;
            await foreach (SKBitmap page in Conversion.ToImagesAsync(pdfStream))
            {
                pageNumber++;
                using (page)
                using (Mat mat = SkBitmapToMat(page))
                {
                    PaddleOcrResult result = _ocr.Run(mat);
                    pages.Add((pageNumber, result.Text ?? string.Empty));
                }
            }
        }
        else
        {
            using Mat mat = Cv2.ImRead(filePath);
            PaddleOcrResult result = _ocr.Run(mat);
            pages.Add((1, result.Text ?? string.Empty));
        }

        return pages;
    }

    private static Mat SkBitmapToMat(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Color);
    }

    public void Dispose() => _ocr.Dispose();
}