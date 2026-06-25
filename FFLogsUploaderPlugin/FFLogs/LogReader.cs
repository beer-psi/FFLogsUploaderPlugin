using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFLogsUploaderPlugin.Extensions;

namespace FFLogsUploaderPlugin.FFLogs;

internal static class LogReader
{
    // Enumerates through chunks of maxLinesPerChunk of the given file at a time.
    // Ignores lines without a trailing newline, since log files all have terminating newlines. This behavior ensures
    // that we don't ingest log lines mid-write.
    public static async IAsyncEnumerable<FileChunk> ReadFileChunkedLinesAsync(
        string filePath, int maxLinesPerChunk = 5000, long startingPosition = 0L)
    {
        var lines = new List<string>(maxLinesPerChunk);
        var lastEndPosition = startingPosition;
        
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
    
    private static async IAsyncEnumerable<(long, string)> ReadFileLinesAsync(string filePath, long startingPosition = 0L)
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
            var startPosition = sr.GetPosition() - sr.CurrentEncoding.GetByteCount(buffer, 0, charsRead);
            
            for (var i = 0; i < charsRead; i++)
            {
                lineBuilder.Append(buffer[i]);

                // Treat LF or CRLF as valid line endings; ACT logs should be CRLF
                if (lineBuilder[^1] == '\n')
                {
                    // Calculate the end position of the current character. The StreamReader will be at the end 
                    // of the chunk we just read, so we have to go back to the start and then append characters 
                    // to get the actual position of this line.
                    var endPosition = startPosition + sr.CurrentEncoding.GetByteCount(buffer, 0, i + 1);

                    // Trim off the line ending, since the parser doesn't like it.
                    if (lineBuilder[^2] == '\r')
                    {
                        lineBuilder.Remove(lineBuilder.Length - 2, 2);
                    }
                    else
                    {
                        lineBuilder.Remove(lineBuilder.Length - 1, 1);
                    }
                    
                    var line = lineBuilder.ToString();
                    lineBuilder.Clear();

                    yield return (endPosition, line);
                }
            }
        }
        
        ArrayPool<char>.Shared.Return(buffer);
        yield return (-1L, string.Empty);
    }
    
    internal class FileChunk
    {
        public required long EndPosition;
        public required bool IsEof;
        public required List<string> Lines;
    }
}
