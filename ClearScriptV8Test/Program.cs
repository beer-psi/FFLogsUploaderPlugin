// See https://aka.ms/new-console-template for more information

using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

// Console.WriteLine(Directory.EnumerateFiles("/home/beerpsi/Documents/IINACT", "Network_*.log", SearchOption.TopDirectoryOnly)
//                            .OrderByDescending(File.GetLastWriteTimeUtc)
//                            .FirstOrDefault());

await foreach (var chunk in ReadFileByChunkedLinesAsync("/home/beerpsi/Documents/IINACT/Network_30203_20260624.log"))
{
    foreach (var line in chunk.Lines)
    {
        Console.WriteLine(line);
    }
}

return;

async IAsyncEnumerable<(long, string)> ReadFileLinesAsync(string filePath, long startingPosition = 0L)
    {
        // Have to create a raw filestream for seeking first since StreamReader is horrendously unusable for seeking
        // and determining stream position
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fs.CanSeek && startingPosition != 0L)
            fs.Seek(startingPosition, SeekOrigin.Begin);
        
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        var lineBuilder = new StringBuilder();
        int charsRead;

        while ((charsRead = await sr.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < charsRead; i++)
            {
                lineBuilder.Append(buffer[i]);

                if (buffer[i] == '\n')
                {
                    var endPosition = sr.GetPosition()
                                 - sr.CurrentEncoding.GetByteCount(buffer, 0, charsRead)
                                 + sr.CurrentEncoding.GetByteCount(buffer, 0, i + 1);
                    var line = lineBuilder.ToString();
                    lineBuilder.Clear();

                    yield return (endPosition, line);
                }
            }
        }

        yield return (-1L, string.Empty);
    }

    // Enumerates through chunks of maxLinesPerChunk of the given file at a time.
    async IAsyncEnumerable<FileChunk> ReadFileByChunkedLinesAsync(
        string filePath, int maxLinesPerChunk = 5000, long startingPosition = 0L)
    {
        var lines = new List<string>(maxLinesPerChunk);
        var lastEndPosition = -1L;
        
        await foreach (var (endPosition, line) in ReadFileLinesAsync(filePath, startingPosition))
        {
            var isEof = endPosition == -1L;

            if (!isEof)
            {
                lines.Add(line);
                lastEndPosition = endPosition;
            }

            if (lines.Count >= maxLinesPerChunk || isEof)
            {
                yield return new FileChunk
                {
                    EndPosition = lastEndPosition,
                    IsEof = isEof,
                    Lines = lines,
                };

                if (!isEof)
                    lines = new List<string>(maxLinesPerChunk);
            }
        }
    }

class FileChunk
{
    public required long EndPosition;
    public required bool IsEof;
    public required List<string> Lines;
}

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

    public static async Task<string?> ReadLineIgnoreEofAsync(this StreamReader sr, CancellationToken token = default)
    {
        var charPos = (int)CharPosField.GetValue(sr)!;
        var charLen = (int)CharLenField.GetValue(sr)!;
        
        if (charPos == charLen && (await ((ValueTask<int>)ReadBufferAsyncMethod.Invoke(sr, [token])!).ConfigureAwait(false)) == 0)
        {
            return null;
        }

        string retVal;
        char[]? arrayPoolBuffer = null;
        int arrayPoolBufferPos = 0;

        do
        {
            char[] charBuffer = (char[])CharBufferField.GetValue(sr)!;
            charLen = (int)CharPosField.GetValue(sr)!;
            charPos = (int)CharPosField.GetValue(sr)!;

            // Look for '\r' or \'n'.
            Debug.Assert(charPos < charLen, "ReadBuffer returned > 0 but didn't bump _charLen?");

            int idxOfNewline = charBuffer.AsSpan(charPos, charLen - charPos).IndexOfAny('\r', '\n');
            if (idxOfNewline >= 0)
            {
                if (arrayPoolBuffer is null)
                {
                    retVal = new string(charBuffer, charPos, idxOfNewline);
                }
                else
                {
                    retVal = string.Concat(arrayPoolBuffer.AsSpan(0, arrayPoolBufferPos), charBuffer.AsSpan(charPos, idxOfNewline));
                    ArrayPool<char>.Shared.Return(arrayPoolBuffer);
                }

                charPos += idxOfNewline;
                char matchedChar = charBuffer[charPos++];
                CharPosField.SetValue(sr, charPos);

                // If we found '\r', consume any immediately following '\n'.
                if (matchedChar == '\r')
                {
                    if (charPos < charLen || (await ((ValueTask<int>)ReadBufferAsyncMethod.Invoke(sr, [token])!).ConfigureAwait(false)) > 0)
                    {
                        if (((char[])CharBufferField.GetValue(sr)!)[(int)CharPosField.GetValue(sr)!] == '\n')
                        {
                            CharPosField.SetValue(sr, (int)CharPosField.GetValue(sr)! + 1);
                        }
                    }
                }

                return retVal;
            }

            // We didn't find '\r' or '\n'. Add the read data to the pooled buffer
            // and loop until we reach a newline or EOF.
            if (arrayPoolBuffer is null)
            {
                arrayPoolBuffer = ArrayPool<char>.Shared.Rent(charLen - charPos + 80);
            }
            else if ((arrayPoolBuffer.Length - arrayPoolBufferPos) < (charLen - charPos))
            {
                char[] newBuffer = ArrayPool<char>.Shared.Rent(checked(arrayPoolBufferPos + charLen - charPos));
                arrayPoolBuffer.AsSpan(0, arrayPoolBufferPos).CopyTo(newBuffer);
                ArrayPool<char>.Shared.Return(arrayPoolBuffer);
                arrayPoolBuffer = newBuffer;
            }
            charBuffer.AsSpan(charPos, charLen - charPos).CopyTo(arrayPoolBuffer.AsSpan(arrayPoolBufferPos));
            arrayPoolBufferPos += charLen - charPos;
        }
        while ((await ((ValueTask<int>)ReadBufferAsyncMethod.Invoke(sr, [token])!).ConfigureAwait(false)) > 0);

        ArrayPool<char>.Shared.Return(arrayPoolBuffer);

        return null;
    }
}

