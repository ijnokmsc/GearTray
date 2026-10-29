using System.IO;
using System.Text;

namespace GearTray.Host;

/// <summary>
/// 同时写两份目的地。WPF 是 WinExe，控制台输出抓不到管道，
/// 所以诊断结果必须落一份到文件才能被自动化读取。
/// </summary>
internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void Write(string? value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }
}
