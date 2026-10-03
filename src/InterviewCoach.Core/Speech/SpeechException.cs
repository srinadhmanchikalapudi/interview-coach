namespace InterviewCoach.Core.Speech;

/// <summary>
/// Something went wrong with speech (no microphone, a rejected key, no network, a voice that would not play). The message is written to be
/// shown to the user as it is. A speech problem never ends a session: the text stays on screen and typing still works.
/// </summary>
public sealed class SpeechException(string message, Exception? inner = null) : Exception(message, inner);
