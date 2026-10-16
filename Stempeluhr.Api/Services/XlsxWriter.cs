using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Stempeluhr.Api.Services;

/// <summary>
/// Ein Blatt der Arbeitsmappe: Kopfzeile (fett, fixiert), Datenzeilen und
/// Spaltenbreiten. Zellwerte: <see cref="string"/> (Inline-String),
/// <see cref="int"/>/<see cref="long"/> (Zahl), <see cref="decimal"/>/<see cref="double"/>
/// (Zahl <c>0.00</c>), <see cref="DateOnly"/> (<c>dd.MM.yyyy</c>), <see cref="TimeOnly"/>
/// (<c>HH:mm</c>); <c>null</c> bleibt leer.
/// </summary>
public sealed class XlsxSheet(string name, IReadOnlyList<string> header, IReadOnlyList<double> columnWidths)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Header { get; } = header;
    public IReadOnlyList<double> ColumnWidths { get; } = columnWidths;
    internal List<(IReadOnlyList<object?> Cells, bool Bold)> Rows { get; } = [];

    public XlsxSheet AddRow(params object?[] cells)
    {
        Rows.Add((cells, false));
        return this;
    }

    /// <summary>Zeile in Fettschrift, z. B. eine Summenzeile.</summary>
    public XlsxSheet AddBoldRow(params object?[] cells)
    {
        Rows.Add((cells, true));
        return this;
    }
}

