using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Stratagem.Server;

/// <summary>An IPv4 address of this PC, with the network it belongs to.</summary>
public sealed record LocalAddress(int InterfaceIndex, IPAddress Address, int PrefixLength)
{
    public bool SameNetwork(IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork) return false;
        var mask = PrefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(PrefixLength, 32));
        return (ToUInt(Address) & mask) == (ToUInt(other) & mask);
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>IPv4 addresses of all network adapters that are up (no loopback).</summary>
    public static IReadOnlyList<LocalAddress> All()
    {
        var result = new List<LocalAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            int index;
            try
            {
                props = nic.GetIPProperties();
                index = props.GetIPv4Properties()?.Index ?? -1;
            }
            catch (Exception) { continue; }
            foreach (var a in props.UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    result.Add(new LocalAddress(index, a.Address, a.PrefixLength));
        }
        return result;
    }
}

/// <summary>
/// Answers multicast DNS (RFC 6762) for one name, e.g. "hellwheel.local", so the phone can open
/// http://hellwheel.local instead of an IP address that changes. Only A records (IPv4); questions for
/// IPv6 get a "no such record" (NSEC) so phones do not wait for it.
/// Cost: one UDP socket; mDNS traffic in the home network is a few small packets per second at most.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    public const int Port = 5353;
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const uint Ttl = 120;

    private readonly Func<string> _name;
    private readonly Func<IReadOnlyList<LocalAddress>> _addresses;
    private readonly Action<string> _log;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _joined = [];

    /// <param name="name">Host name without ".local", read on every question (renaming works at once).</param>
    public MdnsResponder(Func<string> name, Action<string> log, Func<IReadOnlyList<LocalAddress>>? addresses = null)
    {
        _name = name;
        _log = log;
        _addresses = addresses ?? LocalAddress.All;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows' own mDNS service (and e.g. Chrome) uses the same port: share it.
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows()) _socket.ExclusiveAddressUse = false;
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        JoinGroups();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _ = Task.Run(ReceiveLoop);
        Announce();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        JoinGroups();
        Announce();
    }

    private void JoinGroups()
    {
        foreach (var a in _addresses())
        {
            lock (_joined)
                if (!_joined.Add(a.Address.ToString())) continue;
            try { _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, a.Address)); }
            catch (Exception e) { _log($"mDNS: Netzwerk {a.Address} nicht nutzbar ({e.Message})"); }
        }
    }

    // Tells the network the name right away (caches of phones that asked before get the new address).
    private void Announce()
    {
        try
        {
            foreach (var group in _addresses().GroupBy(a => a.InterfaceIndex))
            {
                var answer = BuildAnswer(_name(), group.Select(a => a.Address).ToList(), id: 0, question: null, cacheFlush: true);
                SendMulticast(answer, group.Key);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult r;
            try { r = await _socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, _stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable from an earlier reply

            try
            {
                var from = (IPEndPoint)r.RemoteEndPoint;
                var reply = Respond(buffer.AsSpan(0, r.ReceivedBytes), _name(), from, r.PacketInformation.Interface, _addresses());
                if (reply is null) continue;
                if (reply.Value.Unicast) await _socket.SendToAsync(reply.Value.Message, from);
                else SendMulticast(reply.Value.Message, r.PacketInformation.Interface);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }
    }

    private void SendMulticast(byte[] message, int interfaceIndex)
    {
        lock (_socket) // the interface choice and the send belong together
        {
            if (interfaceIndex > 0)
            {
                try { _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(interfaceIndex)); }
                catch (SocketException) { /* default adapter */ }
            }
            _socket.SendTo(message, new IPEndPoint(Group, Port));
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _stop.Cancel();
        _socket.Dispose();
    }

    // --- protocol (pure, tested) ------------------------------------------------------------------

    public readonly record struct Reply(byte[] Message, bool Unicast);

    private const ushort TypeA = 1, TypeAaaa = 28, TypeNsec = 47, TypeAny = 255, ClassIn = 1;

    /// <summary>The answer to an mDNS packet, or null if it does not ask for <paramref name="name"/>.local.</summary>
    public static Reply? Respond(ReadOnlySpan<byte> packet, string name, IPEndPoint from, int interfaceIndex, IReadOnlyList<LocalAddress> addresses)
    {
        if (packet.Length < 12 || string.IsNullOrEmpty(name)) return null;
        var flags = Read16(packet, 2);
        if ((flags & 0x8000) != 0 || (flags & 0x7800) != 0) return null; // a response, or not a standard query
        var questions = Read16(packet, 4);
        var target = name + ".local";
        var offset = 12;
        var wantA = false;
        var wantAaaa = false;
        var unicast = false;
        for (var q = 0; q < questions; q++)
        {
            if (!TryReadName(packet, ref offset, out var qname) || offset + 4 > packet.Length) return null;
            var type = Read16(packet, offset);
            var cls = Read16(packet, offset + 2);
            offset += 4;
            if ((cls & 0x7FFF) != ClassIn || !string.Equals(qname, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (type is TypeA or TypeAny) wantA = true;
            if (type is TypeAaaa) wantAaaa = true;
            if ((cls & 0x8000) != 0) unicast = true; // "QU": the asker wants a direct answer
        }
        if (!wantA && !wantAaaa) return null;

        // Answer with the address in the asker's network (a PC can have VPN, Hyper-V, … adapters).
        var mine = addresses.Where(a => a.InterfaceIndex == interfaceIndex && a.SameNetwork(from.Address)).ToList();
        if (mine.Count == 0) mine = addresses.Where(a => a.SameNetwork(from.Address)).ToList();
        if (mine.Count == 0) mine = addresses.Where(a => a.InterfaceIndex == interfaceIndex).ToList();
        if (mine.Count == 0) mine = addresses.ToList();
        if (mine.Count == 0) return null;

        // A "legacy" asker (not from port 5353) expects a normal DNS answer: same id, question repeated.
        var legacy = from.Port != Port;
        var id = legacy ? Read16(packet, 0) : (ushort)0;
        var message = BuildAnswer(name, wantA ? mine.Select(a => a.Address).ToList() : [], id,
            legacy ? (target, wantA ? TypeA : TypeAaaa) : null, cacheFlush: !legacy, nsec: wantAaaa || !wantA);
        return new Reply(message, Unicast: legacy || unicast);
    }

    private static byte[] BuildAnswer(string name, IReadOnlyList<IPAddress> addresses, ushort id, (string Name, ushort Type)? question, bool cacheFlush, bool nsec = false)
    {
        var host = EncodeName(name + ".local");
        var ms = new MemoryStream();
        Write16(ms, id);
        Write16(ms, 0x8400); // response, authoritative
        Write16(ms, (ushort)(question is null ? 0 : 1));
        Write16(ms, (ushort)(addresses.Count + (nsec ? 1 : 0)));
        Write16(ms, 0);
        Write16(ms, 0);
        if (question is { } q)
        {
            ms.Write(EncodeName(q.Name));
            Write16(ms, q.Type);
            Write16(ms, ClassIn);
        }
        var cls = (ushort)(ClassIn | (cacheFlush ? 0x8000 : 0));
        foreach (var a in addresses)
        {
            ms.Write(host);
            Write16(ms, TypeA);
            Write16(ms, cls);
            Write32(ms, Ttl);
            Write16(ms, 4);
            ms.Write(a.GetAddressBytes());
        }
        if (nsec)
        {
            // "This name only has an A record" (RFC 6762 §6.1).
            ms.Write(host);
            Write16(ms, TypeNsec);
            Write16(ms, cls);
            Write32(ms, Ttl);
            Write16(ms, (ushort)(host.Length + 3));
            ms.Write(host);
            ms.Write([0, 1, 0x40]); // window 0, 1 byte, bit for type 1 (A)
        }
        return ms.ToArray();
    }

    private static byte[] EncodeName(string name)
    {
        var ms = new MemoryStream();
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)Math.Min(bytes.Length, 63));
            ms.Write(bytes, 0, Math.Min(bytes.Length, 63));
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    /// <summary>Reads a (possibly compressed) DNS name; <paramref name="offset"/> moves past it.</summary>
    public static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        var sb = new StringBuilder();
        var pos = offset;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            if (pos >= packet.Length) break;
            int len = packet[pos];
            if (len == 0)
            {
                if (!jumped) offset = pos + 1;
                name = sb.ToString();
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= packet.Length) break;
                if (!jumped) offset = pos + 2;
                jumped = true;
                pos = (len & 0x3F) << 8 | packet[pos + 1];
                continue;
            }
            if ((len & 0xC0) != 0 || pos + 1 + len > packet.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(packet.Slice(pos + 1, len)));
            pos += 1 + len;
        }
        name = "";
        return false;
    }

    /// <summary>A query packet (for tests and the self-check).</summary>
    public static byte[] Query(string fqdn, ushort type = TypeA, ushort id = 0, bool unicastResponse = false)
    {
        var ms = new MemoryStream();
        Write16(ms, id);
        Write16(ms, 0);
        Write16(ms, 1);
        Write16(ms, 0);
        Write16(ms, 0);
        Write16(ms, 0);
        ms.Write(EncodeName(fqdn));
        Write16(ms, type);
        Write16(ms, (ushort)(ClassIn | (unicastResponse ? 0x8000 : 0)));
        return ms.ToArray();
    }

    /// <summary>The IPv4 addresses in the answers of a response packet.</summary>
    public static IReadOnlyList<IPAddress> ParseAnswers(ReadOnlySpan<byte> packet, string fqdn)
    {
        var result = new List<IPAddress>();
        if (packet.Length < 12) return result;
        var qd = Read16(packet, 4);
        var an = Read16(packet, 6);
        var offset = 12;
        for (var i = 0; i < qd; i++)
        {
            if (!TryReadName(packet, ref offset, out _)) return result;
            offset += 4;
        }
        for (var i = 0; i < an; i++)
        {
            if (!TryReadName(packet, ref offset, out var n) || offset + 10 > packet.Length) return result;
            var type = Read16(packet, offset);
            var len = Read16(packet, offset + 8);
            offset += 10;
            if (offset + len > packet.Length) return result;
            if (type == TypeA && len == 4 && string.Equals(n, fqdn, StringComparison.OrdinalIgnoreCase))
                result.Add(new IPAddress(packet.Slice(offset, 4)));
            offset += len;
        }
        return result;
    }

    private static ushort Read16(ReadOnlySpan<byte> p, int i) => (ushort)(p[i] << 8 | p[i + 1]);

    private static void Write16(Stream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void Write32(Stream s, uint v)
    {
        Write16(s, (ushort)(v >> 16));
        Write16(s, (ushort)v);
    }
}
