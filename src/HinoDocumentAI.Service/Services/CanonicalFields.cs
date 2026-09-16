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
    // delivery note tidak ada price dan amount.
    // faktur pajak ada semua tetapi jadi satu di kolom "nama barang kena pajak/jasa kena pajak",
    // amount-nya ada di kolom terpisah yaitu "Harga jual/penggantian/uang muka/termin (Rp)".
    public static readonly string[] LineItem =
        [
            "part_number", 
            "part_name", 
            "quantity", 
            "price", 
            "amount"
        ];

    /// Kata kunci untuk klasifikasi jenis dokumen per halaman (FR-8).
    /// Diisi dari istilah yang benar-benar ditemukan di sample data
    /// (misal delivery note punya banyak sebutan berbeda tapi artinya sama).
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

    /// Dictionary sinonim untuk label kolom mentah -> field baku, dicek LEBIH DULU sebelum fallback ke AI LLM.
    /// Mengurangi panggilan Ollama untuk kasus yang sudah pasti/sering muncul.
    /// - "Plu" (Rukun Sejahtera) = kode part, sementara Akebono/Exedy
    ///   menggabungkannya ke kolom "Model/Type"
    /// - Exedy punya DUA kolom kode part: "EXD NO." (kode internal supplier,
    ///   TIDAK dipetakan ke part_number) dan "CUST ITEM NO." (kode Hino, yang dipetakan ke part_number).
    public static readonly Dictionary<string, string> KnownSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // Nomor part
        ["part number"] = "part_number",
        ["plu"] = "part_number",
        ["cust item no"] = "part_number",
        ["cust. item no."] = "part_number",
        ["cust. item code"] = "part_number",
        ["part no"] = "part_number",
        ["part no."] = "part_number",

        // Nama part
        ["part name"] = "part_name",
        ["description"] = "part_name",
        ["nama barang"] = "part_name",
        ["barang"] = "part_name",
        ["cust item name"] = "part_name",
        ["barang / description"] = "part_name",
        ["nama barang dan penjelasan"] = "part_name",
        ["nama barang kena pajak/jasa kena pajak"] = "part_name",

        // Quantity
        ["quantity"] = "quantity",
        ["qty"] = "quantity",
        ["jumlah"] = "quantity",
        ["jumlah / qty"] = "quantity",
        ["total qty"] = "quantity",
        ["Jumlah/Qty (Unit/Set)"] = "quantity",

        // Price
        ["price"] = "price",
        ["harga satuan"] = "price",
        ["price (idr)"] = "price",
        ["unit price"] = "price",

        // amount
        ["amount"] = "amount",
        ["jumlah harga"] = "amount",
        ["Jumlah Harga / Amount"] = "amount",
        ["Harga Jual / Penggantian / Uang Muka / Termin (Rp)"] = "amount",

        // sub total amount
        ["sub total amount"] = "sub_total_amount",
        ["jumlah harga jual"] = "sub_total_amount",
        ["subtotal"] = "sub_total_amount",
        ["total"] = "sub_total_amount",
        ["Harga Jual / Penggantian / Uang Muka / Termin"] = "sub_total_amount",

        //dasar pengenaan pajak
        ["taxable base"] = "taxable_base",
        ["taxable amount"] = "taxable_base",
        ["dasar pengenaan pajak"] = "taxable_base",
        ["dasar pengenaan pajak/taxable amount"] = "taxable_base",
        ["dpp"] = "taxable_base",
        ["DPP Lain-lain"] = "taxable_base",

        ///ppn
        ["pajak pertambahan nilai"] = "tax_amount",
        ["Pajak Pertambahan Nilai(PPN)/Value Added"] = "tax_amount",
        ["tax"] = "tax_amount",
        ["vat = 12% x taxable amount"] = "tax_amount",
        ["jumlah ppn (pajak pertambahan nilai)"] = "tax_amount",
        ["v.a.t"] = "tax_amount",
        ["vat"] = "tax_amount",
        ["ppn"] = "tax_amount",
        ["value added"] = "tax_amount",
        ["value added tax"] = "tax_amount",

        ///grand total
        ["total amount"] = "total_amount",
        ["total harga"] = "total_amount",
        ["total invoice"] = "total_amount",
        ["grand total"] = "total_amount",

        //signature
        ["authorized signatory"] = "signer_name",
        ["ditandatangani oleh"] = "signer_name",
        ["nama penanda tangan"] = "signer_name",
        ["jabatan"] = "signer_position",

        // nama supplier
        ["supplier name"] = "supplier_name",
        ["vendor"] = "supplier_name",
        ["nama supplier"] = "supplier_name",
        ["Pengusaha Kena Pajak: Nama :"] = "supplier_name",

        //nomor surat invocie
        ["Faktur Penjualan / Invoice No."] = "invoice_number",
        ["SALES INVOICE No"] = "invoice_number",
        ["Invoice No."] = "invoice_number",
        ["Invoice No"] = "invoice_number",

        //nomor surat delivery note
        ["No."] = "delivery_note_number",
        ["No"] = "delivery_note_number",
        ["Number"] = "delivery_note_number",
        ["DO No."] = "delivery_note_number",

        //nomor surat faktur pajak
        ["Kode dan Nomor Seri Faktur Pajak"] = "tax_invoice_number",

        //date invoice
        ["Date"] = "invoice_date",

        //date delivery note
        ["Tgl/Date"] = "delivery_note_date",
        ["DO Date"] = "delivery_note_date",
        ["Date"] = "delivery_note_date",

        //date faktur pajak
        //ada di atas signature

    };

    /// Token hasil OCR yang tidak punya makna sendiri sebagai data (simbol
    /// mata uang lepas, tanda baca lepas, dsb.) — dibuang sebelum masuk proses
    /// cleaning, supaya tidak buang panggilan Ollama untuk hal yang jelas tidak
    /// berguna. Ditemukan nyata di sample data: "Rp." sering terdeteksi sebagai
    /// region terpisah dari angkanya sendiri.
    public static readonly HashSet<string> NoiseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "rp", "rp.", "idr", "usd", "jpy", "-", "/", ":", ".", ","
    };
}
