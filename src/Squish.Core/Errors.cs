namespace Squish.Core;

public enum ErrorCode
{
    FileNotFound,
    EmptyFile,
    Unsupported,
    Corrupt,
    NoDuration,
    AudioOnly,
    DrmProtected,
    TargetUnreachable,
    OutputNotWritable,
    DiskFull,
    EncoderFailed,
    ToolMissing,
    Cancelled,
    Unexpected,
}

/// <summary>Every failure the UI can show. Message is plain language: what happened and what to do next.</summary>
public sealed class SquishException : Exception
{
    public ErrorCode Code { get; }
    public string? Detail { get; }

    public SquishException(ErrorCode code, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Detail = detail;
    }

    public static SquishException From(Exception ex) => ex switch
    {
        SquishException s => s,
        OperationCanceledException => new(ErrorCode.Cancelled, "Cancelled."),
        UnauthorizedAccessException u => new(ErrorCode.OutputNotWritable,
            "Can't write to the output folder. Pick a different folder in settings.", u.Message, u),
        IOException io when IsDiskFull(io) => new(ErrorCode.DiskFull,
            "The disk is full. Free some space and try again.", io.Message, io),
        IOException io => new(ErrorCode.OutputNotWritable,
            "Couldn't write the output file. Check the folder isn't read-only.", io.Message, io),
        _ => new(ErrorCode.Unexpected, "Something went wrong. Check the log for details, then retry.", ex.ToString(), ex),
    };

    private static bool IsDiskFull(IOException io)
    {
        const int ERROR_HANDLE_DISK_FULL = 0x27, ERROR_DISK_FULL = 0x70;
        int code = io.HResult & 0xFFFF;
        return code is ERROR_HANDLE_DISK_FULL or ERROR_DISK_FULL;
    }
}
