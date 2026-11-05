using System.Drawing;
using System.Windows.Forms;
using ADHDWindows.Core.Data;

namespace ADHDWindows.App;

/// <summary>Значок в трее. Системный значок временный: свой появится вместе с дизайн-системой.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(TextCatalog texts, bool developerMode, Action onExit)
    {
        var menu = new ContextMenuStrip();
        if (developerMode)
        {
            menu.Items.Add(new ToolStripMenuItem(texts.Get("tray.devMode")) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add(texts.Get("tray.exit"), null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = texts.Get("app.title"),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
