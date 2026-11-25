namespace PowerOfChaos.Launcher.Tests;

internal sealed class CancelDuringDirectoryRead(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
{
    public int ReadCalls { get; private set; }

    public override int Read(Span<byte> buffer)
    {
        var length = base.Read(buffer);
        // The first span is the header. Cancel after the first directory row is read.
        if (++ReadCalls == 2) cancellation.Cancel();
        return length;
    }
}
