using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CsvHelper;

namespace FinanceAssistant.Tools;

public class ImportXmlStatementTool(ImportStatementTool importStatementTool)
{
    [Description(
        "Import a bank statement XML file into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The XML can have any element names — the tool will auto-detect the layout.")]
    public async Task<object> ImportXmlStatement(
        [Description("Absolute path to the XML file on disk. Example: 'C:/Users/me/Downloads/statement.xml'.")]
        string filePath,
        [Description(
            "Skip rows that already exist in the database (matched by Date+Amount+Merchant+Description). Default true.")]
        bool skipDuplicates = true,
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

        XDocument doc;
        try
        {
            await using var stream = File.OpenRead(filePath);
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (Exception ex)
        {
            return new { error = "invalid_xml", hint = ex.Message, path = filePath };
        }

        var root = doc.Root;
        if (root is null)
            return new { error = "invalid_xml", hint = "The file has no root element." };

        var transactions = root.Elements().ToList();
        if (transactions.Count == 0)
            return new { error = "no_transactions", hint = "The root element has no child elements." };

        // Collect XNames (not just LocalName strings) so namespace-qualified elements are matched
        // correctly later. Scan all transactions to pick up optional fields absent from the first row.
        var seenNames = new HashSet<XName>();
        var headerXNames = new List<XName>();
        foreach (var tx in transactions)
        foreach (var el in tx.Elements())
            if (seenNames.Add(el.Name))
                headerXNames.Add(el.Name);

        if (headerXNames.Count == 0)
            return new { error = "no_fields", hint = "Transaction elements have no child elements." };

        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        try
        {
            await using (var sw = new StreamWriter(tempPath, false, Encoding.UTF8))
            await using (var csv = new CsvWriter(sw, CultureInfo.InvariantCulture))
            {
                foreach (var name in headerXNames)
                    csv.WriteField(name.LocalName);
                await csv.NextRecordAsync();

                foreach (var tx in transactions)
                {
                    foreach (var name in headerXNames)
                        csv.WriteField(tx.Element(name)?.Value ?? "");
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
