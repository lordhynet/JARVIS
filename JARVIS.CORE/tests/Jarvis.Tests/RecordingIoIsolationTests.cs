using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class RecordingIoIsolationTests
{
    [Fact]
    public async Task Slow_recording_file_io_does_not_hold_the_memory_runtime_mutex_and_revocation_rejects_and_cleans_the_staged_blob()
    {
        var rig = new RecordingRig(); rig.Files.BeforeScan = () => Assert.False(Monitor.IsEntered(rig.Boundary.Sync));
        var recording = await rig.Prepare(); using var actor = rig.Actor(); await rig.Start(recording.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Files.BeforeWrite = async ct => { Assert.False(Monitor.IsEntered(rig.Boundary.Sync)); entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var upload = rig.Chunk(recording.Id, 0, RecordingRig.Wav()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try {
            await rig.Runtime.UpdateAsync(s => { s.DeviceCredentials[RecordingRig.Device] = s.DeviceCredentials[RecordingRig.Device] with { Revoked = true }; return true; }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(upload.IsCompleted);
        } finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DomainException>(() => upload); Assert.Empty(rig.Files.Blobs); Assert.Empty(await rig.Store.ReadAsync(s => s.Chunks.Values.ToArray()));
    }
}
