namespace Musync.Core;

public delegate int AudioSampleReader(Span<float> samples);

// One encoder invocation owns the entire transport timeline, including its
// codec state. The callbacks may run on the encoder's worker thread.
public interface IAudioStreamEncoder
{
    void EncodeMp3(AudioSampleReader read, Action<ReadOnlyMemory<byte>> write, CancellationToken token);
}
