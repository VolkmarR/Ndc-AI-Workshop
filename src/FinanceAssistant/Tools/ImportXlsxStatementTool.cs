using System.ComponentModel;
using System.Text;
using ClosedXML.Excel;

namespace FinanceAssistant.Tools;

public class ImportXlsxStatementTool
{
    private readonly ImportStatementTool _importStatementTool;

    public ImportXlsxStatementTool(ImportStatementTool importStatementTool)
    {
        _importStatementTool = importStatementTool;
    }

    [Description(
        "Import a bank statement Excel (.xlsx) file into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The spreadsheet can have any column headers — the tool will auto-detect the layout.")]
    public async Task<object> ImportXlsxStatement(
        [Description("Absolute path to the .xlsx file on disk. Example: 'C:/Users/me/Downloads/statement.xlsx'.")] string filePath,
        [Description("Skip rows that already exist in the database (matched by Date+Amount+Merchant+Description). Default true.")] bool skipDuplicates = true,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return new
            {
                error = "file_not_found",
                hint = "Pass an absolute path.",
                path = filePath
            };
        }

        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();
        var usedRange = worksheet.RangeUsed();

        if (usedRange is null)
            return new { error = "empty_sheet", hint = "The first worksheet contains no data." };

        var firstRow = usedRange.FirstRow();
        var lastRow = usedRange.LastRow();
        var columnCount = usedRange.ColumnCount();

        var headers = Enumerable.Range(1, columnCount)
            .Select(c => firstRow.Cell(c).GetValue<string>())
            .ToList();

        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        try
        {
            await using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                await writer.WriteLineAsync(string.Join(",", headers.Select(CsvQuote)));

                for (int r = 2; r <= usedRange.RowCount(); r++)
                {
                    var row = usedRange.Row(r);
                    var values = Enumerable.Range(1, columnCount)
                        .Select(c => CsvQuote(row.Cell(c).GetValue<string>()));
                    await writer.WriteLineAsync(string.Join(",", values));
                }
            }

            return await _importStatementTool.ImportStatement(tempPath, skipDuplicates, ct);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string CsvQuote(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
