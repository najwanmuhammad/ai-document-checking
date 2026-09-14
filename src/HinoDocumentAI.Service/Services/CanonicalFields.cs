namespace HinoDocumentAI.Service.Services;

/// Jenis dokumen yang didukung. Dipakai untuk klasifikasi halaman (FR-8)
/// sebelum ekstraksi field, karena satu PDF bisa berisi lebih dari satu
/// jenis dokumen atau halaman tidak relevan (PRD 13.2).
public enum DocumentType
{
    Unknown,
    Invoice,
    DeliveryNote,
    TaxInvoice
}

/// Daftar field baku (canonical fields) per jenis dokumen, dipisah
/// header (satu nilai per dokumen) vs line-item (bisa berulang per baris barang) - PRD 13.1.
public static class CanonicalFields
{
    public static readonly string[] InvoiceHeader =
        [
            "supplier_name",
            "invoice_number",
            "invoice_date",
            "sub_total_amount",
            "taxable_base",
            "tax_amount", 
            "total_amount", 
            "signer_name",
            "signer_position"
        ];

    public static readonly string[] DeliveryNoteHeader =
        [
            "supplier_name",
            "delivery_note_number", 
            "delivery_note_date"
        ];

    public static readonly string[] TaxInvoiceHeader =
        [
            "supplier_name",
            "tax_invoice_number",
            "tax_invoice_date",
            "sub_total_amount",
            "discount",
            "down_payment",
            "taxable_base",
            "tax_amount",
            "luxury_goods_sales_tax",
            "signer_name"
        ];

    // invoice lengkap terpakai semua.
    // dn tidak ada price dan amount.
    // faktur pajak ada semua tetapi jadi satu di kolom nama barang kena pajak/jasa kena pajak,
    // amount-nya ada di kolom terpisah yaitu Harga jual/penggantian/uang muka/termin (Rp)
    public static readonly string[] LineItem =
        [
            "part_number", 
            "part_name", 
            "quantity", 
            "price", 
            "amount"
        ];

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
            "delivery slip", "packing slip", "delivery order", "surat jalan"
        ],
        [DocumentType.TaxInvoice] =
        [
            "faktur pajak"
        ],
    };

    /// <summary>
    /// Dictionary sinonim untuk label kolom mentah -> field baku, dicek
    /// LEBIH DULU sebelum fallback ke AI LLM. Mengurangi panggilan Ollama untuk kasus yang sudah
    /// pasti/sering muncul.
    ///
    /// Semua entri di bawah adalah temuan NYATA dari sample data — lihat PRD Bagian 13.2:
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
        ["part no."] = "part_number",
        // NOTE: "exd no" sengaja TIDAK dimasukkan — itu kode internal
        // supplier Exedy, bukan part_number versi Hino. Lihat catatan di atas.

        // Nama part
        ["description"] = "part_name",
        ["nama barang"] = "part_name",
        ["cust item name"] = "part_name",
        ["barang / description"] = "part_name",
        ["nama barang dan penjelasan"] = "part_name",

        // Quantity
        ["qty"] = "quantity",
        ["jumlah"] = "quantity",
        ["jumlah / qty"] = "quantity", // Dari JSON Anda
        ["total qty"] = "quantity",

        // Price
        ["price"] = "price",
        ["harga satuan"] = "price",
        ["price (idr)"] = "price",
        ["amount"] = "amount",
        ["jumlah harga jual"] = "sub_total_amount",
        ["harga jual"] = "sub_total_amount",

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
        ["ppn"] = "tax_amount",
        ["value added"] = "tax_amount",
        ["total harga"] = "total_amount",
        ["total invoice"] = "total_amount",
        ["ditandatangani oleh"] = "signer_name",
        ["nama penanda tangan"] = "signer_name",
        ["jabatan"] = "signer_position",
    };

    /// <summary>
    /// Token hasil OCR yang tidak punya makna sendiri sebagai data (simbol
    /// mata uang lepas, tanda baca lepas, dsb.) — dibuang sebelum masuk proses
    /// cleaning, supaya tidak buang panggilan Ollama untuk hal yang jelas tidak
    /// berguna. Ditemukan nyata di sample data: "Rp." sering terdeteksi sebagai
    /// region terpisah dari angkanya sendiri.
    /// </summary>
    public static readonly HashSet<string> NoiseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "rp", "rp.", "idr", "usd", "jpy", "-", "/", ":", ".", ","
    };
}
