using System.IO;
using System.Reflection;

namespace FFLogsUploaderPlugin.Extensions;

public static class StreamReaderExtensions
{
    private static readonly FieldInfo CharPosField =
        typeof(StreamReader).GetField(
            "_charPos", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    private static readonly FieldInfo CharLenField =
        typeof(StreamReader).GetField(
            "_charLen", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    private static readonly FieldInfo CharBufferField =
        typeof(StreamReader).GetField("_charBuffer",
                                      BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    private static readonly MethodInfo ReadBufferAsyncMethod =
        typeof(StreamReader).GetMethod("ReadBufferAsync",
                                       BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    public static long GetPosition(this StreamReader sr)
    {
        var charBuffer = (char[])CharBufferField.GetValue(sr)!;
        var charLen = (int)CharLenField.GetValue(sr)!;
        var charPos = (int)CharPosField.GetValue(sr)!;
        
        return sr.BaseStream.Position - sr.CurrentEncoding.GetByteCount(charBuffer, charPos, charLen - charPos);
    }
}
