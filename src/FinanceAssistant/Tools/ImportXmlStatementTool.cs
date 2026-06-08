using System.ComponentModel;
using System.Text;
using System.Xml.Linq;

namespace FinanceAssistant.Tools;

public class ImportXmlStatementTool
{
    private readonly ImportStatementTool _importStatementTool;

    public ImportXmlStatementTool(ImportStatementTool importStatementTool)
    {
        _importStatementTool = importStatementTool;
    }

    [Description(
        "Import a bank statement XML file into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The XML can have any element names — the tool will auto-detect the layout.")]
    public async Task<object> ImportXmlStatement(
        [Description("Absolute path to the XML file on disk. Example: 'C:/Users/me/Downloads/statement.xml'.")] string filePath,
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

        var doc = XDocument.Load(filePath);
        var root = doc.Root;
        if (root is null)
            return new { error = "invalid_xml", hint = "The file has no root element." };

        var transactions = root.Elements().ToList();
        if (transactions.Count == 0)
            return new { error = "no_transactions", hint = "The root element has no child elements." };

        var headers = transactions[0].Elements().Select(e => e.Name.LocalName).ToList();
        if (headers.Count == 0)
            return new { error = "no_fields", hint = "The first transaction element has no child elements." };

        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        try
        {
            await using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                await writer.WriteLineAsync(string.Join(",", headers.Select(CsvQuote)));

                foreach (var tx in transactions)
                {
                    var values = headers.Select(h => CsvQuote(tx.Element(h)?.Value ?? ""));
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
