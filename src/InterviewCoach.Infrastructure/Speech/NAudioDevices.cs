using InterviewCoach.Core.Speech;
using NAudio.Wave;

namespace InterviewCoach.Infrastructure.Speech;

/// <summary>Records from the default microphone with NAudio, into an in-memory WAV file.</summary>
public sealed class NAudioRecorder : IAudioRecorder
{
    private static readonly WaveFormat Format = new(16000, 16, 1);

    private WaveInEvent? _wave;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<byte[]>? _stopped;

    public bool IsAvailable => WaveInEvent.DeviceCount > 0;

    public void Start()
    {
        if (!IsAvailable)
            throw new SpeechException("No microphone was found. Plug one in or check Windows Settings, Privacy, Microphone, then try again.");
        if (_wave is not null) return;

        _buffer = new MemoryStream();
        _writer = new WaveFileWriter(_buffer, Format);
        _wave = new WaveInEvent { WaveFormat = Format };
        _wave.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _wave.RecordingStopped += (_, e) =>
        {
            var stopped = _stopped;
            try
            {
                _writer?.Flush();
                var bytes = _buffer?.ToArray() ?? [];
                if (e.Exception is not null) stopped?.TrySetException(new SpeechException("The microphone stopped working: " + e.Exception.Message, e.Exception));
                else stopped?.TrySetResult(bytes);
            }
            finally
            {
                Release();
            }
        };

        try
        {
            _wave.StartRecording();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            Release();
            throw new SpeechException("The microphone could not be opened. Another program may be using it, or access may be turned off in Windows Settings, Privacy, Microphone.", ex);
        }
    }

    public Task<byte[]> StopAsync()
    {
        if (_wave is null) return Task.FromResult<byte[]>([]);
        _stopped = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _wave.StopRecording();
        return _stopped.Task;
    }

    private void Release()
    {
        _wave?.Dispose();
        _writer?.Dispose();
        _wave = null;
        _writer = null;
        _buffer = null;
    }
}

/// <summary>Plays a WAV file through the default speakers with NAudio.</summary>
public sealed class NAudioPlayer : IAudioPlayer
{
    private WaveOutEvent? _output;

    public async Task PlayWavAsync(byte[] wav, CancellationToken ct)
    {
        if (wav.Length == 0 || ct.IsCancellationRequested) return;

        try
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            using var output = new WaveOutEvent();
            _output = output;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) done.TrySetException(e.Exception);
                else done.TrySetResult();
            };
            output.Init(reader);
            using var registration = ct.Register(() => output.Stop());
            output.Play();
            await done.Task;
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or FormatException or InvalidDataException)
        {
            throw new SpeechException("The voice could not be played through the speakers: " + ex.Message, ex);
        }
        finally
        {
            _output = null;
        }
    }

    public void Stop() => _output?.Stop();
}
