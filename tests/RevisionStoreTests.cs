using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AzureArchive.RevisionCompare;

public static class RevisionStoreTests
{
    private sealed class Actor
    {
        internal string Name;
        internal string Face = "neutral";
        internal List<int> Position = new() { 0, 0 };
        internal Actor(string name) => Name = name;
        internal Actor Copy() => new(Name) { Face = Face, Position = new(Position) };
    }
    private sealed class Line
    {
        internal string Text;
        internal List<Actor> Stage;
        internal Line(string text, params string[] stage) { Text = text; Stage = stage.Select(name => new Actor(name)).ToList(); }
        internal Line Copy() => new(Text) { Stage = Stage.Select(actor => actor.Copy()).ToList() };
    }
    private static RevisionIdentity Id(string line = "line-1", string project = "project-1", string session = "session-1") =>
        new(project, session, "node-1", line);
    private static bool Same(Line left, Line right) => left.Text == right.Text && left.Stage.Count == right.Stage.Count &&
        left.Stage.Zip(right.Stage).All(pair => pair.First.Name == pair.Second.Name && pair.First.Face == pair.Second.Face &&
            pair.First.Position.SequenceEqual(pair.Second.Position));
    private static RevisionStore<Line> Store() => new(line => line.Copy(), Same);
    private static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected stale comparison to be rejected.");
    }

    public static string Run()
    {
        var checks = new List<object>();
        bool allPassed = true;
        void Test(string name, Action test)
        {
            try { test(); checks.Add(new { name, passed = true, error = "" }); }
            catch (Exception error) { allPassed = false; checks.Add(new { name, passed = false, error = error.Message }); }
        }

        Test("首次选中初始化fallback；仅观察编辑和刷新不推进", () =>
        {
            var store = Store(); var live = new Line("A", "stage-A");
            store.Observe(Id(), live);
            live.Text = "B"; live.Stage[0].Name = "stage-B";
            store.Observe(Id(), live);
            var comparison = store.CaptureComparison(live);
            Check(comparison.Previous.Text == "A" && comparison.Previous.Stage[0].Name == "stage-A", "Baseline changed during editing.");
            Check(comparison.Current.Text == "B", "Current content was not captured.");
            Check(store.LatestPlayedCopy() == null, "First selection was incorrectly counted as a manual playback.");
        });
        Test("首次正式播放替换首次选中fallback而不产生虚构的上一场播放", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("fallback"));
            var fallback = store.CaptureComparison(new Line("A"));
            Check(store.CaptureAtManualPreview(Id(), new Line("A")), "First playback was not recorded.");
            Check(store.BaselineCopy()!.Text == "A" && store.LatestPlayedCopy()!.Text == "A", "Fallback was promoted into a separate playback revision.");
            Check(!store.IsCurrent(fallback), "Formal playback did not invalidate the fallback comparison token.");
            store.CaptureAtManualPreview(Id(), new Line("B"));
            Check(store.BaselineCopy()!.Text == "A", "Second manual playback lost the first actual playback.");
        });
        Test("播放A→改B仍A→播放B仍A→改C未播仍A→播放C比较B", () =>
        {
            var store = Store(); var live = new Line("A");
            store.CaptureAtManualPreview(Id(), live);
            Check(store.BaselineCopy()!.Text == "A", "First playback must compare against itself.");
            live.Text = "B";
            var first = store.CaptureComparison(live);
            Check(first.Previous.Text == "A", "First manual preview was lost.");
            Check(store.CaptureAtManualPreview(Id(), live), "Different B playback was not recorded.");
            var second = store.CaptureComparison(live);
            Check(second.Previous.Text == "A" && store.LatestPlayedCopy()!.Text == "B", "Second playback overwrote A with B.");
            live.Text = "C";
            store.Observe(Id(), live);
            var unplayed = store.CaptureComparison(live);
            Check(unplayed.Previous.Text == "A" && unplayed.Current.Text == "C", "Unplayed C advanced the previous playback.");
            store.CaptureAtManualPreview(Id(), live);
            var third = store.CaptureComparison(live);
            Check(third.Previous.Text == "B" && third.Current.Text == "C", "Third different playback did not advance previous to B.");
            Check(!store.IsCurrent(first), "Old popup token survived a new manual preview.");
        });
        Test("反复播放内容相同的新对象不推进版本或使窗口令牌过时", () =>
        {
            var store = Store(); store.CaptureAtManualPreview(Id(), new Line("A", "actor"));
            Check(!store.CaptureAtManualPreview(Id(), new Line("A", "actor")), "Repeated first playback was counted twice.");
            store.CaptureAtManualPreview(Id(), new Line("B", "actor"));
            var comparison = store.CaptureComparison(new Line("B", "actor"));
            for (int i = 0; i < 4; i++)
                Check(!store.CaptureAtManualPreview(Id(), new Line("B", "actor")), "Identical playback incorrectly advanced history.");
            Check(store.BaselineCopy()!.Text == "A" && store.LatestPlayedCopy()!.Text == "B", "Repeated B playback overwrote A.");
            Check(store.IsCurrent(comparison), "Identical playback changed the generation token.");
        });
        Test("正文相同但演员表情或嵌套位置改变仍推进版本", () =>
        {
            var store = Store(); var live = new Line("same text", "actor");
            store.CaptureAtManualPreview(Id(), live);
            live.Stage[0].Face = "smile";
            Check(store.CaptureAtManualPreview(Id(), live), "Facial expression change was ignored.");
            Check(store.BaselineCopy()!.Stage[0].Face == "neutral" && store.LatestPlayedCopy()!.Stage[0].Face == "smile", "Stage snapshots did not preserve both versions.");
            live.Stage[0].Position[1] = 120;
            Check(store.CaptureAtManualPreview(Id(), live), "Nested position change was ignored.");
            var previous = store.BaselineCopy()!;
            Check(previous.Stage[0].Face == "smile" && previous.Stage[0].Position[1] == 0, "Nested stage state was not deep-copied.");
            Check(store.LatestPlayedCopy()!.Stage[0].Position[1] == 120, "Latest stage position was not recorded.");
        });
        Test("切换到其他条目再回来保留各自上一版", () =>
        {
            var store = Store();
            store.CaptureAtManualPreview(Id("one"), new Line("one-A"));
            store.CaptureAtManualPreview(Id("one"), new Line("one-B"));
            store.CaptureAtManualPreview(Id("two"), new Line("two-A"));
            store.CaptureAtManualPreview(Id("two"), new Line("two-B"));
            store.CaptureAtManualPreview(Id("two"), new Line("two-C"));
            store.Observe(Id("one"), new Line("one-C"));
            Check(store.CaptureComparison(new Line("one-C")).Previous.Text == "one-A" && store.LatestPlayedCopy()!.Text == "one-B", "Reselect overwrote either stored version.");
            store.Observe(Id("two"), new Line("two-D"));
            Check(store.CaptureComparison(new Line("two-D")).Previous.Text == "two-B" && store.LatestPlayedCopy()!.Text == "two-C", "Another line's playback history changed.");
        });
        Test("相同节点和条目ID在不同工程之间隔离", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("project-A"));
            var old = store.CaptureComparison(new Line("project-B"));
            store.Observe(Id(project: "project-2"), new Line("other-project"));
            Check(store.BaselineCopy()!.Text == "other-project" && !store.IsCurrent(old), "Project identity was not isolated.");
        });
        Test("同工程重开或连接会话改变使旧窗口失效", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("old-session"));
            var old = store.CaptureComparison(new Line("edited"));
            store.Observe(Id(session: "session-2"), new Line("reopened"));
            Check(!store.IsCurrent(old) && store.BaselineCopy()!.Text == "reopened", "Session identity was reused.");
        });
        Test("删除后撤销重建同ID时保留上一版但拒绝删除前窗口", () =>
        {
            var store = Store(); store.CaptureAtManualPreview(Id(), new Line("A"));
            var old = store.CaptureComparison(new Line("B"));
            store.Observe(null, null);
            Check(!store.IsCurrent(old), "Deleted item still accepted old popup.");
            store.Observe(Id(), new Line("B"));
            Check(store.BaselineCopy()!.Text == "A" && !store.IsCurrent(old), "Undo reconstruction corrupted identity or popup generation.");
        });
        Test("删除后新建的不同ID不能继承旧条目的快照", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("old"));
            store.Observe(null, null); store.Observe(Id("new-id"), new Line("new"));
            Check(store.BaselineCopy()!.Text == "new", "A new identity inherited another baseline.");
        });
        Test("窗口固定绑定条目且切走回来不能复用旧令牌", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("A"));
            var comparison = store.CaptureComparison(new Line("B"));
            store.Observe(Id("two"), new Line("two")); store.Observe(Id(), new Line("B"));
            Reject(() => store.PrepareRestore(comparison, new Line("B")));
        });
        Test("同条继续编辑允许恢复且Undo前态使用恢复瞬间内容", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("A"));
            var comparison = store.CaptureComparison(new Line("B"));
            store.Observe(Id(), new Line("C"));
            var refreshed = store.RefreshComparison(comparison, new Line("C"));
            var plan = store.PrepareRestore(comparison, new Line("C"));
            Check(store.IsCurrent(comparison) && refreshed.Current.Text == "C", "Same-line changes incorrectly invalidated comparison.");
            Check(plan.Before.Text == "C" && plan.After.Text == "A", "Restore plan did not preserve current Undo state.");
        });
        Test("对比或预览副本被修改也不污染受保护的快照", () =>
        {
            var store = Store(); var live = new Line("A", "stage-A"); store.Observe(Id(), live);
            var comparison = store.CaptureComparison(new Line("B", "stage-B"));
            comparison.Previous.Text = "corrupt"; comparison.Previous.Stage[0].Name = "corrupt-stage";
            var plan = store.PrepareRestore(comparison, new Line("B"));
            Check(plan.After.Text == "A" && plan.After.Stage[0].Name == "stage-A", "A comparison copy changed restore contents.");
        });
        Test("新旧播放输入、读取副本、对比副本和恢复副本均相互隔离", () =>
        {
            var store = Store(); var first = new Line("A", "actor-A");
            store.CaptureAtManualPreview(Id(), first);
            first.Text = "mutated input A"; first.Stage[0].Position[0] = 99;
            Check(store.LatestPlayedCopy()!.Text == "A" && store.BaselineCopy()!.Stage[0].Position[0] == 0, "Original playback input was retained by reference.");
            var second = new Line("B", "actor-B"); store.CaptureAtManualPreview(Id(), second);
            second.Text = "mutated input B"; second.Stage[0].Face = "wrong";
            var latestCopy = store.LatestPlayedCopy()!; latestCopy.Text = "corrupt latest"; latestCopy.Stage.Clear();
            var previousCopy = store.BaselineCopy()!; previousCopy.Text = "corrupt previous"; previousCopy.Stage[0].Position[0] = 77;
            var current = new Line("C", "actor-C"); var comparison = store.CaptureComparison(current);
            comparison.Previous.Text = "corrupt comparison A"; comparison.Previous.Stage.Clear();
            comparison.Current.Text = "corrupt comparison C"; comparison.Current.Stage[0].Position[0] = 88;
            var plan = store.PrepareRestore(comparison, current);
            Check(plan.Before.Text == "C" && plan.Before.Stage[0].Position[0] == 0, "Current comparison copy polluted the current edit.");
            Check(plan.After.Text == "A" && plan.After.Stage[0].Position[0] == 0, "Previous comparison copy polluted protected restore content.");
            plan.Before.Text = "corrupt undo"; plan.Before.Stage.Clear(); plan.After.Stage[0].Name = "corrupt restore";
            Check(current.Text == "C" && current.Stage.Count == 1 && store.BaselineCopy()!.Stage[0].Name == "actor-A", "Restore plan shared references with its inputs or history.");
            store.CaptureAtManualPreview(Id(), current);
            Check(store.BaselineCopy()!.Text == "B" && store.BaselineCopy()!.Stage[0].Face == "neutral", "Latest playback was polluted before advancing into previous.");
        });
        Test("钉住A后播放B和C仍显示并恢复A且Undo前态取瞬时内容", () =>
        {
            var store = Store(); store.CaptureAtManualPreview(Id(), new Line("A", "actor-A"));
            var comparison = store.CaptureComparison(new Line("B", "actor-B"));
            store.PinComparison(comparison);
            store.CaptureAtManualPreview(Id(), new Line("B", "actor-B"));
            store.CaptureAtManualPreview(Id(), new Line("C", "actor-C"));
            Check(store.IsCurrent(comparison), "Playback invalidated the pinned window.");
            Check(store.BaselineCopy()!.Text == "B" && store.LatestPlayedCopy()!.Text == "C", "Pinning prevented normal history updates.");
            var refreshed = store.RefreshComparison(comparison, new Line("C-now", "current-stage"));
            var plan = store.PrepareRestore(comparison, new Line("C-now", "current-stage"));
            Check(refreshed.Previous.Text == "A" && refreshed.Current.Text == "C-now", "Refreshing retargeted the pinned previous version.");
            Check(plan.After.Text == "A" && plan.After.Stage[0].Name == "actor-A" && plan.Before.Text == "C-now", "Pinned restore did not retain A and the instantaneous Undo contents.");
            store.ReleaseComparison();
            Check(!store.IsCurrent(comparison), "Released old token survived playback history changes.");
            Reject(() => store.PrepareRestore(comparison, new Line("C-now")));
            Check(store.CaptureComparison(new Line("C")).Previous.Text == "B", "Released pin polluted the next comparison window.");
        });
        Test("钉住版本拒绝输入和新旧读取副本污染且重复pin不会漂移", () =>
        {
            var store = Store(); var original = new Line("A", "actor-A");
            store.CaptureAtManualPreview(Id(), original);
            var comparison = store.CaptureComparison(new Line("B", "actor-B"));
            // Pin must not trust a caller-owned comparison, even before pinning.
            comparison.Previous.Text = "corrupt before pin"; comparison.Previous.Stage[0].Name = "bad";
            store.PinComparison(comparison);
            original.Text = "mutated original"; original.Stage.Clear();
            store.CaptureAtManualPreview(Id(), new Line("B", "actor-B"));
            store.CaptureAtManualPreview(Id(), new Line("C", "actor-C"));
            comparison.Previous.Stage.Clear(); comparison.Current.Text = "corrupt current";
            var refreshed = store.RefreshComparison(comparison, new Line("C", "actor-C"));
            Check(refreshed.Previous.Text == "A" && refreshed.Previous.Stage[0].Name == "actor-A", "Pin retained unprotected comparison data.");
            refreshed.Previous.Stage[0].Position[0] = 73;
            store.PinComparison(comparison);
            var plan = store.PrepareRestore(comparison, new Line("C", "actor-C"));
            Check(plan.After.Text == "A" && plan.After.Stage[0].Position[0] == 0, "Re-pin read rolled history or a mutated preview copy.");
            plan.After.Text = "corrupt restore"; plan.After.Stage.Clear();
            Check(store.PrepareRestore(comparison, new Line("D")).After.Text == "A", "Restore-plan mutation polluted the protected pin.");
        });
        Test("钉住窗口切条后失效且切回原条不能复用旧令牌", () =>
        {
            var store = Store(); store.CaptureAtManualPreview(Id(), new Line("A"));
            var comparison = store.CaptureComparison(new Line("B")); store.PinComparison(comparison);
            store.CaptureAtManualPreview(Id(), new Line("B"));
            store.Observe(Id("other"), new Line("other"));
            Check(!store.IsCurrent(comparison), "Pinned identity survived selecting another line.");
            store.Observe(Id(), new Line("B"));
            Check(!store.IsCurrent(comparison), "Pinned token revived after returning to the original line.");
            Reject(() => store.PinComparison(comparison));
            Reject(() => store.RefreshComparison(comparison, new Line("B")));
        });
        Test("清理会话释放pin且过时未pin比较不能被重新钉住", () =>
        {
            var store = Store(); store.CaptureAtManualPreview(Id(), new Line("A"));
            var comparison = store.CaptureComparison(new Line("B"));
            store.CaptureAtManualPreview(Id(), new Line("B"));
            Reject(() => store.PinComparison(comparison));
            var current = store.CaptureComparison(new Line("B")); store.PinComparison(current);
            store.Clear(); store.Observe(Id(), new Line("B"));
            Check(!store.IsCurrent(current), "Cleared pin survived into a reopened session.");
            Reject(() => store.PrepareRestore(current, new Line("B")));
        });
        Test("无修改恢复产生相同值且恢复计划仅包含指定条目", () =>
        {
            var store = Store(); var lines = new Dictionary<string, Line> { ["one"] = new("A"), ["two"] = new("keep") };
            store.Observe(Id("one"), lines["one"]);
            var unchanged = store.PrepareRestore(store.CaptureComparison(lines["one"]), lines["one"]);
            Check(unchanged.Before.Text == unchanged.After.Text, "No-op restore changed content.");
            lines["one"] = new("B");
            var changed = store.PrepareRestore(store.CaptureComparison(lines["one"]), lines["one"]);
            lines[changed.Identity.LineId] = changed.After;
            Check(lines["one"].Text == "A" && lines["two"].Text == "keep", "Single-item restore plan affected another line.");
            Check(changed.Before.Text == "B", "Undo source was not retained.");
        });
        Test("清理会话后旧令牌不能因计数器重用而重新有效", () =>
        {
            var store = Store(); store.Observe(Id(), new Line("A")); var old = store.CaptureComparison(new Line("B"));
            store.Clear(); store.Observe(Id(), new Line("A"));
            Check(!store.IsCurrent(old), "Cleared generation was reused.");
        });
        return JsonSerializer.Serialize(new { passed = allPassed, count = checks.Count, checks });
    }
}
