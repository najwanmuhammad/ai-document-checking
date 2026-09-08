namespace HinoDocumentAI.Service.Services;

/// <summary>
/// Jenis dokumen yang didukung. Dipakai untuk klasifikasi halaman (FR-8)
/// sebelum ekstraksi field, karena satu PDF bisa berisi lebih dari satu
/// jenis dokumen atau halaman tidak relevan (lihat PRD Bagian 13.2).
/// </summary>
public enum DocumentType
{
    Unknown,
    Invoice,
    DeliveryNote,
    TaxInvoice
}

/// <summary>
/// Daftar field baku (canonical fields) per jenis dokumen, dipisah
/// header (satu nilai per dokumen) vs line-item (bisa berulang per baris
/// barang) — lihat PRD Bagian 13.1.
/// </summary>
public static class CanonicalFields
{
    public static readonly string[] InvoiceHeader =
        ["invoice_number", "invoice_date", "supplier_name"];

    public static readonly string[] DeliveryNoteHeader =
        ["delivery_note_number", "delivery_date", "supplier_name"];

    public static readonly string[] TaxInvoiceHeader =
        ["tax_invoice_number", "tax_invoice_date", "supplier_name", "tax_amount"];

    // Sama untuk ketiga jenis dokumen (quantity opsional untuk faktur pajak)
    public static readonly string[] LineItem =
        ["part_number", "part_name", "quantity", "price"];

    /// <summary>
    /// Kata kunci untuk klasifikasi jenis dokumen per halaman (FR-8).
    /// Diisi dari istilah yang benar-benar ditemukan di sample data —
    /// perhatikan variasi istilah antar-supplier untuk konsep yang sama
    /// (mis. delivery note punya banyak sebutan berbeda).
    /// TODO: tambah terus seiring makin banyak sample supplier yang masuk.
    /// </summary>
    public static readonly Dictionary<DocumentType, string[]> DocumentTypeKeywords = new()
    {
        [DocumentType.Invoice] =
        [
            "invoice", "faktur penjualan", "sales invoice"
        ],
        [DocumentType.DeliveryNote] =
        [
            "delivery slip", "packing slip", "delivery order", "shipping note",
            "surat jalan", "good received report" // GR biasanya dari sisi penerima, tapi sering menyertai proses delivery
        ],
        [DocumentType.TaxInvoice] =
        [
            "faktur pajak", "kode dan nomor seri faktur pajak"
        ],
    };

    /// <summary>
    /// Dictionary sinonim untuk label kolom mentah -> field baku, dicek
    /// LEBIH DULU sebelum fallback ke embedding similarity (bge-m3) atau
    /// LLM (qwen2.5). Mengurangi panggilan Ollama untuk kasus yang sudah
    /// pasti/sering muncul.
    ///
    /// Semua entri di bawah adalah temuan NYATA dari sample data (bukan
    /// contoh hipotetis) — lihat PRD Bagian 13.2:
    /// - "Plu" (Rukun Sejahtera) = kode part, sementara Akebono/Exedy
    ///   menggabungkannya ke deskripsi atau kolom "Model/Type" / "EXD NO."
    /// - Exedy punya DUA kolom kode part: "EXD NO." (kode internal supplier,
    ///   JANGAN dipetakan ke part_number) dan "CUST ITEM NO." (kode Hino,
    ///   INI yang dipetakan ke part_number).
    /// TODO: lengkapi terus seiring bertambahnya sample data.
    /// </summary>
    public static readonly Dictionary<string, string> KnownSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // Nomor part
        ["plu"] = "part_number",
        ["cust item no"] = "part_number",
        ["cust item code"] = "part_number",
        ["part no"] = "part_number",
        // NOTE: "exd no" sengaja TIDAK dimasukkan — itu kode internal
        // supplier Exedy, bukan part_number versi Hino. Lihat catatan di atas.

        // Nama part
        ["description"] = "part_name",
        ["nama barang"] = "part_name",
        ["cust item name"] = "part_name",
        ["barang / description"] = "part_name",

        // Nomor dokumen (akan dipetakan ke header field yang sesuai
        // berdasarkan DocumentType hasil klasifikasi, bukan statis di sini)
        ["no"] = "document_number",
        ["no."] = "document_number",
        ["number"] = "document_number",

        // Supplier
        ["supplier name"] = "supplier_name",
        ["nama"] = "supplier_name", // hati-hati: label "Nama" generik, perlu context tambahan (lihat TODO di CleaningService)

        // Pajak
        ["jumlah ppn (pajak pertambahan nilai)"] = "tax_amount",
        ["v.a.t"] = "tax_amount",
        ["vat"] = "tax_amount",
    };
}
