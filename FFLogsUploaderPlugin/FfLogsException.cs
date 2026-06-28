using System;

namespace FFLogsUploaderPlugin;

public class FfLogsException(string message) : Exception(message);
public class SplitLogException(string message) : FfLogsException(message);
