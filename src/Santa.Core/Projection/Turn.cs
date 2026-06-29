namespace Santa.Core.Projection;

/// <summary>
/// A user turn plus the assistant prose that immediately followed.
/// Tool I/O has already been stripped at construction time.
/// </summary>
public sealed record Turn(
    int Seq,                       // monotonically increasing within a session
    string SessionId,
    DateTimeOffset? UserTimestamp,
    DateTimeOffset? AssistantTimestamp,
    string UserText,
    string AssistantText,
    int ApproxTokens)
{
    public string Render() =>
        AssistantText.Length == 0
            ? $"USER: {UserText}"
            : $"USER: {UserText}\n\nASSISTANT: {AssistantText}";
}
