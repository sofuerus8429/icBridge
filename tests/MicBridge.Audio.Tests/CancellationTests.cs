using MicBridge.Audio.Compatibility;
using MicBridge.Audio.Interop;

namespace MicBridge.Audio.Tests;

public sealed class CancellationTests
{
    [Fact]
    public async Task CancellingAnActivationWaitLeavesTheNativeCompletionAlive()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<int> waiting = Platform.WithTimeout(completion.Task, TimeSpan.FromSeconds(10), cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(completion.Task.IsCompleted);
        completion.SetResult(27); Assert.Equal(27, await completion.Task);
    }
    [Fact]
    public async Task ActivationWaitKeepsSuccessfulResultsAndNativeErrors()
    {
        Assert.Equal(17, await Platform.WithTimeout(Task.FromResult(17), TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Platform.WithTimeout(Task.FromException<int>(new InvalidOperationException()), TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<TimeoutException>(() => Platform.WithTimeout(new TaskCompletionSource<int>().Task, TimeSpan.FromMilliseconds(20)));
    }
    [Fact]
    public async Task CancelledStartupDoesNotOpenAnInvalidEndpoint()
    {
        using var engine = new AudioEngine(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(new("never-open-this-device"), cancellation.Token));
        Assert.False(engine.IsRunning); Assert.False(engine.IsMicrophoneCapturing);
    }
    [Fact]
    public async Task ProcessCaptureCannotStartAfterDisposalOrBeforeCancelledStartup()
    {
        if (!ProcessLoopbackCapture.IsSupported) return;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var capture = new ProcessLoopbackCapture(123456789);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StartAsync(cancellation.Token));
        capture.Dispose(); capture.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.StartAsync());
    }
}
