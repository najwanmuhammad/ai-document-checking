using System.Text.RegularExpressions;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

/// <summary>
/// Deterministic domain guardrails around the LLM output. These rules only
/// recover values explicitly present in OCR and focus on stable business
/// semantics, not supplier-specific pixel layouts.
/// </summary>
public static partial class DocumentPostProcessor
{
    public static List<CleanedLineItem> Process(
        DocumentType documentType,
        string rawText,
        Dictionary<string, string?> header,
        List<CleanedLineItem> lineItems,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        var processedItems = lineItems.Select(SplitCompositeField).ToList();

        if (documentType == DocumentType.TaxInvoice)
        {
            List<CleanedLineItem> recoveredTaxItems = RecoverTaxInvoiceItems(rawText);
            RecoverTaxInvoiceHeader(rawText, header, warnings, evidence);
            RecoverTaxInvoiceSummary(
                rawText,
                recoveredTaxItems,
                header,
                warnings,
                evidence);
            processedItems = MergeTaxInvoiceItems(
                processedItems,
                recoveredTaxItems,
                warnings,
                evidence);

            processedItems = processedItems
                .Where(item => !IsTaxGoodsCode(item.PartNumber))
                .Select(item => item with
                {
                    PartName = IsTaxGoodsCode(item.PartName) || IsNumeric(item.PartName)
                        ? null
                        : item.PartName
                })
                .Where(IsMathematicallyConsistent)
                .ToList();
        }
        else if (documentType == DocumentType.DeliveryNote)
        {
            processedItems = MergeGenericRows(
                processedItems,
                RecoverDeliveryRows(rawText),
                "delivery_note_row",
                warnings,
                evidence);
            processedItems = processedItems
                .Select(item => item with { Price = null, Amount = null })
                .ToList();
        }
        else if (documentType == DocumentType.Invoice)
        {
            RecoverInvoiceHeader(rawText, header, warnings, evidence);
            processedItems = MergeGenericRows(
                processedItems,
                RecoverInvoiceRows(rawText, warnings, evidence),
                "invoice_row_arithmetic",
                warnings,
                evidence);
            for (int index = 0; index < processedItems.Count; index++)
            {
                processedItems[index] = RepairInvoiceLineItem(
                    rawText,
                    processedItems[index],
                    index,
                    warnings,
                    evidence);
            }

            ValidateInvoiceTotals(header, processedItems, warnings, evidence);
        }

        List<CleanedLineItem> finalItems = processedItems
            .Where(HasAnyValue)
            .DistinctBy(item => string.Join('|',
                item.PartNumber?.ToUpperInvariant(),
                item.PartName?.ToUpperInvariant(),
                item.Quantity,
                item.Price,
                item.Amount))
            .ToList();

        if (documentType == DocumentType.TaxInvoice)
        {
            evidence.RemoveAll(item => item.LineItemIndex.HasValue);
            for (int index = 0; index < finalItems.Count; index++)
            {
                AddFinalTaxItemEvidence(evidence, finalItems[index], index);
            }
        }

        return finalItems;
    }

    private static void RecoverInvoiceHeader(
        string rawText,
        Dictionary<string, string?> header,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        Match invoiceNumber = InvoiceNumberLabelRegex().Match(rawText);
        if (invoiceNumber.Success)
        {
            SetHeaderFromRule(
                header,
                evidence,
                "invoice_number",
                invoiceNumber.Groups["value"].Value.Trim(),
                invoiceNumber.Value,
                "invoice_number_label_rule");
        }

        string? supplier = FindInvoiceSupplier(rawText);
        if (supplier is not null)
        {
            SetHeaderFromRule(
                header,
                evidence,
                "supplier_name",
                supplier,
                supplier,
                "invoice_supplier_header_rule");
        }

        Match? issueDate = InvoiceDateLabelRegex().Match(rawText) is { Success: true } labelled
            ? labelled
            : null;

        if (issueDate is null)
        {
            MatchCollection printedDates = PrintedDateRegex().Matches(rawText);
            issueDate = printedDates
                .Cast<Match>()
                .Reverse()
                .FirstOrDefault(match => !IsDueDateContext(rawText, match.Index));
        }

        if (issueDate is not null)
        {
            string value = issueDate.Groups["value"].Value.Trim();
            if (!string.Equals(header["invoice_date"], value, StringComparison.Ordinal))
            {
                header["invoice_date"] = value;
                warnings.Add("invoice_date dipastikan dari tanggal penerbitan/tanda tangan; tanggal jatuh tempo tidak dipakai.");
            }

            evidence.RemoveAll(item =>
                item.CanonicalField == "invoice_date" && !item.LineItemIndex.HasValue);
            evidence.Add(new CleanedFieldEvidence(
                "invoice_date",
                value,
                issueDate.Value,
                1.0,
                "invoice_issue_date_rule"));
        }

        RecoverInvoiceTotals(rawText, header, evidence);
        RecoverInvoiceSigner(rawText, header, evidence);
    }

