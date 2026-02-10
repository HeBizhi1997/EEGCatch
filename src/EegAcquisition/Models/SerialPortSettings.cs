using System.IO.Ports;

namespace EegAcquisition.Models;

public sealed class SerialPortSettings
{
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
}
