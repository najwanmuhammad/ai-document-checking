using OpenCvSharp;
using PDFtoImage;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using SkiaSharp;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface IOcrService
{
    Task<(string RawText, List<ExtractedField> Fields)> ExtractAsync(string filePath);
}

/// <summary>
/// OCR ekstraksi via PaddleSharp (Sdcb.PaddleOCR) — binding langsung ke
/// Paddle Inference native engine, model & hasil setara dengan PaddleOCR
/// versi Python (lihat PRD Bagian 12).
///
/// CATATAN PENTING (revisi setelah testing nyata):
/// 1. Model LOCAL (bukan Online) dipakai — tidak butuh akses internet saat
///    runtime, sejalan dengan prinsip self-hosted. Butuh NuGet:
///    Sdcb.PaddleOCR.Models.Local (bukan .Models.Online).
/// 2. Model "Latin" (bukan "English") dipakai — PaddleOCR secara resmi
///    mengelompokkan Bahasa Indonesia di bawah grup Latin (bersama Melayu,
///    Turki, dan bahasa Eropa Latin lainnya), bukan di grup English.
///    Tetap membaca teks Inggris dengan baik karena sama-sama alfabet Latin.
/// 3. Bug terkonfirmasi di GitHub PaddleSharp (PR #188): kombinasi model V5
///    dengan OpenCvSharp4 versi 4.13.0.20260602 (dan versi 4.13.x terdekat)
///    merusak akurasi OCR TANPA error/crash. Pastikan OpenCvSharp4 &
///    OpenCvSharp4.runtime.win di-pin ke versi 4.11.0.20250507 (lihat README).
/// 4. Input dari Invoice Portal berupa PDF (bukan gambar) — Cv2.ImRead TIDAK
///    bisa membaca PDF sama sekali (itu murni fungsi baca gambar). PDF perlu
///    di-render dulu jadi gambar per halaman via PDFtoImage (berbasis
///    PDFium), baru tiap halaman di-OCR.
/// </summary>
public class OcrService : IOcrService, IDisposable
{
    private readonly PaddleOcrAll _ocr;

    public OcrService(ILogger<OcrService> logger)
    {
        FullOcrModel model = LocalFullModels.LatinV5;

        // PaddleDevice.Mkldnn() = mode CPU dioptimasi untuk Intel (cocok
        // untuk spek server: Xeon Gold 5118, tanpa GPU — lihat PRD Bagian 16).
        _ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = false,
        };
    }

    public async Task<(string RawText, List<ExtractedField> Fields)> ExtractAsync(string filePath)
    {
        bool isPdf = string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

        var allText = new List<string>();
        var allFields = new List<ExtractedField>();

        if (isPdf)
        {
            // TODO (FR-8): satu file PDF bisa berisi lebih dari satu jenis
            // dokumen atau halaman tidak relevan (lihat PRD Bagian 13.2 poin
            // 2 & 5, temuan nyata dari sample data Exedy). Untuk versi awal
            // ini, semua halaman digabung jadi satu; klasifikasi per halaman
            // untuk memisahkan/membuang halaman tidak relevan menyusul.
            //
            // Catatan: PDFium (native engine di balik PDFtoImage) tidak
            // thread-safe, semua pemanggilannya di-lock secara internal oleh
            // library ini — jadi beberapa request PDF yang datang bersamaan
            // akan diproses bergiliran (serial), bukan crash. Untuk volume
            // dokumen yang moderat ini bukan masalah, tapi perlu diingat
            // kalau nanti mengukur throughput di bawah beban tinggi.
            using var pdfStream = File.OpenRead(filePath);
            await foreach (SKBitmap page in Conversion.ToImagesAsync(pdfStream))
            {
                using (page)
                using (Mat mat = SkBitmapToMat(page))
                {
                    var (text, fields) = RunOcr(mat);
                    allText.Add(text);
                    allFields.AddRange(fields);
                }
            }
        }
        else
        {
            using Mat mat = Cv2.ImRead(filePath);
            var (text, fields) = RunOcr(mat);
            allText.Add(text);
            allFields.AddRange(fields);
        }

        return (string.Join("\n", allText), allFields);
    }

    private (string Text, List<ExtractedField> Fields) RunOcr(Mat mat)
    {
        // TODO: tambahkan preprocessing OpenCvSharp4 di sini kalau kualitas
        // scan kurang baik (denoise, deskew, binarization) — lihat FATURA
        // dataset finding di PRD Bagian 5: kualitas OCR sangat menentukan
        // akurasi tahap berikutnya.
        PaddleOcrResult result = _ocr.Run(mat);

        // TODO: hasil PaddleOcrResult punya info bounding box per region
        // (result.Regions), yang bisa dipakai untuk analisis layout
        // (mana yang "keyword" vs "data" berdasarkan posisi relatif) —
        // sinyal tipe data + posisi ini yang terbukti penting di ablation
        // study OCRMiner (PRD Bagian 5 & 15). Untuk versi awal, field masih
        // diperlakukan flat per baris teks; penyempurnaan posisi menyusul.
        //var fields = result.Regions
        //    .Select(r => new ExtractedField(Label: "", Value: r.Text, Confidence: r.Score))
        //    .ToList();

        //return (result.Text, fields);

        var fields = result.Regions
            .Select(r =>
            {
                // 1. Ambil nilai score asli
                double safeScore = r.Score;

                // 2. Sanitasi: Jika NaN atau Infinity, ubah menjadi 0.0 agar valid untuk JSON
                if (double.IsNaN(safeScore) || double.IsInfinity(safeScore))
                {
                    safeScore = 0.0;
                }

                // 3. Pastikan Value tidak null untuk keamanan tambahan
                string safeText = r.Text ?? string.Empty;

                return new ExtractedField(Label: "", Value: safeText, Confidence: safeScore);
            })
            .ToList();
        // Pastikan Text utama juga tidak null
        return (result.Text ?? string.Empty, fields);
    }

    private static Mat SkBitmapToMat(SKBitmap bitmap)
    {
        // Jalur konversi aman: SKBitmap -> PNG bytes -> Mat, menghindari
        // asumsi format piksel native yang bisa beda antara SkiaSharp dan
        // OpenCvSharp kalau di-convert langsung dari memori.
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Color);
    }

    public void Dispose() => _ocr.Dispose();
}
