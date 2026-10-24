using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VCast.Avatar;

// VMC プロトコル（OSC over UDP）受信。VSeeFace / Webcam Motion Capture / iFacialMocap などのトラッカーをそのまま使える
//   /VMC/Ext/Blend/Val  (string 名前, float 値)
//   /VMC/Ext/Blend/Apply
//   /VMC/Ext/Bone/Pos   (string ボーン名, float px py pz qx qy qz qw)
public sealed class VmcReceiver : IDisposable
{
    readonly UdpClient _udp;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, float> _blendPending = new();
    readonly object _lock = new();
    public Dictionary<string, float[]> Bones { get; } = new();
    public event Action<Dictionary<string, float>, Dictionary<string, float[]>>? Frame; // Blend/Apply ごと
    public int Port { get; }
    bool _bonesChanged, _applied;
    public long Packets;

    public bool FromLan { get; }

    // 既定はこの PC 内（loopback）のみ。fromLan=true でスマホなど同じネットワークの機器から受け取れる
    public VmcReceiver(int port = 39539, bool fromLan = false)
    {
        Port = port;
        FromLan = fromLan;
        _udp = new UdpClient(new IPEndPoint(fromLan ? IPAddress.Any : IPAddress.Loopback, port));
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                Interlocked.Increment(ref Packets);
                _bonesChanged = false; _applied = false;
                Parse(r.Buffer, 0, r.Buffer.Length);
                // 表情の Apply が無くても、骨が更新されたパケットなら送る
                if (_bonesChanged && !_applied)
                    lock (_lock) Frame?.Invoke(new Dictionary<string, float>(_blendPending), new Dictionary<string, float[]>(Bones));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Obs.ObsEngine.Log("[vmc] " + ex.Message); }
        }
    }

    void Parse(byte[] b, int off, int len)
    {
        if (len >= 8 && Encoding.ASCII.GetString(b, off, 7) == "#bundle")
        {
            var p = off + 16; // "#bundle\0" + timetag(8)
            while (p + 4 <= off + len)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
                p += 4;
                if (size <= 0 || p + size > off + len) break;
                Parse(b, p, size);
                p += size;
            }
            return;
        }
        var pos = off;
        var address = ReadString(b, ref pos);
        var tags = ReadString(b, ref pos);
        if (!tags.StartsWith(',')) return;
        var args = new List<object>();
        foreach (var t in tags.AsSpan(1))
        {
            switch (t)
            {
                case 's': args.Add(ReadString(b, ref pos)); break;
                case 'f': args.Add(BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(pos))); pos += 4; break;
                case 'i': args.Add(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos))); pos += 4; break;
                default: return; // 未対応の型
            }
        }
        lock (_lock)
        {
            switch (address)
            {
                case "/VMC/Ext/Blend/Val" when args.Count >= 2 && args[0] is string n && args[1] is float v:
                    _blendPending[n] = v; break;
                case "/VMC/Ext/Blend/Apply":
                    _applied = true;
                    Frame?.Invoke(new Dictionary<string, float>(_blendPending), new Dictionary<string, float[]>(Bones));
                    break;
                case "/VMC/Ext/Bone/Pos" when args.Count >= 8 && args[0] is string bone:
                    Bones[bone] = new[] { (float)args[4], (float)args[5], (float)args[6], (float)args[7] };
                    _bonesChanged = true;
                    break;
            }
        }
    }

    static string ReadString(byte[] b, ref int pos)
    {
        var start = pos;
        while (pos < b.Length && b[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(b, start, pos - start);
        pos = (pos + 4) & ~3; // 終端の NUL を含めて 4 バイト境界へ
        return s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
