using System.ComponentModel;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;

namespace FinanceAssistant.Tools;

public class ImportXlsxStatementTool(ImportStatementTool importStatementTool)
{
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

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(filePath);
        }
        catch (Exception ex)
        {
            return new { error = "invalid_xlsx", hint = ex.Message, path = filePath };
        }

        using (workbook)
        {
            var worksheet = workbook.Worksheets.FirstOrDefault();
            if (worksheet is null)
                return new { error = "empty_workbook", hint = "The file contains no worksheets." };

            var usedRange = worksheet.RangeUsed();
            if (usedRange is null)
                return new { error = "empty_sheet", hint = "The first worksheet contains no data." };

            var firstRow = usedRange.FirstRow();
            var columnCount = usedRange.ColumnCount();

            var headers = Enumerable.Range(1, columnCount)
                .Select(c => firstRow.Cell(c).GetValue<string>())
                .ToList();

            var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
            try
            {
                await using (var sw = new StreamWriter(tempPath, false, Encoding.UTF8))
                await using (var csv = new CsvWriter(sw, CultureInfo.InvariantCulture))
                {
                    foreach (var header in headers)
                        csv.WriteField(header);
                    await csv.NextRecordAsync();

                    for (int r = 2; r <= usedRange.RowCount(); r++)
                    {
                        var row = usedRange.Row(r);
                        for (int c = 1; c <= columnCount; c++)
                            csv.WriteField(row.Cell(c).GetValue<string>());
                        await csv.NextRecordAsync();
                    }
                }

                return await importStatementTool.InternalImportStatement(tempPath, skipDuplicates, filePath, ct);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }
}
