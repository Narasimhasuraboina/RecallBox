using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace RecallBox.App;

public sealed record AppSettings(string PreferredFolder);

public sealed class DownloadRequest
{
    public string PhoneNumber { get; set; } = "";
    public string DestinationFolder { get; set; } = "";
    public bool ConfirmedOwnChat { get; set; }
}

public sealed class DownloadJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string PhoneNumber { get; init; } = "";
    public string AcquisitionMethod { get; set; } = "WhatsApp Desktop UI Automation (best effort)";
    public string DestinationFolder { get; init; } = "";
    public string ContactFolder { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "queued";
    public string Phase { get; set; } = "Queued";
    public int PagesRead { get; set; }
    public int MessagesFound { get; set; }
    public int MessagesSaved { get; set; }
    public int AttachmentsSaved { get; set; }
    public int ItemsSkipped { get; set; }
    public ConcurrentQueue<string> Warnings { get; set; } = new();
    public ConcurrentQueue<string> Errors { get; set; } = new();
    public string? ResultPath { get; set; }
    public string Coverage { get; set; } = "unknown";
}

public sealed record UiMessage(string Text, string ControlType, string AutomationId, string? TimestampText, string? Sender, string? UnsupportedReason);

public sealed class ArchiveMessage
{
    public string Id { get; set; } = "";
    public int Sequence { get; set; }
    public int SourcePageNumber { get; set; }
    public string Text { get; set; } = "";
    public string? Sender { get; set; }
    public string? TimestampText { get; set; }
    public DateTime? LocalTimestamp { get; set; }
    public string? ReplyOrEditMetadata { get; set; }
    public string MessageType { get; set; } = "text-or-unknown";
    public string SourceControlType { get; set; } = "";
    public string SourceAutomationId { get; set; } = "";
    public List<ArchiveMedia> Media { get; set; } = [];
    public string? Limitation { get; set; }
}

public sealed class ArchiveMedia
{
    public string Category { get; set; } = "other";
    public string? FileName { get; set; }
    public string? RelativePath { get; set; }
    public bool Downloaded { get; set; }
    public string? Limitation { get; set; }
}

public sealed record SourcePage(IReadOnlyList<UiMessage> Messages, bool Scrolled, string? Warning, int PageNumber);

public sealed record ArchiveMetadata
{
    public string ContactIdentifier { get; init; } = "";
    public DateTimeOffset DownloadedAt { get; init; }
    public string RetrievalStatus { get; init; } = "partial";
    public string Coverage { get; init; } = "unknown";
    public int MessagesFound { get; init; }
    public int AttachmentsSaved { get; init; }
    public int ItemsSkipped { get; init; }
    public string Source { get; init; } = "WhatsApp Desktop UI Automation";
    public string[] KnownLimitations { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string[] Errors { get; init; } = [];
}
