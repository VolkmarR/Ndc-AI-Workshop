using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using FinanceAssistant.Data;
using FinanceAssistant.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace FinanceAssistant.Tools;

public class ImportStatementTool
{
    private readonly IChatClient _chatClient;

    public ImportStatementTool(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    [Description(
        "Import a bank statement CSV into the transactions database. " +
        "This tool MODIFIES the database. Only call it when the user explicitly asks to import, load, or upload a statement file. " +
        "The CSV can have any column names and separators — the tool will auto-detect the layout.")]
    public Task<object> ImportStatement(
        [Description("Absolute path to the CSV file on disk. Example: '/Users/me/Downloads/statement.csv'.")]
        string filePath,
        [Description(
            "Skip rows that already exist in the database (matched by Date+Amount+Merchant+Description). Default true.")]
        bool skipDuplicates = true,
        CancellationToken ct = default)
        => InternalImportStatement(filePath, skipDuplicates, null, ct);

    public async Task<object> InternalImportStatement(
        string filePath,
        bool skipDuplicates = true,
        string? displayFilePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            return new
            {
                error = "file_not_found",
                hint = "Pass an absolute path. Tilde (~) is not expanded.",
                path = filePath
            };
        }

        // Phase 1: read raw lines before delimiter is known
        var rawLines = File.ReadLines(filePath).Take(11).ToArray();

        // Phase 2: AI detects delimiter, column names, date format, and decimal separator
        var detected = await DetectColumnMappingsAsync(rawLines, ct);
        bool aiDetected = detected is not null;
        var mappings = detected ?? FallbackMappings();

        // Phase 3: build CsvHelper config using the detected delimiter
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            Delimiter = mappings.Delimiter
        };

        var imported = new List<object>();
        var skipped = new List<object>();
        var errors = new List<object>();

        await using var db = new FinanceDbContext();

