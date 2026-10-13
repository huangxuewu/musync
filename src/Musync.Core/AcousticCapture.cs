namespace Musync.Core;

public sealed record AcousticInputDevice(string Id, string Name);
public interface IAcousticInputProvider
{
    IReadOnlyList<AcousticInputDevice> GetInputs();
    Task<IAcousticCaptureSession> StartAsync(string inputId, TimeSpan maximumDuration, CancellationToken token);
}
public interface IAcousticCaptureSession : IAsyncDisposable
{
    long SamplesCaptured { get; }
    Task<AcousticRecording> CompleteAsync(CancellationToken token);
}
