using ClosedXML.Excel;
using XlsxCensor;

const int HeaderScanRows = 20;

string[] nameAliases = ["name", "nama"];
string[] descAliases = ["description", "deskripsi", "desc", "keterangan", "remark", "remarks"];

string? input = null, output = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name-col": nameAliases = [args[++i].Trim().ToLowerInvariant()]; break;
        case "--desc-col": descAliases = [args[++i].Trim().ToLowerInvariant()]; break;
        case "-h" or "--help": PrintUsage(); return 0;
        default:
            if (input is null) input = args[i];
            else output = args[i];
            break;
    }
}

if (input is null) { PrintUsage(); return 1; }
if (!File.Exists(input)) { Console.Error.WriteLine($"File tidak ditemukan: {input}"); return 1; }
output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
    Path.GetFileNameWithoutExtension(input) + "_censored.xlsx");

using var wb = new XLWorkbook(input);
int sheetsDone = 0, rowsDone = 0, cellsChanged = 0;

foreach (var ws in wb.Worksheets)
{
    var used = ws.RangeUsed();
    if (used is null) continue;

    var header = FindHeader(ws, used);
    if (header is null)
    {
        Console.WriteLine($"[{ws.Name}] dilewati: header name/description tidak ditemukan");
        continue;
    }
    var (headerRow, nameCol, descCol) = header.Value;

    int lastRow = used.LastRow().RowNumber();
    for (int r = headerRow + 1; r <= lastRow; r++)
    {
        var nameCell = nameCol > 0 ? ws.Cell(r, nameCol) : null;
        var descCell = ws.Cell(r, descCol);

        string name = TextOf(nameCell);
        string desc = TextOf(descCell);
        if (name.Length == 0 && desc.Length == 0) continue;

        var (newName, newDesc) = Censor.CensorPair(name, desc);
        if (nameCell is not null && newName != name) { nameCell.SetValue(newName); cellsChanged++; }
        if (newDesc != desc) { descCell.SetValue(newDesc); cellsChanged++; }
        rowsDone++;
    }

    sheetsDone++;
    Console.WriteLine($"[{ws.Name}] header baris {headerRow}, kolom name={(nameCol > 0 ? nameCol : "-")} description={descCol}");
}

wb.SaveAs(output);
Console.WriteLine($"Selesai: {sheetsDone} sheet, {rowsDone} baris, {cellsChanged} sel disensor -> {output}");
return 0;

(int Row, int NameCol, int DescCol)? FindHeader(IXLWorksheet ws, IXLRange used)
{
    int firstRow = used.FirstRow().RowNumber();
    int lastScan = Math.Min(used.LastRow().RowNumber(), firstRow + HeaderScanRows - 1);
    for (int r = firstRow; r <= lastScan; r++)
    {
        int nameCol = 0, descCol = 0;
        foreach (var cell in ws.Row(r).CellsUsed())
        {
            var h = cell.GetString().Trim().ToLowerInvariant();
            if (nameCol == 0 && nameAliases.Contains(h)) nameCol = cell.Address.ColumnNumber;
            else if (descCol == 0 && descAliases.Contains(h)) descCol = cell.Address.ColumnNumber;
        }
        // Tanpa kolom name, nomor rekening di description tetap disensor.
        if (descCol > 0) return (r, nameCol, descCol);
    }
    return null;
}

// Hanya sel teks yang diproses; formula dan angka dibiarkan.
static string TextOf(IXLCell? cell) =>
    cell is null || cell.HasFormula || cell.DataType != XLDataType.Text ? "" : cell.GetString();

static void PrintUsage() => Console.WriteLine(
    """
    Pemakaian: XlsxCensor <input.xlsx> [output.xlsx] [--name-col <header>] [--desc-col <header>]

      Default output : <input>_censored.xlsx
      Header default : name/nama dan description/deskripsi/keterangan/remark
    """);
