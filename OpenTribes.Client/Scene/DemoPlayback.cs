namespace OpenTribes.Client.Scene;

using OpenTribes.Game;

/// <summary>Plays a <c>.rec</c> demo: the records run through the same receive path as the network, in 32 ms client frames.</summary>
/// <remarks>docs/client-net.md 5. Moves are appended to the PSC buffer and packets feed the ghost tables. A listener gets every decoded packet.</remarks>
public sealed class DemoPlayback
{
    /// <summary>The parsed demo file.</summary>
    public DemoFile Demo { get; }
    /// <summary>The client world the packets feed.</summary>
    public ClientWorld World { get; } = new();
    /// <summary>The connection that decodes the packets.</summary>
    public ClientGameConnection Conn { get; }
    /// <summary>Called with every decoded packet.</summary>
    public Action<DecodedPacket>? OnPacket { get; set; }
    /// <summary>Called with the client clock after each frame.</summary>
    public Action<double>? OnFrame { get; set; }
    private int _next;
    /// <summary>The client clock in milliseconds.</summary>
    public double ClockMs { get; private set; }
    /// <summary>The number of packets applied so far.</summary>
    public long PacketsApplied { get; set; }

    /// <summary>Parses a demo and prepares the client world.</summary>
    /// <param name="data">The bytes of the <c>.rec</c> file.</param>
    public DemoPlayback(byte[] data)
    {
        Demo = DemoFile.Parse(data);
        Conn = new ClientGameConnection(World);
        World.PlayerId = (int)Demo.ClientId;
        World.ServerId = (int)Demo.ServerId;
    }

    /// <summary>True when every record has played.</summary>
    public bool Finished => _next >= Demo.Records.Count;

    /// <summary>Advances the client clock in frames of 32 ms.</summary>
    /// <param name="ms">The clock to reach, in milliseconds.</param>
    public void StepTo(double ms)
    {
        while (ClockMs < ms)
        {
            ClockMs += 32;
            while (_next < Demo.Records.Count && Demo.Records[_next].Time <= ClockMs)
            {
                var rec = Demo.Records[_next++];
                switch (rec.Kind)
                {
                    case DemoRecordKind.Packet:
                        World.NetTimeMs = rec.Time;
                        var pkt = Conn.Receive(rec.Packet);
                        ++PacketsApplied;
                        OnPacket?.Invoke(pkt);
                        break;
                    case DemoRecordKind.Move:
                        World.Psc.Moves.Add(rec.Move);
                        break;
                    case DemoRecordKind.Action:
                    case DemoRecordKind.Other:
                    default:
                        break;
                }
            }
            OnFrame?.Invoke(0.032);
        }
    }

    /// <summary>Plays until the always phase is complete (the environment ghosts exist) or the limit passed.</summary>
    /// <param name="limitMs">The limit in milliseconds.</param>
    public void StepUntilEnvironment(double limitMs = 20000)
    {
        while (!World.AlwaysDone && ClockMs < limitMs && !Finished)
            StepTo(ClockMs + 32);
    }
}
