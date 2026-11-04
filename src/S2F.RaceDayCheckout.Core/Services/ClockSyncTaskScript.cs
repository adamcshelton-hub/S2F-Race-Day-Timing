using System.Globalization;
using System.Text;
using System.Text.Json;

namespace S2F.RaceDayCheckout.Core.Services;

/// <summary>
/// One-time scheduled task that sets the Windows clock. The task action is stored in
/// Task Scheduler (not a user-writable script). The unelevated app writes a request
/// file with the HTTPS time, then <c>schtasks /Run</c>. Later Sync Clock clicks do not prompt.
/// </summary>
public static class ClockSyncTaskScript
{
    public const string TaskName = "S2F Clock Sync";

    /// <summary>
    /// Baked into the task description. Sync Clock re-registers when this token is missing
    /// so an app update can replace the elevated script. That asks for administrator permission once.
    /// </summary>
    public const string TaskVersionToken = "s2f-clock-sync-v1";

    /// <summary>schtasks /TN value. Root folder so we only have to loosen this task's DACL.</summary>
    public const string TaskQueryName = @"\S2F Clock Sync";

    /// <summary>
    /// Request and result files only. Users can modify this folder; it must never hold the task script.
    /// </summary>
    public const string StateDirectory = @"C:\ProgramData\S2F\clock-sync";

    public const string RequestFileName = "request.json";
    public const string ResultFileName = "result.json";

    /// <summary>Task ignores a request older than this. Both sides see the same (possibly wrong) clock.</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(3);

    public static string RequestFilePath => Path.Combine(StateDirectory, RequestFileName);
    public static string ResultFilePath => Path.Combine(StateDirectory, ResultFileName);

    public static string? BuildRequestJson(string id, DateTimeOffset? referenceUtc)
    {
        if (id.Length != 32 || !IsLowerHex(id))
            return null;

        if (referenceUtc is not { } utc)
            return "{\"id\":\"" + id + "\"}";

        var roundTrip = utc.ToUniversalTime().UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        return "{\"id\":\"" + id + "\",\"utc\":\"" + roundTrip + "\"}";
    }

