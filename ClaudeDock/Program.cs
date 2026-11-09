using System;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace ClaudeDock;

public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            global::Shmuelie.WinRTServer.ComServer server = new();
            ManualResetEvent disposed = new(false);
            ClaudeDockExtension instance = new(disposed);
            server.RegisterClass<ClaudeDockExtension, IExtension>(() => instance);
            server.Start();
            disposed.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else if (args.Length > 0 && args[0] == "--dump")
        {
            // Diagnostics: print what the monitor sees, without CmdPal.
            var sessions = SessionMonitor.PollOnce();
            foreach (var s in sessions)
            {
                Console.WriteLine($"== {SessionView.DockTitle(s)}  [{s.Source}]");
                Console.WriteLine(SessionView.Hover(s));
                Console.WriteLine($"   row: {SessionView.RowSubtitle(s)} | tags: {string.Join(", ", SessionView.Tags(s).Select(t => t.Text))}");
            }
        }
        else if (args.Length > 0 && args[0] == "--toast-test")
        {
            // Needs package identity: Invoke-CommandInDesktopPackage ... -Args "--toast-test"
            Notifier.Init();
            Notifier.Show("Claude Dock test", "Toasts work.", "You can dismiss this.");
            Thread.Sleep(1500);
        }
        else if (args.Length > 0 && args[0] == "--work")
        {
            var done = new ManualResetEventSlim();
            Work.WorkMonitor.Changed += _ => done.Set();
            Work.WorkMonitor.Refresh();
            done.Wait(TimeSpan.FromSeconds(120));
            var w = Work.WorkMonitor.Current;
            Console.WriteLine($"Jira: {w.JiraError ?? w.Issues.Count + " issues"}");
            foreach (var i in w.Issues.Take(5))
            {
                Console.WriteLine($"  {i.Key} [{i.Status}] {i.Summary}");
            }

            Console.WriteLine($"Bitbucket: {w.BitbucketError ?? $"{w.MyPrs.Count} mine, {w.ToReview.Count} to review"}");
            foreach (var p in w.MyPrs.Take(4))
            {
                Console.WriteLine($"  mine {p.Repo}#{p.Id} ready={p.ReadyToMerge} {p.Approvals}/{p.Reviewers.Length} build={p.Build} changes={p.ChangesRequested} {p.SourceBranch}->{p.TargetBranch}");
            }

            Console.WriteLine(Work.WorkView.MyPrsHover(w));
            Console.WriteLine(Work.WorkView.ReviewHover(w));
            foreach (var p in w.ToReview.Take(5))
            {
                Console.WriteLine($"  review {p.Repo}#{p.Id} {p.Approvals}/{p.Reviewers.Length} build={p.Build} comments={p.Comments} tasks={p.Tasks} bot={p.IsBot} approved={p.IApproved} jira={p.JiraKey} {p.Title}");
            }
        }
        else if (args.Length > 1 && args[0] == "--rename")
        {
            var s = SessionMonitor.PollOnce().Find(x => x.SessionId.StartsWith(args[1], StringComparison.OrdinalIgnoreCase));
            if (s is null)
            {
                Console.WriteLine("no such session");
                return;
            }

            RenameDialog.Show(s);
            RenameDialog.WaitClosed();
            Console.WriteLine($"label={LabelStore.Get(s.SessionId)}");
        }
        else if (args.Length > 1 && args[0] == "--focus")
        {
            var sessions = SessionMonitor.PollOnce();
            foreach (var s in sessions)
            {
                if (s.SessionId.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(Focuser.Focus(s));
                    return;
                }
            }

            Console.WriteLine("no such session");
        }
        else
        {
            Console.WriteLine("Not launched as an extension. Use --dump or --focus <sessionIdPrefix>.");
        }
    }
}
