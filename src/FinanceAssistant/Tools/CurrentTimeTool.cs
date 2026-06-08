using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class CurrentTimeTool
{
    [Description("Returns the current time in the ISO 8601 format. Use this when the user asks about the current date or time")]
    public string GetCurrentTime() =>  DateTimeOffset.Now.ToString("O");
}
