using System.IO.Pipes;
using System.Text;
using TalosForge.Core;
using TalosForge.Core.Warden;
using Xunit;

namespace TalosForge.Tests.Smoke;

/// <summary>
/// End-to-end smoke tests for the injection pipeline.
/// Auto-skip when WoW is not running or the agent is not injected.
/// Run after injection: dotnet test --filter FullyQualifiedName~InjectionPipeline
/// </summary>
[Trait(LiveSmokePreconditions.TraitName, LiveSmokePreconditions.TraitValue)]
public sealed class InjectionPipelineSmokeTests
{
    [LiveInjectedFact]
    public void Agent_Discovery_File_Exists()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        var path = Path.Combine(Path.GetTempPath(), $"TalosForge.pipe.{wow.Id}");
        Assert.True(File.Exists(path), $"Expected discovery file at '{path}'.");

        var content = File.ReadAllText(path).Trim();
        Assert.False(string.IsNullOrWhiteSpace(content), "Discovery file is empty");
        Assert.Contains("WinSvc_", content);
    }

    [LiveInjectedFact]
    public void Agent_Pipe_Accepts_Connection()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        var pipeName = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(3000);

        Assert.True(client.IsConnected);
    }

    [LiveInjectedFact]
    public void Agent_Responds_To_Heartbeat()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        var pipeName = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(3000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);

        writer.WriteLine("Heartbeat");
        writer.WriteLine("{}");
        writer.WriteLine("3000");

        var success = reader.ReadLine();
        var code = reader.ReadLine();
        var message = reader.ReadLine();
        var _ = reader.ReadLine();

        Assert.NotNull(success);
        Assert.Equal("1", success!.Trim());
    }

    [LiveInjectedFact]
    public void Agent_ReadBytes_Returns_Valid_Data()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        var pipeName = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(3000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);

        writer.WriteLine("ReadBytes");
        writer.WriteLine("{\"address\":\"0x00400000\",\"size\":2}");
        writer.WriteLine("3000");

        var success = reader.ReadLine();
        var code = reader.ReadLine();
        var message = reader.ReadLine();
        var _ = reader.ReadLine();

        Assert.NotNull(success);
        Assert.Equal("1", success!.Trim());
        Assert.NotNull(message);
        Assert.Equal("4D5A", message!.Trim());
    }

    [LiveInjectedFact]
    public void InternalMemoryReader_Attaches_Successfully()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        _ = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var internalReader = new InternalMemoryReader();
        var attached = internalReader.Attach();

        Assert.True(attached);
        Assert.True(internalReader.IsAttached);
        Assert.NotEqual(IntPtr.Zero, internalReader.BaseAddress);
    }

    [LiveInjectedFact]
    public void InternalMemoryReader_Can_Read_MZ_Header()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        _ = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var internalReader = new InternalMemoryReader();
        var attached = internalReader.Attach();
        Assert.True(attached);

        var mz = internalReader.Read<ushort>(new IntPtr(0x00400000));
        Assert.Equal(0x5A4D, mz);
    }

    [LiveInjectedFact]
    public void WardenMonitor_Works_With_InternalReader()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        _ = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var internalReader = new InternalMemoryReader();
        var attached = internalReader.Attach();
        Assert.True(attached);

        var monitor = new WardenMonitor(internalReader, MemoryReaderMode.Internal);
        var snapshot = monitor.TakeSnapshot();

        Assert.NotEqual(WardenState.Unknown, snapshot.State);
        Assert.Equal(MemoryReaderMode.Internal, snapshot.ReaderMode);
        Assert.True(snapshot.AgentPipeAlive);
    }

    [LiveInjectedFact]
    public void Agent_WalkObjects_Returns_Json()
    {
        var wow = LiveSmokePreconditions.RequireWowProcess();
        var pipeName = LiveSmokePreconditions.RequireAgentPipeName(wow.Id);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(3000);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);

        writer.WriteLine("WalkObjects");
        writer.WriteLine("{}");
        writer.WriteLine("5000");

        var success = reader.ReadLine();
        var code = reader.ReadLine();
        var message = reader.ReadLine();
        var _ = reader.ReadLine();

        Assert.NotNull(success);
        Assert.Equal("1", success!.Trim());
        Assert.NotNull(message);
        Assert.Contains("objects", message!, StringComparison.OrdinalIgnoreCase);
    }
}
