using Verify11683A.Instruments.Visa;

namespace Verify11683A.Tests.Fakes;

/// <summary>
/// A VISA session that records every command and answers queries from a script. A query with a queued
/// reply returns the next queued reply; otherwise the default reply for that query is used.
/// </summary>
public sealed class ScriptedVisaSession : IVisaSession
{
    private readonly Dictionary<string, Queue<string>> _queued = [];
    private readonly Dictionary<string, string> _defaults = [];
    private string? _pendingQuery;

    public List<string> Sent { get; } = [];
    public int Clears { get; private set; }

    public string ResourceName => "GPIB0::22::INSTR";
    public int TimeoutMilliseconds { get; set; } = 20000;

    /// <summary>Always answer <paramref name="query"/> with <paramref name="reply"/> unless a reply is queued.</summary>
    public ScriptedVisaSession Answer(string query, string reply)
    {
        _defaults[query] = reply;
        return this;
    }

    /// <summary>Answer the next <paramref name="query"/> with <paramref name="reply"/>, once.</summary>
    public ScriptedVisaSession AnswerOnce(string query, string reply)
    {
        if (!_queued.TryGetValue(query, out var queue))
            _queued[query] = queue = new Queue<string>();
        queue.Enqueue(reply);
        return this;
    }

    public void Write(string command)
    {
        Sent.Add(command);
        _pendingQuery = command;
    }

    private readonly HashSet<string> _timeOutOnce = [];

    /// <summary>The next <paramref name="query"/> gets no reply: its read times out, once.</summary>
    public ScriptedVisaSession TimeOutOnce(string query)
    {
        _timeOutOnce.Add(query);
        return this;
    }

    public string ReadLine()
    {
        var query = _pendingQuery ?? throw new InvalidOperationException("Read with nothing written.");
        _pendingQuery = null;
        if (_timeOutOnce.Remove(query))
            throw new InstrumentTimeoutException($"{ResourceName} did not respond within {TimeoutMilliseconds / 1000.0:0.#} s while waiting for the reply to '{query}'.");
        if (_queued.TryGetValue(query, out var queue) && queue.Count > 0)
            return queue.Dequeue();
        if (_defaults.TryGetValue(query, out var reply))
            return reply;
        throw new InvalidOperationException($"No scripted reply for '{query}'.");
    }

    public void Clear() => Clears++;

    /// <summary>Status bytes for successive serial polls; 0 once they are used up.</summary>
    public Queue<byte> StatusBytes { get; } = new();

    public int SerialPolls { get; private set; }

    public byte ReadStatusByte()
    {
        SerialPolls++;
        return StatusBytes.Count > 0 ? StatusBytes.Dequeue() : (byte)0;
    }

    public void Dispose() { }

    /// <summary>The commands sent, without the queries.</summary>
    public IEnumerable<string> Commands => Sent.Where(c => !c.EndsWith('?'));
}
