using System.ComponentModel;

namespace FinanceAssistant.Tools;

public class CurrentTimeTool
{
    [Description("Returns the current time in the ISO 8601 format.")]
    public string GetCurrentTime() =>  DateTimeOffset.Now.ToString("O");
}
