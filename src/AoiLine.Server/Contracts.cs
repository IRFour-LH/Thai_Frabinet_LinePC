using AoiLine.Core.Models;

namespace AoiLine.Server;

// ---------------------------------------------------------------- agent <-> server

/// <summary>Sent by each agent every few seconds.</summary>
public class HeartbeatRequest
{
    public string LineId { get; set; } = "";
    public string LineName { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
    public string WatchFolder { get; set; } = "";
    public string FilePattern { get; set; } = "";
    public bool FolderExists { get; set; }

    /// <summary>Files found by the agent that still have to be forwarded.</summary>
    public int Pending { get; set; }

    public string LastForwarded { get; set; } = "";
    public string LastError { get; set; } = "";

    /// <summary>Agent clock, used to convert the run start time to the agent's clock.</summary>
    public DateTime AgentNowUtc { get; set; }
}

public class HeartbeatResponse
{
    public bool Running { get; set; }

    /// <summary>Run start time expressed in the AGENT's clock (corrects clock differences between PCs).</summary>
    public DateTime? RunStartAgentUtc { get; set; }

    public bool OnlyFilesAfterStart { get; set; }
    public DateTime ServerNowUtc { get; set; }
}

public class FileUploadRequest
{
    public string LineId { get; set; } = "";
    public string LineName { get; set; } = "";
    public string FileName { get; set; } = "";

    /// <summary>File created/written time on the line PC (agent clock).</summary>
    public DateTime FileTimeUtc { get; set; }

    public string Content { get; set; } = "";
}

/// <summary>accepted = saved on the server; duplicate = already sent/queued; ignored = line not running.</summary>
public record FileUploadResponse(string Status, string Message);

// ---------------------------------------------------------------- web pages

public class StartRequest
{
    public string InputValue { get; set; } = "";
    public Dictionary<string, string> Extras { get; set; } = new();
}

public class StopRequest
{
    public bool Force { get; set; }
}

public class ResendRequest
{
    public string FileName { get; set; } = "";
}

public record StopResponse(bool Stopped, bool NeedsConfirm, List<string> QueuedFiles, int AgentPending, string Message);

public record CountTotals(int Files, int Qty, int Good, int Ng);

public class AgentView
{
    public bool Online { get; set; }
    public DateTime? LastSeen { get; set; }
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
    public string WatchFolder { get; set; } = "";
    public string FilePattern { get; set; } = "";
    public bool FolderExists { get; set; }
    public int Pending { get; set; }
    public string LastForwarded { get; set; } = "";
    public string LastError { get; set; } = "";
}

/// <summary>One line on the overview page.</summary>
public class LineView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public string InputValue { get; set; } = "";
    public Dictionary<string, string> Extras { get; set; } = new();
    public DateTime? RunStart { get; set; }
    public DateTime? RunStop { get; set; }
    public int RunFiles { get; set; }
    public int RunQty { get; set; }
    public int RunGood { get; set; }
    public int RunNg { get; set; }
    public int RunFailures { get; set; }

    /// <summary>Files received from the agent and not yet sent to the API.</summary>
    public int Queued { get; set; }

    /// <summary>Queued files that failed and wait to retry.</summary>
    public int RetryWaiting { get; set; }

    public string WarningText { get; set; } = "";
    public int WarningCount { get; set; }
    public CountTotals Today { get; set; } = new(0, 0, 0, 0);
    public AgentView Agent { get; set; } = new();

    /// <summary>ok / warn / error / stopped</summary>
    public string StatusLevel { get; set; } = "stopped";
    public string StatusText { get; set; } = "";
}

public class InputDefinition
{
    public string Label { get; set; } = "";
    public List<ExtraInput> Extras { get; set; } = new();
    public string LastInputValue { get; set; } = "";
    public Dictionary<string, string> LastExtras { get; set; } = new();
}

/// <summary>Everything the line page shows.</summary>
public class LineDetail : LineView
{
    public List<FileResult> Recent { get; set; } = new();
    public List<string> Logs { get; set; } = new();
    public InputDefinition Inputs { get; set; } = new();
    public bool ApiEnabled { get; set; }
    public string ApiInfo { get; set; } = "";
    public string QtyUnit { get; set; } = "";
    public string MatchInfo { get; set; } = "";
}

// ---------------------------------------------------------------- settings tools

public class DetectRequest
{
    public ParsingSettings Parsing { get; set; } = new();
    public string Content { get; set; } = "";
}

public class PreviewRequest
{
    public ProcessingSettings Processing { get; set; } = new();
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";

    /// <summary>Optional value to test matching with.</summary>
    public string? InputValue { get; set; }
}