    private static string? FindInvoiceSupplier(string rawText)
    {
        int invoiceMarker = rawText.IndexOf("Invoice", StringComparison.OrdinalIgnoreCase);
        string headerArea = invoiceMarker > 0 ? rawText[..invoiceMarker] : rawText;

        return headerArea
            .Split(['\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment.Length is >= 5 and <= 120)
            .Where(segment => !segment.Contains(':') && segment.Count(char.IsDigit) <= 2)
            .FirstOrDefault(segment => CorporateNameRegex().IsMatch(segment));
    }

    private static void RecoverInvoiceTotals(
        string rawText,
        Dictionary<string, string?> header,
        List<CleanedFieldEvidence> evidence)
    {
        string[] lines = rawText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        (string Value, string Source)? subtotal = FindLabelledMoney(
            lines,
            "Jumlah Harga Jual",
            "Sub Total",
            "Subtotal");
        (string Value, string Source)? taxableBase = FindLabelledMoney(
            lines,
            "Dasar Pengenaan Pajak",
            "Taxable Amount",
            "DPP");
        (string Value, string Source)? tax = FindLabelledMoney(
            lines,
            "Pajak Pertambahan Nilai",
            "Value Added Tax",
            "VAT Amount",
            "Tax Amount");
        (string Value, string Source)? total = FindLabelledMoney(
            lines,
            "Grand Total",
            "Invoice Total",
            "Total Amount",
            "Jumlah Tagihan");

        if (subtotal.HasValue)
        {
            SetHeaderFromRule(header, evidence, "sub_total_amount", subtotal.Value.Value,
                subtotal.Value.Source, "invoice_total_label_rule");
        }
        if (taxableBase.HasValue)
        {
            SetHeaderFromRule(header, evidence, "taxable_base", taxableBase.Value.Value,
                taxableBase.Value.Source, "invoice_total_label_rule");
        }
        if (tax.HasValue)
        {
            SetHeaderFromRule(header, evidence, "tax_amount", tax.Value.Value,
                tax.Value.Source, "invoice_total_label_rule");
        }

        if (!total.HasValue &&
            subtotal.HasValue && tax.HasValue &&
            OcrEvidenceGrounder.TryParseFlexibleDecimal(subtotal.Value.Value, out decimal subtotalValue) &&
            OcrEvidenceGrounder.TryParseFlexibleDecimal(tax.Value.Value, out decimal taxValue))
        {
            decimal expectedTotal = subtotalValue + taxValue;
            total = FindPrintedMoney(lines, expectedTotal);
        }

        if (total.HasValue)
        {
            SetHeaderFromRule(header, evidence, "total_amount", total.Value.Value,
                total.Value.Source, "invoice_total_arithmetic_rule");
        }
    }

    private static (string Value, string Source)? FindLabelledMoney(
        IEnumerable<string> lines,
        params string[] labels)
    {
        foreach (string line in lines)
        {
            if (!labels.Any(label => line.Contains(label, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            MatchCollection values = InlineNumberRegex().Matches(line);
            for (int index = values.Count - 1; index >= 0; index--)
            {
                string value = values[index].Value;
                if (OcrEvidenceGrounder.TryParseFlexibleDecimal(value, out _))
                {
                    return (value, line);
                }
            }
        }

        return null;
    }

    private static (string Value, string Source)? FindPrintedMoney(
        IEnumerable<string> lines,
        decimal expected)
    {
        foreach (string line in lines)
        {
            foreach (Match match in InlineNumberRegex().Matches(line).Cast<Match>().Reverse())
            {
                if (OcrEvidenceGrounder.TryParseFlexibleDecimal(match.Value, out decimal value) &&
                    value == expected)
                {
                    return (match.Value, line);
                }
            }
        }

        return null;
    }

    private static void SetHeaderFromRule(
        Dictionary<string, string?> header,
        List<CleanedFieldEvidence> evidence,
        string field,
        string value,
        string source,
        string method)
    {
        header[field] = value;
        evidence.RemoveAll(item =>
            item.CanonicalField == field && !item.LineItemIndex.HasValue);
        evidence.Add(new CleanedFieldEvidence(field, value, source, 1.0, method));
    }

    private static bool IsDueDateContext(string rawText, int dateIndex)
    {
        int contextStart = Math.Max(0, dateIndex - 60);
        string context = rawText[contextStart..dateIndex];
        return context.Contains("due date", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("jatuh waktu", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("jatuh tempo", StringComparison.OrdinalIgnoreCase);
    }

    private static void RecoverInvoiceSigner(
        string rawText,
        Dictionary<string, string?> header,
        List<CleanedFieldEvidence> evidence)
    {
        string[] lines = rawText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int index = lines.Length - 1; index > 0; index--)
        {
            string? positionSource = lines[index]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(segment => SignerPositionLineRegex().IsMatch(segment));
            if (positionSource is null)
            {
                continue;
            }

            Match positionMatch = SignerPositionLineRegex().Match(positionSource);
            string position = positionMatch.Groups["value"].Value.Trim(' ', '|');
            string? name = lines[index - 1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Reverse()
                .FirstOrDefault(IsPlausibleSignerName);
            if (name is null)
            {
                return;
            }

            header["signer_name"] = name;
            header["signer_position"] = position;
            evidence.RemoveAll(item =>
                (item.CanonicalField == "signer_name" ||
                 item.CanonicalField == "signer_position") &&
                !item.LineItemIndex.HasValue);
            evidence.Add(new CleanedFieldEvidence(
                "signer_name", name, name, 0.95, "signature_block_rule"));
            evidence.Add(new CleanedFieldEvidence(
                "signer_position", position, positionSource, 0.95, "signature_block_rule"));
            return;
        }
    }

    private static bool IsPlausibleSignerName(string value)
    {
        string candidate = value.Trim(' ', '|');
        return candidate.Length is >= 4 and <= 80 &&
               candidate.Count(char.IsWhiteSpace) >= 1 &&
               candidate.Any(char.IsLetter) &&
               !candidate.Any(char.IsDigit) &&
               !candidate.Contains(':') &&
               !PrintedDateRegex().IsMatch(candidate);
    }

    private static void RecoverTaxInvoiceHeader(
        string rawText,
        Dictionary<string, string?> header,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        Match numberMatch = TaxInvoiceNumberRegex().Match(rawText);
        if (numberMatch.Success)
        {
            string value = numberMatch.Groups["value"].Value.Trim();
            if (!string.Equals(header["tax_invoice_number"], value, StringComparison.Ordinal))
            {
                header["tax_invoice_number"] = value;
                warnings.Add("tax_invoice_number dipulihkan dari label resmi e-Faktur.");
            }

            evidence.RemoveAll(item => item.CanonicalField == "tax_invoice_number");
            evidence.Add(new CleanedFieldEvidence(
                "tax_invoice_number", value, numberMatch.Value, 1.0, "label_rule"));
        }

        Match supplierMatch = TaxSupplierRegex().Match(rawText);
        if (supplierMatch.Success)
        {
            string value = supplierMatch.Groups["value"].Value.Trim().Trim(':');
            if (!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(header["supplier_name"], value, StringComparison.OrdinalIgnoreCase))
            {
                header["supplier_name"] = value;
                warnings.Add("supplier_name dipastikan dari bagian Pengusaha Kena Pajak.");
            }

            evidence.RemoveAll(item => item.CanonicalField == "supplier_name");
            evidence.Add(new CleanedFieldEvidence(
                "supplier_name", value, supplierMatch.Value, 1.0, "label_rule"));
        }

        MatchCollection dateMatches = PrintedDateRegex().Matches(rawText);
        if (dateMatches.Count > 0)
        {
            Match dateMatch = dateMatches[^1];
            string value = dateMatch.Groups["value"].Value.Trim();
            header["tax_invoice_date"] = value;
            evidence.RemoveAll(item => item.CanonicalField == "tax_invoice_date");
            evidence.Add(new CleanedFieldEvidence(
                "tax_invoice_date", value, dateMatch.Value, 1.0, "last_printed_date_rule"));

            string afterDate = rawText[dateMatch.Index..];
            int referenceIndex = afterDate.IndexOf("(Referensi", StringComparison.OrdinalIgnoreCase);
            int noticeIndex = afterDate.IndexOf("Pemberitahuan", StringComparison.OrdinalIgnoreCase);
            int signatureEnd = new[] { referenceIndex, noticeIndex }
                .Where(index => index >= 0)
                .DefaultIfEmpty(afterDate.Length)
                .Min();
            string signatureArea = afterDate[..signatureEnd];
            MatchCollection signerMatches = SignerNameRegex().Matches(signatureArea);
            if (signerMatches.Count > 0)
            {
                Match signerMatch = signerMatches[^1];
                string signer = signerMatch.Groups["value"].Value.Trim();
                header["signer_name"] = signer;
                evidence.RemoveAll(item => item.CanonicalField == "signer_name");
                evidence.Add(new CleanedFieldEvidence(
                    "signer_name", signer, signerMatch.Value, 0.95, "signature_area_rule"));
            }
        }
    }

    private static List<CleanedLineItem> RecoverTaxInvoiceItems(string rawText)
    {
        string[] lines = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var recovered = new List<CleanedLineItem>();

        for (int index = 0; index < lines.Length; index++)
        {
            Match formula = PriceQuantityRegex().Match(lines[index]);
            if (!formula.Success)
            {
                continue;
            }

            string? partNumber = null;
            string? partName = null;
            for (int previous = index - 1; previous >= Math.Max(0, index - 3); previous--)
            {
                Match part = PartAtStartRegex().Match(lines[previous]);
                if (!part.Success)
                {
                    continue;
                }

                partNumber = part.Groups["part"].Value.Trim();
                partName = NullIfNoise(part.Groups["name"].Value);
                break;
            }

            if (partNumber is null)
            {
                continue;
            }

            string price = formula.Groups["price"].Value.Trim();
            string quantity = formula.Groups["quantity"].Value.Trim();
            string? tailName = NullIfNoise(formula.Groups["name"].Value);
            partName ??= tailName;
            string? amount = FindCalculatedAmount(lines, index + 1, price, quantity);

            recovered.Add(new CleanedLineItem(partNumber, partName, quantity, price, amount));
        }

        return recovered;
    }

    private static void RecoverTaxInvoiceSummary(
        string rawText,
        IReadOnlyList<CleanedLineItem> recoveredItems,
        Dictionary<string, string?> header,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        string[] rawLines = rawText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int summaryLineIndex = Array.FindLastIndex(
            rawLines,
            line => line.Contains(
                "Harga Jual / Penggantian / Uang Muka / Termin",
                StringComparison.OrdinalIgnoreCase));
        if (summaryLineIndex < 0)
        {
            return;
        }

        int startLineIndex = summaryLineIndex;
        if (summaryLineIndex > 0 && StandaloneNumberRegex().IsMatch(rawLines[summaryLineIndex - 1]))
        {
            startLineIndex--;
        }

        int endLineIndex = Array.FindIndex(
            rawLines,
            summaryLineIndex,
            line => line.Contains("Sesuai dengan ketentuan", StringComparison.OrdinalIgnoreCase));
        if (endLineIndex < 0)
        {
            endLineIndex = rawLines.Length;
        }

        string summary = string.Join('\n', rawLines[startLineIndex..endLineIndex]);
        var values = SummaryMoneyRegex().Matches(summary)
            .Select(match => match.Groups["value"].Value.Trim())
            .Select(text => new
            {
                Text = text,
                Parsed = OcrEvidenceGrounder.TryParseFlexibleDecimal(text, out decimal parsed)
                    ? parsed
                    : (decimal?)null
            })
            .Where(item => item.Parsed.HasValue)
            .ToList();

        decimal itemSum = recoveredItems
            .Select(item => OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Amount, out decimal amount)
                ? amount
                : 0)
            .Sum();
        var subtotal = values.FirstOrDefault(item => itemSum > 0 && item.Parsed == itemSum);
        if (subtotal is not null)
        {
            SetHeaderRule(header, evidence, "sub_total_amount", subtotal.Text, summary);
        }

        var positiveValues = values
            .Where(item => item.Parsed > 0 && item != subtotal)
            .ToList();
        for (int first = 0; first < positiveValues.Count; first++)
        {
            for (int second = 0; second < positiveValues.Count; second++)
            {
                if (first == second)
                {
                    continue;
                }

                decimal taxableBase = positiveValues[first].Parsed!.Value;
                decimal tax = positiveValues[second].Parsed!.Value;
                if (taxableBase <= tax)
                {
                    continue;
                }

                decimal ratio = tax / taxableBase;
                if (ratio is >= 0.099m and <= 0.125m)
                {
                    SetHeaderRule(header, evidence, "taxable_base", positiveValues[first].Text, summary);
                    SetHeaderRule(header, evidence, "tax_amount", positiveValues[second].Text, summary);
                    first = positiveValues.Count;
                    break;
                }
            }
        }

        var zeroValues = values.Where(item => item.Parsed == 0).ToList();
        if (zeroValues.Count > 0)
        {
            SetHeaderRule(header, evidence, "discount", zeroValues[0].Text, summary);
            SetHeaderRule(
                header,
                evidence,
                "luxury_goods_sales_tax",
                zeroValues[^1].Text,
                summary);
        }
        if (zeroValues.Count >= 3)
        {
            SetHeaderRule(header, evidence, "down_payment", zeroValues[1].Text, summary);
        }

        warnings.Add("Ringkasan e-Faktur divalidasi dengan subtotal item dan rasio DPP/PPN.");
    }

    private static void SetHeaderRule(
        Dictionary<string, string?> header,
        List<CleanedFieldEvidence> evidence,
        string field,
        string value,
        string source)
    {
        header[field] = value;
        evidence.RemoveAll(item => item.CanonicalField == field && !item.LineItemIndex.HasValue);
        evidence.Add(new CleanedFieldEvidence(field, value, source, 1.0, "tax_summary_validation"));
    }

    private static string? FindCalculatedAmount(
        IReadOnlyList<string> lines,
        int startIndex,
        string price,
        string quantity)
    {
        if (!OcrEvidenceGrounder.TryParseFlexibleDecimal(price, out decimal parsedPrice) ||
            !OcrEvidenceGrounder.TryParseFlexibleDecimal(quantity, out decimal parsedQuantity))
        {
            return null;
        }

        decimal expected = parsedPrice * parsedQuantity;
        for (int index = startIndex; index < Math.Min(lines.Count, startIndex + 8); index++)
        {
            if (PriceQuantityRegex().IsMatch(lines[index]))
            {
                break;
            }

            string candidate = lines[index].Trim();
            if (StandaloneNumberRegex().IsMatch(candidate) &&
                OcrEvidenceGrounder.TryParseFlexibleDecimal(candidate, out decimal parsed) &&
                parsed == expected)
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<CleanedLineItem> MergeTaxInvoiceItems(
        List<CleanedLineItem> modelItems,
        List<CleanedLineItem> ruleItems,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        var merged = new List<CleanedLineItem>(modelItems);

        foreach (CleanedLineItem ruleItem in ruleItems)
        {
            int existingIndex = merged.FindIndex(item => string.Equals(
                item.PartNumber,
                ruleItem.PartNumber,
                StringComparison.OrdinalIgnoreCase));

            CleanedLineItem result;
            if (existingIndex < 0)
            {
                result = ruleItem;
                merged.Add(result);
                existingIndex = merged.Count - 1;
            }
            else
            {
                CleanedLineItem existing = merged[existingIndex];
                result = existing with
                {
                    PartName = IsTaxGoodsCode(existing.PartName)
                        ? ruleItem.PartName
                        : existing.PartName ?? ruleItem.PartName,
                    Quantity = ruleItem.Quantity ?? existing.Quantity,
                    Price = ruleItem.Price ?? existing.Price,
                    Amount = ruleItem.Amount ?? existing.Amount
                };
                merged[existingIndex] = result;
            }

        }

        if (ruleItems.Count > 0)
        {
            warnings.Add("Line item faktur pajak divalidasi dengan pola Rp price x quantity.");
        }

        return merged;
    }

    private static void AddFinalTaxItemEvidence(
        List<CleanedFieldEvidence> evidence,
        CleanedLineItem item,
        int itemIndex)
    {
        foreach ((string field, string? value) in new[]
                 {
                     ("part_number", item.PartNumber),
                     ("part_name", item.PartName),
                     ("quantity", item.Quantity),
                     ("price", item.Price),
                     ("amount", item.Amount)
                 })
        {
            if (value is not null)
            {
                evidence.Add(new CleanedFieldEvidence(
                    field, value, value, 1.0, "tax_invoice_validated", itemIndex));
            }
        }
    }

    private static CleanedLineItem SplitCompositeField(CleanedLineItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PartNumber))
        {
            Match match = CompositePartRegex().Match(item.PartNumber);
            if (match.Success && match.Groups["name"].Success)
            {
                return item with
                {
                    PartNumber = match.Groups["part"].Value.Trim(),
                    PartName = item.PartName ?? match.Groups["name"].Value.Trim()
                };
            }
        }

        if (string.IsNullOrWhiteSpace(item.PartNumber) && !string.IsNullOrWhiteSpace(item.PartName))
        {
            Match match = CompositePartRegex().Match(item.PartName);
            if (match.Success)
            {
                return item with
                {
                    PartNumber = match.Groups["part"].Value.Trim(),
                    PartName = match.Groups["name"].Success
                        ? match.Groups["name"].Value.Trim()
                        : null
                };
            }
        }

        return item;
    }

    private static bool HasAnyValue(CleanedLineItem item) =>
        !string.IsNullOrWhiteSpace(item.PartNumber) ||
        !string.IsNullOrWhiteSpace(item.PartName) ||
        !string.IsNullOrWhiteSpace(item.Quantity) ||
        !string.IsNullOrWhiteSpace(item.Price) ||
        !string.IsNullOrWhiteSpace(item.Amount);

    private static bool IsTaxGoodsCode(string? value) =>
        string.Equals(value?.Trim(), "000000", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "Lainnya", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumeric(string? value) =>
        OcrEvidenceGrounder.TryParseFlexibleDecimal(value, out _);

    private static bool IsMathematicallyConsistent(CleanedLineItem item)
    {
        if (!OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Price, out decimal price) ||
            !OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Quantity, out decimal quantity) ||
            !OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Amount, out decimal amount))
        {
            return true;
        }

        return price * quantity == amount;
    }

    private static CleanedLineItem RepairInvoiceLineItem(
        string rawText,
        CleanedLineItem item,
        int itemIndex,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(item.PartNumber))
        {
            return item;
        }

        string? row = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains(item.PartNumber, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return RejectInconsistentArithmetic(item, itemIndex, warnings, evidence);
        }

        int partIndex = row.IndexOf(item.PartNumber, StringComparison.OrdinalIgnoreCase);
        string afterPart = row[(partIndex + item.PartNumber.Length)..];
        var numbers = InlineNumberRegex().Matches(afterPart)
            .Select(match => match.Value.Trim())
            .Select((text, index) => new
            {
                Text = text,
                Index = index,
                Parsed = OcrEvidenceGrounder.TryParseFlexibleDecimal(text, out decimal parsed)
                    ? parsed
                    : (decimal?)null
            })
            .Where(value => value.Parsed.HasValue)
            .ToList();

        if (numbers.Count < 3)
        {
            return RejectInconsistentArithmetic(item, itemIndex, warnings, evidence);
        }

        var matches = new List<(string Quantity, string Price, string Amount, int Score)>();
        for (int quantityIndex = 0; quantityIndex < numbers.Count; quantityIndex++)
        {
            for (int priceIndex = quantityIndex + 1; priceIndex < numbers.Count; priceIndex++)
            {
                for (int amountIndex = priceIndex + 1; amountIndex < numbers.Count; amountIndex++)
                {
                    decimal quantity = numbers[quantityIndex].Parsed!.Value;
                    decimal price = numbers[priceIndex].Parsed!.Value;
                    decimal amount = numbers[amountIndex].Parsed!.Value;
                    if (quantity <= 0 || price <= 0 || quantity * price != amount)
                    {
                        continue;
                    }

                    int score = 0;
                    score += NumericEquals(item.Quantity, quantity) ? 2 : 0;
                    score += NumericEquals(item.Price, price) ? 2 : 0;
                    score += NumericEquals(item.Amount, amount) ? 4 : 0;
                    matches.Add((
                        numbers[quantityIndex].Text,
                        numbers[priceIndex].Text,
                        numbers[amountIndex].Text,
                        score));
                }
            }
        }

        var best = matches.OrderByDescending(match => match.Score).FirstOrDefault();
        if (best == default)
        {
            return RejectInconsistentArithmetic(item, itemIndex, warnings, evidence);
        }

        var repaired = item with
        {
            Quantity = best.Quantity,
            Price = best.Price,
            Amount = best.Amount
        };

        foreach ((string field, string? value) in new[]
                 {
                     ("quantity", repaired.Quantity),
                     ("price", repaired.Price),
                     ("amount", repaired.Amount)
                 })
        {
            evidence.RemoveAll(entry =>
                entry.CanonicalField == field && entry.LineItemIndex == itemIndex);
            evidence.Add(new CleanedFieldEvidence(
                field, value, row, 1.0, "row_arithmetic_validation", itemIndex));
        }

        if (!string.Equals(item.Price, repaired.Price, StringComparison.Ordinal) ||
            !string.Equals(item.Quantity, repaired.Quantity, StringComparison.Ordinal))
        {
            warnings.Add($"line_items[{itemIndex}] diperbaiki menggunakan quantity x price = amount pada baris OCR yang sama.");
        }

        return repaired;
    }

    private static CleanedLineItem RejectInconsistentArithmetic(
        CleanedLineItem item,
        int itemIndex,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        if (!OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Quantity, out decimal quantity) ||
            !OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Price, out decimal price) ||
            !OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Amount, out decimal amount) ||
            quantity * price == amount)
        {
            return item;
        }

        warnings.Add($"line_items[{itemIndex}] gagal validasi quantity x price = amount; price dan amount dikosongkan untuk review.");
        evidence.RemoveAll(entry =>
            entry.LineItemIndex == itemIndex &&
            entry.CanonicalField is "price" or "amount");
        return item with { Price = null, Amount = null };
    }

