using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public interface IOcrService
{
    Task<(string RawText, List<ExtractedField> Fields)> ExtractAsync(string imagePath);
}

/// <summary>
/// OCR ekstraksi via PaddleSharp (Sdcb.PaddleOCR) — binding langsung ke
/// Paddle Inference native engine, model & hasil setara dengan PaddleOCR
/// versi Python (lihat PRD Bagian 12).
///
/// PENTING — bug yang dikonfirmasi di GitHub PaddleSharp (PR #188):
/// kombinasi model V5 dengan OpenCvSharp4 versi 4.13.0.20260602 (dan
/// kemungkinan versi 4.13.x terdekat) menyebabkan akurasi OCR anjlok
/// drastis TANPA error/crash — murni karena bug rotated-rect di versi
/// OpenCvSharp tersebut. Pin OpenCvSharp4 & OpenCvSharp4.runtime.win ke
/// versi 4.11.0.20250507 (dikonfirmasi baik di laporan bug tsb) sampai
/// ada versi 4.13.x yang terkonfirmasi memuat perbaikannya.
/// SELALU validasi hasil OCR ke sample nyata sebelum lanjut ke tahap
/// berikutnya — bug ini tidak akan terlihat dari compile/run saja.
/// </summary>
public class OcrService : IOcrService, IDisposable
{
    private readonly PaddleOcrAll _ocr;

    public OcrService(ILogger<OcrService> logger)
    {
        // EnglishV5 dipakai untuk Bahasa Indonesia + Inggris (keduanya
        // alfabet Latin standar, tidak perlu model "MultiLanguage" yang
        // ternyata tidak ada di versi package ini).
        FullOcrModel model = OnlineFullModels.EnglishV5.DownloadAsync().Result;

        // PaddleDevice.Mkldnn() = mode CPU dioptimasi untuk Intel (cocok
        // untuk spek server: Xeon Gold 5118, tanpa GPU — lihat PRD Bagian 16).
        _ocr = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = true,
            Enable180Classification = true,
        };
    }

    public Task<(string RawText, List<ExtractedField> Fields)> ExtractAsync(string imagePath)
    {
        using Mat src = Cv2.ImRead(imagePath);
        // TODO: tambahkan preprocessing OpenCvSharp4 di sini kalau kualitas
        // scan kurang baik (denoise, deskew, binarization) — lihat FATURA
        // dataset finding di PRD Bagian 5: kualitas OCR sangat menentukan
        // akurasi tahap berikutnya.

        PaddleOcrResult result = _ocr.Run(src);

        // TODO: hasil PaddleOcrResult punya info bounding box per region
        // (result.Regions), yang bisa dipakai untuk analisis layout
        // (mana yang "keyword" vs "data" berdasarkan posisi relatif) —
        // sinyal tipe data + posisi ini yang terbukti penting di ablation
        // study OCRMiner (PRD Bagian 5 & 15). Untuk versi awal, field masih
        // diperlakukan flat per baris teks; penyempurnaan posisi menyusul.
        var fields = result.Regions
            .Select(r => new ExtractedField(Label: "", Value: r.Text, Confidence: r.Score))
            .ToList();

        return Task.FromResult((result.Text, fields));
    }

    public void Dispose() => _ocr.Dispose();
}
