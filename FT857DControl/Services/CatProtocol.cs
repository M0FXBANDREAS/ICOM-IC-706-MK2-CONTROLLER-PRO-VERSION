using FT857DControl.Models;

namespace FT857DControl.Services;

// Icom CI-V implementation for the IC-706MKIIG (default address 0x58).
public static class CatProtocol
{
    public const byte RadioAddress = 0x58;
    public const byte ControllerAddress = 0xE0;

    private static byte[] Frame(params byte[] body) => [0xFE,0xFE,RadioAddress,ControllerAddress,..body,0xFD];

    public static byte[] SetFrequency(long hz)
    {
        if (hz is < 30_000 or > 470_000_000) throw new ArgumentOutOfRangeException(nameof(hz));
        var d = hz.ToString("D10");
        // CI-V frequency is five packed-BCD bytes, least-significant pair first.
        return Frame(0x05, Pack(d[8],d[9]), Pack(d[6],d[7]), Pack(d[4],d[5]), Pack(d[2],d[3]), Pack(d[0],d[1]));
    }

    public static byte[] SetMode(RadioMode mode) => Frame(0x06, ModeCode(mode), 0x01);
    // Optional filter-width byte documented for command 06: 00 wide, 01 normal, 02 narrow where supported.
    public static byte[] SetModeFilter(RadioMode mode, byte filter)
    {
        if (filter > 2) throw new ArgumentOutOfRangeException(nameof(filter));
        return Frame(0x06, ModeCode(mode), filter);
    }
    public static byte[] SetTuningStep(long hz) => hz switch
    {
        10 => Frame(0x10, 0x00), 100 => Frame(0x10, 0x01), 1000 => Frame(0x10, 0x02),
        5000 => Frame(0x10, 0x03), 9000 => Frame(0x10, 0x04), 10000 => Frame(0x10, 0x05),
        12500 => Frame(0x10, 0x06), 20000 => Frame(0x10, 0x07), 25000 => Frame(0x10, 0x08),
        100000 => Frame(0x10, 0x09), _ => throw new ArgumentOutOfRangeException(nameof(hz))
    };
    public static byte[] ReadFrequency() => Frame(0x03);
    public static byte[] ReadMode() => Frame(0x04);
    public static byte[] SetVfoMode() => Frame(0x07);
    public static byte[] ToggleVfo() => Frame(0x07, 0xB0);
    public static byte[] SetVfoA() => Frame(0x07, 0x00);
    public static byte[] SetVfoB() => Frame(0x07, 0x01);
    public static byte[] EqualizeVfos() => Frame(0x07, 0xA0);
    public static byte[] SetMemoryMode() => Frame(0x08);
    public static byte[] SelectMemory(int channel)
    {
        if (channel is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(channel), "IC-706MKIIG normal memory channel must be 1-99.");
        var d = channel.ToString("D2");
        return Frame(0x08, Pack(d[0], d[1]));
    }
    public static byte[] MemoryWrite() => Frame(0x09);
    public static byte[] MemoryToVfo() => Frame(0x0A);
    public static byte[] MemoryClear() => Frame(0x0B);
    public static byte[] Scan(bool on) => Frame(0x0E, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetSplit(bool on) => Frame(0x0F, on ? (byte)0x01 : (byte)0x00);
    public static byte[] ReadRxStatus() => Frame(0x15, 0x02); // documented S-meter level
    public static byte[] ReadSquelch() => Frame(0x15, 0x01);   // documented squelch condition
    public static byte[] SetAttenuator(bool on) => Frame(0x11, on ? (byte)0x20 : (byte)0x00);
    public static byte[] SetPreamp(bool on) => Frame(0x16, 0x02, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetAgcFast(bool fast) => Frame(0x16, 0x12, fast ? (byte)0x01 : (byte)0x00);
    public static byte[] SetNoiseBlanker(bool on) => Frame(0x16, 0x22, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetTone(bool on) => Frame(0x16, 0x42, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetToneSquelch(bool on) => Frame(0x16, 0x43, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetCompressor(bool on) => Frame(0x16, 0x44, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetVox(bool on) => Frame(0x16, 0x46, on ? (byte)0x01 : (byte)0x00);
    public static byte[] SetBreakIn(bool on) => Frame(0x16, 0x47, on ? (byte)0x01 : (byte)0x00);
    public static byte[] ReadTransceiverId() => Frame(0x19, 0x00);

    public static byte[] SetPtt(bool transmit) => throw new NotSupportedException("IC-706MKIIG manual CI-V command table does not document a PTT command. Use the radio/microphone PTT.");
    public static byte[] SetClarifier(bool on) => throw new NotSupportedException("RIT on/off is not exposed in the IC-706MKIIG CI-V command table.");
    public static byte[] SetClarifierOffset(int offsetHz) => throw new NotSupportedException("RIT offset is not exposed in the IC-706MKIIG CI-V command table.");

    public static byte[] SetRepeaterShift(int direction) => Frame(0x0F, direction < 0 ? (byte)0x11 : direction > 0 ? (byte)0x12 : (byte)0x10);
    public static byte[] SetRepeaterOffset(long offsetHz)
    {
        var khz = Math.Clamp(Math.Abs(offsetHz) / 1000, 0, 9_999_999);
        var d = khz.ToString("D6");
        return Frame(0x0D, Pack(d[4],d[5]), Pack(d[2],d[3]), Pack(d[0],d[1]));
    }
    public static byte[] SetCtcssMode(bool enabled) => Frame(0x16, 0x42, enabled ? (byte)0x01 : (byte)0x00);
    public static byte[] SetCtcssTone(decimal toneHz) => throw new NotSupportedException("Tone-frequency programming is not documented in the IC-706MKIIG CI-V command table. Set the tone frequency on the radio.");

    public static long ParseFrequency(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) throw new FormatException("CI-V frequency response is incomplete.");
        long hz = 0, mul = 1;
        for (int i=0;i<5;i++) { int lo=data[i]&0xF, hi=(data[i]>>4)&0xF; if(lo>9||hi>9) throw new FormatException("Invalid CI-V BCD frequency."); hz += (hi*10L+lo)*mul; mul*=100; }
        return hz;
    }
    public static RadioMode ParseMode(ReadOnlySpan<byte> data) => data.Length == 0 ? RadioMode.FM : data[0] switch { 0x00=>RadioMode.LSB,0x01=>RadioMode.USB,0x02=>RadioMode.AM,0x03=>RadioMode.CW,0x04=>RadioMode.DIG,0x05=>RadioMode.FM,0x06=>RadioMode.WFM,_=>RadioMode.FM };
    public static byte ParseLevel(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return 0;
        int aHi=(data[0]>>4)&0xF, aLo=data[0]&0xF, bHi=(data[1]>>4)&0xF, bLo=data[1]&0xF;
        if (aHi>9 || aLo>9 || bHi>9 || bLo>9) throw new FormatException("Invalid CI-V BCD meter value.");
        int value = aHi*1000 + aLo*100 + bHi*10 + bLo;
        return (byte)Math.Clamp(value, 0, 255);
    }
    public static bool ParseSquelchOpen(ReadOnlySpan<byte> data) => ParseLevel(data) != 0;
    private static byte ModeCode(RadioMode m) => m switch { RadioMode.LSB=>0x00,RadioMode.USB=>0x01,RadioMode.AM=>0x02,RadioMode.CW or RadioMode.CWR=>0x03,RadioMode.DIG=>0x04,RadioMode.FM or RadioMode.PKT=>0x05,RadioMode.WFM=>0x06,_=>0x05 };
    private static byte Pack(char a,char b)=>(byte)(((a-'0')<<4)|(b-'0'));
}
