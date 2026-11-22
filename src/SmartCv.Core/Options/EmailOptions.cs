namespace SmartCv.Core.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public string Mode { get; set; } = "File"; // File|Smtp|Disabled; fail closed on unknown
    public string PickupDirectory { get; set; } = ".mail-dev";
    public string FromAddress { get; set; } = "scapp@localhost.invalid";
    public string FromName { get; set; } = "SCApp";
    public bool WorkerEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public SmtpEmailOptions Smtp { get; set; } = new();
    public bool IsValid() => (Mode is "File" or "Smtp" or "Disabled")
        && PollIntervalSeconds is >= 1 and <= 3600 && BatchSize is >= 1 and <= 100
        && Smtp.Port is >= 1 and <= 65535 && Smtp.TimeoutSeconds is >= 5 and <= 90
        && !string.IsNullOrWhiteSpace(FromName) && FromName.Length <= 100
        && !string.IsNullOrWhiteSpace(PickupDirectory);
}
public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}
