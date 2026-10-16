using System.IO.Compression;
using System.Xml.Linq;

namespace Stempeluhr.Api.Tests;

/// <summary>Reads back an .xlsx with System.Xml.Linq only, as an independent check of the writer.</summary>
internal sealed class XlsxTestReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly Dictionary<string, byte[]> _parts = [];

    public XlsxTestReader(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            _parts[entry.FullName] = copy.ToArray();
        }
    }

    public IReadOnlyCollection<string> PartNames => _parts.Keys;

    public XDocument Part(string name) => XDocument.Load(new MemoryStream(_parts[name]));

    public IReadOnlyList<string> SheetNames
        => Part("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet")
            .Select(sheet => (string)sheet.Attribute("name")!).ToArray();

    public XDocument SheetDocument(string name)
    {
        var sheet = Part("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet")
            .Single(candidate => (string)candidate.Attribute("name")! == name);
        var id = (string)sheet.Attribute(Rel + "id")!;
        var target = Part("xl/_rels/workbook.xml.rels").Root!.Elements(PackageRel + "Relationship")
            .Single(relationship => (string)relationship.Attribute("Id")! == id);
        return Part("xl/" + (string)target.Attribute("Target")!);
    }

    private XElement? CellElement(string sheet, string reference)
        => SheetDocument(sheet).Descendants(Main + "c").FirstOrDefault(cell => (string)cell.Attribute("r")! == reference);

    /// <summary>Inline text or the raw numeric value; null for an empty cell.</summary>
    public string? Cell(string sheet, string reference)
    {
        var cell = CellElement(sheet, reference);
        if (cell is null)
        {
            return null;
        }
        return (string?)cell.Element(Main + "is")?.Element(Main + "t") ?? (string?)cell.Element(Main + "v");
    }

    public int Style(string sheet, string reference) => (int?)CellElement(sheet, reference)?.Attribute("s") ?? 0;

    public int RowCount(string sheet) => SheetDocument(sheet).Descendants(Main + "row").Count();

    /// <summary>The number format code of a cell's style.</summary>
    public string? FormatCode(string sheet, string reference)
    {
        var styles = Part("xl/styles.xml").Root!;
        var xf = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").ElementAt(Style(sheet, reference));
        var numFmtId = (int)xf.Attribute("numFmtId")!;
        return numFmtId == 2
            ? "0.00"
            : styles.Element(Main + "numFmts")?.Elements(Main + "numFmt")
                .FirstOrDefault(format => (int)format.Attribute("numFmtId")! == numFmtId)?.Attribute("formatCode")?.Value;
    }

    public bool IsBold(string sheet, string reference)
    {
        var styles = Part("xl/styles.xml").Root!;
        var xf = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").ElementAt(Style(sheet, reference));
        var font = styles.Element(Main + "fonts")!.Elements(Main + "font").ElementAt((int)xf.Attribute("fontId")!);
        return font.Element(Main + "b") is not null;
    }
}
