namespace Multiverse.Services;

/// <summary>Authenticated session input. X/Y/Wheel are cumulative totals since Epoch began.
/// Key/button/reset/heartbeat use a reliable ordered channel; move has an independent sequence.
/// Epochs must increase on every capture/reconnection. Reset permanently closes its epoch.</summary>
public sealed class DesktopInputPacket
{
    public string Kind { get; set; } = "";
    public string? Reason { get; set; }
    public long Epoch { get; set; }
    public long Sequence { get; set; }
    public int Code { get; set; }
    public bool Down { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Wheel { get; set; }
    // Reserved diagnostic snapshot: comma-separated decimal VKs, never an injection command.
    public string? HeldKeys { get; set; }
}

public interface IDesktopInputSink
{
    void Key(int virtualKey, bool down);
    // 1=left, 2=right, 3=middle, 4=X1, 5=X2.
    void Button(int code, bool down);
    void Move(int dx, int dy);
    void Wheel(int delta);
}
