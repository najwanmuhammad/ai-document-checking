namespace HinoDocumentAI.Service.Services;

public sealed record DocumentCleaningProfile(
    string DisplayName,
    string Rules,
    IReadOnlySet<string> CriticalHeaderFields,
    IReadOnlySet<string> CriticalLineItemFields);

/// <summary>
/// Supplier-independent extraction rules. Profiles describe the business
/// meaning of fields; they deliberately do not encode fixed pixel positions
/// or one supplier's template.
/// </summary>
public static class DocumentCleaningProfiles
{
    public static DocumentCleaningProfile Get(DocumentType documentType) => documentType switch
    {
        DocumentType.Invoice => Invoice,
        DocumentType.DeliveryNote => DeliveryNote,
        DocumentType.TaxInvoice => TaxInvoice,
        _ => throw new ArgumentOutOfRangeException(nameof(documentType))
    };

    private static readonly DocumentCleaningProfile Invoice = new(
        "invoice/faktur penjualan",
        """
        - supplier_name adalah pihak yang MENERBITKAN invoice, bukan buyer/ship-to Hino.
        - invoice_number hanya nomor dokumen invoice; jangan gunakan nomor PO, NPWP, delivery note, atau rekening.
        - invoice_date adalah tanggal penerbitan invoice. Tanggal dapat berada dekat nomor invoice atau tanda tangan.
        - Satu line_item harus berasal dari satu baris barang. Jangan masukkan subtotal, pajak, atau grand total sebagai barang.
        - Jika part number dan part name tergabung seperti "04905-37220 - CUP KIT", pisahkan menjadi dua field.
        - Jika ada kode internal supplier dan kode customer/Hino (mis. EXD NO. dan CUST ITEM NO.), gunakan kode customer/Hino sebagai part_number.
        - price adalah harga satu unit, amount adalah total baris, quantity adalah jumlah unit.
        - sub_total_amount adalah jumlah sebelum pajak, taxable_base adalah DPP, tax_amount adalah PPN, total_amount adalah nilai akhir invoice.
        - signer_name dan signer_position hanya diisi jika benar-benar tercetak pada dokumen.
        """,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "supplier_name", "invoice_number", "invoice_date", "total_amount"
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part_number", "part_name", "quantity", "price", "amount"
        });

    private static readonly DocumentCleaningProfile DeliveryNote = new(
        "surat jalan/delivery note/packing slip",
        """
        - supplier_name adalah pihak yang MENGIRIM barang, bukan penerima/consignee Hino.
        - delivery_note_number adalah nomor surat jalan/delivery note/packing slip; jangan gunakan nomor invoice atau PO.
        - delivery_note_date adalah tanggal pengiriman atau tanggal dokumen.
        - Satu line_item harus berasal dari satu baris barang yang dikirim.
        - Nomor part dapat berlabel Part No, PLU, Item No, Model/Type, atau Cust Item No.
        - Jika part number dan part name tergabung seperti "04905-37220 - CUP KIT", pisahkan menjadi dua field.
        - quantity adalah jumlah barang yang dikirim, bukan nomor urut baris atau jumlah kemasan bila keduanya berbeda.
        - Dokumen jenis ini tidak mempunyai price dan amount; keduanya harus null.
        """,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "supplier_name", "delivery_note_number", "delivery_note_date"
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part_number", "part_name", "quantity"
        });

    private static readonly DocumentCleaningProfile TaxInvoice = new(
        "faktur pajak/e-Faktur DJP",
        """
        - supplier_name adalah Nama pada bagian PENGUSAHA KENA PAJAK, bukan Nama pembeli/penerima jasa.
        - tax_invoice_number HANYA angka setelah label "Kode dan Nomor Seri Faktur Pajak". Jangan gunakan NPWP, NIK, nomor QR, referensi, atau deretan angka setelah tanda #.
        - tax_invoice_date adalah tanggal kota+tanggal tepat di atas/dekat penanda tangan.
        - Kolom "Kode Barang/Jasa" (sering 000000) adalah kode pajak dan BUKAN part_number atau part_name.
        - Pada deskripsi item, pola "Rp PRICE x QUANTITY" berarti price dan quantity. amount adalah total baris pada kolom nilai.
        - part_number dan part_name berada dalam kolom Nama Barang Kena Pajak/Jasa Kena Pajak. Jangan mengarang nama barang bila OCR hanya berisi nomor part.
        - "Harga Jual / Penggantian / Uang Muka / Termin" pada bagian ringkasan adalah sub_total_amount, bukan amount line item.
        - discount, down_payment, taxable_base, tax_amount, dan luxury_goods_sales_tax harus diambil dari baris ringkasan masing-masing.
        - signer_name adalah nama orang di bawah area tanda tangan elektronik, bukan nama pembeli atau supplier.
        """,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "supplier_name", "tax_invoice_number", "tax_invoice_date", "taxable_base", "tax_amount"
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "part_number", "part_name", "quantity", "price", "amount"
        });
}
