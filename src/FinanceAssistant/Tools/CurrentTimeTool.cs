using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class CurrentTimeTool
{
    [Description("Returns the current time in the ISO 8601 format. Always call this tool whenever the user " +
                 "asks about the current date or time, even if you already have a date in your context. " +
                 "Never rely on your training data or context for the current date or time.")]
    public string GetCurrentTime() =>  DateTimeOffset.Now.ToString("O");
}
