using System.Globalization;
using System.Text;
using HinoDocumentAI.Service.Models;

namespace HinoDocumentAI.Service.Services;

public record OcrPageLayout(string RawText, string LayoutText, List<OcrTextRegion> Regions);

/// <summary>
/// Rebuilds reading order from OCR coordinates instead of trusting the order
/// returned by the detector. Regions are grouped into visual rows and then
/// sorted left-to-right inside each row. LayoutText keeps compact coordinates
/// so the structuring model can reason about arbitrary supplier tables.
/// </summary>
public static class OcrLayoutBuilder
{
    public static OcrPageLayout Build(IEnumerable<OcrTextRegion> source)
    {
        var regions = source
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .Select(Sanitize)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToList();

        if (regions.Count == 0)
        {
            return new OcrPageLayout(string.Empty, string.Empty, []);
        }

        double medianHeight = Median(regions.Select(region => region.Height));
        var rows = new List<List<OcrTextRegion>>();

        foreach (var region in regions)
        {
            List<OcrTextRegion>? bestRow = null;
            double bestDistance = double.MaxValue;

            foreach (var row in rows)
            {
                double rowY = row.Average(item => item.Y);
                double rowHeight = Math.Max(medianHeight, row.Average(item => item.Height));
                double distance = Math.Abs(region.Y - rowY);
                double tolerance = Math.Max(medianHeight * 0.65, Math.Max(rowHeight, region.Height) * 0.55);

                if (distance <= tolerance && distance < bestDistance)
                {
                    bestRow = row;
                    bestDistance = distance;
                }
            }

            if (bestRow is null)
            {
                rows.Add([region]);
            }
            else
            {
                bestRow.Add(region);
            }
        }

        var orderedRows = rows
            .OrderBy(row => row.Average(item => item.Y))
            .Select(row => row.OrderBy(item => item.X).ToList())
            .ToList();

        var raw = new StringBuilder();
        var layout = new StringBuilder();
        var orderedRegions = new List<OcrTextRegion>(regions.Count);

        for (int rowIndex = 0; rowIndex < orderedRows.Count; rowIndex++)
        {
            var row = orderedRows[rowIndex];
            orderedRegions.AddRange(row);

            string rawLine = JoinVisualRow(row, includeCoordinates: false);
            string layoutLine = JoinVisualRow(row, includeCoordinates: true);

            if (rawLine.Length > 0)
            {
                raw.AppendLine(rawLine);
                int yPercent = ClampPercent(row.Average(item => item.Y));
                layout.Append("L")
                    .Append((rowIndex + 1).ToString("D3", CultureInfo.InvariantCulture))
                    .Append(" y=")
                    .Append(yPercent.ToString("D3", CultureInfo.InvariantCulture))
                    .Append(": ")
                    .AppendLine(layoutLine);
            }
        }

        return new OcrPageLayout(
            raw.ToString().TrimEnd(),
            layout.ToString().TrimEnd(),
            orderedRegions);
    }

    private static string JoinVisualRow(IReadOnlyList<OcrTextRegion> row, bool includeCoordinates)
    {
        var builder = new StringBuilder();
        OcrTextRegion? previous = null;

        foreach (var region in row)
        {
            if (builder.Length > 0)
            {
                double previousRight = previous!.X + (previous.Width / 2.0);
                double currentLeft = region.X - (region.Width / 2.0);
                double gap = currentLeft - previousRight;
                double largeGap = Math.Max(0.025, Math.Min(previous.Height, region.Height) * 1.5);
                builder.Append(gap > largeGap ? " | " : " ");
            }

            if (includeCoordinates)
            {
                int left = ClampPercent(region.X - (region.Width / 2.0));
                int right = ClampPercent(region.X + (region.Width / 2.0));
                builder.Append("[x=")
                    .Append(left.ToString("D3", CultureInfo.InvariantCulture))
                    .Append('-')
                    .Append(right.ToString("D3", CultureInfo.InvariantCulture))
                    .Append("] ");
            }

            builder.Append(region.Text.Trim());
            previous = region;
        }

        return builder.ToString();
    }

    private static OcrTextRegion Sanitize(OcrTextRegion region) => region with
    {
        Text = region.Text.Trim(),
        Confidence = double.IsFinite(region.Confidence) ? Math.Clamp(region.Confidence, 0, 1) : 0,
        X = SafeUnit(region.X),
        Y = SafeUnit(region.Y),
        // Older output used RotatedRect.Size directly. OpenCV commonly stores
        // horizontal OCR boxes as angle ~= 90 degrees, so width and height
        // appeared swapped. Repair those payloads when rebuilding a layout.
        Width = SafeUnit(ShouldSwapLegacyBox(region) ? region.Height : region.Width),
        Height = SafeUnit(ShouldSwapLegacyBox(region) ? region.Width : region.Height)
    };

    private static bool ShouldSwapLegacyBox(OcrTextRegion region) =>
        region.Text.Trim().Length > 1 &&
        double.IsFinite(region.Width) &&
        double.IsFinite(region.Height) &&
        region.Height > region.Width * 1.2;

    private static double SafeUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static int ClampPercent(double value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * 100, MidpointRounding.AwayFromZero);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0.015;
        }

        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2.0
            : ordered[middle];
    }
}
