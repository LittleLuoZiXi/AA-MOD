using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AzureArchive.RevisionCompare;

// Compile only alongside RevisionUpdateApplySession.cs for cancellation unit checks.
internal readonly struct RevisionSemanticVersion : IComparable<RevisionSemanticVersion>
{
    internal static bool TryParse(string value, out RevisionSemanticVersion result) { result = default; return true; }
    public int CompareTo(RevisionSemanticVersion other) => 1;
    public override string ToString() => "1.1.0";
}
public static class UpdateApplySessionTargetedTests
{
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static int Run(string workspace)
    {
        Directory.CreateDirectory(workspace);
        string first = Path.Combine(workspace, "normal-cancel"); Directory.CreateDirectory(first);
        using (var session = new RevisionUpdateApplySession(workspace, "1.0.0"))
        {
            Set(session, "taskDirectory", first); Set(session, "token", new string('a', 64)); Set(session, "confirmed", true);
            session.Cancel();
            Assert(File.ReadAllText(Path.Combine(first, "cancel")) == new string('a', 64), "Acknowledged authorization was not revoked.");
            Assert(session.Snapshot.State == RevisionUpdateApplyState.Cancelled, "Cancellation state missing.");
        }
        string second = Path.Combine(workspace, "cancel-write-fails"); Directory.CreateDirectory(Path.Combine(second, "cancel.new"));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
        using (var child = Process.Start(start)!)
        using (var session = new RevisionUpdateApplySession(workspace, "1.0.0"))
        {
            try
            {
                Set(session, "taskDirectory", second); Set(session, "token", new string('b', 64)); Set(session, "helper", child); Set(session, "confirmed", true);
                session.Cancel();
                Assert(child.HasExited, "Helper was not stopped when cancellation file write failed.");
                Assert(session.Snapshot.State == RevisionUpdateApplyState.Cancelled, "Safe cancellation fallback did not complete.");
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
        }
        string third = Path.Combine(workspace, "handoff-dispose"); Directory.CreateDirectory(third);
        var handedOff = new RevisionUpdateApplySession(workspace, "1.0.0");
        Set(handedOff, "taskDirectory", third); Set(handedOff, "token", new string('c', 64)); Set(handedOff, "confirmed", true); handedOff.Dispose();
        Assert(!File.Exists(Path.Combine(third, "cancel")), "Disposal after authorized handoff cancelled the helper.");
        return 3;
    }
}
