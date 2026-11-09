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