    private static void ValidateInvoiceTotals(
        Dictionary<string, string?> header,
        IReadOnlyList<CleanedLineItem> items,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        if (items.Count == 0 || items.Any(item =>
                !OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Amount, out _)))
        {
            return;
        }

        decimal calculatedSubtotal = items.Sum(item =>
        {
            OcrEvidenceGrounder.TryParseFlexibleDecimal(item.Amount, out decimal amount);
            return amount;
        });

        if (OcrEvidenceGrounder.TryParseFlexibleDecimal(header.GetValueOrDefault("sub_total_amount"), out decimal subtotal) &&
            subtotal != calculatedSubtotal)
        {
            header["sub_total_amount"] = null;
            evidence.RemoveAll(entry => entry.CanonicalField == "sub_total_amount");
            warnings.Add("sub_total_amount dikosongkan karena tidak sama dengan jumlah amount line item.");
        }

        if (OcrEvidenceGrounder.TryParseFlexibleDecimal(header.GetValueOrDefault("sub_total_amount"), out subtotal) &&
            OcrEvidenceGrounder.TryParseFlexibleDecimal(header.GetValueOrDefault("tax_amount"), out decimal tax) &&
            OcrEvidenceGrounder.TryParseFlexibleDecimal(header.GetValueOrDefault("total_amount"), out decimal total) &&
            subtotal + tax != total)
        {
            header["total_amount"] = null;
            evidence.RemoveAll(entry => entry.CanonicalField == "total_amount");
            warnings.Add("total_amount dikosongkan karena subtotal + tax tidak konsisten.");
        }
    }

    private static bool NumericEquals(string? text, decimal expected) =>
        OcrEvidenceGrounder.TryParseFlexibleDecimal(text, out decimal value) && value == expected;

    private static List<CleanedLineItem> RecoverDeliveryRows(string rawText)
    {
        var recovered = new List<CleanedLineItem>();
        foreach (string line in rawText.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match partMatch = PartAnywhereRegex().Match(line);
            if (!partMatch.Success)
            {
                continue;
            }

            string remainder = line[(partMatch.Index + partMatch.Length)..].Trim(' ', '|', '-', ':');
            MatchCollection numberMatches = InlineNumberRegex().Matches(remainder);
            if (string.IsNullOrWhiteSpace(remainder) || numberMatches.Count == 0)
            {
                continue;
            }

            Match quantityMatch = numberMatches[^1];
            string partName = remainder[..quantityMatch.Index].Trim(' ', '|', '-', ':');
            if (string.IsNullOrWhiteSpace(partName))
            {
                continue;
            }

            recovered.Add(new CleanedLineItem(
                partMatch.Groups["part"].Value,
                partName,
                quantityMatch.Value,
                null,
                null));
        }

        return recovered;
    }

    private static List<CleanedLineItem> RecoverInvoiceRows(
        string rawText,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        var recovered = new List<CleanedLineItem>();
        foreach (string line in rawText.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match partMatch = PartAnywhereRegex().Match(line);
            if (!partMatch.Success)
            {
                continue;
            }

            string remainder = line[(partMatch.Index + partMatch.Length)..];
            Match firstNumber = InlineNumberRegex().Match(remainder);
            if (!firstNumber.Success)
            {
                continue;
            }

            string partName = remainder[..firstNumber.Index].Trim(' ', '|', '-', ':');
            var stub = new CleanedLineItem(
                partMatch.Groups["part"].Value,
                string.IsNullOrWhiteSpace(partName) ? null : partName,
                null,
                null,
                null);
            CleanedLineItem repaired = RepairInvoiceLineItem(
                rawText,
                stub,
                recovered.Count,
                warnings,
                evidence);

            if (repaired.Quantity is not null && repaired.Price is not null && repaired.Amount is not null)
            {
                recovered.Add(repaired);
            }
        }

        return recovered;
    }

    private static List<CleanedLineItem> MergeGenericRows(
        List<CleanedLineItem> modelItems,
        IReadOnlyList<CleanedLineItem> recoveredItems,
        string method,
        List<string> warnings,
        List<CleanedFieldEvidence> evidence)
    {
        var merged = new List<CleanedLineItem>(modelItems);
        int recoveredCount = 0;

        foreach (CleanedLineItem recovered in recoveredItems)
        {
            int existingIndex = merged.FindIndex(item =>
                (!string.IsNullOrWhiteSpace(item.PartNumber) &&
                 string.Equals(item.PartNumber, recovered.PartNumber, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(item.PartName) &&
                 !string.IsNullOrWhiteSpace(recovered.PartName) &&
                 string.Equals(item.PartName, recovered.PartName, StringComparison.OrdinalIgnoreCase)));

            CleanedLineItem result;
            if (existingIndex < 0)
            {
                result = recovered;
                merged.Add(result);
                existingIndex = merged.Count - 1;
            }
            else
            {
                CleanedLineItem existing = merged[existingIndex];
                result = existing with
                {
                    PartNumber = recovered.PartNumber ?? existing.PartNumber,
                    PartName = recovered.PartName ?? existing.PartName,
                    Quantity = recovered.Quantity ?? existing.Quantity,
                    Price = recovered.Price ?? existing.Price,
                    Amount = recovered.Amount ?? existing.Amount
                };
                merged[existingIndex] = result;
            }

            evidence.RemoveAll(entry => entry.LineItemIndex == existingIndex);
            foreach ((string field, string? value) in new[]
                     {
                         ("part_number", result.PartNumber),
                         ("part_name", result.PartName),
                         ("quantity", result.Quantity),
                         ("price", result.Price),
                         ("amount", result.Amount)
                     })
            {
                if (value is not null)
                {
                    evidence.Add(new CleanedFieldEvidence(
                        field, value, value, 1.0, method, existingIndex));
                }
            }

            recoveredCount++;
        }

        if (recoveredCount > 0)
        {
            warnings.Add($"{recoveredCount} line item divalidasi dari baris tabel OCR.");
        }

        return merged;
    }

    private static string? NullIfNoise(string? value)
    {
        string? trimmed = value?.Trim(' ', '-', ':');
        return string.IsNullOrWhiteSpace(trimmed) || IsTaxGoodsCode(trimmed) ? null : trimmed;
    }

    [GeneratedRegex(@"Kode\s+dan\s+Nomor\s+Seri\s+Faktur\s+Pajak\s*:\s*(?<value>\d[\d .-]{10,25})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaxInvoiceNumberRegex();

    [GeneratedRegex(@"Pengusaha\s+Kena\s+Pajak\s*:\s*(?:\r?\n\s*)?Nama\s*:\s*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaxSupplierRegex();

    [GeneratedRegex(@"\bRp\.?\s*(?<price>\d[\d.,]*)\s*[xX×]\s*(?<quantity>\d[\d.,]*)(?:\s+(?<name>.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PriceQuantityRegex();

    [GeneratedRegex(@"^(?<part>[A-Z0-9]+(?:[-/][A-Z0-9]+)+)(?:\s+-\s+(?<name>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartAtStartRegex();

    [GeneratedRegex(@"^(?<part>[A-Z0-9]+(?:[-/][A-Z0-9]+)+)\s+-\s+(?<name>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompositePartRegex();

    [GeneratedRegex(@"^(?:Rp\.?\s*)?[+-]?\d[\d.,]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneNumberRegex();

    [GeneratedRegex(@"(?m)^\s*(?:Rp\.?\s*)?(?<value>[+-]?\d[\d.]*,\d{2})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SummaryMoneyRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d[\d.,]*(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex InlineNumberRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])(?<part>[A-Z0-9]+(?:[-/][A-Z0-9]+)+)(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartAnywhereRegex();

    [GeneratedRegex(@"(?<!\d)(?<value>\d{1,2}\s+[A-Za-z]+\s+\d{4})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrintedDateRegex();

    [GeneratedRegex(@"(?:Invoice\s+Date|Tanggal\s+(?:Invoice|Faktur)|Tgl\.?\s*(?:Invoice|Faktur))\s*[:#]?\s*(?<value>\d{1,2}\s+[A-Za-z]+\s+\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvoiceDateLabelRegex();

    [GeneratedRegex(@"(?:Faktur[\t ]+Penjualan[\t ]*/[\t ]*Invoice|Invoice)[\t ]*(?:\|[\t ]*)?(?:No\.?|Number|#)?[\t ]*[:#-]?[\t ]*(?<value>[A-Z0-9][A-Z0-9./-]{4,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvoiceNumberLabelRegex();

    [GeneratedRegex(@"^(?:(?:PT|CV|UD)\.?\s+.+|.+(?:LTD\.?|LIMITED|CORPORATION|CORP\.?|CO\.?\s*,?\s*LTD\.?)$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorporateNameRegex();

    [GeneratedRegex(@"^(?<value>[^\r\n|]{0,60}(?:Dept\.?\s*Head|Department\s+Head|General\s+Manager|Manager|Director|Direktur))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignerPositionLineRegex();

    [GeneratedRegex(@"(?m)^\s*(?<value>[A-Z][A-Z .'-]{5,})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SignerNameRegex();
}
