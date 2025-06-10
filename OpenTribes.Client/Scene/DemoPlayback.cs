// `.rec` playback (docs/client-net.md 5): the records run through the same receive path as the network, in 32 ms client
// frames. Moves are appended to the PSC buffer, packets feed the ghost tables; a listener gets every decoded packet.
using OpenTribes.Game;

namespace OpenTribes.Client.Scene;

public sealed class DemoPlayback
{
    public readonly DemoFile Demo;
    public readonly ClientWorld World = new();
    public readonly ClientGameConnection Conn;
    public Action<DecodedPacket>? OnPacket;
    public Action<double>? OnFrame;
    private int mNext;
    public double ClockMs { get; private set; }
    public long PacketsApplied;

    public DemoPlayback(byte[] data)
    {
        Demo = DemoFile.Parse(data);
        Conn = new ClientGameConnection(World);
        World.PlayerId = (int)Demo.ClientId;
        World.ServerId = (int)Demo.ServerId;
    }

    public bool Finished => mNext >= Demo.Records.Count;

    // Advances the client clock to `ms` in frames of 32 ms.
    public void StepTo(double ms)
    {
        while (ClockMs < ms)
        {
            ClockMs += 32;
            while (mNext < Demo.Records.Count && Demo.Records[mNext].Time <= ClockMs)
            {
                DemoRecord rec = Demo.Records[mNext++];
                switch (rec.Kind)
                {
                    case DemoRecordKind.Packet:
                        World.NetTimeMs = rec.Time;
                        DecodedPacket pkt = Conn.Receive(rec.Packet);
                        ++PacketsApplied;
                        OnPacket?.Invoke(pkt);
                        break;
                    case DemoRecordKind.Move:
                        World.Psc.Moves.Add(rec.Move);
                        break;
                }
            }
            OnFrame?.Invoke(0.032);
        }
    }

    // Plays until the always phase is complete (the environment ghosts exist) or `limitMs` passed.
    public void StepUntilEnvironment(double limitMs = 20000)
    {
        while (!World.AlwaysDone && ClockMs < limitMs && !Finished)
            StepTo(ClockMs + 32);
    }
}