/// <summary>
/// Kleiner Writer für .xlsx (SpreadsheetML in einem ZIP) ohne NuGet-Pakete. Er
/// kann genau das, was der Monatsexport braucht: mehrere Blätter, fixierte
/// Kopfzeile, Spaltenbreiten, Zahl-, Datums- und Uhrzeitformate.
/// </summary>
public static class XlsxWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxSheetNameLength = 31;

    // Index in cellXfs (siehe WriteStyles).
    private const int StyleGeneral = 0;
    private const int StyleBold = 1;
    private const int StyleNumber = 2;
    private const int StyleDate = 3;
    private const int StyleTime = 4;
    private const int StyleBoldNumber = 5;

    private static readonly DateOnly SerialEpoch = new(1899, 12, 30);
    private static readonly char[] ForbiddenSheetChars = ['[', ']', ':', '*', '?', '/', '\\'];

    public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 0)
        {
            throw new ArgumentException("Eine Arbeitsmappe braucht mindestens ein Blatt.", nameof(sheets));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.Name) || sheet.Name.Length > MaxSheetNameLength
                || sheet.Name.IndexOfAny(ForbiddenSheetChars) >= 0 || !names.Add(sheet.Name))
            {
                throw new ArgumentException($"Ungültiger oder doppelter Blattname: '{sheet.Name}'.", nameof(sheets));
            }
        }

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WritePart(zip, "[Content_Types].xml", writer => WriteContentTypes(writer, sheets.Count));
            WritePart(zip, "_rels/.rels", WriteRootRelationships);
            WritePart(zip, "xl/workbook.xml", writer => WriteWorkbook(writer, sheets));
            WritePart(zip, "xl/_rels/workbook.xml.rels", writer => WriteWorkbookRelationships(writer, sheets.Count));
            WritePart(zip, "xl/styles.xml", WriteStyles);
            for (var index = 0; index < sheets.Count; index++)
            {
                var sheet = sheets[index];
                WritePart(zip, $"xl/worksheets/sheet{index + 1}.xml", writer => WriteSheet(writer, sheet));
            }
        }
        return stream.ToArray();
    }

    private static void WritePart(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false };
        using var writer = XmlWriter.Create(entryStream, settings);
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteContentTypes(XmlWriter writer, int sheetCount)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        writer.WriteStartElement("Types", ns);
        Default(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(writer, "xml", "application/xml");
        Override(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        for (var index = 1; index <= sheetCount; index++)
        {
            Override(writer, $"/xl/worksheets/sheet{index}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }
        Override(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        writer.WriteEndElement();

        static void Default(XmlWriter w, string extension, string contentType)
        {
            w.WriteStartElement("Default");
            w.WriteAttributeString("Extension", extension);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }

        static void Override(XmlWriter w, string partName, string contentType)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName", partName);
            w.WriteAttributeString("ContentType", contentType);
            w.WriteEndElement();
        }
    }

    private static void WriteRootRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", PackageRelNs);
        Relationship(writer, "rId1", RelNs + "/officeDocument", "xl/workbook.xml");
        writer.WriteEndElement();
    }

    private static void WriteWorkbook(XmlWriter writer, IReadOnlyList<XlsxSheet> sheets)
    {
        writer.WriteStartElement("workbook", MainNs);
        writer.WriteAttributeString("xmlns", "r", null, RelNs);
        writer.WriteStartElement("sheets");
        for (var index = 0; index < sheets.Count; index++)
        {
            writer.WriteStartElement("sheet");
            writer.WriteAttributeString("name", Sanitize(sheets[index].Name));
            writer.WriteAttributeString("sheetId", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("id", RelNs, $"rId{index + 1}");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(XmlWriter writer, int sheetCount)
    {
        writer.WriteStartElement("Relationships", PackageRelNs);
        for (var index = 1; index <= sheetCount; index++)
        {
            Relationship(writer, $"rId{index}", RelNs + "/worksheet", $"worksheets/sheet{index}.xml");
        }
        Relationship(writer, $"rId{sheetCount + 1}", RelNs + "/styles", "styles.xml");
        writer.WriteEndElement();
    }

    private static void Relationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartElement("styleSheet", MainNs);

        // 164 = Datum, 165 = Uhrzeit; 2 ist das eingebaute "0.00".
        writer.WriteStartElement("numFmts");
        writer.WriteAttributeString("count", "2");
        NumFmt(writer, 164, "dd.MM.yyyy");
        NumFmt(writer, 165, "HH:mm");
        writer.WriteEndElement();

        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "2");
        Font(writer, bold: false);
        Font(writer, bold: true);
        writer.WriteEndElement();

        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "2");
        Fill(writer, "none");
        Fill(writer, "gray125");
        writer.WriteEndElement();

        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            writer.WriteStartElement(side);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", "0");
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs");
        writer.WriteAttributeString("count", "6");
        Xf(writer, 0, 0);   // StyleGeneral
        Xf(writer, 0, 1);   // StyleBold
        Xf(writer, 2, 0);   // StyleNumber
        Xf(writer, 164, 0); // StyleDate
        Xf(writer, 165, 0); // StyleTime
        Xf(writer, 2, 1);   // StyleBoldNumber
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle");
        writer.WriteAttributeString("name", "Standard");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();

        static void NumFmt(XmlWriter w, int id, string code)
        {
            w.WriteStartElement("numFmt");
            w.WriteAttributeString("numFmtId", id.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("formatCode", code);
            w.WriteEndElement();
        }

        static void Font(XmlWriter w, bool bold)
        {
            w.WriteStartElement("font");
            if (bold)
            {
                w.WriteStartElement("b");
                w.WriteEndElement();
            }
            w.WriteStartElement("sz");
            w.WriteAttributeString("val", "11");
            w.WriteEndElement();
            w.WriteStartElement("name");
            w.WriteAttributeString("val", "Calibri");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Fill(XmlWriter w, string pattern)
        {
            w.WriteStartElement("fill");
            w.WriteStartElement("patternFill");
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void Xf(XmlWriter w, int numFmtId, int fontId)
        {
            w.WriteStartElement("xf");
            w.WriteAttributeString("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            w.WriteAttributeString("xfId", "0");
            if (numFmtId != 0)
            {
                w.WriteAttributeString("applyNumberFormat", "1");
            }
            if (fontId != 0)
            {
                w.WriteAttributeString("applyFont", "1");
            }
            w.WriteEndElement();
        }
    }

    private static void WriteSheet(XmlWriter writer, XlsxSheet sheet)
    {
        writer.WriteStartElement("worksheet", MainNs);

        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "1");
        writer.WriteAttributeString("topLeftCell", "A2");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        if (sheet.ColumnWidths.Count > 0)
        {
            writer.WriteStartElement("cols");
            for (var index = 0; index < sheet.ColumnWidths.Count; index++)
            {
                writer.WriteStartElement("col");
                var column = (index + 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteAttributeString("min", column);
                writer.WriteAttributeString("max", column);
                writer.WriteAttributeString("width", sheet.ColumnWidths[index].ToString("0.##", CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, sheet.Header.Cast<object?>().ToArray(), bold: true);
        var rowNumber = 2;
        foreach (var (cells, bold) in sheet.Rows)
        {
            WriteRow(writer, rowNumber++, cells, bold);
        }
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteRow(XmlWriter writer, int rowNumber, IReadOnlyList<object?> cells, bool bold)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < cells.Count; index++)
        {
            if (cells[index] is not { } value)
            {
                continue;
            }

            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", ColumnName(index) + rowNumber.ToString(CultureInfo.InvariantCulture));
            switch (value)
            {
                case string text:
                    WriteStyle(writer, bold ? StyleBold : StyleGeneral);
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is");
                    writer.WriteStartElement("t");
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(Sanitize(text));
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                    break;
                case int or long:
                    WriteStyle(writer, bold ? StyleBold : StyleGeneral);
                    writer.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case decimal or double:
                    WriteStyle(writer, bold ? StyleBoldNumber : StyleNumber);
                    writer.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case DateOnly date:
                    WriteStyle(writer, StyleDate);
                    writer.WriteElementString("v", (date.DayNumber - SerialEpoch.DayNumber).ToString(CultureInfo.InvariantCulture));
                    break;
                case TimeOnly time:
                    WriteStyle(writer, StyleTime);
                    writer.WriteElementString("v", (time.Ticks / (double)TimeSpan.TicksPerDay).ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new NotSupportedException($"Zelltyp {value.GetType().Name} wird nicht unterstützt.");
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        static void WriteStyle(XmlWriter w, int style)
        {
            if (style != StyleGeneral)
            {
                w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>A, B, ..., Z, AA, AB, ... für den nullbasierten Spaltenindex.</summary>
    public static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var rest = index + 1; rest > 0; rest = (rest - 1) / 26)
        {
            name = (char)('A' + (rest - 1) % 26) + name;
        }
        return name;
    }

    /// <summary>Entfernt Zeichen, die XML 1.0 auch maskiert nicht erlaubt (Steuerzeichen, einzelne Surrogate, U+FFFE/U+FFFF).</summary>
    public static string Sanitize(string text)
    {
        StringBuilder? clean = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                clean?.Append(character).Append(text[index + 1]);
                index++;
                continue;
            }

            var valid = character is '\t' or '\n' or '\r'
                || (character >= 0x20 && character <= 0xD7FF)
                || (character >= 0xE000 && character <= 0xFFFD);
            if (valid)
            {
                clean?.Append(character);
            }
            else
            {
                clean ??= new StringBuilder(text, 0, index, text.Length);
            }
        }
        return clean?.ToString() ?? text;
    }
}
