using Microsoft.Extensions.AI;

namespace FinanceAssistant.Memory;

public class ConversationStore
{
    private readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AppendSystemMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.System, text));
    }

    public void AppendUserMessage(string text)
    {
        _messages.Add(new ChatMessage(ChatRole.User, text));
    }

    public void AppendResponseMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            _messages.Add(message);
        }
    }

    public void AppendToolResult(AIContent content)
    {
        _messages.Add(new ChatMessage(ChatRole.Tool, [content]));
    }

    public void Clear()
    {
        var systemMessages = _messages.Where(m => m.Role == ChatRole.System).ToList();
        _messages.Clear();
        _messages.AddRange(systemMessages);
    }

    public void Compact(string summary, int keepTailCount)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var systemMessage = _messages[0];
        var tail = _messages
            .Skip(Math.Max(1, _messages.Count - keepTailCount))
            .ToList();

        var summaryMessage = new ChatMessage(
            ChatRole.System,
            $"Conversation summary so far: {summary}");

        _messages.Clear();
        _messages.Add(systemMessage);
        _messages.Add(summaryMessage);
        _messages.AddRange(tail);
    }
}