        var existing = skipDuplicates
            ? (await db.Transactions
                .Select(t => new { t.Date, t.Amount, t.Merchant, t.Description })
                .ToListAsync(ct))
            .Select(t => HashKey(t.Date, t.Amount, t.Merchant, t.Description))
            .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        var rowNumber = 1;
        while (await csv.ReadAsync())
        {
            rowNumber++;
            try
            {
                // Date
                csv.TryGetField<string>(mappings.DateColumn, out var dateStr);
                if (!DateOnly.TryParseExact(dateStr, mappings.DateFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    if (!DateOnly.TryParseExact(dateStr,
                            ["yyyy-MM-dd", "dd/MM/yyyy", "M/d/yyyy", "d.M.yyyy"],
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    {
                        errors.Add(new { row = rowNumber, message = $"Cannot parse date '{dateStr}'" });
                        continue;
                    }
                }

                // Amount — normalize European decimal separator before parsing
                csv.TryGetField<string>(mappings.AmountColumn, out var amountStr);
                if (mappings.DecimalSeparator == ",")
                    amountStr = amountStr?.Replace(".", "").Replace(",", ".");
                if (!decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                {
                    errors.Add(new { row = rowNumber, message = $"Cannot parse amount '{amountStr}'" });
                    continue;
                }

                // Merchant (required)
                csv.TryGetField<string>(mappings.MerchantColumn, out var merchant);
                if (string.IsNullOrWhiteSpace(merchant))
                {
                    errors.Add(new { row = rowNumber, message = "Empty merchant" });
                    continue;
                }

                // Category and Description (optional — empty string if column absent)
                var category = mappings.CategoryColumn is not null
                               && csv.TryGetField<string>(mappings.CategoryColumn, out var catVal)
                    ? catVal ?? ""
                    : "";
                var description = mappings.DescriptionColumn is not null
                                  && csv.TryGetField<string>(mappings.DescriptionColumn, out var descVal)
                    ? descVal ?? ""
                    : "";

                var key = HashKey(date, amount, merchant, description);

                if (existing.Contains(key))
                {
                    skipped.Add(new { row = rowNumber, reason = "duplicate", date, amount, merchant });
                    continue;
                }

                db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid(),
                    Date = date,
                    Amount = amount,
                    Merchant = merchant,
                    Category = category,
                    Description = description
                });

                existing.Add(key);
                imported.Add(new { row = rowNumber, date, amount, merchant });
            }
            catch (CsvHelperException ex)
            {
                errors.Add(new { row = rowNumber, message = ex.Message });
            }
        }

        await db.SaveChangesAsync(ct);

        return new
        {
            file = displayFilePath ?? filePath,
            importedCount = imported.Count,
            skippedCount = skipped.Count,
            errorCount = errors.Count,
            columnMappings = new
            {
                detectedByAi = aiDetected,
                delimiter = mappings.Delimiter == "\t" ? "\\t" : mappings.Delimiter,
                dateColumn = mappings.DateColumn,
                amountColumn = mappings.AmountColumn,
                merchantColumn = mappings.MerchantColumn,
                categoryColumn = mappings.CategoryColumn,
                descriptionColumn = mappings.DescriptionColumn,
                dateFormat = mappings.DateFormat,
                decimalSeparator = mappings.DecimalSeparator
            },
            skipped = skipped.Take(5).ToList(),
            errors = errors.Take(5).ToList(),
            note = imported.Count > 0
                ? "Imported rows do not have embeddings yet. Restart the app so the embedding pass in Program.cs picks them up, or search will not find them."
                : null
        };
    }

    private async Task<ColumnMappings?> DetectColumnMappingsAsync(string[] rawLines, CancellationToken ct)
    {
        try
        {
            var prompt = $$"""
                           You are a CSV parser. Given the raw first lines of a bank statement file,
                           detect the column separator and identify which column corresponds to each of these fields:
                             date, amount, merchant, category, description.

                           Raw lines (first line is the header):
                           {{string.Join("\n", rawLines)}}

                           Respond with ONLY a JSON object:
                           {
                             "delimiter":         "<the column separator character: comma, semicolon, tab, or pipe>",
                             "dateColumn":        "<exact header name>",
                             "amountColumn":      "<exact header name>",
                             "merchantColumn":    "<exact header name>",
                             "categoryColumn":    "<exact header name or null>",
                             "descriptionColumn": "<exact header name or null>",
                             "dateFormat":        "<.NET date format string>",
                             "decimalSeparator":  "<'.' or ','>"
                           }
                           Rules: use the exact casing of the header name as it appears after splitting by the detected delimiter;
                           use null (not empty string) when a field has no matching column;
                           infer dateFormat from the sample date values; infer decimalSeparator from the amount sample values;
                           for delimiter use the literal character: "," or ";" or a tab character or "|".
                           """;

            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json, MaxOutputTokens = 256 };
            var response = await _chatClient.GetResponseAsync(messages, options, ct);

            var dto = JsonSerializer.Deserialize<ColumnMappingsDto>(response.Text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dto is null) return null;

            // Normalize delimiter
            var delimiter = dto.Delimiter switch
            {
                "," => ",",
                ";" => ";",
                "|" => "|",
                _ when dto.Delimiter?.Contains('\t') == true => "\t",
                _ => null
            };
            if (delimiter is null) return null;

            // Validate required columns exist in the header
            var headers = rawLines.Length > 0 ? rawLines[0].Split(delimiter) : [];
            if (string.IsNullOrEmpty(dto.DateColumn) || !headers.Contains(dto.DateColumn, StringComparer.Ordinal))
                return null;
            if (string.IsNullOrEmpty(dto.AmountColumn) || !headers.Contains(dto.AmountColumn, StringComparer.Ordinal))
                return null;
            if (string.IsNullOrEmpty(dto.MerchantColumn) ||
                !headers.Contains(dto.MerchantColumn, StringComparer.Ordinal))
                return null;
            if (string.IsNullOrEmpty(dto.DateFormat))
                return null;

            // Optional columns: only keep if they actually exist in headers
            var categoryCol = !string.IsNullOrEmpty(dto.CategoryColumn)
                              && headers.Contains(dto.CategoryColumn, StringComparer.Ordinal)
                ? dto.CategoryColumn
                : null;
            var descriptionCol = !string.IsNullOrEmpty(dto.DescriptionColumn)
                                 && headers.Contains(dto.DescriptionColumn, StringComparer.Ordinal)
                ? dto.DescriptionColumn
                : null;

            var decimalSep = dto.DecimalSeparator == "," ? "," : ".";

            return new ColumnMappings(delimiter, dto.DateColumn, dto.AmountColumn, dto.MerchantColumn,
                categoryCol, descriptionCol, dto.DateFormat, decimalSep);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ColumnMappings FallbackMappings() =>
        new(",", "Date", "Amount", "Merchant", "Category", "Description", "yyyy-MM-dd", ".");

    private static string HashKey(DateOnly date, decimal amount, string merchant, string description)
    {
        var raw = $"{date:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{merchant}|{description}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed record ColumnMappings(
        string Delimiter,
        string DateColumn,
        string AmountColumn,
        string MerchantColumn,
        string? CategoryColumn,
        string? DescriptionColumn,
        string DateFormat,
        string DecimalSeparator
    );

    private sealed class ColumnMappingsDto
    {
        public string? Delimiter { get; set; }
        public string? DateColumn { get; set; }
        public string? AmountColumn { get; set; }
        public string? MerchantColumn { get; set; }
        public string? CategoryColumn { get; set; }
        public string? DescriptionColumn { get; set; }
        public string? DateFormat { get; set; }
        public string? DecimalSeparator { get; set; }
    }
}
