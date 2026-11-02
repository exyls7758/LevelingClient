using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Multiverse.Services;

internal static class ControlHardeningTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    internal static async Task RunAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\rfour");
        using (var stream = new MemoryStream(payload))
        using (var bounded = new BoundedLineReader(
            new StreamReader(stream, Encoding.UTF8, false, 16, leaveOpen: false), 16, 3))
        {
            Check(await bounded.ReadLineAsync(CancellationToken.None) == "one", "CRLF line");
            Check(await bounded.ReadLineAsync(CancellationToken.None) == "two", "LF line");
            Check(await bounded.ReadLineAsync(CancellationToken.None) == "three", "CR line");
            Check(await bounded.ReadLineAsync(CancellationToken.None) == "four", "EOF line");
            Check(await bounded.ReadLineAsync(CancellationToken.None) is null, "EOF returns null");
        }

        byte[] oversized = Encoding.UTF8.GetBytes(new string('x', 17) + "\n");
        using (var stream = new MemoryStream(oversized))
        using (var bounded = new BoundedLineReader(
            new StreamReader(stream, Encoding.UTF8, false, 16, leaveOpen: false), 16, 4))
        {
            bool rejected = false;
            try { _ = await bounded.ReadLineAsync(CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "oversized control line is rejected");
        }

        var critical = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        Check(critical.Writer.TryWrite(1) && critical.Writer.TryWrite(2), "bounded critical queue accepts capacity");
        Check(!critical.Writer.TryWrite(3), "bounded critical queue fails fast when saturated");

        Console.WriteLine("control hardening regression: PASS");
    }
}
