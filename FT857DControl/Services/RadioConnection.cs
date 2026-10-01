using System.IO;
using System.IO.Ports;
using Microsoft.Win32;
using FT857DControl.Models;

namespace FT857DControl.Services;

public sealed class RadioConnection : IDisposable
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsOpen => _port?.IsOpen == true;
    public string LastTrace { get; private set; } = "";
    public bool IsBusy => _gate.CurrentCount == 0;

    public static string[] AvailablePorts()
    {
        var p = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var x in SerialPort.GetPortNames()) p.Add(x); } catch { }
        try { using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"); if (k != null) foreach (var n in k.GetValueNames()) if (k.GetValue(n) is string x) p.Add(x); } catch { }
        return p.OrderBy(x => x).ToArray();
    }

    public void Open(string name, int baud)
    {
        Close();
        _port = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 850,
            WriteTimeout = 850,
            DtrEnable = false,
            RtsEnable = false
        };
        _port.Open();
        _port.DiscardInBuffer();
        Thread.Sleep(150);
        LastTrace = $"PORT {name} OPEN @ {baud} 8N1";
    }

    public void Close() { if (_port?.IsOpen == true) _port.Close(); _port?.Dispose(); _port = null; }

    public async Task SendAsync(byte[] cmd, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Ensure();
            // IC-706MKIIG control commands are deliberately fire-and-settle here.
            // Some 706/USB-CI-V combinations echo the command but do not deliver a
            // reliable FB/FA acknowledgement for every control operation. Waiting
            // for FB therefore made otherwise-successful VFO/MEM/button operations
            // appear to time out.  Serialize the write, allow the rig to settle,
            // then let the caller's separate read/refresh verify the resulting state.
            _port!.DiscardInBuffer();
            LastTrace = "TX " + Hex(cmd);
            _port.Write(cmd, 0, cmd.Length);
            await Task.Delay(90, ct);
            // Remove local cable echo and any short FB/FA acknowledgement so neither
            // can be mistaken for the following frequency/mode/meter transaction.
            try { _port.DiscardInBuffer(); } catch { }
            LastTrace += " · sent";
        }
        finally { _gate.Release(); }
    }

    public async Task<RadioState> ReadStateAsync(CancellationToken ct = default)
    {
        var f = await Exchange(CatProtocol.ReadFrequency(), 0x03, ct, null, 5);
        var m = await Exchange(CatProtocol.ReadMode(), 0x04, ct, null, 1);
        return new RadioState(CatProtocol.ParseFrequency(f), CatProtocol.ParseMode(m));
    }

    public async Task<byte> ReadRxStatusAsync(CancellationToken ct = default) => CatProtocol.ParseLevel(await Exchange(CatProtocol.ReadRxStatus(), 0x15, ct, 0x02));
    public async Task<bool> ReadSquelchOpenAsync(CancellationToken ct = default) => CatProtocol.ParseSquelchOpen(await Exchange(CatProtocol.ReadSquelch(), 0x15, ct, 0x01));
    public async Task<byte> ReadTransceiverIdAsync(CancellationToken ct = default)
    {
        var d = await Exchange(CatProtocol.ReadTransceiverId(), 0x19, ct, 0x00);
        return d.Length > 0 ? d[0] : (byte)0;
    }
    public Task<byte[]> ReadEepromAsync(ushort a, int n, CancellationToken ct = default) => throw new NotSupportedException("Direct EEPROM access is not used on the IC-706MKIIG controller.");

    private async Task<byte[]> Exchange(byte[] cmd, byte expected, CancellationToken ct, byte? sub = null, int minPayload = 0)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Ensure();
            _port!.DiscardInBuffer();
            LastTrace = "TX " + Hex(cmd);
            _port.Write(cmd, 0, cmd.Length);
            return await Task.Run(() => ReadMatchingFrame(expected, sub, ct, minPayload), ct);
        }
        finally { _gate.Release(); }
    }


    private void ReadAck(CancellationToken ct)
    {
        var seen = new List<string>();
        for (var frameNo = 0; frameNo < 8; frameNo++)
        {
            var frame = ReadOneFrame(ct);
            seen.Add(Hex(frame));
            LastTrace = "RX " + string.Join(" | ", seen);
            if (frame.Length < 6 || frame[0] != 0xFE || frame[1] != 0xFE || frame[^1] != 0xFD) continue;

            // Ignore the normal USB-interface echo (it contains the original command).
            // Accept the radio's short FB/FA response. Some legacy CI-V diagrams print
            // the address bytes in the opposite order, so either address orientation is
            // accepted only for these unambiguous one-byte ACK/NG frames.
            if (frame.Length != 6) continue;
            bool addressesOk =
                (frame[2] == CatProtocol.ControllerAddress && frame[3] == CatProtocol.RadioAddress) ||
                (frame[2] == CatProtocol.RadioAddress && frame[3] == CatProtocol.ControllerAddress);
            if (!addressesOk) continue;
            if (frame[4] == 0xFB) return;
            if (frame[4] == 0xFA) throw new IOException("IC-706MKIIG returned CI-V NG (FA). " + LastTrace);
        }
        throw new TimeoutException("No CI-V OK (FB) reply from IC-706MKIIG. " + LastTrace);
    }

    // CI-V is a single-wire bus. Many USB CI-V cables echo the transmitted frame.
    // Ignore echoed controller->radio frames and keep reading until a genuine
    // radio(58)->controller(E0) response with the requested command arrives.
    private byte[] ReadMatchingFrame(byte expected, byte? sub, CancellationToken ct, int minPayload)
    {
        var seen = new List<string>();
        for (var frameNo = 0; frameNo < 8; frameNo++)
        {
            var frame = ReadOneFrame(ct);
            seen.Add(Hex(frame));
            LastTrace = "RX " + string.Join(" | ", seen);

            if (frame.Length < 6) continue;
            if (frame[0] != 0xFE || frame[1] != 0xFE || frame[^1] != 0xFD) continue;

            // Expected IC-706MKIIG reply: FE FE E0 58 <cmd> ... FD
            if (frame[2] != CatProtocol.ControllerAddress || frame[3] != CatProtocol.RadioAddress)
                continue; // normally the local TX echo: FE FE 58 E0 ... FD

            var i = 4;
            if (frame[i++] == 0xFA) throw new IOException("IC-706MKIIG returned CI-V NG (FA). " + LastTrace);
            if (frame[4] == 0xFB) continue; // ACK for write commands, not a read response
            if (frame[4] != expected) continue;
            i = 5;
            if (sub.HasValue)
            {
                if (i >= frame.Length - 1 || frame[i++] != sub.Value) continue;
            }
            var payload = frame.Skip(i).Take(frame.Length - i - 1).ToArray();
            // A busy CI-V bus or transceive traffic can occasionally leave a short frame.
            // Do not hand an incomplete frequency/mode payload to the parser; keep waiting.
            if (payload.Length < minPayload) continue;
            return payload;
        }
        throw new TimeoutException("No matching IC-706MKIIG CI-V response. " + LastTrace);
    }

    private byte[] ReadOneFrame(CancellationToken ct)
    {
        var b = new List<byte>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var x = (byte)_port!.ReadByte();
            if (b.Count == 0)
            {
                if (x == 0xFE) { b.Add(x); }
                continue;
            }
            if (b.Count == 1)
            {
                if (x == 0xFE) { b.Add(x); }
                else { b.Clear(); }
                continue;
            }
            b.Add(x);
            if (x == 0xFD) return b.ToArray();
            if (b.Count > 96) throw new FormatException("CI-V frame too long.");
        }
    }

    private static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(x => x.ToString("X2")));
    private void Ensure() { if (!IsOpen) throw new InvalidOperationException("The radio is not connected."); }
    public void Dispose() { Close(); _gate.Dispose(); }
}