    public static bool TryParseResult(string? json, out ClockSyncTaskResult result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var detail = root.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
                ? detailEl.GetString() ?? string.Empty
                : string.Empty;
            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;
            DateTimeOffset? finished = null;
            if (root.TryGetProperty("finishedUtc", out var finishedEl)
                && finishedEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    finishedEl.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                finished = parsed;
            }

            result = new ClockSyncTaskResult(ok, detail, id, finished);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs as SYSTEM. Sets the clock from the request UTC (user-probed HTTPS time).
    /// Falls back to a short <c>w32tm</c> only when the request has no time.
    /// Exits 0 only when <c>Set-Date</c> ran. The app still re-probes before recording success.
    /// </summary>
    public static string BuildSyncScript()
    {
        var maxAge = ((int)RequestMaxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$dir = '" + StateDirectory + "'");
        sb.AppendLine("$reqPath = Join-Path $dir '" + RequestFileName + "'");
        sb.AppendLine("$resultPath = Join-Path $dir '" + ResultFileName + "'");
        sb.AppendLine("$id = ''");
        sb.AppendLine("$utc = ''");
        sb.AppendLine("$detail = 'no request'");
        sb.AppendLine("$set = $false");
        sb.AppendLine("try {");
        sb.AppendLine("  $raw = [IO.File]::ReadAllText($reqPath)");
        sb.AppendLine("  if ($raw -match '\"id\"\\s*:\\s*\"([a-f0-9]{32})\"') { $id = $Matches[1] }");
        sb.AppendLine("  if ($raw -match '\"utc\"\\s*:\\s*\"(\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?Z)\"') { $utc = $Matches[1] }");
        sb.AppendLine("} catch { }");
        sb.AppendLine("if ($utc.Length -gt 0) {");
        sb.AppendLine("  $ageSec = ([DateTime]::UtcNow - (Get-Item -LiteralPath $reqPath).LastWriteTimeUtc).TotalSeconds");
        sb.AppendLine("  if ($ageSec -lt 0) { $ageSec = 0 }");
        sb.AppendLine("  if ($ageSec -gt " + maxAge + ") {");
        sb.AppendLine("    $detail = 'request was stale'");
        sb.AppendLine("  } else {");
        sb.AppendLine("    try {");
        sb.AppendLine("      $parsed = [DateTime]::Parse($utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)");
        sb.AppendLine("      $local = $parsed.AddSeconds($ageSec).ToLocalTime()");
        sb.AppendLine("      Set-Date -Date $local | Out-Null");
        sb.AppendLine("      $set = $true");
        sb.AppendLine("      $detail = 'set'");
        sb.AppendLine("    } catch {");
        sb.AppendLine("      $detail = $_.Exception.Message");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $set -and $utc.Length -eq 0) {");
        sb.AppendLine("  try {");
        sb.AppendLine("    $w32 = Join-Path $env:SystemRoot 'System32\\w32tm.exe'");
        sb.AppendLine("    $p = Start-Process -FilePath $w32 -ArgumentList '/resync','/force' -WindowStyle Hidden -PassThru");
        sb.AppendLine("    if (-not $p.WaitForExit(8000)) {");
        sb.AppendLine("      try { Stop-Process -Id $p.Id -Force } catch { }");
        sb.AppendLine("      $detail = 'time sync timed out'");
        sb.AppendLine("    } else {");
        sb.AppendLine("      $detail = 'ntp exit ' + $p.ExitCode");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch {");
        sb.AppendLine("    $detail = $_.Exception.Message");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("$safe = -join ($detail.ToCharArray() | ForEach-Object { if ($_ -match '[A-Za-z0-9 .:_-]') { $_ } else { ' ' } })");
        sb.AppendLine("$ok = if ($set) { 'true' } else { 'false' }");
        sb.AppendLine("$finished = [DateTimeOffset]::UtcNow.ToString('o')");
        sb.AppendLine("$json = '{\"ok\":' + $ok + ',\"detail\":\"' + $safe + '\",\"id\":\"' + $id + '\",\"finishedUtc\":\"' + $finished + '\"}'");
        sb.AppendLine("try {");
        sb.AppendLine("  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }");
        sb.AppendLine("  $tmp = $resultPath + '.tmp'");
        sb.AppendLine("  [IO.File]::WriteAllText($tmp, $json)");
        sb.AppendLine("  if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }");
        sb.AppendLine("  [IO.File]::Move($tmp, $resultPath)");
        sb.AppendLine("} catch {");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        sb.AppendLine("if ($set) { exit 0 }");
        sb.AppendLine("exit 1");
        return sb.ToString();
    }

    /// <summary>
    /// Elevated once. Registers the SYSTEM task, grants Users read/execute on that task,
    /// and grants Users modify on the state folder (request/result only).
    /// </summary>
    public static string BuildRegistrationScript()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildSyncScript()));
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$dir = '" + StateDirectory + "'");
        sb.AppendLine("New-Item -ItemType Directory -Force -Path $dir | Out-Null");
        sb.AppendLine("$icacls = Join-Path $env:SystemRoot 'System32\\icacls.exe'");
        sb.AppendLine("& $icacls $dir '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)M'");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { exit 1 }");
        sb.AppendLine("$exe = Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'");
        sb.AppendLine("$arg = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded + "'");
        sb.AppendLine("$action = New-ScheduledTaskAction -Execute $exe -Argument $arg");
        sb.AppendLine("$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest");
        sb.AppendLine("$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)");
        sb.AppendLine("Register-ScheduledTask -TaskName '" + TaskName + "' -TaskPath '\\' -Action $action -Principal $principal -Settings $settings -Description 'S2F Race Day Timing clock sync " + TaskVersionToken + "' -Force | Out-Null");
        sb.AppendLine("$svc = New-Object -ComObject Schedule.Service");
        sb.AppendLine("$svc.Connect()");
        sb.AppendLine("$folder = $svc.GetFolder('\\')");
        sb.AppendLine("$task = $folder.GetTask('" + TaskName + "')");
        sb.AppendLine("$sec = $task.GetSecurityDescriptor(4)");
        sb.AppendLine("$hasUser = ($sec -match ';;;BU\\)') -or ($sec -match ';;;S-1-5-32-545\\)')");
        sb.AppendLine("if (-not $hasUser) {");
        sb.AppendLine("  $idx = $sec.IndexOf('(')");
        sb.AppendLine("  if ($idx -lt 1) { exit 1 }");
        sb.AppendLine("  $sec = $sec.Insert($idx, '(A;;0x1200A9;;;BU)')");
        sb.AppendLine("  $task.SetSecurityDescriptor($sec, 0)");
        sb.AppendLine("}");
        sb.AppendLine("$check = $task.GetSecurityDescriptor(4)");
        sb.AppendLine("$hasUser = ($check -match ';;;BU\\)') -or ($check -match ';;;S-1-5-32-545\\)')");
        sb.AppendLine("if (-not $hasUser) { exit 1 }");
        sb.AppendLine("$schtasks = Join-Path $env:SystemRoot 'System32\\schtasks.exe'");
        sb.AppendLine("& $schtasks /Query /TN '" + TaskQueryName + "' /FO LIST");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { exit 1 }");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    private static bool IsLowerHex(string id)
    {
        foreach (var c in id)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }

        return true;
    }
}

public readonly record struct ClockSyncTaskResult(
    bool Ok,
    string Detail,
    string Id,
    DateTimeOffset? FinishedUtc);
