namespace Verify11683A.Instruments.Visa;

/// <summary>
/// An instrument failed or answered something unexpected: no reply, a VISA error, an error it reported. The message
/// is written for the operator (which instrument, what was sent, what to check); the app shows it without a stack
/// trace and, where it can, offers to retry.
/// </summary>
public class InstrumentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An instrument did not answer, or did not accept a command, within the session's timeout.</summary>
public sealed class InstrumentTimeoutException(string message, Exception? inner = null) : InstrumentException(message, inner);
